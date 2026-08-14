using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;

namespace KeyClick.App;

public sealed record StatisticRow(string Label, long Count, string Detail = "");

public sealed class StatisticsViewModel : INotifyPropertyChanged, IDisposable
{
  private static readonly StatisticsPeriod[] PeriodValues =
  [
    StatisticsPeriod.Today,
    StatisticsPeriod.LastThirtyMinutes,
    StatisticsPeriod.LastHour,
    StatisticsPeriod.LastFiveHours,
    StatisticsPeriod.SevenDays,
    StatisticsPeriod.ThirtyDays,
    StatisticsPeriod.ThisMonth,
    StatisticsPeriod.ThisYear,
    StatisticsPeriod.AllTime,
    StatisticsPeriod.Custom
  ];
  private static readonly StatisticsPeriod[] HeatmapPeriodValues = PeriodValues.Where(value => value != StatisticsPeriod.Custom).ToArray();
  private readonly StatisticsService _service;
  private readonly LocalizationService _localization;
  private CancellationTokenSource? _visibleRefresh;
  private StatisticsSnapshot? _snapshot;
  private StatisticsSnapshot? _heatmapSnapshot;
  private int _periodIndex;
  private int _heatmapPeriodIndex;
  private int _comparisonIndex;
  private int _pointerDeviceFilterIndex;
  private DateTime? _customStart = DateTime.Today.AddDays(-6);
  private DateTime? _customEnd = DateTime.Today;
  private bool _loading;
  private bool _heatmapLoading;
  private bool _heatmapRefreshPending;
  private bool _heatmapVisible;
  private bool _applicationsVisible;
  private bool _heatmapTooltipsEnabled = true;

