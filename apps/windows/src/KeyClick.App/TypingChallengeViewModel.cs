using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;

namespace KeyClick.App;

public sealed class TypingChallengeViewModel : INotifyPropertyChanged, IDisposable
{
  private static readonly int[] DurationValues = [15, 30, 60, 180, 300, -1];
  private readonly TypingChallengeService _service;
  private readonly StatisticsService _statistics;
  private AppSettings _settings;
  private readonly Func<Task> _saveSettings;
  private readonly LocalizationService _localization;
  private readonly DispatcherTimer _timer;
  private TypingChallengeSession? _session;
  private TypingChallengeResult? _result;
  private TypingChallengeComparison? _comparison;
  private TypingChallengeStreakSnapshot? _streaks;
  private SavedTypingPrompt? _selectedSavedPrompt;
  private TypingChallengeDefinition? _selectedPassage;
  private TypingChallengeResult? _selectedHistory;
  private TypingChallengeResult? _selectedComparison;
  private string? _savedPromptId;
  private int _view;
  private int _sourceIndex;
  private int _languageIndex;
  private int _difficultyIndex = 1;
  private int _runModeIndex;
  private int _mistakeModeIndex;
  private int _durationIndex = 2;
  private int _customDurationSeconds = 60;
  private int _historyPeriodIndex = 3;
  private int _historySourceIndex;
  private int _historyModeIndex;
  private int _normalComparisonPeriodIndex = 4;
  private bool _freeWritingTimed;
  private bool _saveResult = true;
  private bool _compareResult = true;
  private bool _saveCustomPrompt;
  private string _customPromptTitle = string.Empty;
  private string _customPromptText = string.Empty;

