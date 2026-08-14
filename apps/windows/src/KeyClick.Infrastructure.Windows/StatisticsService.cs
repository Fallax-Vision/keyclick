using System.Diagnostics;
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
    settings.StatisticsDisclosureConfirmed,
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

  public void UpdatePolicy(AppSettings settings)
  {
    Volatile.Write(ref _policy, StatisticsCapturePolicy.FromSettings(settings));
    TryQueue(QueueItem.FlushRequest);
  }

  public bool TryRecord(InputActionEvent action)
  {
    var policy = Volatile.Read(ref _policy);
    if (!policy.DisclosureConfirmed) return false;
    if (action.ForegroundExecutable is not null && policy.ExcludedExecutables.Contains(action.ForegroundExecutable)) return false;
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
    return TryQueue(new QueueItem(action, false, null));
  }

  public async Task<StatisticsSnapshot> QueryAsync(StatisticsQuery query, CancellationToken cancellationToken = default)
  {
    await RequestFlushAsync(cancellationToken);
    return await _store.QueryStatisticsAsync(query, cancellationToken);
  }

  public Task DeleteAsync(StatisticsDeleteRequest request, CancellationToken cancellationToken = default) =>
    _store.DeleteStatisticsAsync(request, cancellationToken);

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
    var typingWindow = new Queue<long>();
    var clickingWindow = new Queue<long>();
    DateTimeOffset? activeBucket = null;
    long? lastAny = null, lastKeyboard = null, lastPointer = null;
    var lastFlush = Stopwatch.GetTimestamp();
    await foreach (var item in _queue.Reader.ReadAllAsync())
    {
      if (item.Flush)
      {
        await FlushAsync(aggregates);
        item.Completion?.TrySetResult();
        lastFlush = Stopwatch.GetTimestamp();
        continue;
      }

      var action = item.Action;
      var nowUtc = DateTimeOffset.UtcNow;
      var bucket = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, TimeSpan.Zero);
      if (activeBucket is not null && activeBucket != bucket) await FlushAsync(aggregates);
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

      if (Stopwatch.GetTimestamp() - lastFlush >= 60L * Stopwatch.Frequency)
      {
        await FlushAsync(aggregates);
        lastFlush = Stopwatch.GetTimestamp();
      }
    }
    await FlushAsync(aggregates);
  }

  private async Task FlushAsync(Dictionary<StatisticsAggregateKey, MutableAggregate> aggregates)
  {
    if (aggregates.Count == 0) return;
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
    aggregates.Clear();
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

  private async Task RequestFlushAsync(CancellationToken cancellationToken)
  {
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    if (!TryQueue(new QueueItem(default, true, completion))) return;
    await completion.Task.WaitAsync(cancellationToken);
  }

  private readonly record struct QueueItem(InputActionEvent Action, bool Flush, TaskCompletionSource? Completion)
  {
    public static QueueItem FlushRequest => new(default, true, null);
  }

  private sealed class MutableAggregate
  {
    public long Count;
    public long ActiveMilliseconds;
    public long KeyboardActiveMilliseconds;
    public long PointerActiveMilliseconds;
    public int PeakTypingKeysPerMinute;
    public int PeakClicksPerFiveSeconds;
  }
}