  public StatisticsViewModel(StatisticsService service, LocalizationService localization)
  {
    _service = service;
    _localization = localization;
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  public IReadOnlyList<string> PeriodOptions =>
  [
    _localization.Get("PeriodToday"), _localization.Get("PeriodLastThirtyMinutes"), _localization.Get("PeriodLastHour"),
    _localization.Get("PeriodLastFiveHours"), _localization.Get("PeriodSevenDays"), _localization.Get("PeriodThirtyDays"),
    _localization.Get("PeriodThisMonth"), _localization.Get("PeriodThisYear"), _localization.Get("PeriodAllTime"), _localization.Get("PeriodCustom")
  ];
  public IReadOnlyList<string> ComparisonOptions =>
  [
    _localization.Get("ComparisonNone"), _localization.Get("ComparisonPrevious"), _localization.Get("ComparisonLastYear")
  ];
  public IReadOnlyList<string> HeatmapPeriodOptions =>
  [
    _localization.Get("PeriodToday"), _localization.Get("PeriodLastThirtyMinutes"), _localization.Get("PeriodLastHour"),
    _localization.Get("PeriodLastFiveHours"), _localization.Get("PeriodSevenDays"), _localization.Get("PeriodThirtyDays"),
    _localization.Get("PeriodThisMonth"), _localization.Get("PeriodThisYear"), _localization.Get("PeriodAllTime")
  ];
  public IReadOnlyList<string> PointerDeviceFilterOptions =>
  [
    _localization.Get("AllPointerDevices"), _localization.Get("DeviceExternalMouse"),
    _localization.Get("DeviceTrackpad"), _localization.Get("DeviceUnknownPointer")
  ];
  public ObservableCollection<StatisticRow> PointerRows { get; } = [];
  public ObservableCollection<StatisticRow> KeyboardRows { get; } = [];
  public ObservableCollection<ApplicationStatisticsRow> ApplicationRows { get; } = [];
  public StatisticsSnapshot? Snapshot { get => _snapshot; private set { _snapshot = value; Notify(); NotifyMetrics(); } }
  public StatisticsSnapshot? HeatmapSnapshot { get => _heatmapSnapshot; private set { _heatmapSnapshot = value; Notify(); } }
  public bool IsLoading { get => _loading; private set { _loading = value; Notify(); } }
  public string QueueDiagnostics => _localization.Format("StatisticsQueueDiagnosticsFormat", _service.OverflowCount);

  public int PeriodIndex
  {
    get => _periodIndex;
    set
    {
      if (_periodIndex == value) return;
      _periodIndex = Math.Clamp(value, 0, PeriodValues.Length - 1);
      if (SelectedPeriod == StatisticsPeriod.AllTime) _comparisonIndex = 0;
      Notify();
      Notify(nameof(CustomDatesVisible), nameof(ComparisonEnabled), nameof(ComparisonIndex));
      _ = RefreshAsync();
    }
  }

  public int ComparisonIndex
  {
    get => _comparisonIndex;
    set
    {
      var next = ComparisonEnabled ? Math.Clamp(value, 0, 2) : 0;
      if (_comparisonIndex == next) return;
      _comparisonIndex = next;
      Notify();
      _ = RefreshAsync();
    }
  }

  public bool ComparisonEnabled => SelectedPeriod != StatisticsPeriod.AllTime;
  public int HeatmapPeriodIndex
  {
    get => _heatmapPeriodIndex;
    set
    {
      var next = Math.Clamp(value, 0, HeatmapPeriodValues.Length - 1);
      if (_heatmapPeriodIndex == next) return;
      _heatmapPeriodIndex = next;
      Notify();
      if (_heatmapVisible) _ = RefreshHeatmapAsync();
    }
  }
  public bool HeatmapTooltipsEnabled
  {
    get => _heatmapTooltipsEnabled;
    set { if (_heatmapTooltipsEnabled == value) return; _heatmapTooltipsEnabled = value; Notify(); }
  }
  public int PointerDeviceFilterIndex
  {
    get => _pointerDeviceFilterIndex;
    set
    {
      var next = Math.Clamp(value, 0, 3);
      if (_pointerDeviceFilterIndex == next) return;
      _pointerDeviceFilterIndex = next;
      Notify();
      RebuildPointerRows();
    }
  }
  public bool CustomDatesVisible => SelectedPeriod == StatisticsPeriod.Custom;
  public DateTime? CustomStart { get => _customStart; set { _customStart = value; Notify(); _ = RefreshAsync(); } }
  public DateTime? CustomEnd { get => _customEnd; set { _customEnd = value; Notify(); _ = RefreshAsync(); } }

  public string KeyboardCount => FormatCount(Snapshot?.KeyboardPresses ?? 0);
  public string PointerCount => FormatCount(Snapshot?.PointerClicks ?? 0);
  public string ActiveTime => FormatDuration(Snapshot?.ActiveMilliseconds ?? 0);
  public string AverageTypingSpeed => _localization.Format("WpmFormat", Snapshot?.AverageWordsPerMinute ?? 0);
  public string PeakTypingSpeed => _localization.Format("WpmFormat", Snapshot?.PeakWordsPerMinute ?? 0);
  public string AverageClickingSpeed => _localization.Format("CpsFormat", Snapshot?.AverageClicksPerActiveSecond ?? 0);
  public string PeakClickingSpeed => _localization.Format("CpsFormat", (Snapshot?.PeakClicksPerFiveSeconds ?? 0) / 5d);
  public string ScrollingCount => FormatCount((Snapshot?.VerticalScroll ?? 0) + (Snapshot?.HorizontalScroll ?? 0));
  public string BusiestHour => Snapshot is null || Snapshot.KeyboardPresses + Snapshot.PointerClicks + Snapshot.VerticalScroll + Snapshot.HorizontalScroll == 0
    ? "—"
    : DateTime.Today.AddHours(Snapshot.BusiestHour).ToString("h tt", CultureInfo.CurrentUICulture);
  public string KeyboardComparison => Compare(Snapshot?.KeyboardPresses ?? 0, Snapshot?.Comparison?.KeyboardPresses);
  public string PointerComparison => Compare(Snapshot?.PointerClicks ?? 0, Snapshot?.Comparison?.PointerClicks);

  public void SetVisible(bool visible)
  {
    _visibleRefresh?.Cancel();
    _visibleRefresh?.Dispose();
    _visibleRefresh = null;
    if (!visible) return;
    _visibleRefresh = new CancellationTokenSource();
    _ = RefreshWhileVisibleAsync(_visibleRefresh.Token);
  }

  public void SetHeatmapVisible(bool visible)
  {
    if (_heatmapVisible == visible) return;
    _heatmapVisible = visible;
    if (visible) _ = RefreshHeatmapAsync();
  }

  public void SetApplicationsVisible(bool visible)
  {
    if (_applicationsVisible == visible) return;
    _applicationsVisible = visible;
    if (visible) _ = RefreshApplicationsAsync();
  }

  public async Task RefreshAsync(CancellationToken cancellationToken = default)
  {
    if (IsLoading) return;
    IsLoading = true;
    try
    {
      var query = CreateQuery();
      Snapshot = await _service.QueryAsync(query, cancellationToken);
      RebuildBreakdowns();
      if (_heatmapVisible)
      {
        var heatmapQuery = CreateHeatmapQuery();
        if (heatmapQuery.StartUtc == query.StartUtc && heatmapQuery.EndUtc == query.EndUtc)
          HeatmapSnapshot = Snapshot;
        else
          await RefreshHeatmapAsync(cancellationToken);
      }
      if (_applicationsVisible) await RefreshApplicationsAsync(query, cancellationToken);
      Notify(nameof(QueueDiagnostics));
    }
    catch (OperationCanceledException) { }
    finally { IsLoading = false; }
  }

  public Task ExportAsync(string path, CancellationToken cancellationToken = default) => Snapshot is null
    ? Task.CompletedTask
    : _service.ExportCsvAsync(Snapshot, path, cancellationToken);

  public Task DeleteAsync(StatisticsDeleteRequest request, CancellationToken cancellationToken = default) => _service.DeleteAsync(request, cancellationToken);

  public async Task RefreshHeatmapAsync(CancellationToken cancellationToken = default)
  {
    if (_heatmapLoading)
    {
      _heatmapRefreshPending = true;
      return;
    }
    _heatmapLoading = true;
    try
    {
      do
      {
        _heatmapRefreshPending = false;
        HeatmapSnapshot = await _service.QueryAsync(CreateHeatmapQuery(), cancellationToken);
      }
      while (_heatmapVisible && _heatmapRefreshPending && !cancellationToken.IsCancellationRequested);
    }
    catch (OperationCanceledException) { }
    finally { _heatmapLoading = false; }
  }

  public Task RefreshApplicationsAsync(CancellationToken cancellationToken = default) =>
    RefreshApplicationsAsync(CreateQuery(), cancellationToken);

  public void RefreshLocalization()
  {
    Notify(nameof(PeriodOptions), nameof(ComparisonOptions), nameof(HeatmapPeriodOptions), nameof(PointerDeviceFilterOptions), nameof(QueueDiagnostics));
    RebuildBreakdowns();
    NotifyMetrics();
  }

  public void Dispose()
  {
    _visibleRefresh?.Cancel();
    _visibleRefresh?.Dispose();
  }

  private async Task RefreshWhileVisibleAsync(CancellationToken cancellationToken)
  {
    try
    {
      while (!cancellationToken.IsCancellationRequested)
      {
        await RefreshAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
      }
    }
    catch (OperationCanceledException) { }
  }

  private StatisticsQuery CreateQuery()
  {
    var (start, end) = CreateRange(SelectedPeriod);
    var comparison = ComparisonEnabled ? (StatisticsComparison)ComparisonIndex : StatisticsComparison.None;
    return new(ToUtc(start), ToUtc(end), comparison);
  }

  private async Task RefreshApplicationsAsync(StatisticsQuery query, CancellationToken cancellationToken)
  {
    try
    {
      var rows = await _service.QueryApplicationAsync(query with { Comparison = StatisticsComparison.None }, cancellationToken);
      ApplicationRows.Clear();
      foreach (var row in rows) ApplicationRows.Add(row);
    }
    catch (OperationCanceledException) { }
  }

  private StatisticsQuery CreateHeatmapQuery()
  {
    var (start, end) = CreateRange(HeatmapPeriodValues[HeatmapPeriodIndex]);
    return new(ToUtc(start), ToUtc(end));
  }

  private (DateTime Start, DateTime End) CreateRange(StatisticsPeriod period)
  {
    var now = DateTime.Now;
    var today = now.Date;
    var (start, end) = period switch
    {
      StatisticsPeriod.LastThirtyMinutes => (now.AddMinutes(-30), now),
      StatisticsPeriod.LastHour => (now.AddHours(-1), now),
      StatisticsPeriod.LastFiveHours => (now.AddHours(-5), now),
      StatisticsPeriod.Today => (today, today.AddDays(1)),
      StatisticsPeriod.SevenDays => (today.AddDays(-6), today.AddDays(1)),
      StatisticsPeriod.ThirtyDays => (today.AddDays(-29), today.AddDays(1)),
      StatisticsPeriod.ThisMonth => (new DateTime(today.Year, today.Month, 1), today.AddDays(1)),
      StatisticsPeriod.ThisYear => (new DateTime(today.Year, 1, 1), today.AddDays(1)),
      StatisticsPeriod.AllTime => (DateTime.UnixEpoch, today.AddDays(1)),
      _ => ((_customStart ?? today).Date, (_customEnd ?? today).Date.AddDays(1))
    };
    if (end <= start) end = start.AddDays(1);
    return (start, end);
  }

  private StatisticsPeriod SelectedPeriod => PeriodValues[PeriodIndex];

  private void RebuildBreakdowns()
  {
    PointerRows.Clear();
    KeyboardRows.Clear();
    if (Snapshot is null) return;
    foreach (var item in Snapshot.Breakdown)
    {
      if (item.Kind == InputKind.KeyboardKey)
        KeyboardRows.Add(new(_localization.KeyNameFromScanCode(item.PhysicalCode, item.Extended), item.Count, _localization.EnumName(item.Group)));
    }
    RebuildPointerRows();
  }

  private void RebuildPointerRows()
  {
    PointerRows.Clear();
    if (Snapshot is null) return;
    DeviceFamily? filter = PointerDeviceFilterIndex switch
    {
      1 => DeviceFamily.ExternalMouse,
      2 => DeviceFamily.Trackpad,
      3 => DeviceFamily.UnknownPointer,
      _ => null
    };
    foreach (var item in Snapshot.Breakdown.Where(item => item.Kind != InputKind.KeyboardKey && (filter is null || item.DeviceFamily == filter)))
      PointerRows.Add(new(PointerLabel(item), item.Count, _localization.EnumName(item.DeviceFamily)));
  }

  private string PointerLabel(StatisticsBreakdown item) => item.Kind == InputKind.Wheel
    ? _localization.Get(item.PhysicalCode switch { 6 => "WheelUp", 7 => "WheelDown", 8 => "WheelLeft", _ => "WheelRight" })
    : _localization.Get(item.PhysicalCode switch { 1 => "PrimaryButton", 2 => "SecondaryButton", 3 => "MiddleButton", 4 => "X1Button", _ => "X2Button" });

  private string Compare(long current, long? previous)
  {
    if (previous is null) return string.Empty;
    if (previous == 0) return current == 0 ? _localization.Get("NoChange") : _localization.Get("NewActivity");
    return string.Format(CultureInfo.CurrentUICulture, "{0:+0.0;-0.0;0}%", (current - previous.Value) * 100d / previous.Value);
  }

  private static DateTimeOffset ToUtc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);
  private static string FormatCount(long value) => value.ToString("N0", CultureInfo.CurrentUICulture);
  private static string FormatDuration(long milliseconds)
  {
    var value = TimeSpan.FromMilliseconds(milliseconds);
    return value.TotalHours >= 1 ? $"{value.TotalHours:0.#} h" : $"{value.TotalMinutes:0.#} min";
  }

  private void NotifyMetrics() => Notify(
    nameof(KeyboardCount), nameof(PointerCount), nameof(ActiveTime), nameof(AverageTypingSpeed), nameof(PeakTypingSpeed),
    nameof(AverageClickingSpeed), nameof(PeakClickingSpeed), nameof(ScrollingCount), nameof(BusiestHour), nameof(KeyboardComparison), nameof(PointerComparison));

  private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
  private void Notify(params string[] properties) { foreach (var property in properties) Notify(property); }
}
