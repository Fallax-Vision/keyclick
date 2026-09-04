using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed record StatisticsCapturePolicy(
  bool DisclosureConfirmed,
  bool KeyboardEnabled,
  bool PointerEnabled,
  bool ScrollingEnabled,
  IReadOnlySet<string> ExcludedExecutables,
  IReadOnlyDictionary<string, DeviceFamily> DeviceClassifications)
{
  public static StatisticsCapturePolicy FromSettings(AppSettings settings) => new(
    settings.StatisticsDisclosureConfirmed && settings.StatisticsDisclosureVersion >= AppSettings.CurrentStatisticsDisclosureVersion,
    settings.KeyboardStatisticsEnabled,
    settings.PointerStatisticsEnabled,
    settings.ScrollingStatisticsEnabled,
    new HashSet<string>(settings.StatisticsExcludedExecutables, StringComparer.OrdinalIgnoreCase),
    new Dictionary<string, DeviceFamily>(settings.DeviceClassifications, StringComparer.Ordinal));
}

public sealed class StatisticsService : IAsyncDisposable
{
  private const int QueueCapacity = 8192;
  private static readonly long SessionGapTicks = 60L * Stopwatch.Frequency;
  private readonly IStatisticsStore _store;
  private readonly Channel<QueueItem> _queue;
  private readonly Task _consumer;
  private readonly Timer _flushTimer;
  private StatisticsCapturePolicy _policy;
  private long _overflowCount;

  public StatisticsService(IStatisticsStore store, AppSettings initialSettings)
  {
    _store = store;
    _policy = StatisticsCapturePolicy.FromSettings(initialSettings);
    _queue = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(QueueCapacity)
    {
      SingleReader = true,
      SingleWriter = false,
      FullMode = BoundedChannelFullMode.Wait,
      AllowSynchronousContinuations = false
    });
    _consumer = Task.Run(ConsumeAsync);
    _flushTimer = new(_ => _queue.Writer.TryWrite(QueueItem.FlushRequest), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
  }

  public long OverflowCount => Interlocked.Read(ref _overflowCount);
  public event Action? DataChanged;

  public void UpdatePolicy(AppSettings settings)
  {
    Volatile.Write(ref _policy, StatisticsCapturePolicy.FromSettings(settings));
    TryQueue(QueueItem.FlushRequest);
  }

  public bool TryRecord(InputActionEvent action)
  {
    var policy = Volatile.Read(ref _policy);
    if (!policy.DisclosureConfirmed) return false;
    if (ExecutableExclusionMatcher.Matches(policy.ExcludedExecutables, action.ForegroundExecutable)) return false;
    var accepted = action.Input.Kind switch
    {
      InputKind.KeyboardKey => action.Phase == InputPhase.Down && policy.KeyboardEnabled,
      InputKind.PointerButton => action.Phase == InputPhase.Up && policy.PointerEnabled,
      InputKind.Wheel => action.Phase == InputPhase.WheelDetent && policy.PointerEnabled && policy.ScrollingEnabled,
      _ => false
    };
    if (!accepted) return false;
    if (action.Input.DeviceId is { Length: > 0 } deviceId && policy.DeviceClassifications.TryGetValue(deviceId, out var family))
      action = action with { Input = action.Input with { DeviceFamily = family } };
    var queued = TryQueue(new QueueItem(action, false, null, null, null, null));
    if (queued) DataChanged?.Invoke();
    return queued;
  }

  public async Task<StatisticsSnapshot> QueryAsync(StatisticsQuery query, CancellationToken cancellationToken = default)
  {
    var completion = new TaskCompletionSource<StatisticsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    await _queue.Writer.WriteAsync(QueueItem.StatisticsQueryRequest(query, completion), cancellationToken);
    return await completion.Task.WaitAsync(cancellationToken);
  }

  public async Task<IReadOnlyList<ApplicationStatisticsRow>> QueryApplicationAsync(StatisticsQuery query, CancellationToken cancellationToken = default)
  {
    var completion = new TaskCompletionSource<IReadOnlyList<ApplicationStatisticsRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
    await _queue.Writer.WriteAsync(QueueItem.ApplicationQueryRequest(query, completion), cancellationToken);
    return await completion.Task.WaitAsync(cancellationToken);
  }

