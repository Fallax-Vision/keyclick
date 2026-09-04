using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;

namespace KeyClick.App;

public sealed record StatisticRow(string Label, long Count, string Detail = "");

public sealed record KeyboardStatisticRow(string Label, long Count, string Detail, long MaximumCount)
{
  public long ProgressMaximum => Math.Max(1, MaximumCount);
  public double ProgressOpacity => 0.12 + (0.28 * Math.Clamp((double)Count / ProgressMaximum, 0, 1));
}

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
  private static readonly StatisticsPeriod[] PresetPeriodValues = PeriodValues.Where(value => value != StatisticsPeriod.Custom).ToArray();
  private static readonly (string Id, StatisticsChartMetricFamily Family, string ResourceKey)[] ChartSeriesDefinitions =
  [
    ("keyboard", StatisticsChartMetricFamily.Counts, "Keyboard"),
    ("pointer", StatisticsChartMetricFamily.Counts, "Pointer"),
    ("vertical-scroll", StatisticsChartMetricFamily.Counts, "ChartVerticalScroll"),
    ("horizontal-scroll", StatisticsChartMetricFamily.Counts, "ChartHorizontalScroll"),
    ("average-wpm", StatisticsChartMetricFamily.Rates, "ChartAverageWpm"),
    ("peak-wpm", StatisticsChartMetricFamily.Rates, "ChartPeakWpm"),
    ("average-cps", StatisticsChartMetricFamily.Rates, "ChartAverageCps"),
    ("peak-cps", StatisticsChartMetricFamily.Rates, "ChartPeakCps"),
    ("active", StatisticsChartMetricFamily.ActiveTime, "ChartActiveTime"),
    ("keyboard-active", StatisticsChartMetricFamily.ActiveTime, "ChartKeyboardActive"),
    ("pointer-active", StatisticsChartMetricFamily.ActiveTime, "ChartPointerActive")
  ];

  private readonly StatisticsService _service;
  private readonly LocalizationService _localization;
  private AppSettings _settings;
  private readonly Func<Task> _saveSettings;
  private FunStatsEngine _funStats;
  private CancellationTokenSource? _visibleRefresh;
  private StatisticsSnapshot? _snapshot;
  private StatisticsSnapshot? _homeSnapshot;
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
  private bool _homeVisible;
  private bool _statisticsVisible;
  private int _dirtyRefreshScheduled;
  private long _dirtyRefreshRevision;
  private long _lastDirtyRefreshRevision;
  private bool _applicationsVisible;
  private bool _heatmapTooltipsEnabled = true;
  private bool _funStatsPresented;
  private bool _homeFunStatsPresented;
  private StatisticsChartModel? _chartModel;

  public StatisticsViewModel(StatisticsService service, AppSettings settings, LocalizationService localization, Func<Task> saveSettings)
  {
    _service = service;
    _settings = settings;
    _localization = localization;
    _saveSettings = saveSettings;
    _settings.NormalizeFunStats();
    _funStats = new(settings, localization);
    RebuildFunStatOptions();
    RebuildFunStatCategoryOptions();
    RebuildChartSeriesOptions();
    RebuildChartOptions();
    _service.DataChanged += StatisticsDataChanged;
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  public IReadOnlyList<string> PeriodOptions => PeriodLabels(true);
  public IReadOnlyList<string> HomeFunStatsPeriodOptions => PeriodLabels(false);
  public IReadOnlyList<string> ComparisonOptions =>
  [
    _localization.Get("ComparisonNone"), _localization.Get("ComparisonPrevious"), _localization.Get("ComparisonLastYear")
  ];
  public IReadOnlyList<string> HeatmapPeriodOptions => PeriodLabels(false);
  public IReadOnlyList<string> PointerDeviceFilterOptions =>
  [
    _localization.Get("AllPointerDevices"), _localization.Get("DeviceExternalMouse"),
    _localization.Get("DeviceTrackpad"), _localization.Get("DeviceUnknownPointer")
  ];
  public IReadOnlyList<string> FunFactRotationOptions =>
  [
    _localization.Get("FunRotationTenMinutes"), _localization.Get("FunRotationOneHour"), _localization.Get("FunRotationDaily"),
    _localization.Get("FunRotationAppLaunch"), _localization.Get("FunRotationCardClick"), _localization.Get("FunRotationManual")
  ];
  public IReadOnlyList<string> FunStatsCopyModeOptions =>
  [
    _localization.Get("FunCopyImageOnly"), _localization.Get("FunCopyImageCaption"), _localization.Get("FunCopyWholeApp")
  ];
  public IReadOnlyList<string> ChartMetricFamilyOptions =>
  [
    _localization.Get("ChartFamilyCounts"), _localization.Get("ChartFamilyRates"), _localization.Get("ChartFamilyActiveTime")
  ];
  public ObservableCollection<LocalizedOption> ChartViewOptions { get; } = [];
  public ObservableCollection<LocalizedOption> ChartGranularityOptions { get; } = [];
  public IReadOnlyList<string> CustomMetricOptions => Enum.GetValues<FunStatMetric>().Select(MetricLabel).ToArray();

  public ObservableCollection<StatisticRow> PointerRows { get; } = [];
  public ObservableCollection<KeyboardStatisticRow> KeyboardRows { get; } = [];
  public ObservableCollection<ApplicationStatisticsRow> ApplicationRows { get; } = [];
  public ObservableCollection<FunStatTile> FunStatsTiles { get; } = [];
  public ObservableCollection<FunStatTile> HomeFunStatsTiles { get; } = [];
  public ObservableCollection<FunStatOption> FunStatOptions { get; } = [];
  public ObservableCollection<FunStatOption> FunStatCategoryOptions { get; } = [];
  public ObservableCollection<FunStatOption> ChartSeriesOptions { get; } = [];

  public StatisticsSnapshot? Snapshot
  {
    get => _snapshot;
    private set
    {
      _snapshot = value;
      Notify();
      RebuildPresentation();
    }
  }
  public StatisticsSnapshot? HomeSnapshot
  {
    get => _homeSnapshot;
    private set
    {
      _homeSnapshot = value;
      Notify();
      RebuildHomeFunStats();
    }
  }
  public StatisticsSnapshot? HeatmapSnapshot { get => _heatmapSnapshot; private set { _heatmapSnapshot = value; Notify(); } }
  public StatisticsChartModel? ChartModel
  {
    get => _chartModel;
    private set { _chartModel = value; Notify(); Notify(nameof(ChartAccessibleSummary)); }
  }
  public bool IsLoading { get => _loading; private set { _loading = value; Notify(); } }
  public string QueueDiagnostics => _localization.Format("StatisticsQueueDiagnosticsFormat", _service.OverflowCount);
  public bool FunStatsVisible => _settings.FunStatsEnabled;
  public bool ReducedMotion => _settings.ReducedMotion;
  public string ChartAccessibleSummary => ChartModel is null
    ? _localization.Get("ChartAccessibleEmpty")
    : _localization.Format("ChartAccessibleSummaryFormat", ChartMetricFamilyOptions[(int)ChartModel.Family], ChartModel.Points.Count,
      string.Join(", ", ChartModel.Series.Select(item => item.Label)));

  public int PeriodIndex
  {
    get => _periodIndex;
    set
    {
      var next = Math.Clamp(value, 0, PeriodValues.Length - 1);
      if (_periodIndex == next) return;
      _periodIndex = next;
      if (SelectedPeriod == StatisticsPeriod.AllTime) _comparisonIndex = 0;
      Notify();
      Notify(nameof(CustomDatesVisible), nameof(ComparisonEnabled), nameof(ComparisonIndex), nameof(CurrentPeriodLabel));
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
      var next = Math.Clamp(value, 0, PresetPeriodValues.Length - 1);
      if (_heatmapPeriodIndex == next) return;
      _heatmapPeriodIndex = next;
      Notify();
      if (_heatmapVisible) _ = RefreshHeatmapAsync();
    }
  }
  public int HomeFunStatsPeriodIndex
  {
    get
    {
      var index = Array.IndexOf(PresetPeriodValues, _settings.HomeFunStatsPeriod);
      return index < 0 ? Array.IndexOf(PresetPeriodValues, StatisticsPeriod.AllTime) : index;
    }
    set
    {
      var next = PresetPeriodValues[Math.Clamp(value, 0, PresetPeriodValues.Length - 1)];
      if (_settings.HomeFunStatsPeriod == next) return;
      _settings.HomeFunStatsPeriod = next;
      Notify();
      Notify(nameof(HomeFunStatsPeriodLabel));
      SavePreferences();
      if (_homeVisible) _ = RefreshHomeFunStatsAsync();
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

  public bool FunStatsEnabled
  {
    get => _settings.FunStatsEnabled;
    set
    {
      if (_settings.FunStatsEnabled == value) return;
      _settings.FunStatsEnabled = value;
      Notify();
      Notify(nameof(FunStatsVisible));
      SavePreferences();
      RebuildPresentation();
      RebuildHomeFunStats();
    }
  }
  public bool MetricCardFunFactsEnabled
  {
    get => _settings.MetricCardFunFactsEnabled;
    set
    {
      if (_settings.MetricCardFunFactsEnabled == value) return;
      _settings.MetricCardFunFactsEnabled = value;
      Notify();
      SavePreferences();
      NotifyCardFacts();
    }
  }
  public int FunFactRotationIndex
  {
    get => (int)_settings.FunFactRotation;
    set
    {
      var next = (FunFactRotation)Math.Clamp(value, 0, Enum.GetValues<FunFactRotation>().Length - 1);
      if (_settings.FunFactRotation == next) return;
      _settings.FunFactRotation = next;
      Notify();
      SavePreferences();
      NotifyCardFacts();
    }
  }
  public int FunStatsCopyModeIndex
  {
    get => (int)_settings.FunStatsCopyMode;
    set
    {
      var next = (FunStatsCopyMode)Math.Clamp(value, 0, Enum.GetValues<FunStatsCopyMode>().Length - 1);
      if (_settings.FunStatsCopyMode == next) return;
      _settings.FunStatsCopyMode = next;
      Notify();
      SavePreferences();
    }
  }
  public FunStatsCopyMode FunStatsCopyMode => _settings.FunStatsCopyMode;
  public double ScrollCentimetersPerDetent
  {
    get => _settings.ScrollCentimetersPerDetent;
    set
    {
      var next = double.IsFinite(value) ? Math.Clamp(value, 0.01, 100) : 1.27;
      if (Math.Abs(_settings.ScrollCentimetersPerDetent - next) < 0.0001) return;
      _settings.ScrollCentimetersPerDetent = next;
      Notify();
      SavePreferences();
      RebuildPresentation();
      RebuildHomeFunStats();
    }
  }

  public int ChartMetricFamilyIndex
  {
    get => (int)_settings.StatisticsChartMetricFamily;
    set
    {
      var next = (StatisticsChartMetricFamily)Math.Clamp(value, 0, 2);
      if (_settings.StatisticsChartMetricFamily == next) return;
      _settings.StatisticsChartMetricFamily = next;
      if (next == StatisticsChartMetricFamily.Rates && _settings.StatisticsChartViewType == StatisticsChartViewType.Donut)
        _settings.StatisticsChartViewType = StatisticsChartViewType.Line;
      Notify();
      Notify(nameof(ChartViewTypeIndex));
      RebuildChartOptions();
      SavePreferences();
      RebuildChart();
    }
  }
  public int ChartViewTypeIndex
  {
    get => (int)_settings.StatisticsChartViewType;
    set
    {
      var next = (StatisticsChartViewType)Math.Clamp(value, 0, 2);
      if (_settings.StatisticsChartMetricFamily == StatisticsChartMetricFamily.Rates && next == StatisticsChartViewType.Donut)
        next = StatisticsChartViewType.Line;
      if (_settings.StatisticsChartViewType == next) return;
      _settings.StatisticsChartViewType = next;
      Notify();
      SavePreferences();
      RebuildChart();
    }
  }
  public int ChartGranularityIndex
  {
    get => (int)_settings.StatisticsTrendGranularity;
    set
    {
      var next = (StatisticsTrendGranularity)Math.Clamp(value, 0, 4);
      if (_settings.StatisticsTrendGranularity == next) return;
      if (next != StatisticsTrendGranularity.Auto && Snapshot is not null
        && !StatisticsTrendAggregator.IsGranularityAvailable(Snapshot.Trend, next)) return;
      _settings.StatisticsTrendGranularity = next;
      Notify();
      SavePreferences();
      RebuildChart();
    }
  }

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
  public string KeyboardFunFact => CardFact("keyboard");
  public string PointerFunFact => CardFact("pointer");
  public string ActiveFunFact => CardFact("active");
  public string ScrollingFunFact => CardFact("scrolling");
  public string AverageTypingFunFact => CardFact("average-typing");
  public string PeakTypingFunFact => CardFact("peak-typing");
  public string AverageClickingFunFact => CardFact("average-clicking");
  public string BusiestHourFunFact => CardFact("busiest-hour");
  public string CurrentPeriodLabel => PeriodOptions[PeriodIndex];
  public string HomeFunStatsPeriodLabel => HomeFunStatsPeriodOptions[HomeFunStatsPeriodIndex];

  public void SetVisible(bool visible)
  {
    if (_statisticsVisible == visible) return;
    _visibleRefresh?.Cancel();
    _visibleRefresh?.Dispose();
    _visibleRefresh = null;
    _statisticsVisible = visible;
    if (!visible) return;
    _visibleRefresh = new CancellationTokenSource();
    _ = RefreshAsync(_visibleRefresh.Token);
  }

  public void SetHomeVisible(bool visible)
  {
    if (_homeVisible == visible) return;
    _homeVisible = visible;
    if (visible) _ = RefreshHomeFunStatsAsync();
  }

  public void UpdateSettings(AppSettings settings)
  {
    settings.NormalizeFunStats();
    _settings = settings;
    _funStats = new(settings, _localization);
    _funStatsPresented = false;
    _homeFunStatsPresented = false;
    RebuildFunStatOptions();
    RebuildFunStatCategoryOptions();
    RebuildChartSeriesOptions();
    RebuildChartOptions();
    RebuildPresentation();
    RebuildHomeFunStats();
    Notify(nameof(FunStatsEnabled), nameof(FunStatsVisible), nameof(MetricCardFunFactsEnabled),
      nameof(FunFactRotationIndex), nameof(FunStatsCopyModeIndex), nameof(FunStatsCopyMode),
      nameof(ScrollCentimetersPerDetent), nameof(HomeFunStatsPeriodIndex), nameof(HomeFunStatsPeriodLabel),
      nameof(ChartMetricFamilyIndex), nameof(ChartViewTypeIndex), nameof(ChartGranularityIndex), nameof(ReducedMotion));
    if (_homeVisible) _ = RefreshHomeFunStatsAsync();
  }

  public void SetHeatmapVisible(bool visible)
  {
    if (_heatmapVisible == visible) return;
    _heatmapVisible = visible;
    if (visible) _ = RefreshHeatmapAsync();
  }

  public void NotifyReducedMotion() => Notify(nameof(ReducedMotion));

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
      if (_homeVisible)
      {
        var homeQuery = CreateHomeFunStatsQuery();
        HomeSnapshot = SameRange(query, homeQuery) ? Snapshot : await _service.QueryAsync(homeQuery, cancellationToken);
      }
      if (_heatmapVisible)
      {
        var heatmapQuery = CreateHeatmapQuery();
        if (SameRange(query, heatmapQuery)) HeatmapSnapshot = Snapshot;
        else await RefreshHeatmapAsync(cancellationToken);
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

  public async Task RefreshHomeFunStatsAsync(CancellationToken cancellationToken = default)
  {
    try
    {
      var query = CreateHomeFunStatsQuery();
      HomeSnapshot = Snapshot is not null && SameRange(Snapshot.Query, query)
        ? Snapshot
        : await _service.QueryAsync(query, cancellationToken);
    }
    catch (OperationCanceledException) { }
  }

  public Task RefreshApplicationsAsync(CancellationToken cancellationToken = default) => RefreshApplicationsAsync(CreateQuery(), cancellationToken);

  public (string Title, string Value, IReadOnlyList<EvaluatedFunStat> Facts) CardDetails(string cardId)
  {
    var title = cardId switch
    {
      "keyboard" => _localization.Get("KeyboardPresses"),
      "pointer" => _localization.Get("PointerClicks"),
      "active" => _localization.Get("ActiveTime"),
      "scrolling" => _localization.Get("Scrolling"),
      "average-typing" => _localization.Get("AverageTyping"),
      "peak-typing" => _localization.Get("PeakTyping"),
      "average-clicking" => _localization.Get("AverageClicking"),
      _ => _localization.Get("BusiestHour")
    };
    var value = cardId switch
    {
      "keyboard" => KeyboardCount,
      "pointer" => PointerCount,
      "active" => ActiveTime,
      "scrolling" => ScrollingCount,
      "average-typing" => AverageTypingSpeed,
      "peak-typing" => PeakTypingSpeed,
      "average-clicking" => AverageClickingSpeed,
      _ => BusiestHour
    };
    return (title, value, _funStats.Details(cardId, Snapshot));
  }

  public void CardWasClicked(string cardId)
  {
    if (_settings.FunFactRotation != FunFactRotation.CardClick) return;
    _funStats.RotateCard(cardId);
    NotifyCardFacts();
  }

  public void ShuffleFunFacts()
  {
    _funStats.Shuffle();
    NotifyCardFacts();
  }

  public bool TryAddCustomFunStat(string label, int metricIndex, double target, out string error)
  {
    label = label.Trim();
    if (label.Length is < 1 or > 80 || !double.IsFinite(target) || target <= 0 || target > 1e15
      || metricIndex < 0 || metricIndex >= Enum.GetValues<FunStatMetric>().Length)
    {
      error = _localization.Get("FunCustomValidationError");
      return false;
    }
    var custom = new CustomFunStatDefinition
    {
      Id = $"custom-{Guid.NewGuid():N}",
      Label = label,
      Metric = (FunStatMetric)metricIndex,
      Target = target
    };
    _settings.CustomFunStats.Add(custom);
    if (_settings.SelectedFunStatIds.Count < 12) _settings.SelectedFunStatIds.Add(custom.Id);
    _settings.NormalizeFunStats();
    SavePreferences();
    RebuildFunStatOptions();
    RebuildFunStatCategoryOptions();
    RebuildPresentation();
    RebuildHomeFunStats();
    error = string.Empty;
    return true;
  }

  public void RemoveCustomFunStat(string id)
  {
    _settings.CustomFunStats.RemoveAll(item => item.Id == id);
    _settings.SelectedFunStatIds.RemoveAll(item => item == id);
    SavePreferences();
    RebuildFunStatOptions();
    RebuildPresentation();
    RebuildHomeFunStats();
  }

  public void MoveFunStat(string id, int direction)
  {
    var index = _settings.SelectedFunStatIds.IndexOf(id);
    var next = index + Math.Sign(direction);
    if (index < 0 || next < 0 || next >= _settings.SelectedFunStatIds.Count) return;
    (_settings.SelectedFunStatIds[index], _settings.SelectedFunStatIds[next]) = (_settings.SelectedFunStatIds[next], _settings.SelectedFunStatIds[index]);
    SavePreferences();
    RebuildFunStatOptions();
    RebuildPresentation();
    RebuildHomeFunStats();
  }

  public void ResetScrollEstimate() => ScrollCentimetersPerDetent = 1.27;

  public string BuildShareCaption(bool home)
  {
    var period = home ? HomeFunStatsPeriodLabel : CurrentPeriodLabel;
    var tiles = home ? HomeFunStatsTiles : FunStatsTiles;
    var values = string.Join(" • ", tiles.Select(tile => $"{tile.Title}: {tile.Value}"));
    return _localization.Format("FunShareCaptionFormat", period, values);
  }

  public void RefreshLocalization()
  {
    Notify(nameof(PeriodOptions), nameof(HomeFunStatsPeriodOptions), nameof(ComparisonOptions), nameof(HeatmapPeriodOptions),
      nameof(PointerDeviceFilterOptions), nameof(FunFactRotationOptions), nameof(FunStatsCopyModeOptions),
      nameof(ChartMetricFamilyOptions), nameof(CustomMetricOptions), nameof(QueueDiagnostics), nameof(CurrentPeriodLabel), nameof(HomeFunStatsPeriodLabel),
      nameof(ChartAccessibleSummary));
    RebuildBreakdowns();
    RebuildFunStatOptions();
    RebuildFunStatCategoryOptions();
    RebuildChartSeriesOptions();
    RebuildChartOptions();
    RebuildPresentation();
    RebuildHomeFunStats();
  }

  public void Dispose()
  {
    _service.DataChanged -= StatisticsDataChanged;
    _visibleRefresh?.Cancel();
    _visibleRefresh?.Dispose();
  }

  private void StatisticsDataChanged()
  {
    Interlocked.Increment(ref _dirtyRefreshRevision);
    ScheduleDirtyRefresh();
  }

  private void ScheduleDirtyRefresh()
  {
    if (!_statisticsVisible || Interlocked.Exchange(ref _dirtyRefreshScheduled, 1) != 0) return;
    _ = RefreshDirtyAsync();
  }

  private async Task RefreshDirtyAsync()
  {
    try
    {
      while (_statisticsVisible)
      {
        var revision = Volatile.Read(ref _dirtyRefreshRevision);
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (!_statisticsVisible) return;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () => await RefreshAsync()).Task.Unwrap();
        Volatile.Write(ref _lastDirtyRefreshRevision, revision);
        if (revision == Volatile.Read(ref _dirtyRefreshRevision)) return;
      }
    }
    finally
    {
      Interlocked.Exchange(ref _dirtyRefreshScheduled, 0);
      if (_statisticsVisible && Volatile.Read(ref _lastDirtyRefreshRevision) != Volatile.Read(ref _dirtyRefreshRevision))
        ScheduleDirtyRefresh();
    }
  }

  private StatisticsQuery CreateQuery()
  {
    var (start, end) = CreateRange(SelectedPeriod);
    var comparison = ComparisonEnabled ? (StatisticsComparison)ComparisonIndex : StatisticsComparison.None;
    return new(ToUtc(start), ToUtc(end), comparison);
  }

  private StatisticsQuery CreateHomeFunStatsQuery()
  {
    var (start, end) = CreateRange(_settings.HomeFunStatsPeriod);
    return new(ToUtc(start), ToUtc(end));
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
    var (start, end) = CreateRange(PresetPeriodValues[HeatmapPeriodIndex]);
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

  private void RebuildPresentation()
  {
    NotifyMetrics();
    var tiles = _funStats.Dashboard(Snapshot);
    var animate = !_settings.ReducedMotion && !_funStatsPresented && tiles.Count > 0;
    Replace(FunStatsTiles, tiles.Select(tile => tile with { Animate = animate }));
    if (tiles.Count > 0) _funStatsPresented = true;
    RebuildChart();
    UpdateGranularityAvailability();
  }

  private void RebuildHomeFunStats()
  {
    var tiles = _funStats.Dashboard(HomeSnapshot);
    var animate = !_settings.ReducedMotion && !_homeFunStatsPresented && tiles.Count > 0;
    Replace(HomeFunStatsTiles, tiles.Select(tile => tile with { Animate = animate }));
    if (tiles.Count > 0) _homeFunStatsPresented = true;
  }

  private void RebuildChart() => ChartModel = StatisticsTrendAggregator.Build(Snapshot, _settings.StatisticsChartMetricFamily,
    _settings.StatisticsChartViewType, _settings.StatisticsTrendGranularity, _settings.EnabledStatisticsChartSeries, _localization);

  private void RebuildBreakdowns()
  {
    PointerRows.Clear();
    KeyboardRows.Clear();
    if (Snapshot is null) return;
    var keyboardItems = Snapshot.Breakdown
      .Where(item => item.Kind == InputKind.KeyboardKey)
      .OrderByDescending(item => item.Count)
      .ToArray();
    var maximumCount = keyboardItems.Length == 0 ? 1 : keyboardItems.Max(item => item.Count);
    foreach (var item in keyboardItems)
      KeyboardRows.Add(new(_localization.KeyNameFromScanCode(item.PhysicalCode, item.Extended), item.Count, _localization.EnumName(item.Group), maximumCount));
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

  private void RebuildFunStatOptions()
  {
    FunStatOptions.Clear();
    foreach (var fact in FunStatsCatalog.Facts.OrderBy(item => item.Category).ThenBy(item => item.Order).ThenBy(item => item.Id))
      FunStatOptions.Add(new(fact.Id, _localization.Get(fact.TitleKey), _localization.Get($"FunCategory_{fact.Category}"), false,
        !_settings.DisabledFunFactIds.Contains(fact.Id, StringComparer.Ordinal),
        _settings.SelectedFunStatIds.Contains(fact.Id, StringComparer.Ordinal), FunStatOptionChanged));
    foreach (var custom in _settings.CustomFunStats)
      FunStatOptions.Add(new(custom.Id, custom.Label, _localization.Get("FunCategoryCustom"), true, true,
        _settings.SelectedFunStatIds.Contains(custom.Id, StringComparer.Ordinal), FunStatOptionChanged));
  }

  private void RebuildFunStatCategoryOptions()
  {
    FunStatCategoryOptions.Clear();
    foreach (var group in FunStatsCatalog.Facts.GroupBy(item => item.Category).OrderBy(group => _localization.Get($"FunCategory_{group.Key}")))
    {
      var ids = group.Select(item => item.Id).ToArray();
      FunStatCategoryOptions.Add(new(group.Key, _localization.Get($"FunCategory_{group.Key}"), string.Empty, false,
        ids.All(id => !_settings.DisabledFunFactIds.Contains(id, StringComparer.Ordinal)), false, FunStatCategoryOptionChanged));
    }
  }

  private void FunStatCategoryOptionChanged(FunStatOption option, string property)
  {
    if (property != nameof(FunStatOption.IsEnabled)) return;
    var ids = FunStatsCatalog.Facts.Where(item => item.Category == option.Id).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
    if (option.IsEnabled) _settings.DisabledFunFactIds.RemoveAll(ids.Contains);
    else
    {
      foreach (var id in ids)
        if (!_settings.DisabledFunFactIds.Contains(id, StringComparer.Ordinal)) _settings.DisabledFunFactIds.Add(id);
      _settings.SelectedFunStatIds.RemoveAll(ids.Contains);
    }
    _settings.NormalizeFunStats();
    SavePreferences();
    RebuildFunStatOptions();
    RebuildPresentation();
    RebuildHomeFunStats();
  }

  private void FunStatOptionChanged(FunStatOption option, string property)
  {
    if (property == nameof(FunStatOption.IsEnabled) && !option.IsCustom)
    {
      if (option.IsEnabled) _settings.DisabledFunFactIds.RemoveAll(item => item == option.Id);
      else
      {
        if (!_settings.DisabledFunFactIds.Contains(option.Id, StringComparer.Ordinal)) _settings.DisabledFunFactIds.Add(option.Id);
        _settings.SelectedFunStatIds.RemoveAll(item => item == option.Id);
        option.SetSelectedSilently(false);
      }
    }
    else if (property == nameof(FunStatOption.IsSelected))
    {
      if (option.IsSelected)
      {
        if (_settings.SelectedFunStatIds.Count >= 12)
        {
          option.SetSelectedSilently(false);
          return;
        }
        if (!_settings.SelectedFunStatIds.Contains(option.Id, StringComparer.Ordinal)) _settings.SelectedFunStatIds.Add(option.Id);
      }
      else _settings.SelectedFunStatIds.RemoveAll(item => item == option.Id);
    }
    _settings.NormalizeFunStats();
    SavePreferences();
    RebuildFunStatCategoryOptions();
    RebuildPresentation();
    RebuildHomeFunStats();
  }

  private void RebuildChartSeriesOptions()
  {
    ChartSeriesOptions.Clear();
    foreach (var definition in ChartSeriesDefinitions)
      ChartSeriesOptions.Add(new(definition.Id, _localization.Get(definition.ResourceKey), _localization.Get($"ChartFamily{definition.Family}"), false,
        _settings.EnabledStatisticsChartSeries.Contains(definition.Id, StringComparer.Ordinal), false, ChartSeriesOptionChanged));
  }

  private void ChartSeriesOptionChanged(FunStatOption option, string property)
  {
    if (property != nameof(FunStatOption.IsEnabled)) return;
    if (option.IsEnabled)
    {
      if (!_settings.EnabledStatisticsChartSeries.Contains(option.Id, StringComparer.Ordinal)) _settings.EnabledStatisticsChartSeries.Add(option.Id);
    }
    else _settings.EnabledStatisticsChartSeries.RemoveAll(item => item == option.Id);
    _settings.NormalizeFunStats();
    SavePreferences();
    RebuildChart();
  }

  private void RebuildChartOptions()
  {
    SetOptions(ChartViewOptions,
    [
      new(_localization.Get("ChartViewLine")),
      new(_localization.Get("ChartViewBar")),
      new(_localization.Get("ChartViewDonut"), _settings.StatisticsChartMetricFamily != StatisticsChartMetricFamily.Rates)
    ]);
    Notify(nameof(ChartViewTypeIndex));
    UpdateGranularityAvailability();
  }

  private void UpdateGranularityAvailability()
  {
    var points = Snapshot?.Trend ?? [];
    SetOptions(ChartGranularityOptions,
    [
      new(_localization.Get("ChartGranularityAuto")),
      new(_localization.Get("ChartGranularityHourly"), StatisticsTrendAggregator.IsGranularityAvailable(points, StatisticsTrendGranularity.Hourly)),
      new(_localization.Get("ChartGranularityDaily"), StatisticsTrendAggregator.IsGranularityAvailable(points, StatisticsTrendGranularity.Daily)),
      new(_localization.Get("ChartGranularityWeekly"), StatisticsTrendAggregator.IsGranularityAvailable(points, StatisticsTrendGranularity.Weekly)),
      new(_localization.Get("ChartGranularityMonthly"), StatisticsTrendAggregator.IsGranularityAvailable(points, StatisticsTrendGranularity.Monthly))
    ]);
    Notify(nameof(ChartGranularityIndex));
  }

  private string CardFact(string id) => _funStats.CardFact(id, Snapshot, DateTimeOffset.Now);
  private string PointerLabel(StatisticsBreakdown item) => item.Kind == InputKind.Wheel
    ? _localization.Get(item.PhysicalCode switch { 6 => "WheelUp", 7 => "WheelDown", 8 => "WheelLeft", _ => "WheelRight" })
    : _localization.Get(item.PhysicalCode switch { 1 => "PrimaryButton", 2 => "SecondaryButton", 3 => "MiddleButton", 4 => "X1Button", _ => "X2Button" });

  private string Compare(long current, long? previous)
  {
    if (previous is null) return string.Empty;
    if (previous == 0) return current == 0 ? _localization.Get("NoChange") : _localization.Get("NewActivity");
    return string.Format(CultureInfo.CurrentUICulture, "{0:+0.0;-0.0;0}%", (current - previous.Value) * 100d / previous.Value);
  }

  private IReadOnlyList<string> PeriodLabels(bool includeCustom)
  {
    var labels = new List<string>
    {
      _localization.Get("PeriodToday"), _localization.Get("PeriodLastThirtyMinutes"), _localization.Get("PeriodLastHour"),
      _localization.Get("PeriodLastFiveHours"), _localization.Get("PeriodSevenDays"), _localization.Get("PeriodThirtyDays"),
      _localization.Get("PeriodThisMonth"), _localization.Get("PeriodThisYear"), _localization.Get("PeriodAllTime")
    };
    if (includeCustom) labels.Add(_localization.Get("PeriodCustom"));
    return labels;
  }

  private string MetricLabel(FunStatMetric metric) => _localization.Get($"FunMetric{metric}");
  private void SavePreferences() => _ = SavePreferencesAsync();
  private async Task SavePreferencesAsync()
  {
    try { await _saveSettings(); }
    catch { }
  }

  private void NotifyMetrics()
  {
    Notify(nameof(KeyboardCount), nameof(PointerCount), nameof(ActiveTime), nameof(AverageTypingSpeed), nameof(PeakTypingSpeed),
      nameof(AverageClickingSpeed), nameof(PeakClickingSpeed), nameof(ScrollingCount), nameof(BusiestHour),
      nameof(KeyboardComparison), nameof(PointerComparison));
    NotifyCardFacts();
  }

  private void NotifyCardFacts() => Notify(nameof(KeyboardFunFact), nameof(PointerFunFact), nameof(ActiveFunFact),
    nameof(ScrollingFunFact), nameof(AverageTypingFunFact), nameof(PeakTypingFunFact), nameof(AverageClickingFunFact), nameof(BusiestHourFunFact));

  private static bool SameRange(StatisticsQuery first, StatisticsQuery second) => first.StartUtc == second.StartUtc && first.EndUtc == second.EndUtc;
  private static DateTimeOffset ToUtc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);
  private static string FormatCount(long value) => value.ToString("N0", CultureInfo.CurrentUICulture);
  private static string FormatDuration(long milliseconds)
  {
    var value = TimeSpan.FromMilliseconds(milliseconds);
    return value.TotalHours >= 1 ? $"{value.TotalHours:0.#} h" : $"{value.TotalMinutes:0.#} min";
  }

  private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
  {
    collection.Clear();
    foreach (var value in values) collection.Add(value);
  }

  private static void SetOptions(ObservableCollection<LocalizedOption> collection, IReadOnlyList<LocalizedOption> values)
  {
    if (collection.Count != values.Count)
    {
      Replace(collection, values);
      return;
    }
    for (var index = 0; index < values.Count; index++) collection[index].Update(values[index]);
  }

  private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
  private void Notify(params string[] properties) { foreach (var property in properties) Notify(property); }
}
