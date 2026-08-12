using System.Diagnostics;
using System.Threading.Channels;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class WellnessService : IAsyncDisposable
{
  private readonly IStatisticsStore _store;
  private readonly Channel<InputActionEvent> _queue = Channel.CreateBounded<InputActionEvent>(new BoundedChannelOptions(2048)
  {
    SingleReader = true,
    SingleWriter = true,
    FullMode = BoundedChannelFullMode.Wait,
    AllowSynchronousContinuations = false
  });
  private readonly Task _consumer;
  private readonly HashSet<string> _achievementIds = new(StringComparer.Ordinal);
  private readonly List<WellnessAchievement> _achievements = [];
  private WellnessPolicy _policy;
  private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
  private long _keyboard;
  private long _pointer;
  private long _activeMilliseconds;
  private long? _lastInput;
  private long _cycleActiveMilliseconds;
  private bool _reminderArmed = true;

  public WellnessService(IStatisticsStore store, AppSettings settings)
  {
    _store = store;
    _policy = WellnessPolicy.FromSettings(settings);
    _consumer = Task.Run(ConsumeAsync);
  }

  public event EventHandler<string>? NotificationRequested;
  public event EventHandler<WellnessSnapshot>? SnapshotChanged;

  public async Task InitializeAsync(CancellationToken cancellationToken = default)
  {
    var start = DateTime.Today;
    var end = start.AddDays(1);
    var snapshot = await _store.QueryStatisticsAsync(new(ToUtc(start), ToUtc(end)), cancellationToken);
    _keyboard = snapshot.KeyboardPresses;
    _pointer = snapshot.PointerClicks;
    _activeMilliseconds = snapshot.ActiveMilliseconds;
    _achievements.AddRange(await _store.LoadWellnessAchievementsAsync(cancellationToken));
    foreach (var achievement in _achievements) _achievementIds.Add(achievement.Id);
    PublishSnapshot();
  }

  public void UpdatePolicy(AppSettings settings) => Volatile.Write(ref _policy, WellnessPolicy.FromSettings(settings));

  public bool TryRecord(InputActionEvent action)
  {
    var policy = Volatile.Read(ref _policy);
    if (!policy.Enabled || !policy.DisclosureConfirmed) return false;
    var relevant = action.Input.Kind switch
    {
      InputKind.KeyboardKey => action.Phase == InputPhase.Down,
      InputKind.PointerButton => action.Phase == InputPhase.Up,
      InputKind.Wheel => action.Phase == InputPhase.WheelDetent,
      _ => false
    };
    return relevant && _queue.Writer.TryWrite(action);
  }

  public async ValueTask DisposeAsync()
  {
    _queue.Writer.TryComplete();
    await _consumer;
  }

  private async Task ConsumeAsync()
  {
    await foreach (var action in _queue.Reader.ReadAllAsync())
    {
      var policy = Volatile.Read(ref _policy);
      var today = DateOnly.FromDateTime(DateTime.Today);
      if (today != _date)
      {
        _date = today;
        _keyboard = 0;
        _pointer = 0;
        _activeMilliseconds = 0;
      }

      if (_lastInput is { } previous)
      {
        var elapsedTicks = action.Timestamp - previous;
        var elapsedMilliseconds = elapsedTicks > 0 ? elapsedTicks * 1000 / Stopwatch.Frequency : 0;
        if (elapsedMilliseconds >= policy.RestMinutes * 60000L)
        {
          _cycleActiveMilliseconds = 0;
          _reminderArmed = true;
        }
        if (elapsedMilliseconds is > 0 and <= 60000)
        {
          _activeMilliseconds += elapsedMilliseconds;
          _cycleActiveMilliseconds += elapsedMilliseconds;
        }
      }
      _lastInput = action.Timestamp;
      if (action.Input.Kind == InputKind.KeyboardKey) _keyboard++;
      else if (action.Input.Kind == InputKind.PointerButton) _pointer++;

      if (policy.BreakReminderEnabled && _reminderArmed && _cycleActiveMilliseconds >= policy.ActiveMinutes * 60000L)
      {
        _reminderArmed = false;
        NotificationRequested?.Invoke(this, "break");
      }
      await CheckGoalAsync("keyboard", policy.KeyboardGoalEnabled, policy.KeyboardTarget, _keyboard);
      await CheckGoalAsync("pointer", policy.PointerGoalEnabled, policy.PointerTarget, _pointer);
      await CheckGoalAsync("active", policy.ActiveGoalEnabled, policy.ActiveTarget, _activeMilliseconds / 60000);
      PublishSnapshot();
    }
  }

  private async Task CheckGoalAsync(string kind, bool enabled, long target, long actual)
  {
    if (!enabled || actual < target) return;
    var id = $"{kind}:{_date:yyyy-MM-dd}";
    if (!_achievementIds.Add(id)) return;
    var achievement = new WellnessAchievement(id, kind, _date, target, actual, DateTimeOffset.UtcNow);
    _achievements.Add(achievement);
    await _store.SaveWellnessAchievementAsync(achievement);
    NotificationRequested?.Invoke(this, $"goal:{kind}");
  }

  private void PublishSnapshot()
  {
    var keyboard = Streak("keyboard");
    var pointer = Streak("pointer");
    var active = Streak("active");
    SnapshotChanged?.Invoke(this, new(_keyboard, _pointer, _activeMilliseconds / 60000,
      keyboard.Current, keyboard.Longest, pointer.Current, pointer.Longest, active.Current, active.Longest));
  }

  private (int Current, int Longest) Streak(string kind)
  {
    var days = _achievements.Where(item => item.GoalKind == kind).Select(item => item.LocalDate).Distinct().Order().ToArray();
    if (days.Length == 0) return (0, 0);
    var longest = 1;
    var run = 1;
    for (var index = 1; index < days.Length; index++)
    {
      run = days[index].DayNumber == days[index - 1].DayNumber + 1 ? run + 1 : 1;
      longest = Math.Max(longest, run);
    }
    var current = 0;
    var cursor = _date;
    var set = days.ToHashSet();
    while (set.Contains(cursor)) { current++; cursor = cursor.AddDays(-1); }
    return (current, longest);
  }

  private static DateTimeOffset ToUtc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);

  private sealed record WellnessPolicy(
    bool Enabled,
    bool DisclosureConfirmed,
    bool BreakReminderEnabled,
    int ActiveMinutes,
    int RestMinutes,
    bool KeyboardGoalEnabled,
    int KeyboardTarget,
    bool PointerGoalEnabled,
    int PointerTarget,
    bool ActiveGoalEnabled,
    int ActiveTarget)
  {
    public static WellnessPolicy FromSettings(AppSettings settings) => new(
      settings.WellnessEnabled, settings.StatisticsDisclosureConfirmed, settings.BreakReminderEnabled,
      Math.Clamp(settings.BreakReminderActiveMinutes, 1, 1440), Math.Clamp(settings.BreakReminderRestMinutes, 1, 1440),
      settings.KeyboardGoalEnabled, Math.Max(1, settings.KeyboardDailyGoal), settings.PointerGoalEnabled,
      Math.Max(1, settings.PointerDailyGoal), settings.ActiveMinutesGoalEnabled, Math.Max(1, settings.ActiveMinutesDailyGoal));
  }
}