  public async Task DeleteAsync(StatisticsDeleteRequest request, CancellationToken cancellationToken = default)
  {
    await RequestFlushAsync(cancellationToken);
    await _store.DeleteStatisticsAsync(request, cancellationToken);
    DataChanged?.Invoke();
  }

  public async Task ExportCsvAsync(StatisticsSnapshot snapshot, string path, CancellationToken cancellationToken = default)
  {
    var output = new StringBuilder();
    output.AppendLine("bucket_utc,keyboard_presses,pointer_clicks,vertical_scroll,horizontal_scroll,active_milliseconds");
    foreach (var point in snapshot.Trend)
      output.AppendLine($"{point.BucketUtc:O},{point.KeyboardPresses},{point.PointerClicks},{point.VerticalScroll},{point.HorizontalScroll},{point.ActiveMilliseconds}");
    output.AppendLine();
    output.AppendLine("input_kind,device_family,physical_code,extended,input_group,count");
    foreach (var item in snapshot.Breakdown)
      output.AppendLine($"{item.Kind},{item.DeviceFamily},{item.PhysicalCode},{item.Extended},{item.Group},{item.Count}");
    await File.WriteAllTextAsync(path, output.ToString(), new UTF8Encoding(false), cancellationToken);
  }

  public async ValueTask DisposeAsync()
  {
    _flushTimer.Dispose();
    TryQueue(QueueItem.FlushRequest);
    _queue.Writer.TryComplete();
    await _consumer;
  }

  private bool TryQueue(QueueItem item)
  {
    if (_queue.Writer.TryWrite(item)) return true;
    Interlocked.Increment(ref _overflowCount);
    return false;
  }