  public TypingChallengeViewModel(TypingChallengeService service, StatisticsService statistics, AppSettings settings,
    LocalizationService localization, Func<Task> saveSettings)
  {
    _service = service;
    _statistics = statistics;
    _settings = settings;
    _localization = localization;
    _saveSettings = saveSettings;
    _languageIndex = string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    _timer.Tick += Timer_Tick;
    RebuildPassages();
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  public event EventHandler? SessionDisplayChanged;
  public IReadOnlyList<string> SourceOptions => [_localization.Get("ChallengeSourceBuiltIn"), _localization.Get("ChallengeSourceCustom"), _localization.Get("ChallengeSourceFreeWriting")];
  public IReadOnlyList<string> LanguageOptions => [_localization.Get("LanguageEnglish"), _localization.Get("LanguageFrench")];
  public IReadOnlyList<string> DifficultyOptions => [_localization.Get("ChallengeDifficultyEasy"), _localization.Get("ChallengeDifficultyMedium"), _localization.Get("ChallengeDifficultyHard")];
  public IReadOnlyList<string> RunModeOptions => [_localization.Get("ChallengeModePassage"), _localization.Get("ChallengeModeSingleTimed"), _localization.Get("ChallengeModeContinuous")];
  public IReadOnlyList<string> MistakeModeOptions => [_localization.Get("ChallengeMistakeFlow"), _localization.Get("ChallengeMistakeStrict")];
  public IReadOnlyList<string> DurationOptions => ["15 s", "30 s", "1 min", "3 min", "5 min", _localization.Get("Custom")];
  public IReadOnlyList<string> HistoryPeriodOptions => [_localization.Get("PeriodToday"), _localization.Get("PeriodSevenDays"), _localization.Get("PeriodThirtyDays"), _localization.Get("PeriodAllTime")];
  public IReadOnlyList<string> HistorySourceOptions => [_localization.Get("All"), .. SourceOptions];
  public IReadOnlyList<string> HistoryModeOptions => [_localization.Get("All"), .. RunModeOptions, _localization.Get("ChallengeFreeWriting")];
  public IReadOnlyList<string> NormalComparisonPeriodOptions =>
  [
    _localization.Get("PeriodToday"), _localization.Get("PeriodLastThirtyMinutes"), _localization.Get("PeriodLastHour"),
    _localization.Get("PeriodLastFiveHours"), _localization.Get("PeriodSevenDays"), _localization.Get("PeriodThirtyDays"),
    _localization.Get("PeriodThisMonth"), _localization.Get("PeriodThisYear"), _localization.Get("PeriodAllTime")
  ];
  public ObservableCollection<TypingChallengeDefinition> PassageOptions { get; } = [];
  public ObservableCollection<SavedTypingPrompt> SavedPrompts { get; } = [];
  public ObservableCollection<TypingChallengeResult> History { get; } = [];

  public bool SetupVisible => _view == 0;
  public bool ActiveVisible => _view == 1;
  public bool ResultsVisible => _view == 2;
  public bool HistoryVisible => _view == 3;
  public bool IsSessionActive => _view == 1;
  public bool IsPaused => _session?.IsPaused == true;
  public bool BuiltInVisible => SourceIndex == 0;
  public bool CustomVisible => SourceIndex == 1;
  public bool FreeWritingVisible => SourceIndex == 2;
  public bool ReferenceTextVisible => SourceIndex != 2;
  public bool TimedOptionsVisible => RunModeIndex is 1 or 2 || FreeWritingVisible && FreeWritingTimed;
  public bool CustomDurationVisible => DurationIndex == DurationValues.Length - 1;

  public int SourceIndex { get => _sourceIndex; set { _sourceIndex = Math.Clamp(value, 0, 2); if (_sourceIndex == 2) _runModeIndex = 0; NotifySetup(); } }
  public int LanguageIndex { get => _languageIndex; set { _languageIndex = Math.Clamp(value, 0, 1); RebuildPassages(); Notify(); } }
  public int DifficultyIndex { get => _difficultyIndex; set { _difficultyIndex = Math.Clamp(value, 0, 2); RebuildPassages(); Notify(); } }
  public int RunModeIndex { get => _runModeIndex; set { _runModeIndex = Math.Clamp(value, 0, 2); NotifySetup(); } }
  public int MistakeModeIndex { get => _mistakeModeIndex; set { _mistakeModeIndex = Math.Clamp(value, 0, 1); Notify(); } }
  public int DurationIndex { get => _durationIndex; set { _durationIndex = Math.Clamp(value, 0, DurationValues.Length - 1); Notify(nameof(DurationIndex), nameof(CustomDurationVisible)); } }
  public int CustomDurationSeconds { get => _customDurationSeconds; set { _customDurationSeconds = Math.Clamp(value, 10, 3600); Notify(); } }
  public bool FreeWritingTimed { get => _freeWritingTimed; set { _freeWritingTimed = value; NotifySetup(); } }
  public bool SaveResult { get => _saveResult; set { _saveResult = value; Notify(); } }
  public bool CompareResult { get => _compareResult; set { _compareResult = value; Notify(); } }
  public bool SaveCustomPrompt { get => _saveCustomPrompt; set { _saveCustomPrompt = value; Notify(); } }
  public string CustomPromptTitle { get => _customPromptTitle; set { _customPromptTitle = value ?? string.Empty; Notify(); } }
  public string CustomPromptText { get => _customPromptText; set { _customPromptText = value ?? string.Empty; Notify(); } }
  public TypingChallengeDefinition? SelectedPassage { get => _selectedPassage; set { _selectedPassage = value; Notify(); } }
  public SavedTypingPrompt? SelectedSavedPrompt
  {
    get => _selectedSavedPrompt;
    set
    {
      _selectedSavedPrompt = value;
      if (value is not null) { CustomPromptTitle = value.Title; CustomPromptText = value.Text; _savedPromptId = value.Id; }
      Notify();
    }
  }
  public TypingChallengeResult? SelectedHistory { get => _selectedHistory; set { _selectedHistory = value; Notify(); Notify(nameof(CanDeleteSelected)); } }
  public int HistoryPeriodIndex { get => _historyPeriodIndex; set { _historyPeriodIndex = Math.Clamp(value, 0, 3); Notify(); _ = RefreshHistoryAsync(); } }
  public int HistorySourceIndex { get => _historySourceIndex; set { _historySourceIndex = Math.Clamp(value, 0, 3); Notify(); _ = RefreshHistoryAsync(); } }
  public int HistoryModeIndex { get => _historyModeIndex; set { _historyModeIndex = Math.Clamp(value, 0, 4); Notify(); _ = RefreshHistoryAsync(); } }
  public int NormalComparisonPeriodIndex { get => _normalComparisonPeriodIndex; set { _normalComparisonPeriodIndex = Math.Clamp(value, 0, 8); Notify(); } }
  public bool CanDeleteSelected => SelectedHistory is not null;
  public bool IncludeChallengeTypingInStatistics
  {
    get => _settings.IncludeChallengeTypingInStatistics;
    set { _settings.IncludeChallengeTypingInStatistics = value; Notify(); _ = _saveSettings(); }
  }
  public bool DisclosureConfirmed => _settings.TypingChallengeDisclosureConfirmed;
  public double GoalWordsPerMinute
  {
    get => _settings.TypingChallengeGoalWordsPerMinute;
    set { _settings.TypingChallengeGoalWordsPerMinute = Math.Clamp(value, 1, 300); Notify(); _ = _saveSettings(); }
  }
  public double GoalAccuracy
  {
    get => _settings.TypingChallengeGoalAccuracy;
    set { _settings.TypingChallengeGoalAccuracy = Math.Clamp(value, 1, 100); Notify(); _ = _saveSettings(); }
  }

  public string TargetText => _session?.TargetText ?? string.Empty;
  public string ResponseText => _session?.ResponseText ?? string.Empty;
  public string LiveWpm => _localization.Format("WpmFormat", _session?.NetWordsPerMinute ?? 0);
  public string LiveAccuracy => _session?.Definition?.Source == TypingChallengeSource.FreeWriting ? "—" : $"{_session?.AccuracyPercent ?? 0:0.0}%";
  public string Elapsed => TimeSpan.FromMilliseconds(_session?.ActiveMilliseconds ?? 0).ToString(@"mm\:ss", CultureInfo.CurrentCulture);
  public string Remaining => _session?.DurationLimitSeconds is not { } limit ? string.Empty : TimeSpan.FromMilliseconds(Math.Max(0, limit * 1000L - _session.ActiveMilliseconds)).ToString(@"mm\:ss", CultureInfo.CurrentCulture);
  public TypingChallengeResult? Result => _result;
  public IReadOnlyList<TypingChallengeSample> ResultSamples => _result?.Samples ?? [];
  public string ResultWpm => _localization.Format("WpmFormat", _result?.NetWordsPerMinute ?? 0);
  public string ResultGrossWpm => _localization.Format("WpmFormat", _result?.GrossWordsPerMinute ?? 0);
  public string ResultAccuracy => _result?.Source == TypingChallengeSource.FreeWriting ? _localization.Get("NotAvailable") : $"{_result?.AccuracyPercent ?? 0:0.0}%";
  public string ResultConsistency => $"{_result?.ConsistencyPercent ?? 0:0.0}%";
  public string ResultDuration => TimeSpan.FromMilliseconds(_result?.ActiveMilliseconds ?? 0).ToString(@"mm\:ss", CultureInfo.CurrentCulture);
  public string ResultCounts => _result is null ? string.Empty : _localization.Format("ChallengeResultCountsFormat", _result.RetainedCharacters, _result.Words, _result.ErrorAttempts, _result.Corrections);
  public string PreviousComparison => ComparisonText(_comparison?.PreviousSimilar, "ChallengePreviousComparisonFormat");
  public string BestComparison => ComparisonText(_comparison?.PersonalBest, "ChallengeBestComparisonFormat");
  public string SelectedComparison => ComparisonText(_comparison?.SelectedResult, "ChallengeSelectedComparisonFormat");
  public string NormalComparison => _comparison?.NormalStatistics is null ? string.Empty : _localization.Format("ChallengeNormalComparisonFormat", NormalComparisonPeriodOptions[NormalComparisonPeriodIndex], _comparison.NormalStatistics.AverageWordsPerMinute);
  public string ParticipationStreak => _streaks is null ? "—" : _localization.Format("ChallengeStreakFormat", _streaks.ParticipationCurrent, _streaks.ParticipationLongest);
  public string PerformanceStreak => _streaks is null ? "—" : _localization.Format("ChallengeStreakFormat", _streaks.PerformanceCurrent, _streaks.PerformanceLongest);

  public async Task InitializeAsync(CancellationToken cancellationToken = default)
  {
    SavedPrompts.Clear();
    foreach (var prompt in await _service.LoadPromptsAsync(cancellationToken)) SavedPrompts.Add(prompt);
    await RefreshHistoryAsync(cancellationToken);
    _streaks = await _service.GetStreaksAsync(cancellationToken);
    Notify(nameof(ParticipationStreak), nameof(PerformanceStreak));
  }

  public async Task ConfirmDisclosureAsync()
  {
    _settings.TypingChallengeDisclosureConfirmed = true;
    await _saveSettings();
    Notify(nameof(DisclosureConfirmed));
  }

  public async Task StartAsync(CancellationToken cancellationToken = default)
  {
    TypingChallengeDefinition definition;
    _savedPromptId = null;
    if (SourceIndex == 0)
      definition = SelectedPassage ?? throw new InvalidOperationException(_localization.Get("ChallengeSelectPassage"));
    else if (SourceIndex == 1)
    {
      var text = CustomPromptText.Trim();
      if (text.Length is < 25 or > 50_000) throw new InvalidOperationException(_localization.Get("ChallengeCustomTextInvalid"));
      var title = string.IsNullOrWhiteSpace(CustomPromptTitle) ? _localization.Get("ChallengeCustomPrompt") : CustomPromptTitle.Trim();
      definition = new($"custom-session-{Guid.NewGuid():N}", title, text, SelectedLanguage, SelectedDifficulty, TypingChallengeSource.Custom);
      if (SaveCustomPrompt)
      {
        var now = DateTimeOffset.UtcNow;
        _savedPromptId = SelectedSavedPrompt?.Id ?? Guid.NewGuid().ToString("N");
        await _service.SavePromptAsync(new(_savedPromptId, title, text, SelectedLanguage, SelectedDifficulty, false,
          SelectedSavedPrompt?.CreatedUtc ?? now, now, (SelectedSavedPrompt?.Revision ?? 0) + 1), cancellationToken);
        await ReloadPromptsAsync(cancellationToken);
      }
    }
    else
      definition = new("free-writing", _localization.Get("ChallengeFreeWriting"), string.Empty, SelectedLanguage, SelectedDifficulty, TypingChallengeSource.FreeWriting);

    var runMode = SourceIndex == 2 ? TypingChallengeRunMode.FreeWriting : (TypingChallengeRunMode)RunModeIndex;
    int? duration = runMode is TypingChallengeRunMode.SinglePassageTimed or TypingChallengeRunMode.ContinuousTimed
      || runMode == TypingChallengeRunMode.FreeWriting && FreeWritingTimed ? SelectedDurationSeconds : null;
    _session = new(definition, runMode, (TypingChallengeMistakeMode)MistakeModeIndex, duration);
    _result = null;
    _comparison = null;
    _view = 1;
    _service.SetSessionActive(true);
    _timer.Start();
    NotifyAll();
    SessionDisplayChanged?.Invoke(this, EventArgs.Empty);
  }

  public bool Input(string text)
  {
    if (_session is null || _session.IsPaused) return false;
    var accepted = _session.Input(text);
    if (_session.ReferenceTextCompleted)
    {
      if (_session.RunMode == TypingChallengeRunMode.ContinuousTimed && !_session.TimeExpired)
        _session.ContinueWith(RandomPassage(_session.Definition?.Id));
      else _ = FinishAsync();
    }
    NotifyLive();
    SessionDisplayChanged?.Invoke(this, EventArgs.Empty);
    return accepted;
  }

  public bool Backspace()
  {
    var changed = _session?.Backspace() == true;
    if (changed) { NotifyLive(); SessionDisplayChanged?.Invoke(this, EventArgs.Empty); }
    return changed;
  }

  public void Pause()
  {
    _session?.Pause();
    Notify(nameof(IsPaused));
  }

  public void Resume()
  {
    _session?.Resume();
    Notify(nameof(IsPaused));
  }

  public async Task FinishAsync(CancellationToken cancellationToken = default)
  {
    if (_session is null || _view != 1) return;
    _timer.Stop();
    _service.SetSessionActive(false);
    var title = _session.Definition?.Source == TypingChallengeSource.Custom
      ? _localization.Get("ChallengePrivateCustomPrompt") : _session.Definition?.Title ?? _localization.Get("ChallengeFreeWriting");
    _result = await _service.CreateResultAsync(_session, _savedPromptId, title, GoalWordsPerMinute, GoalAccuracy, cancellationToken);
    if (SaveResult)
    {
      await _service.SaveResultAsync(_result, cancellationToken);
      if (CompareResult)
      {
        var normal = await _statistics.QueryAsync(CreateNormalStatisticsQuery(), cancellationToken);
        _comparison = await _service.CompareAsync(_result, normal, _selectedComparison, cancellationToken);
      }
    }
    _session = null;
    _view = 2;
    await RefreshHistoryAsync(cancellationToken);
    _streaks = await _service.GetStreaksAsync(cancellationToken);
    NotifyAll();
  }

  public void Cancel()
  {
    _timer.Stop();
    _service.SetSessionActive(false);
    _session = null;
    _view = 0;
    NotifyAll();
  }

  public void ShowSetup() { if (IsSessionActive) return; _view = 0; NotifyAll(); }
  public async Task ShowHistoryAsync() { if (IsSessionActive) return; _view = 3; await RefreshHistoryAsync(); NotifyAll(); }

  public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
  {
    if (SelectedHistory is null) return;
    await _service.DeleteAsync(new(new HashSet<string> { SelectedHistory.Id }, null, null, true), cancellationToken);
    SelectedHistory = null;
    await RefreshHistoryAsync(cancellationToken);
    _streaks = await _service.GetStreaksAsync(cancellationToken);
    Notify(nameof(ParticipationStreak), nameof(PerformanceStreak));
  }

  public async Task DeleteVisiblePeriodAsync(CancellationToken cancellationToken = default)
  {
    var (start, end) = HistoryRange();
    await _service.DeleteAsync(new(new HashSet<string>(), start, end, true), cancellationToken);
    await RefreshHistoryAsync(cancellationToken);
    _streaks = await _service.GetStreaksAsync(cancellationToken);
    Notify(nameof(ParticipationStreak), nameof(PerformanceStreak));
  }

  public async Task DeleteFromStatisticsDialogAsync(StatisticsDeleteRequest request, CancellationToken cancellationToken = default)
  {
    await _service.DeleteAsync(new(new HashSet<string>(), request.StartUtc, request.EndUtc,
      request.DeleteTypingChallengeAchievements, request.CreateSafetyBackup, request.DeleteTypingChallengeResults), cancellationToken);
    await RefreshHistoryAsync(cancellationToken);
    _streaks = await _service.GetStreaksAsync(cancellationToken);
    Notify(nameof(ParticipationStreak), nameof(PerformanceStreak));
  }

  public async Task DeleteSelectedPromptAsync(CancellationToken cancellationToken = default)
  {
    if (SelectedSavedPrompt is null) return;
    await _service.DeletePromptAsync(SelectedSavedPrompt.Id, cancellationToken);
    SelectedSavedPrompt = null;
    await ReloadPromptsAsync(cancellationToken);
  }

  public void UseSelectedForComparison()
  {
    if (SelectedHistory is null) return;
    _selectedComparison = SelectedHistory;
    _view = 0;
    CompareResult = true;
    NotifyAll();
  }

  public Task ExportCsvAsync(string path, CancellationToken cancellationToken = default)
  {
    var range = HistoryRange();
    return _service.ExportCsvAsync(new(range.Start, range.End), path, cancellationToken);
  }

  public void ToggleFavorite()
  {
    if (SelectedPassage is null) return;
    if (_settings.FavoriteTypingChallengeIds.Remove(SelectedPassage.Id)) { }
    else _settings.FavoriteTypingChallengeIds.Add(SelectedPassage.Id);
    RebuildPassages(SelectedPassage.Id);
    _ = _saveSettings();
  }

  public void SelectRandomPassage() => SelectedPassage = RandomPassage(SelectedPassage?.Id);

  public void RefreshLocalization()
  {
    Notify(nameof(SourceOptions), nameof(LanguageOptions), nameof(DifficultyOptions), nameof(RunModeOptions), nameof(MistakeModeOptions), nameof(DurationOptions), nameof(HistoryPeriodOptions), nameof(HistorySourceOptions), nameof(HistoryModeOptions), nameof(NormalComparisonPeriodOptions));
    NotifyAll();
  }

  public void UpdateSettings(AppSettings settings)
  {
    _settings = settings;
    RebuildPassages();
    Notify(nameof(IncludeChallengeTypingInStatistics), nameof(GoalWordsPerMinute), nameof(GoalAccuracy), nameof(DisclosureConfirmed));
  }

  public void Dispose()
  {
    _timer.Stop();
    _service.SetSessionActive(false);
  }

  private async void Timer_Tick(object? sender, EventArgs e)
  {
    NotifyLive();
    if (_session?.TimeExpired == true) await FinishAsync();
  }

  private async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
  {
    var range = HistoryRange();
    var source = HistorySourceIndex == 0 ? null : (TypingChallengeSource?)(HistorySourceIndex - 1);
    var mode = HistoryModeIndex == 0 ? null : (TypingChallengeRunMode?)(HistoryModeIndex - 1);
    var rows = await _service.QueryAsync(new(range.Start, range.End, source, mode), cancellationToken);
    History.Clear();
    foreach (var row in rows) History.Add(row);
  }

  private async Task ReloadPromptsAsync(CancellationToken cancellationToken)
  {
    SavedPrompts.Clear();
    foreach (var prompt in await _service.LoadPromptsAsync(cancellationToken)) SavedPrompts.Add(prompt);
  }

  private (DateTimeOffset Start, DateTimeOffset End) HistoryRange()
  {
    var today = DateTime.Today;
    var start = HistoryPeriodIndex switch { 0 => today, 1 => today.AddDays(-6), 2 => today.AddDays(-29), _ => DateTime.UnixEpoch };
    return (ToUtc(start), ToUtc(today.AddDays(1)));
  }

  private StatisticsQuery CreateNormalStatisticsQuery()
  {
    var now = DateTime.Now;
    var today = now.Date;
    var start = NormalComparisonPeriodIndex switch
    {
      0 => today,
      1 => now.AddMinutes(-30),
      2 => now.AddHours(-1),
      3 => now.AddHours(-5),
      4 => today.AddDays(-6),
      5 => today.AddDays(-29),
      6 => new DateTime(today.Year, today.Month, 1),
      7 => new DateTime(today.Year, 1, 1),
      _ => DateTime.UnixEpoch
    };
    var end = NormalComparisonPeriodIndex is 1 or 2 or 3 ? now : today.AddDays(1);
    return new(ToUtc(start), ToUtc(end));
  }

  private TypingChallengeDefinition RandomPassage(string? excluding)
  {
    var candidates = PassageOptions.Where(value => value.Id != excluding).ToArray();
    if (candidates.Length == 0) candidates = PassageOptions.ToArray();
    return candidates.Length == 0 ? TypingChallengeCatalog.Passages[0] : candidates[Random.Shared.Next(candidates.Length)];
  }

  private void RebuildPassages(string? selectedId = null)
  {
    selectedId ??= SelectedPassage?.Id;
    PassageOptions.Clear();
    var favorites = new HashSet<string>(_settings.FavoriteTypingChallengeIds, StringComparer.Ordinal);
    foreach (var passage in TypingChallengeCatalog.Filter(SelectedLanguage, SelectedDifficulty, favorites).OrderByDescending(value => value.IsFavorite).ThenBy(value => value.Title))
      PassageOptions.Add(passage);
    SelectedPassage = PassageOptions.FirstOrDefault(value => value.Id == selectedId) ?? PassageOptions.FirstOrDefault();
  }

  private string SelectedLanguage => LanguageIndex == 1 ? "fr" : "en";
  private TypingChallengeDifficulty SelectedDifficulty => (TypingChallengeDifficulty)DifficultyIndex;
  private int SelectedDurationSeconds => DurationValues[DurationIndex] < 0 ? CustomDurationSeconds : DurationValues[DurationIndex];
  private string ComparisonText(TypingChallengeResult? value, string key) => value is null || _result is null ? string.Empty :
    _localization.Format(key, value.NetWordsPerMinute, _result.NetWordsPerMinute - value.NetWordsPerMinute);

  private void NotifySetup() => Notify(nameof(SourceIndex), nameof(RunModeIndex), nameof(FreeWritingTimed), nameof(BuiltInVisible), nameof(CustomVisible), nameof(FreeWritingVisible), nameof(ReferenceTextVisible), nameof(TimedOptionsVisible));
  private void NotifyLive() => Notify(nameof(TargetText), nameof(ResponseText), nameof(LiveWpm), nameof(LiveAccuracy), nameof(Elapsed), nameof(Remaining));
  private void NotifyAll()
  {
    Notify(nameof(SetupVisible), nameof(ActiveVisible), nameof(ResultsVisible), nameof(HistoryVisible), nameof(IsSessionActive), nameof(IsPaused));
    NotifyLive();
    Notify(nameof(Result), nameof(ResultSamples), nameof(ResultWpm), nameof(ResultGrossWpm), nameof(ResultAccuracy), nameof(ResultConsistency), nameof(ResultDuration), nameof(ResultCounts));
    Notify(nameof(PreviousComparison), nameof(BestComparison), nameof(SelectedComparison), nameof(NormalComparison), nameof(ParticipationStreak), nameof(PerformanceStreak));
  }

  private static DateTimeOffset ToUtc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);
  private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
  private void Notify(params string[] properties) { foreach (var property in properties) Notify(property); }
}