  private async Task ConsumeAsync()
  {
    var aggregates = new Dictionary<StatisticsAggregateKey, MutableAggregate>();
    var applicationAggregates = new Dictionary<ApplicationStatisticsAggregateKey, MutableApplicationAggregate>();
    var applicationIdentities = new Dictionary<string, (string Id, string Name)>(StringComparer.OrdinalIgnoreCase);
    var typingWindow = new Queue<long>();
    var clickingWindow = new Queue<long>();
    string? sourceId = null;
    DateTimeOffset? activeBucket = null;
    long? lastAny = null, lastKeyboard = null, lastPointer = null;
    var lastFlush = Stopwatch.GetTimestamp();
    await foreach (var item in _queue.Reader.ReadAllAsync())
    {
      if (item.StatisticsCompletion is not null && item.Query is not null)
      {
        try
        {
          var stored = await _store.QueryStatisticsAsync(item.Query);
          item.StatisticsCompletion.TrySetResult(MergePending(stored, CreatePendingSnapshot(aggregates, applicationAggregates).Inputs));
        }
        catch (Exception exception) { item.StatisticsCompletion.TrySetException(exception); }
        continue;
      }
      if (item.ApplicationCompletion is not null && item.Query is not null)
      {
        try
        {
          var stored = await _store.QueryApplicationStatisticsAsync(item.Query);
          item.ApplicationCompletion.TrySetResult(MergePendingApplications(
            stored,
            CreatePendingSnapshot(aggregates, applicationAggregates).Applications,
            item.Query));
        }
        catch (Exception exception) { item.ApplicationCompletion.TrySetException(exception); }
        continue;
      }
      if (item.Flush)
      {
        await FlushAsync(aggregates, applicationAggregates);
        item.Completion?.TrySetResult();
        lastFlush = Stopwatch.GetTimestamp();
        continue;
      }

      var action = item.Action;
      var nowUtc = DateTimeOffset.UtcNow;
      var bucket = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, TimeSpan.Zero);
      if (activeBucket is not null && activeBucket != bucket) await FlushAsync(aggregates, applicationAggregates);
      activeBucket = bucket;

      var key = new StatisticsAggregateKey(bucket, action.Input.Kind, action.Input.DeviceFamily, action.Input.Code, action.Input.Extended, action.Group);
      if (!aggregates.TryGetValue(key, out var aggregate)) aggregates[key] = aggregate = new MutableAggregate();
      aggregate.Count++;
      aggregate.ActiveMilliseconds += ActiveElapsed(lastAny, action.Timestamp);
      lastAny = action.Timestamp;

      if (action.Input.Kind == InputKind.KeyboardKey)
      {
        aggregate.KeyboardActiveMilliseconds += ActiveElapsed(lastKeyboard, action.Timestamp);
        lastKeyboard = action.Timestamp;
        if (IsTypingGroup(action.Group))
        {
          AddRolling(typingWindow, action.Timestamp, 60L * Stopwatch.Frequency);
          aggregate.PeakTypingKeysPerMinute = Math.Max(aggregate.PeakTypingKeysPerMinute, typingWindow.Count);
        }
      }
      else
      {
        aggregate.PointerActiveMilliseconds += ActiveElapsed(lastPointer, action.Timestamp);
        lastPointer = action.Timestamp;
        if (action.Input.Kind == InputKind.PointerButton)
        {
          AddRolling(clickingWindow, action.Timestamp, 5L * Stopwatch.Frequency);
          aggregate.PeakClicksPerFiveSeconds = Math.Max(aggregate.PeakClicksPerFiveSeconds, clickingWindow.Count);
        }
      }

      if (!string.IsNullOrWhiteSpace(action.ForegroundExecutable))
      {
        var executable = action.ForegroundExecutable;
        if (!applicationIdentities.TryGetValue(executable, out var identity))
        {
          sourceId ??= await _store.GetStatisticsSourceIdAsync();
          identity = CreateApplicationIdentity(sourceId, executable);
          if (applicationIdentities.Count < 1024) applicationIdentities[executable] = identity;
        }
        var applicationKey = new ApplicationStatisticsAggregateKey(bucket, identity.Id, identity.Name);
        if (!applicationAggregates.TryGetValue(applicationKey, out var applicationAggregate))
          applicationAggregates[applicationKey] = applicationAggregate = new MutableApplicationAggregate();
        if (action.Input.Kind == InputKind.KeyboardKey) applicationAggregate.KeyboardPresses++;
        else if (action.Input.Kind == InputKind.PointerButton) applicationAggregate.PointerClicks++;
        else if (action.Input.Code is 6 or 7) applicationAggregate.VerticalScroll++;
        else applicationAggregate.HorizontalScroll++;
      }

      if (Stopwatch.GetTimestamp() - lastFlush >= 60L * Stopwatch.Frequency)
      {
        await FlushAsync(aggregates, applicationAggregates);
        lastFlush = Stopwatch.GetTimestamp();
      }
    }
    await FlushAsync(aggregates, applicationAggregates);
  }

  private async Task FlushAsync(
    Dictionary<StatisticsAggregateKey, MutableAggregate> aggregates,
    Dictionary<ApplicationStatisticsAggregateKey, MutableApplicationAggregate> applicationAggregates)
  {
    var revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var deltas = aggregates.Select(item => new StatisticsAggregateDelta(
      item.Key,
      item.Value.Count,
      item.Value.ActiveMilliseconds,
      item.Value.KeyboardActiveMilliseconds,
      item.Value.PointerActiveMilliseconds,
      item.Value.PeakTypingKeysPerMinute,
      item.Value.PeakClicksPerFiveSeconds,
      revision)).ToArray();
    var applicationDeltas = applicationAggregates.Select(item => new ApplicationStatisticsAggregateDelta(
      item.Key,
      item.Value.KeyboardPresses,
      item.Value.PointerClicks,
      item.Value.VerticalScroll,
      item.Value.HorizontalScroll,
      revision)).ToArray();
    aggregates.Clear();
    applicationAggregates.Clear();
    try { await _store.MergeStatisticsAsync(deltas); }
    catch
    {
      foreach (var delta in deltas)
      {
        if (!aggregates.TryGetValue(delta.Key, out var aggregate)) aggregates[delta.Key] = aggregate = new MutableAggregate();
        aggregate.Count += delta.Count;
        aggregate.ActiveMilliseconds += delta.ActiveMilliseconds;
        aggregate.KeyboardActiveMilliseconds += delta.KeyboardActiveMilliseconds;
        aggregate.PointerActiveMilliseconds += delta.PointerActiveMilliseconds;
        aggregate.PeakTypingKeysPerMinute = Math.Max(aggregate.PeakTypingKeysPerMinute, delta.PeakTypingKeysPerMinute);
        aggregate.PeakClicksPerFiveSeconds = Math.Max(aggregate.PeakClicksPerFiveSeconds, delta.PeakClicksPerFiveSeconds);
      }
    }
    try { await _store.MergeApplicationStatisticsAsync(applicationDeltas); }
    catch
    {
      foreach (var delta in applicationDeltas)
      {
        if (!applicationAggregates.TryGetValue(delta.Key, out var aggregate))
          applicationAggregates[delta.Key] = aggregate = new MutableApplicationAggregate();
        aggregate.KeyboardPresses += delta.KeyboardPresses;
        aggregate.PointerClicks += delta.PointerClicks;
        aggregate.VerticalScroll += delta.VerticalScroll;
        aggregate.HorizontalScroll += delta.HorizontalScroll;
      }
    }
  }

  private static long ActiveElapsed(long? previous, long current)
  {
    if (previous is null) return 0;
    var elapsed = current - previous.Value;
    return elapsed > 0 && elapsed <= SessionGapTicks ? elapsed * 1000 / Stopwatch.Frequency : 0;
  }

  private static void AddRolling(Queue<long> window, long timestamp, long duration)
  {
    window.Enqueue(timestamp);
    while (window.TryPeek(out var oldest) && timestamp - oldest > duration) window.Dequeue();
  }

  private static bool IsTypingGroup(InputGroup group) => group is InputGroup.Letters or InputGroup.Numbers or InputGroup.Punctuation or InputGroup.Space or InputGroup.Enter or InputGroup.Editing;

  private static (string Id, string Name) CreateApplicationIdentity(string sourceId, string executable)
  {
    string normalized;
    try { normalized = Path.GetFullPath(executable).Trim().ToUpperInvariant(); }
    catch { normalized = executable.Trim().ToUpperInvariant(); }
    var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId}\n{normalized}")));
    string name;
    try { name = Path.GetFileNameWithoutExtension(executable); }
    catch { name = string.Empty; }
    if (string.IsNullOrWhiteSpace(name)) name = "Unknown application";
    return (id, name.Length <= 128 ? name : name[..128]);
  }

  private static PendingStatistics CreatePendingSnapshot(
    IReadOnlyDictionary<StatisticsAggregateKey, MutableAggregate> aggregates,
    IReadOnlyDictionary<ApplicationStatisticsAggregateKey, MutableApplicationAggregate> applications) => new(
      aggregates.Select(item => new StatisticsAggregateDelta(
        item.Key,
        item.Value.Count,
        item.Value.ActiveMilliseconds,
        item.Value.KeyboardActiveMilliseconds,
        item.Value.PointerActiveMilliseconds,
        item.Value.PeakTypingKeysPerMinute,
        item.Value.PeakClicksPerFiveSeconds,
        0)).ToArray(),
      applications.Select(item => new ApplicationStatisticsAggregateDelta(
        item.Key,
        item.Value.KeyboardPresses,
        item.Value.PointerClicks,
        item.Value.VerticalScroll,
        item.Value.HorizontalScroll,
        0)).ToArray());

  private static StatisticsSnapshot MergePending(StatisticsSnapshot snapshot, IReadOnlyList<StatisticsAggregateDelta> pending)
  {
    var matching = pending.Where(delta => Overlaps(delta.Key.BucketUtc, snapshot.Query)).ToArray();
    var comparison = snapshot.Comparison is null ? null : MergePending(snapshot.Comparison, pending);
    if (matching.Length == 0) return snapshot with { Comparison = comparison };

    var keyboard = snapshot.KeyboardPresses;
    var typing = snapshot.TypingKeyPresses;
    var pointer = snapshot.PointerClicks;
    var vertical = snapshot.VerticalScroll;
    var horizontal = snapshot.HorizontalScroll;
    var active = snapshot.ActiveMilliseconds;
    var keyboardActive = snapshot.KeyboardActiveMilliseconds;
    var pointerActive = snapshot.PointerActiveMilliseconds;
    var peakTyping = snapshot.PeakTypingKeysPerMinute;
    var peakClicks = snapshot.PeakClicksPerFiveSeconds;
    var trends = snapshot.Trend.ToDictionary(point => point.BucketUtc);
    var breakdown = snapshot.Breakdown.ToDictionary(
      item => (item.Kind, item.DeviceFamily, item.PhysicalCode, item.Extended, item.Group),
      item => item.Count);

    foreach (var delta in matching)
    {
      var isKeyboard = delta.Key.Kind == InputKind.KeyboardKey;
      var isPointer = delta.Key.Kind == InputKind.PointerButton;
      var isVertical = delta.Key.Kind == InputKind.Wheel && delta.Key.PhysicalCode is 6 or 7;
      var isHorizontal = delta.Key.Kind == InputKind.Wheel && delta.Key.PhysicalCode is 8 or 9;
      if (isKeyboard)
      {
        keyboard += delta.Count;
        if (IsTypingGroup(delta.Key.Group)) typing += delta.Count;
      }
      if (isPointer) pointer += delta.Count;
      if (isVertical) vertical += delta.Count;
      if (isHorizontal) horizontal += delta.Count;
      active += delta.ActiveMilliseconds;
      keyboardActive += delta.KeyboardActiveMilliseconds;
      pointerActive += delta.PointerActiveMilliseconds;
      peakTyping = Math.Max(peakTyping, delta.PeakTypingKeysPerMinute);
      peakClicks = Math.Max(peakClicks, delta.PeakClicksPerFiveSeconds);

      if (!trends.TryGetValue(delta.Key.BucketUtc, out var trend))
        trend = new(delta.Key.BucketUtc, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
      trends[delta.Key.BucketUtc] = trend with
      {
        KeyboardPresses = trend.KeyboardPresses + (isKeyboard ? delta.Count : 0),
        TypingKeyPresses = trend.TypingKeyPresses + (isKeyboard && IsTypingGroup(delta.Key.Group) ? delta.Count : 0),
        PointerClicks = trend.PointerClicks + (isPointer ? delta.Count : 0),
        VerticalScroll = trend.VerticalScroll + (isVertical ? delta.Count : 0),
        HorizontalScroll = trend.HorizontalScroll + (isHorizontal ? delta.Count : 0),
        ActiveMilliseconds = trend.ActiveMilliseconds + delta.ActiveMilliseconds,
        KeyboardActiveMilliseconds = trend.KeyboardActiveMilliseconds + delta.KeyboardActiveMilliseconds,
        PointerActiveMilliseconds = trend.PointerActiveMilliseconds + delta.PointerActiveMilliseconds,
        PeakTypingKeysPerMinute = Math.Max(trend.PeakTypingKeysPerMinute, delta.PeakTypingKeysPerMinute),
        PeakClicksPerFiveSeconds = Math.Max(trend.PeakClicksPerFiveSeconds, delta.PeakClicksPerFiveSeconds)
      };

      var breakdownKey = (delta.Key.Kind, delta.Key.DeviceFamily, delta.Key.PhysicalCode, delta.Key.Extended, delta.Key.Group);
      breakdown[breakdownKey] = breakdown.GetValueOrDefault(breakdownKey) + delta.Count;
    }

    var orderedTrends = trends.Values.OrderBy(point => point.BucketUtc).ToArray();
    var busiest = orderedTrends
      .OrderByDescending(point => point.KeyboardPresses + point.PointerClicks)
      .FirstOrDefault()?.BucketUtc.ToLocalTime().Hour ?? snapshot.BusiestHour;
    var orderedBreakdown = breakdown
      .Select(item => new StatisticsBreakdown(item.Key.Kind, item.Key.DeviceFamily, item.Key.PhysicalCode, item.Key.Extended, item.Key.Group, item.Value))
      .OrderByDescending(item => item.Count)
      .ToArray();

    return snapshot with
    {
      KeyboardPresses = keyboard,
      TypingKeyPresses = typing,
      PointerClicks = pointer,
      VerticalScroll = vertical,
      HorizontalScroll = horizontal,
      ActiveMilliseconds = active,
      KeyboardActiveMilliseconds = keyboardActive,
      PointerActiveMilliseconds = pointerActive,
      PeakTypingKeysPerMinute = peakTyping,
      PeakClicksPerFiveSeconds = peakClicks,
      BusiestHour = busiest,
      Trend = orderedTrends,
      Breakdown = orderedBreakdown,
      Comparison = comparison
    };
  }

  private static IReadOnlyList<ApplicationStatisticsRow> MergePendingApplications(
    IReadOnlyList<ApplicationStatisticsRow> stored,
    IReadOnlyList<ApplicationStatisticsAggregateDelta> pending,
    StatisticsQuery query)
  {
    var rows = stored.ToDictionary(row => row.ApplicationId, StringComparer.Ordinal);
    foreach (var delta in pending.Where(item => Overlaps(item.Key.BucketUtc, query)))
    {
      rows.TryGetValue(delta.Key.ApplicationId, out var row);
      rows[delta.Key.ApplicationId] = new(
        delta.Key.ApplicationId,
        delta.Key.DisplayName,
        (row?.KeyboardPresses ?? 0) + delta.KeyboardPresses,
        (row?.PointerClicks ?? 0) + delta.PointerClicks,
        (row?.VerticalScroll ?? 0) + delta.VerticalScroll,
        (row?.HorizontalScroll ?? 0) + delta.HorizontalScroll);
    }
    return rows.Values
      .OrderByDescending(row => row.KeyboardPresses + row.PointerClicks + row.VerticalScroll + row.HorizontalScroll)
      .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
      .ToArray();
  }

  private static bool Overlaps(DateTimeOffset bucketUtc, StatisticsQuery query) =>
    bucketUtc < query.EndUtc && bucketUtc.AddHours(1) > query.StartUtc;

  private async Task RequestFlushAsync(CancellationToken cancellationToken)
  {
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await _queue.Writer.WriteAsync(new QueueItem(default, true, completion, null, null, null), cancellationToken);
    await completion.Task.WaitAsync(cancellationToken);
  }

  private readonly record struct QueueItem(
    InputActionEvent Action,
    bool Flush,
    TaskCompletionSource? Completion,
    StatisticsQuery? Query,
    TaskCompletionSource<StatisticsSnapshot>? StatisticsCompletion,
    TaskCompletionSource<IReadOnlyList<ApplicationStatisticsRow>>? ApplicationCompletion)
  {
    public static QueueItem FlushRequest => new(default, true, null, null, null, null);
    public static QueueItem StatisticsQueryRequest(StatisticsQuery query, TaskCompletionSource<StatisticsSnapshot> completion) =>
      new(default, false, null, query, completion, null);
    public static QueueItem ApplicationQueryRequest(StatisticsQuery query, TaskCompletionSource<IReadOnlyList<ApplicationStatisticsRow>> completion) =>
      new(default, false, null, query, null, completion);
  }

  private sealed record PendingStatistics(
    IReadOnlyList<StatisticsAggregateDelta> Inputs,
    IReadOnlyList<ApplicationStatisticsAggregateDelta> Applications);

  private sealed class MutableAggregate
  {
    public long Count;
    public long ActiveMilliseconds;
    public long KeyboardActiveMilliseconds;
    public long PointerActiveMilliseconds;
    public int PeakTypingKeysPerMinute;
    public int PeakClicksPerFiveSeconds;
  }

  private sealed class MutableApplicationAggregate
  {
    public long KeyboardPresses;
    public long PointerClicks;
    public long VerticalScroll;
    public long HorizontalScroll;
  }
}
