using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using KeyClick.Updater;
using Application = System.Windows.Application;
using ThemeMode = KeyClick.Core.ThemeMode;
using Timer = System.Threading.Timer;

namespace KeyClick.App;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
  private readonly IAppStore _store;
  private readonly ISoundEngine _audio;
  private readonly IGlobalShortcutService _globalShortcuts;
  private readonly StartupService _startup;
  private readonly BackupService _backup;
  private readonly UpdateService _updates;
  private readonly AudioImportService _imports;
  private readonly SoundPackImportService _packImports;
  private readonly ThemeService _themes;
  private readonly LocalizationService _localization;
  private readonly SoundMappingResolver _resolver = new();
  private ProfileTransferService? _profiles;
  private readonly ShortcutMatcher _sequenceMatcher = new();
  private readonly SemaphoreSlim _packGate = new(1, 1);
  private readonly object _settingsGate = new();
  private readonly Timer _rotationTimer;
  private readonly Dictionary<(string InputId, KeyVariant Variant), InputOverride> _overrides = [];
  private readonly Dictionary<(InputGroup Group, KeyVariant Variant, DeviceFamily? Family), GroupMapping> _groupMappings = [];
  private CancellationTokenSource? _saveDebounce;
  private AppSettings _settings = new();
  private SoundPackDefinition _activePack = BuiltInCatalog.Packs[0];
  private InputActionEvent? _capturedInput;
  private bool _captureInput;
  private string _statusMessage = string.Empty;
  private string _selectedPage = "Home";
  private KeyVariant _mappingVariant = KeyVariant.Base;
  private bool _mappingEnabled = true;
  private double _mappingVolume = 100;
  private string _mappingSound = string.Empty;
  private bool _applyToGroup;
  private ShortcutBinding? _selectedShortcut;
  private UpdateInfo? _availableUpdate;
  private volatile bool _appFocused;

  public MainViewModel(
    IAppStore store,
    ISoundEngine audio,
    IGlobalShortcutService globalShortcuts,
    StartupService startup,
    BackupService backup,
    UpdateService updates,
    AudioImportService imports,
    SoundPackImportService packImports,
    ThemeService themes,
    LocalizationService localization)
  {
    _store = store;
    _audio = audio;
    _globalShortcuts = globalShortcuts;
    _startup = startup;
    _backup = backup;
    _updates = updates;
    _imports = imports;
    _packImports = packImports;
    _themes = themes;
    _localization = localization;
    Packs = new(BuiltInCatalog.Packs.Select(_localization.LocalizePack));
    _activePack = Packs[0];
    OutputDevices = new(audio.OutputDevices.Select(LocalizeOutputDevice));
    _statusMessage = _localization.Get("StatusReady");
    _mappingSound = _localization.Get("BuiltInSoundPool");
    CapturedInputLabel = _localization.Get("NoInputSelected");

    ToggleSoundsCommand = new DelegateCommand(() => SoundsEnabled = !SoundsEnabled);
    PreviousPackCommand = new DelegateCommand(() => SelectRelativePack(-1));
    NextPackCommand = new DelegateCommand(() => SelectRelativePack(1));
    CaptureInputCommand = new DelegateCommand(() =>
    {
      _captureInput = true;
      CapturedInputLabel = _localization.Get("PressReleaseInput");
      StatusMessage = _localization.Get("WaitingInputRelease");
    });
    PreviewMappingCommand = new DelegateCommand(PreviewMapping, () => _capturedInput is not null);
    RestoreMappingCommand = new AsyncDelegateCommand(RestoreMappingAsync, () => _capturedInput is not null);
    SaveMappingCommand = new AsyncDelegateCommand(SaveMappingAsync, () => _capturedInput is not null);
    BackupCommand = new AsyncDelegateCommand(CreateBackupAsync);
    CheckUpdatesCommand = new AsyncDelegateCommand(CheckUpdatesAsync);
    RotatePackNowCommand = new AsyncDelegateCommand(() => RotatePackAsync(true));
    _rotationTimer = new Timer(_ => Application.Current?.Dispatcher.BeginInvoke(async () => await RotationDueAsync()), null, Timeout.Infinite, Timeout.Infinite);
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  public event EventHandler? ShowHideRequested;
  public event EventHandler? LanguageChanged;
  public event EventHandler? StatisticsPolicyChanged;

  public ObservableCollection<SoundPackDefinition> Packs { get; }
  public ObservableCollection<ShortcutBinding> Shortcuts { get; } = [];
  public ObservableCollection<AudioOutputDevice> OutputDevices { get; }
  public ObservableCollection<string> ExcludedExecutables { get; } = [];
  public ObservableCollection<string> StatisticsExcludedExecutables { get; } = [];
  public ObservableCollection<string> AllowedIntegrationClients { get; } = [];
  public ObservableCollection<RotationPackOption> RotationPackOptions { get; } = [];
  public ObservableCollection<DeviceClassificationOption> PointerDevices { get; } = [];
  public IReadOnlyList<string> ThemeModes => Options<ThemeMode>();
  public IReadOnlyList<string> DisplayLanguages => Options<DisplayLanguageMode>();
  public IReadOnlyList<string> MappingVariants => Options<KeyVariant>();
  public IReadOnlyList<string> KeyboardSoundTimings => Options<KeyboardSoundTiming>();
  public IReadOnlyList<string> RotationIntervals => Options<PackRotationInterval>();
  public IReadOnlyList<string> RotationPoolModes => Options<PackRotationPoolMode>();

  public ICommand ToggleSoundsCommand { get; }
  public ICommand PreviousPackCommand { get; }
  public ICommand NextPackCommand { get; }
  public ICommand CaptureInputCommand { get; }
  public ICommand PreviewMappingCommand { get; }
  public ICommand RestoreMappingCommand { get; }
  public ICommand SaveMappingCommand { get; }
  public ICommand BackupCommand { get; }
  public ICommand CheckUpdatesCommand { get; }
  public ICommand RotatePackNowCommand { get; }

  public AppSettings Settings => _settings;
  public string AppTitle => string.IsNullOrWhiteSpace(DisplayName) ? "KeyClick" : DisplayName.Trim();
  public string SoundStateText => _localization.Get(SoundsEnabled ? "SoundsOn" : "SoundsPaused");
  public string SoundStateDescription => _localization.Get(SoundsEnabled ? "ListeningReleases" : "PlaybackPaused");
  public string ActivePackName => _activePack.Name;
  public string ActivePackDescription => _activePack.Description;
  public string MasterVolumeLabel => $"{MasterVolume:0}%";
  public string KeyboardVolumeLabel => $"{KeyboardVolume:0}%";
  public string PointerVolumeLabel => $"{PointerVolume:0}%";
  public string ResultVolumeLabel => $"{ResultVolume:0}%";
  public string IntegrationPipeName { get; set; } = string.Empty;
  public string VersionText => _localization.Format("VersionFormat", GetVersion());
  public string DataLocation { get; set; } = string.Empty;
  public bool IsPortable { get; private set; }
  public UpdateInfo? AvailableUpdate
  {
    get => _availableUpdate;
    private set
    {
      if (Equals(_availableUpdate, value)) return;
      _availableUpdate = value;
      Notify(nameof(AvailableUpdate), nameof(HasAvailableUpdate), nameof(AvailableUpdateText));
    }
  }
  public bool HasAvailableUpdate => !IsPortable && AvailableUpdate is not null;
  public string AvailableUpdateText => AvailableUpdate is null ? string.Empty : _localization.Format(
    AvailableUpdate.IsLocal ? "LocalUpdateAvailableFormat" : "UpdateReadyFormat", AvailableUpdate.Version);
  public StatisticsViewModel? Statistics { get; private set; }
  public WellnessSnapshot? WellnessSnapshot { get; private set; }
  public string WellnessTodaySummary => WellnessSnapshot is null ? _localization.Get("NoStatisticsYet") :
    _localization.Format("WellnessTodayFormat", WellnessSnapshot.KeyboardPressesToday, WellnessSnapshot.PointerClicksToday, WellnessSnapshot.ActiveMinutesToday);
  public string WellnessStreakSummary => WellnessSnapshot is null ? string.Empty :
    _localization.Format("WellnessStreakFormat", WellnessSnapshot.KeyboardCurrentStreak, WellnessSnapshot.KeyboardLongestStreak,
      WellnessSnapshot.PointerCurrentStreak, WellnessSnapshot.PointerLongestStreak, WellnessSnapshot.ActiveCurrentStreak, WellnessSnapshot.ActiveLongestStreak);
  public string NextRotationTime => !_settings.PackRotation.Enabled
    ? _localization.Get("RotationDisabled")
    : _settings.PackRotation.Interval == PackRotationInterval.WindowsBoot
      ? _localization.Get("RotationNextWindowsBoot")
      : _settings.PackRotation.NextDueUtc is { } due
        ? _localization.Format("RotationNextFormat", due.ToLocalTime().ToString("g"))
        : _localization.Get("RotationWaitingForPacks");

  public string SelectedPage
  {
    get => _selectedPage;
    set => Set(ref _selectedPage, value);
  }

  public string StatusMessage
  {
    get => _statusMessage;
    set => Set(ref _statusMessage, value);
  }

  public string CapturedInputLabel { get; private set; } = string.Empty;
  public string CapturedInputDetail => _capturedInput is null
    ? _localization.Get("CaptureInputHelp")
    : $"{_localization.EnumName(_capturedInput.Value.Input.DeviceFamily)} · {_localization.EnumName(_capturedInput.Value.Group)} · {_capturedInput.Value.Input.StableId}";

  public KeyVariant MappingVariant
  {
    get => _mappingVariant;
    set
    {
      if (!Set(ref _mappingVariant, value)) return;
      Notify(nameof(MappingVariantIndex));
      LoadMappingEditor();
    }
  }

  public int MappingVariantIndex
  {
    get => (int)MappingVariant;
    set { if (Enum.IsDefined(typeof(KeyVariant), value)) MappingVariant = (KeyVariant)value; }
  }

  public bool MappingEnabled
  {
    get => _mappingEnabled;
    set => Set(ref _mappingEnabled, value);
  }

  public double MappingVolume
  {
    get => _mappingVolume;
    set => Set(ref _mappingVolume, value);
  }

  public string MappingSound
  {
    get => _mappingSound;
    private set => Set(ref _mappingSound, value);
  }

  public bool ApplyToGroup
  {
    get => _applyToGroup;
    set
    {
      if (!Set(ref _applyToGroup, value)) return;
      Notify(nameof(MappingScopeLabel));
      LoadMappingEditor();
    }
  }

  public string MappingScopeLabel => _capturedInput is not { } input || !ApplyToGroup
    ? _localization.Get("ThisExactInput")
    : _localization.Format("GroupScopeFormat", _localization.EnumName(input.Group), GroupFamily(input) is { } family ? _localization.EnumName(family) : _localization.Get("AllDevices"));

  public ShortcutBinding? SelectedShortcut
  {
    get => _selectedShortcut;
    set => Set(ref _selectedShortcut, value);
  }

  public SoundPackDefinition SelectedPack
  {
    get => _activePack;
    set
    {
      if (value is null || value == _activePack) return;
      _activePack = value;
      _settings.ActivePackId = value.Id;
      Notify(nameof(SelectedPack), nameof(ActivePackName), nameof(ActivePackDescription));
      _ = LoadPackAndOverridesAsync(value);
      QueueSettingsSave();
    }
  }

  public string DisplayName { get => _settings.DisplayName; set { if (_settings.DisplayName == value) return; _settings.DisplayName = value; Notify(nameof(DisplayName), nameof(AppTitle)); QueueSettingsSave(); } }
  public bool SoundsEnabled { get => _settings.SoundsEnabled; set { if (_settings.SoundsEnabled == value) return; _settings.SoundsEnabled = value; Notify(nameof(SoundsEnabled), nameof(SoundStateText), nameof(SoundStateDescription)); QueueSettingsSave(); } }
  public bool KeyboardEnabled { get => _settings.KeyboardEnabled; set { if (_settings.KeyboardEnabled == value) return; _settings.KeyboardEnabled = value; SettingChanged(nameof(KeyboardEnabled)); } }
  public bool PointerEnabled { get => _settings.PointerEnabled; set { if (_settings.PointerEnabled == value) return; _settings.PointerEnabled = value; SettingChanged(nameof(PointerEnabled)); } }
  public bool WheelEnabled { get => _settings.WheelEnabled; set { if (_settings.WheelEnabled == value) return; _settings.WheelEnabled = value; SettingChanged(nameof(WheelEnabled)); } }
  public bool ResultSoundsEnabled { get => _settings.ResultSoundsEnabled; set { if (_settings.ResultSoundsEnabled == value) return; _settings.ResultSoundsEnabled = value; SettingChanged(nameof(ResultSoundsEnabled)); } }
  public bool LaunchAtStartup { get => _settings.LaunchAtStartup; set { if (_settings.LaunchAtStartup == value) return; _startup.SetEnabled(value); _settings.LaunchAtStartup = value; Notify(nameof(LaunchAtStartup)); QueueSettingsSave(); } }
  public bool StartMinimized { get => _settings.StartMinimized; set { if (_settings.StartMinimized == value) return; _settings.StartMinimized = value; SettingChanged(nameof(StartMinimized)); } }
  public bool CloseToTray { get => _settings.CloseToTray; set { if (_settings.CloseToTray == value) return; _settings.CloseToTray = value; SettingChanged(nameof(CloseToTray)); } }
  public bool PauseInFullscreen { get => _settings.PauseInFullscreen; set { if (_settings.PauseInFullscreen == value) return; _settings.PauseInFullscreen = value; SettingChanged(nameof(PauseInFullscreen)); } }
  public bool ReducedMotion { get => _settings.ReducedMotion; set { if (_settings.ReducedMotion == value) return; _settings.ReducedMotion = value; SettingChanged(nameof(ReducedMotion)); } }
  public bool IntegrationApiEnabled { get => _settings.IntegrationApiEnabled; set { if (_settings.IntegrationApiEnabled == value) return; _settings.IntegrationApiEnabled = value; SettingChanged(nameof(IntegrationApiEnabled)); } }
  public bool NormalizeImports { get => _settings.NormalizeImports; set { if (_settings.NormalizeImports == value) return; _settings.NormalizeImports = value; SettingChanged(nameof(NormalizeImports)); } }
  public bool StatisticsDisclosureConfirmed => _settings.StatisticsDisclosureConfirmed;
  public bool KeyboardStatisticsEnabled { get => _settings.KeyboardStatisticsEnabled; set { if (_settings.KeyboardStatisticsEnabled == value) return; _settings.KeyboardStatisticsEnabled = value; StatisticsSettingChanged(nameof(KeyboardStatisticsEnabled)); } }
  public bool PointerStatisticsEnabled { get => _settings.PointerStatisticsEnabled; set { if (_settings.PointerStatisticsEnabled == value) return; _settings.PointerStatisticsEnabled = value; StatisticsSettingChanged(nameof(PointerStatisticsEnabled)); } }
  public bool ScrollingStatisticsEnabled { get => _settings.ScrollingStatisticsEnabled; set { if (_settings.ScrollingStatisticsEnabled == value) return; _settings.ScrollingStatisticsEnabled = value; StatisticsSettingChanged(nameof(ScrollingStatisticsEnabled)); } }
  public bool WellnessEnabled { get => _settings.WellnessEnabled; set { if (_settings.WellnessEnabled == value) return; _settings.WellnessEnabled = value; StatisticsSettingChanged(nameof(WellnessEnabled)); } }
  public bool BreakReminderEnabled { get => _settings.BreakReminderEnabled; set { if (_settings.BreakReminderEnabled == value) return; _settings.BreakReminderEnabled = value; StatisticsSettingChanged(nameof(BreakReminderEnabled)); } }
  public int BreakReminderActiveMinutes { get => _settings.BreakReminderActiveMinutes; set { _settings.BreakReminderActiveMinutes = Math.Clamp(value, 1, 1440); StatisticsSettingChanged(nameof(BreakReminderActiveMinutes)); } }
  public int BreakReminderRestMinutes { get => _settings.BreakReminderRestMinutes; set { _settings.BreakReminderRestMinutes = Math.Clamp(value, 1, 1440); StatisticsSettingChanged(nameof(BreakReminderRestMinutes)); } }
  public bool KeyboardGoalEnabled { get => _settings.KeyboardGoalEnabled; set { if (_settings.KeyboardGoalEnabled == value) return; _settings.KeyboardGoalEnabled = value; StatisticsSettingChanged(nameof(KeyboardGoalEnabled)); } }
  public bool PointerGoalEnabled { get => _settings.PointerGoalEnabled; set { if (_settings.PointerGoalEnabled == value) return; _settings.PointerGoalEnabled = value; StatisticsSettingChanged(nameof(PointerGoalEnabled)); } }
  public bool ActiveMinutesGoalEnabled { get => _settings.ActiveMinutesGoalEnabled; set { if (_settings.ActiveMinutesGoalEnabled == value) return; _settings.ActiveMinutesGoalEnabled = value; StatisticsSettingChanged(nameof(ActiveMinutesGoalEnabled)); } }
  public int KeyboardDailyGoal { get => _settings.KeyboardDailyGoal; set { _settings.KeyboardDailyGoal = Math.Max(1, value); StatisticsSettingChanged(nameof(KeyboardDailyGoal)); } }
  public int PointerDailyGoal { get => _settings.PointerDailyGoal; set { _settings.PointerDailyGoal = Math.Max(1, value); StatisticsSettingChanged(nameof(PointerDailyGoal)); } }
  public int ActiveMinutesDailyGoal { get => _settings.ActiveMinutesDailyGoal; set { _settings.ActiveMinutesDailyGoal = Math.Max(1, value); StatisticsSettingChanged(nameof(ActiveMinutesDailyGoal)); } }
  public KeyboardSoundTiming KeyboardSoundTiming
  {
    get => _settings.KeyboardSoundTiming;
    set
    {
      if (_settings.KeyboardSoundTiming == value) return;
      _settings.KeyboardSoundTiming = value;
      Notify(nameof(KeyboardSoundTiming), nameof(KeyboardSoundTimingIndex));
      QueueSettingsSave();
    }
  }
  public int KeyboardSoundTimingIndex
  {
    get => (int)KeyboardSoundTiming;
    set { if (Enum.IsDefined(typeof(KeyboardSoundTiming), value)) KeyboardSoundTiming = (KeyboardSoundTiming)value; }
  }
  public bool RotationEnabled
  {
    get => _settings.PackRotation.Enabled;
    set
    {
      if (_settings.PackRotation.Enabled == value) return;
      _settings.PackRotation = _settings.PackRotation with { Enabled = value };
      Notify(nameof(RotationEnabled), nameof(NextRotationTime));
      _ = ScheduleRotationAsync(true);
    }
  }
  public int RotationIntervalIndex
  {
    get => (int)_settings.PackRotation.Interval;
    set
    {
      if (!Enum.IsDefined(typeof(PackRotationInterval), value) || (int)_settings.PackRotation.Interval == value) return;
      _settings.PackRotation = _settings.PackRotation with { Interval = (PackRotationInterval)value, NextDueUtc = null };
      Notify(nameof(RotationIntervalIndex), nameof(RotationCustomVisible), nameof(NextRotationTime));
      _ = ScheduleRotationAsync(false);
    }
  }
  public bool RotationCustomVisible => _settings.PackRotation.Interval == PackRotationInterval.Custom;
  public int RotationCustomMinutes
  {
    get => _settings.PackRotation.CustomMinutes;
    set
    {
      var next = Math.Clamp(value, 1, 525600);
      if (_settings.PackRotation.CustomMinutes == next) return;
      _settings.PackRotation = _settings.PackRotation with { CustomMinutes = next, NextDueUtc = null };
      Notify(nameof(RotationCustomMinutes), nameof(NextRotationTime));
      _ = ScheduleRotationAsync(false);
    }
  }
  public int RotationPoolModeIndex
  {
    get => (int)_settings.PackRotation.PoolMode;
    set
    {
      if (!Enum.IsDefined(typeof(PackRotationPoolMode), value) || (int)_settings.PackRotation.PoolMode == value) return;
      _settings.PackRotation = _settings.PackRotation with { PoolMode = (PackRotationPoolMode)value };
      Notify(nameof(RotationPoolModeIndex), nameof(RotationSelectedPoolVisible));
      _ = ScheduleRotationAsync(false);
    }
  }
  public bool RotationSelectedPoolVisible => _settings.PackRotation.PoolMode == PackRotationPoolMode.SelectedPacks;

  public ThemeMode Theme
  {
    get => _settings.Theme;
    set
    {
      if (_settings.Theme == value) return;
      _settings.Theme = value;
      Notify(nameof(Theme), nameof(ThemeIndex));
      _themes.Apply(value, Application.Current?.MainWindow);
      QueueSettingsSave();
    }
  }

  public int ThemeIndex
  {
    get => (int)Theme;
    set { if (Enum.IsDefined(typeof(ThemeMode), value)) Theme = (ThemeMode)value; }
  }

  public DisplayLanguageMode DisplayLanguage
  {
    get => _settings.DisplayLanguage;
    set
    {
      if (_settings.DisplayLanguage == value) return;
      _settings.DisplayLanguage = value;
      _localization.Apply(value);
      RefreshLocalizedContent();
      StatusMessage = _localization.Get("LanguageChanged");
      QueueSettingsSave();
      LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
  }

  public int DisplayLanguageIndex
  {
    get => (int)DisplayLanguage;
    set { if (Enum.IsDefined(typeof(DisplayLanguageMode), value)) DisplayLanguage = (DisplayLanguageMode)value; }
  }

  public string OutputDeviceId
  {
    get => _settings.OutputDeviceId;
    set
    {
      if (_settings.OutputDeviceId == value) return;
      _settings.OutputDeviceId = value;
      Notify(nameof(OutputDeviceId));
      _ = ChangeOutputAsync(value);
      QueueSettingsSave();
    }
  }

  public double MasterVolume { get => _settings.MasterVolume * 100; set { _settings.MasterVolume = (float)(value / 100); Notify(nameof(MasterVolume), nameof(MasterVolumeLabel)); QueueSettingsSave(); } }
  public double KeyboardVolume { get => _settings.KeyboardVolume * 100; set { _settings.KeyboardVolume = (float)(value / 100); Notify(nameof(KeyboardVolume), nameof(KeyboardVolumeLabel)); QueueSettingsSave(); } }
  public double PointerVolume { get => _settings.PointerVolume * 100; set { _settings.PointerVolume = (float)(value / 100); Notify(nameof(PointerVolume), nameof(PointerVolumeLabel)); QueueSettingsSave(); } }
  public double ResultVolume { get => _settings.ResultVolume * 100; set { _settings.ResultVolume = (float)(value / 100); Notify(nameof(ResultVolume), nameof(ResultVolumeLabel)); QueueSettingsSave(); } }
  public int SequenceTimeoutMs { get => _settings.SequenceTimeoutMs; set { _settings.SequenceTimeoutMs = Math.Clamp(value, 300, 5000); Notify(nameof(SequenceTimeoutMs)); QueueSettingsSave(); } }

  public async Task InitializeAsync()
  {
    _settings = await _store.LoadSettingsAsync();
    _settings.LaunchAtStartup = _startup.IsEnabled();
    foreach (var pack in await _packImports.LoadInstalledAsync()) Packs.Add(pack);
    RebuildRotationPackOptions();
    _activePack = Packs.FirstOrDefault(pack => pack.Id == _settings.ActivePackId) ?? Packs[0];
    var shortcuts = await _store.LoadShortcutsAsync();
    foreach (var shortcut in shortcuts) Shortcuts.Add(LocalizeShortcut(shortcut));
    foreach (var executable in _settings.ExcludedExecutables) ExcludedExecutables.Add(executable);
    foreach (var executable in _settings.StatisticsExcludedExecutables) StatisticsExcludedExecutables.Add(executable);
    foreach (var client in _settings.AllowedIntegrationClients) AllowedIntegrationClients.Add(client);
    SelectedShortcut = Shortcuts.FirstOrDefault();
    if (!_globalShortcuts.ReplaceBindings(Shortcuts, out var error)) StatusMessage = error ?? _localization.Get("ShortcutRegistrationFailed");
    await LoadPackAndOverridesAsync(_activePack);
    await ScheduleRotationAsync(true);
    NotifyAllSettings();
  }

  public void HandleInputAction(InputActionEvent input)
  {
    if (_captureInput && input.Phase is InputPhase.Up or InputPhase.WheelDetent)
    {
      _captureInput = false;
      Application.Current.Dispatcher.BeginInvoke(() =>
      {
        _capturedInput = input;
        CapturedInputLabel = DisplayInput(input);
        LoadMappingEditor();
        Notify(nameof(CapturedInputLabel), nameof(CapturedInputDetail));
        StatusMessage = _localization.Get("InputSelectedStatus");
      });
    }

    if (input.Phase == InputPhase.Up && input.ShortcutStep is { } step)
    {
      var candidates = Shortcuts.Where(item =>
        (item.Scope == ShortcutScope.Global && item.Kind == ShortcutKind.Sequence) ||
        (item.Scope == ShortcutScope.App && _appFocused));
      var command = _sequenceMatcher.Process(step, Environment.TickCount64, candidates, _settings.SequenceTimeoutMs);
      if (command is not null) ExecuteShortcut(command);
    }

    if (_settings.PauseInFullscreen && FullscreenDetector.IsForegroundFullscreen()) return;
    _overrides.TryGetValue((input.Input.StableId, input.Variant), out var inputOverride);
    var family = GroupFamily(input);
    if (!_groupMappings.TryGetValue((input.Group, input.Variant, family), out var groupMapping))
      _groupMappings.TryGetValue((input.Group, input.Variant, null), out groupMapping);
    var resolved = _resolver.Resolve(_settings, _activePack, input, groupMapping, inputOverride);
    if (!resolved.Enabled) return;
    var sample = _resolver.SelectStable(resolved, $"{_activePack.Id}:{input.Input.StableId}");
    _audio.TryPlay(new SoundTrigger(sample, resolved.Gain, input.Timestamp));
  }

  public void HandleShortcut(string commandId) => Application.Current.Dispatcher.BeginInvoke(() => ExecuteShortcut(commandId));

  public void SetAppFocused(bool focused) => _appFocused = focused;

  public void PlayOutcome(IntegrationResultRequest request)
  {
    if (!_settings.SoundsEnabled || !_settings.ResultSoundsEnabled) return;
    var variant = request.Outcome is SoundOutcome.Success or SoundOutcome.Authorized ? KeyVariant.Enabled : KeyVariant.Disabled;
    var samples = _activePack.SamplesFor(InputGroup.Outcomes, variant);
    var resolved = new ResolvedSound(true, _settings.MasterVolume * _settings.ResultVolume, samples, false);
    var sample = _resolver.SelectStable(resolved, $"{_activePack.Id}:outcome:{variant}");
    _audio.TryPlay(new SoundTrigger(sample, resolved.Gain, Stopwatch.GetTimestamp(), request.Outcome));
  }

  public async Task ImportMappingSoundAsync(string sourcePath)
  {
    if (_capturedInput is null) return;
    var imported = await _imports.ImportAsync(sourcePath, _settings.NormalizeImports);
    var sampleId = $"custom:{imported.Id}";
    await _audio.LoadCustomSampleAsync(sampleId, imported.Path);
    var input = _capturedInput.Value;
    if (ApplyToGroup)
    {
      var current = new GroupMapping(_activePack.Id, input.Group, MappingVariant, MappingEnabled, (float)(MappingVolume / 100), [sampleId], GroupFamily(input));
      _groupMappings[(current.Group, current.Variant, current.DeviceFamily)] = current;
      await _store.SaveGroupMappingAsync(current);
    }
    else
    {
      var current = new InputOverride(_activePack.Id, input.Input.StableId, MappingVariant, MappingEnabled, (float)(MappingVolume / 100), [sampleId]);
      _overrides[(current.InputId, current.Variant)] = current;
      await _store.SaveOverrideAsync(current);
    }
    MappingSound = Path.GetFileName(sourcePath);
    StatusMessage = _localization.Format("ImportedStatusFormat", MappingSound, imported.Duration.TotalMilliseconds);
  }

  public async Task ImportSoundPackAsync(string archivePath)
  {
    StatusMessage = _localization.Get("ImportingSoundPack");
    var imported = await _packImports.ImportAsync(archivePath, _settings.NormalizeImports);
    var existing = Packs.FirstOrDefault(pack => string.Equals(pack.Id, imported.Id, StringComparison.OrdinalIgnoreCase));
    if (existing is null) Packs.Add(imported);
    else Packs[Packs.IndexOf(existing)] = imported;
    _activePack = imported;
    _settings.ActivePackId = imported.Id;
    Notify(nameof(SelectedPack), nameof(ActivePackName), nameof(ActivePackDescription));
    await LoadPackAndOverridesAsync(imported);
    await _store.SaveSettingsAsync(_settings);
    RebuildRotationPackOptions();
    await ScheduleRotationAsync(false);
    StatusMessage = _localization.Format("SoundPackImportedFormat", imported.Name);
  }

  public async Task SaveShortcutAsync(ShortcutBinding replacement)
  {
    var old = Shortcuts.First(item => item.CommandId == replacement.CommandId);
    var index = Shortcuts.IndexOf(old);
    var candidate = Shortcuts.ToArray();
    candidate[index] = replacement;
    var validationError = ShortcutBindingValidator.Validate(candidate);
    if (validationError is not null) throw new InvalidOperationException(validationError);
    if (!_globalShortcuts.ReplaceBindings(candidate, out var error)) throw new InvalidOperationException(error);
    await _store.SaveShortcutAsync(replacement);
    var localized = LocalizeShortcut(replacement);
    Shortcuts[index] = localized;
    SelectedShortcut = localized;
    StatusMessage = _localization.Get("ShortcutSaved");
  }

  public async Task<UpdateInfo?> CheckForUpdateAsync()
  {
    StatusMessage = _localization.Get("CheckingReleases");
    var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    var packageKind = IsPortable ? UpdatePackageKind.Portable : UpdatePackageKind.Setup;
    var update = await _updates.CheckAsync(architecture, packageKind);
    var current = GetVersion();
    if (update is not null && !UpdateService.IsNewer(update.Version, current)) update = null;
    if (!IsPortable && update is not null && (AvailableUpdate is null || UpdateService.IsNewer(update.Version, AvailableUpdate.Version))) AvailableUpdate = update;
    var result = IsPortable ? update : AvailableUpdate;
    StatusMessage = result is null
      ? _localization.Get("UpToDate")
      : _localization.Format("UpdateAvailableFormat", result.Version, result.Size / 1024d / 1024d);
    return result;
  }

  public async Task<string> PrepareUpdateAsync(UpdateInfo update)
  {
    StatusMessage = _localization.Format(update.IsLocal ? "PreparingLocalUpdateFormat" : "DownloadingUpdateFormat", update.Version);
    await CreateBackupNowAsync();
    var paths = ((App)Application.Current).Paths;
    var destination = IsPortable
      ? Path.GetDirectoryName(paths.Launcher) ?? throw new InvalidOperationException("The portable launcher folder is unavailable.")
      : paths.Updates;
    var path = await _updates.DownloadVerifiedAsync(update, destination);
    StatusMessage = _localization.Get("UpdateVerified");
    return path;
  }

  public async Task DiscoverLocalUpdateAsync(string artifactsDirectory)
  {
    if (IsPortable) return;
    try
    {
      var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
      var update = await _updates.FindLocalAsync(artifactsDirectory, architecture, GetVersion(), UpdatePackageKind.Setup);
      if (update is null || (AvailableUpdate is not null && !UpdateService.IsNewer(update.Version, AvailableUpdate.Version))) return;
      AvailableUpdate = update;
      StatusMessage = _localization.Format("LocalUpdateAvailableFormat", update.Version);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
    {
      Debug.WriteLine($"Local update discovery skipped: {exception.Message}");
    }
  }

  public async Task<string> CreateBackupNowAsync()
  {
    await SaveSettingsNowAsync();
    await _store.CheckpointAsync();
    return await _backup.CreateAsync();
  }

  public async Task PrepareRestoreAsync(string archivePath)
  {
    await _backup.ValidateAsync(archivePath);
    var safetyBackup = await CreateBackupNowAsync();
    StatusMessage = _localization.Format("BackupValidatedFormat", safetyBackup);
  }

  public async Task ResetSettingsAsync()
  {
    await CreateBackupNowAsync();
    _startup.SetEnabled(false);
    var disclosureConfirmed = _settings.StatisticsDisclosureConfirmed;
    _settings = new AppSettings { StatisticsDisclosureConfirmed = disclosureConfirmed };
    ExcludedExecutables.Clear();
    StatisticsExcludedExecutables.Clear();
    AllowedIntegrationClients.Clear();
    _activePack = Packs.FirstOrDefault(pack => pack.Id == BuiltInCatalog.DefaultPackId) ?? Packs[0];
    await _store.SaveSettingsAsync(_settings);
    await _audio.ChangeOutputDeviceAsync("default");
    await LoadPackAndOverridesAsync(_activePack);
    _themes.Apply(_settings.Theme, Application.Current.MainWindow);
    _localization.Apply(_settings.DisplayLanguage);
    RefreshLocalizedContent();
    NotifyAllSettings();
    StatisticsPolicyChanged?.Invoke(this, EventArgs.Empty);
    StatusMessage = _localization.Get("SettingsResetStatus");
    LanguageChanged?.Invoke(this, EventArgs.Empty);
  }

  public void AddExcludedExecutable(string path)
  {
    path = path.Trim();
    if (path.Length == 0 || ExcludedExecutables.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase))) return;
    ExcludedExecutables.Add(path);
    _settings.ExcludedExecutables = ExcludedExecutables.ToList();
    QueueSettingsSave();
  }

  public void RemoveExcludedExecutable(string path)
  {
    ExcludedExecutables.Remove(path);
    _settings.ExcludedExecutables = ExcludedExecutables.ToList();
    QueueSettingsSave();
  }

  public void AddIntegrationClient(string path)
  {
    path = Path.GetFullPath(path.Trim());
    if (AllowedIntegrationClients.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase))) return;
    AllowedIntegrationClients.Add(path);
    _settings.AllowedIntegrationClients = AllowedIntegrationClients.ToList();
    QueueSettingsSave();
  }

  public void RemoveIntegrationClient(string path)
  {
    AllowedIntegrationClients.Remove(path);
    _settings.AllowedIntegrationClients = AllowedIntegrationClients.ToList();
    QueueSettingsSave();
  }

  public void Dispose()
  {
    Statistics?.Dispose();
    _rotationTimer.Dispose();
    _saveDebounce?.Cancel();
    _saveDebounce?.Dispose();
    _packGate.Dispose();
  }

  private async Task LoadPackAndOverridesAsync(SoundPackDefinition pack)
  {
    await _packGate.WaitAsync();
    try
    {
      StatusMessage = _localization.Format("LoadingPackFormat", pack.Name);
      var overrides = await _store.LoadOverridesAsync(pack.Id);
      var groupMappings = await _store.LoadGroupMappingsAsync(pack.Id);
      var customSampleIds = pack.AllSampleIds()
        .Concat(overrides.SelectMany(item => item.SampleIds))
        .Concat(groupMappings.SelectMany(item => item.SampleIds))
        .Where(value => value.StartsWith("custom:", StringComparison.Ordinal) && value.Length > 7)
        .Distinct(StringComparer.Ordinal);
      var customSamplePaths = customSampleIds
        .Select(sampleId => (SampleId: sampleId, Path: Path.Combine(((App)Application.Current).Paths.Sounds, $"{sampleId[7..]}.wav")))
        .Where(item => File.Exists(item.Path))
        .ToDictionary(item => item.SampleId, item => item.Path, StringComparer.Ordinal);
      await _audio.LoadPackAsync(pack, customSamplePaths);
      _overrides.Clear();
      _groupMappings.Clear();
      foreach (var item in overrides)
      {
        _overrides[(item.InputId, item.Variant)] = item;
      }
      foreach (var item in groupMappings)
      {
        _groupMappings[(item.Group, item.Variant, item.DeviceFamily)] = item;
      }
      StatusMessage = _localization.Format("PackActiveFormat", pack.Name);
      LoadMappingEditor();
    }
    finally
    {
      _packGate.Release();
    }
  }

  private void SelectRelativePack(int offset)
  {
    var index = Packs.IndexOf(_activePack);
    SelectedPack = Packs[(index + offset + Packs.Count) % Packs.Count];
  }

  public void AddStatisticsExcludedExecutable(string path)
  {
    path = path.Trim();
    if (path.Length == 0 || StatisticsExcludedExecutables.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase))) return;
    StatisticsExcludedExecutables.Add(path);
    _settings.StatisticsExcludedExecutables = StatisticsExcludedExecutables.ToList();
    StatisticsSettingChanged(nameof(StatisticsExcludedExecutables));
  }

  public void RemoveStatisticsExcludedExecutable(string path)
  {
    StatisticsExcludedExecutables.Remove(path);
    _settings.StatisticsExcludedExecutables = StatisticsExcludedExecutables.ToList();
    StatisticsSettingChanged(nameof(StatisticsExcludedExecutables));
  }

  public async Task ConfirmStatisticsDisclosureAsync(bool keyboardEnabled, bool pointerEnabled)
  {
    _settings.StatisticsDisclosureConfirmed = true;
    _settings.KeyboardStatisticsEnabled = keyboardEnabled;
    _settings.PointerStatisticsEnabled = pointerEnabled;
    _settings.ScrollingStatisticsEnabled = pointerEnabled;
    await _store.SaveSettingsAsync(_settings);
    Notify(nameof(StatisticsDisclosureConfirmed), nameof(KeyboardStatisticsEnabled), nameof(PointerStatisticsEnabled), nameof(ScrollingStatisticsEnabled));
    StatisticsPolicyChanged?.Invoke(this, EventArgs.Empty);
  }

  public void AttachStatistics(StatisticsService service)
  {
    Statistics?.Dispose();
    Statistics = new StatisticsViewModel(service, _localization);
    Notify(nameof(Statistics));
  }

  public void AttachWellness(WellnessService service)
  {
    service.SnapshotChanged += (_, snapshot) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
      WellnessSnapshot = snapshot;
      Notify(nameof(WellnessSnapshot), nameof(WellnessTodaySummary), nameof(WellnessStreakSummary));
    });
  }

  public void AttachProfiles(ProfileTransferService service) => _profiles = service;

  public void SetDistributionMode(DistributionMode mode)
  {
    IsPortable = mode == DistributionMode.Portable;
    if (IsPortable) AvailableUpdate = null;
    Notify(nameof(IsPortable), nameof(HasAvailableUpdate));
  }

  public void HandleDeviceChanged(InputDeviceDescriptor device)
  {
    if (device.Family == DeviceFamily.Keyboard) return;
    Application.Current.Dispatcher.BeginInvoke(() =>
    {
      var existing = PointerDevices.FirstOrDefault(item => item.Id == device.Id);
      if (!device.Connected)
      {
        if (existing is not null) existing.IsConnected = false;
        return;
      }
      if (existing is not null) { existing.IsConnected = true; return; }
      var family = _settings.DeviceClassifications.TryGetValue(device.Id, out var manual) ? manual : device.Family;
      PointerDevices.Add(new(device.Id, family, _localization, selected =>
      {
        _settings.DeviceClassifications[device.Id] = selected;
        StatisticsSettingChanged(nameof(PointerDevices));
      }));
    });
  }

  public Task ExportProfileAsync(string path, ProfileExportOptions options) => (_profiles ?? throw new InvalidOperationException("Profile transfer is unavailable.")).ExportAsync(path, options);
  public Task<bool> ProfileRequiresPasswordAsync(string path) => (_profiles ?? throw new InvalidOperationException("Profile transfer is unavailable.")).RequiresPasswordAsync(path);
  public Task<ProfileImportPreview> PreviewProfileAsync(string path, string? password) => (_profiles ?? throw new InvalidOperationException("Profile transfer is unavailable.")).PreviewAsync(path, password);

  public async Task ImportProfileAsync(string path, string? password, bool useImportedMedia)
  {
    _settings = await (_profiles ?? throw new InvalidOperationException("Profile transfer is unavailable.")).ImportAsync(path, password, useImportedMedia);
    ExcludedExecutables.Clear();
    foreach (var executable in _settings.ExcludedExecutables) ExcludedExecutables.Add(executable);
    StatisticsExcludedExecutables.Clear();
    foreach (var executable in _settings.StatisticsExcludedExecutables) StatisticsExcludedExecutables.Add(executable);
    foreach (var pack in await _packImports.LoadInstalledAsync())
      if (!Packs.Any(existing => existing.Id == pack.Id)) Packs.Add(pack);
    _activePack = Packs.FirstOrDefault(pack => pack.Id == _settings.ActivePackId) ?? Packs.FirstOrDefault(pack => pack.Id == BuiltInCatalog.DefaultPackId) ?? Packs[0];
    RebuildRotationPackOptions();
    await LoadPackAndOverridesAsync(_activePack);
    NotifyAllSettings();
    StatisticsPolicyChanged?.Invoke(this, EventArgs.Empty);
    StatusMessage = _localization.Get("ProfileImported");
  }

  private void RebuildRotationPackOptions()
  {
    RotationPackOptions.Clear();
    foreach (var pack in Packs)
      RotationPackOptions.Add(new RotationPackOption(pack.Id, pack.Name, _settings.PackRotation.SelectedPackIds.Contains(pack.Id, StringComparer.Ordinal), selected => RotationPackSelectionChanged(pack.Id, selected)));
  }

  private void RotationPackSelectionChanged(string packId, bool selected)
  {
    var ids = _settings.PackRotation.SelectedPackIds.ToList();
    if (selected && !ids.Contains(packId, StringComparer.Ordinal)) ids.Add(packId);
    if (!selected) ids.RemoveAll(id => string.Equals(id, packId, StringComparison.Ordinal));
    _settings.PackRotation = _settings.PackRotation with { SelectedPackIds = ids };
    _ = ScheduleRotationAsync(false);
  }

  private async Task ScheduleRotationAsync(bool rotateIfOverdue)
  {
    _rotationTimer.Change(Timeout.Infinite, Timeout.Infinite);
    var policy = _settings.PackRotation;
    if (!policy.Enabled)
    {
      policy.NextDueUtc = null;
      await _store.SaveSettingsAsync(_settings);
      Notify(nameof(NextRotationTime));
      return;
    }
    if (RotationCandidates().Count < 2)
    {
      policy.NextDueUtc = null;
      await _store.SaveSettingsAsync(_settings);
      Notify(nameof(NextRotationTime));
      return;
    }
    if (policy.Interval == PackRotationInterval.WindowsBoot)
    {
      var bootIdentity = (DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).ToUnixTimeSeconds() / 60;
      if (policy.LastWindowsBootTicks != bootIdentity)
      {
        await RotatePackAsync(false);
        policy.LastWindowsBootTicks = bootIdentity;
      }
      policy.NextDueUtc = null;
      await _store.SaveSettingsAsync(_settings);
      Notify(nameof(NextRotationTime));
      return;
    }
    var now = DateTimeOffset.UtcNow;
    if (rotateIfOverdue && policy.NextDueUtc is { } due && due <= now) await RotatePackAsync(false);
    policy.NextDueUtc = policy.NextDueUtc is { } next && next > now ? next : now + RotationDuration(policy);
    var delay = policy.NextDueUtc.Value - now;
    _rotationTimer.Change(delay > TimeSpan.FromMilliseconds(int.MaxValue) ? int.MaxValue : Math.Max(1, (int)delay.TotalMilliseconds), Timeout.Infinite);
    await _store.SaveSettingsAsync(_settings);
    Notify(nameof(NextRotationTime));
  }

  private async Task RotationDueAsync()
  {
    if (!_settings.PackRotation.Enabled) return;
    var now = DateTimeOffset.UtcNow;
    if (_settings.PackRotation.NextDueUtc is { } due && due > now)
    {
      await ScheduleRotationAsync(false);
      return;
    }
    await RotatePackAsync(false);
    _settings.PackRotation.NextDueUtc = now + RotationDuration(_settings.PackRotation);
    await ScheduleRotationAsync(false);
  }

  private async Task RotatePackAsync(bool manual)
  {
    var candidates = RotationCandidates().Where(pack => pack.Id != _activePack.Id).ToArray();
    if (candidates.Length == 0)
    {
      StatusMessage = _localization.Get("RotationNeedsTwoPacks");
      return;
    }
    var selected = candidates[Random.Shared.Next(candidates.Length)];
    _activePack = selected;
    _settings.ActivePackId = selected.Id;
    Notify(nameof(SelectedPack), nameof(ActivePackName), nameof(ActivePackDescription));
    await LoadPackAndOverridesAsync(selected);
    if (manual && _settings.PackRotation.Enabled && _settings.PackRotation.Interval != PackRotationInterval.WindowsBoot)
      _settings.PackRotation.NextDueUtc = DateTimeOffset.UtcNow + RotationDuration(_settings.PackRotation);
    await _store.SaveSettingsAsync(_settings);
    StatusMessage = _localization.Format("RotatedPackFormat", selected.Name);
    Notify(nameof(NextRotationTime));
  }

  private IReadOnlyList<SoundPackDefinition> RotationCandidates() => _settings.PackRotation.PoolMode == PackRotationPoolMode.AllPacks
    ? Packs.ToArray()
    : Packs.Where(pack => _settings.PackRotation.SelectedPackIds.Contains(pack.Id, StringComparer.Ordinal)).ToArray();

  private static TimeSpan RotationDuration(PackRotationPolicy policy) => policy.Interval switch
  {
    PackRotationInterval.OneMinute => TimeSpan.FromMinutes(1),
    PackRotationInterval.TenMinutes => TimeSpan.FromMinutes(10),
    PackRotationInterval.ThirtyMinutes => TimeSpan.FromMinutes(30),
    PackRotationInterval.OneHour => TimeSpan.FromHours(1),
    PackRotationInterval.OneDay => TimeSpan.FromDays(1),
    PackRotationInterval.OneWeek => TimeSpan.FromDays(7),
    _ => TimeSpan.FromMinutes(Math.Clamp(policy.CustomMinutes, 1, 525600))
  };

  private void ExecuteShortcut(string commandId)
  {
    switch (commandId)
    {
      case "show-hide": ShowHideRequested?.Invoke(this, EventArgs.Empty); break;
      case "toggle-sounds": ToggleSoundsCommand.Execute(null); break;
      case "previous-pack": PreviousPackCommand.Execute(null); break;
      case "next-pack": NextPackCommand.Execute(null); break;
    }
  }

  private void PreviewMapping()
  {
    if (_capturedInput is not { } input) return;
    _overrides.TryGetValue((input.Input.StableId, MappingVariant), out var currentInput);
    _groupMappings.TryGetValue((input.Group, MappingVariant, GroupFamily(input)), out var currentGroup);
    var previewOverride = ApplyToGroup ? null : new InputOverride(_activePack.Id, input.Input.StableId, MappingVariant, MappingEnabled, (float)(MappingVolume / 100), currentInput?.SampleIds ?? []);
    var previewGroup = ApplyToGroup
      ? new GroupMapping(_activePack.Id, input.Group, MappingVariant, MappingEnabled, (float)(MappingVolume / 100), currentGroup?.SampleIds ?? [], GroupFamily(input))
      : currentGroup;
    var previewEvent = input with { Variant = MappingVariant };
    var resolved = _resolver.Resolve(_settings, _activePack, previewEvent, previewGroup, previewOverride);
    if (!resolved.Enabled) { StatusMessage = _localization.Get("InputMuted"); return; }
    _audio.TryPlay(new SoundTrigger(_resolver.SelectStable(resolved, $"preview:{_activePack.Id}:{input.Input.StableId}"), resolved.Gain, Stopwatch.GetTimestamp()));
  }

  private async Task SaveMappingAsync()
  {
    if (_capturedInput is not { } input) return;
    if (ApplyToGroup)
    {
      var key = (input.Group, MappingVariant, GroupFamily(input));
      _groupMappings.TryGetValue(key, out var old);
      var value = new GroupMapping(_activePack.Id, input.Group, MappingVariant, MappingEnabled, (float)(MappingVolume / 100), old?.SampleIds ?? [], key.Item3);
      _groupMappings[key] = value;
      await _store.SaveGroupMappingAsync(value);
      MappingSound = _localization.Get(value.SampleIds.Count > 0 ? "CustomSample" : "BuiltInSoundPool");
      StatusMessage = _localization.Format("SavedGroupOverrideFormat", MappingScopeLabel, _localization.EnumName(MappingVariant));
    }
    else
    {
      _overrides.TryGetValue((input.Input.StableId, MappingVariant), out var old);
      var value = new InputOverride(_activePack.Id, input.Input.StableId, MappingVariant, MappingEnabled, (float)(MappingVolume / 100), old?.SampleIds ?? []);
      _overrides[(value.InputId, value.Variant)] = value;
      await _store.SaveOverrideAsync(value);
      MappingSound = _localization.Get(value.SampleIds.Count > 0 ? "CustomSample" : "BuiltInSoundPool");
      StatusMessage = _localization.Format("SavedOverrideFormat", CapturedInputLabel, _localization.EnumName(MappingVariant));
    }
  }

  private async Task RestoreMappingAsync()
  {
    if (_capturedInput is not { } input) return;
    if (ApplyToGroup)
    {
      var family = GroupFamily(input);
      await _store.RemoveGroupMappingAsync(_activePack.Id, input.Group, MappingVariant, family);
      _groupMappings.Remove((input.Group, MappingVariant, family));
    }
    else
    {
      await _store.RemoveOverrideAsync(_activePack.Id, input.Input.StableId, MappingVariant);
      _overrides.Remove((input.Input.StableId, MappingVariant));
    }
    LoadMappingEditor();
    StatusMessage = _localization.Get("BuiltInRestored");
  }

  private void LoadMappingEditor()
  {
    if (_capturedInput is not { } input) return;
    var enabled = true;
    float? volume = null;
    IReadOnlyList<string>? samples = null;
    var found = ApplyToGroup
      ? _groupMappings.TryGetValue((input.Group, MappingVariant, GroupFamily(input)), out var group) && ReadGroup(group, out enabled, out volume, out samples)
      : _overrides.TryGetValue((input.Input.StableId, MappingVariant), out var exact) && ReadInput(exact, out enabled, out volume, out samples);
    if (found)
    {
      MappingEnabled = enabled;
      MappingVolume = (volume ?? 1) * 100;
      MappingSound = _localization.Get(samples!.Count > 0 ? "CustomSample" : "BuiltInSoundPool");
    }
    else
    {
      MappingEnabled = true;
      MappingVolume = 100;
      MappingSound = _localization.Get("BuiltInSoundPool");
    }
    Notify(nameof(MappingScopeLabel));
  }

  private static DeviceFamily? GroupFamily(InputActionEvent input) => input.Input.Kind == InputKind.KeyboardKey ? null : input.Input.DeviceFamily;
  private static bool ReadGroup(GroupMapping? value, out bool enabled, out float? volume, out IReadOnlyList<string>? samples)
  {
    enabled = value?.Enabled ?? true; volume = value?.Volume; samples = value?.SampleIds; return value is not null;
  }
  private static bool ReadInput(InputOverride? value, out bool enabled, out float? volume, out IReadOnlyList<string>? samples)
  {
    enabled = value?.Enabled ?? true; volume = value?.Volume; samples = value?.SampleIds; return value is not null;
  }

  private async Task CreateBackupAsync()
  {
    try
    {
      var path = await CreateBackupNowAsync();
      StatusMessage = _localization.Format("BackupCreatedFormat", path);
    }
    catch (Exception exception) { StatusMessage = _localization.Format("BackupFailedFormat", exception.Message); }
  }

  private async Task CheckUpdatesAsync()
  {
    try
    {
      await CheckForUpdateAsync();
    }
    catch (Exception exception) { StatusMessage = _localization.Format("UpdateCheckFailedFormat", exception.Message); }
  }

  private async Task ChangeOutputAsync(string outputDeviceId)
  {
    try
    {
      await _audio.ChangeOutputDeviceAsync(outputDeviceId);
      StatusMessage = _localization.Get("AudioOutputChanged");
    }
    catch (Exception exception)
    {
      _settings.OutputDeviceId = "default";
      Notify(nameof(OutputDeviceId));
      await _audio.ChangeOutputDeviceAsync("default");
      StatusMessage = _localization.Format("OutputUnavailableFormat", exception.Message);
    }
  }

  private void QueueSettingsSave()
  {
    CancellationTokenSource cancellation;
    lock (_settingsGate)
    {
      _saveDebounce?.Cancel();
      _saveDebounce?.Dispose();
      _saveDebounce = new CancellationTokenSource();
      cancellation = _saveDebounce;
    }
    _ = SaveAfterDelayAsync(cancellation.Token);
  }

  private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
  {
    try
    {
      await Task.Delay(350, cancellationToken);
      await _store.SaveSettingsAsync(_settings, cancellationToken);
    }
    catch (OperationCanceledException) { }
    catch (Exception exception) { StatusMessage = _localization.Format("SettingsSaveFailedFormat", exception.Message); }
  }

  private Task SaveSettingsNowAsync() => _store.SaveSettingsAsync(_settings);

  private void RefreshLocalizedContent()
  {
    var activePackId = _activePack.Id;
    var customPacks = Packs.Where(pack => pack.IsCustom).ToArray();
    var localizedPacks = BuiltInCatalog.Packs.Select(_localization.LocalizePack).Concat(customPacks).ToArray();
    Packs.Clear();
    foreach (var pack in localizedPacks) Packs.Add(pack);
    _activePack = Packs.First(pack => pack.Id == activePackId);

    for (var index = 0; index < OutputDevices.Count; index++)
      OutputDevices[index] = LocalizeOutputDevice(OutputDevices[index]);

    var selectedCommandId = SelectedShortcut?.CommandId;
    for (var index = 0; index < Shortcuts.Count; index++) Shortcuts[index] = LocalizeShortcut(Shortcuts[index]);
    SelectedShortcut = Shortcuts.FirstOrDefault(item => item.CommandId == selectedCommandId) ?? Shortcuts.FirstOrDefault();

    CapturedInputLabel = _capturedInput is { } input ? DisplayInput(input) : _localization.Get("NoInputSelected");
    if (_capturedInput is not null) LoadMappingEditor();
    else MappingSound = _localization.Get("BuiltInSoundPool");
    Statistics?.RefreshLocalization();

    Notify(
      nameof(DisplayLanguage), nameof(DisplayLanguageIndex), nameof(DisplayLanguages), nameof(Theme), nameof(ThemeIndex),
      nameof(ThemeModes), nameof(MappingVariant), nameof(MappingVariantIndex), nameof(MappingVariants),
      nameof(SelectedPack), nameof(ActivePackName), nameof(ActivePackDescription), nameof(SoundStateText),
      nameof(SoundStateDescription), nameof(VersionText), nameof(AvailableUpdateText), nameof(CapturedInputLabel), nameof(CapturedInputDetail),
      nameof(MappingScopeLabel), nameof(OutputDevices));
  }

  private IReadOnlyList<string> Options<T>() where T : struct, Enum =>
    Enum.GetValues<T>().Select(value => _localization.EnumName(value)).ToArray();

  private ShortcutBinding LocalizeShortcut(ShortcutBinding binding) => binding with { Name = _localization.ShortcutName(binding) };

  private AudioOutputDevice LocalizeOutputDevice(AudioOutputDevice device) => device.Id == "default"
    ? device with { Name = _localization.Get("SystemDefault") }
    : device;

  private void NotifyAllSettings() => Notify(
    nameof(Settings), nameof(DisplayName), nameof(AppTitle), nameof(SoundsEnabled), nameof(SoundStateText), nameof(SoundStateDescription),
    nameof(KeyboardEnabled), nameof(PointerEnabled), nameof(WheelEnabled), nameof(ResultSoundsEnabled), nameof(LaunchAtStartup),
    nameof(StartMinimized), nameof(CloseToTray), nameof(PauseInFullscreen), nameof(ReducedMotion), nameof(IntegrationApiEnabled),
      nameof(NormalizeImports), nameof(Theme), nameof(ThemeIndex), nameof(ThemeModes), nameof(DisplayLanguage),
      nameof(KeyboardStatisticsEnabled), nameof(PointerStatisticsEnabled), nameof(ScrollingStatisticsEnabled),
      nameof(WellnessEnabled),
      nameof(BreakReminderEnabled), nameof(BreakReminderActiveMinutes), nameof(BreakReminderRestMinutes),
      nameof(KeyboardGoalEnabled), nameof(PointerGoalEnabled), nameof(ActiveMinutesGoalEnabled),
      nameof(KeyboardDailyGoal), nameof(PointerDailyGoal), nameof(ActiveMinutesDailyGoal), nameof(WellnessTodaySummary), nameof(WellnessStreakSummary),
      nameof(KeyboardSoundTiming), nameof(KeyboardSoundTimingIndex), nameof(KeyboardSoundTimings),
      nameof(RotationEnabled), nameof(RotationIntervalIndex), nameof(RotationIntervals), nameof(RotationCustomVisible),
      nameof(RotationCustomMinutes), nameof(RotationPoolModeIndex), nameof(RotationPoolModes), nameof(RotationSelectedPoolVisible), nameof(NextRotationTime),
    nameof(DisplayLanguageIndex), nameof(DisplayLanguages),
    nameof(OutputDeviceId), nameof(MasterVolume), nameof(MasterVolumeLabel),
    nameof(KeyboardVolume), nameof(KeyboardVolumeLabel), nameof(PointerVolume), nameof(PointerVolumeLabel),
    nameof(ResultVolume), nameof(ResultVolumeLabel), nameof(SequenceTimeoutMs), nameof(SelectedPack), nameof(ActivePackName), nameof(ActivePackDescription));

  private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
  {
    if (EqualityComparer<T>.Default.Equals(field, value)) return false;
    field = value;
    Notify(property!);
    return true;
  }

  private void SettingChanged(string property)
  {
    Notify(property);
    QueueSettingsSave();
  }

  private void StatisticsSettingChanged(string property)
  {
    Notify(property);
    QueueSettingsSave();
    StatisticsPolicyChanged?.Invoke(this, EventArgs.Empty);
  }

  private void Notify(params string[] properties)
  {
    foreach (var property in properties) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
  }

  private string DisplayInput(InputActionEvent input) => input.Input.Kind == InputKind.KeyboardKey
    ? _localization.KeyName(input.VirtualKey)
    : input.Input.Kind == InputKind.Wheel
      ? _localization.Get(input.Input.Code switch { 6 => "WheelUp", 7 => "WheelDown", 8 => "WheelLeft", _ => "WheelRight" })
      : _localization.Get(input.Input.Code switch { 1 => "PrimaryButton", 2 => "SecondaryButton", 3 => "MiddleButton", 4 => "X1Button", _ => "X2Button" });

  private static string GetVersion() => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

internal sealed class DelegateCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
  public event EventHandler? CanExecuteChanged;
  public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
  public void Execute(object? parameter) => execute();
  public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RotationPackOption : INotifyPropertyChanged
{
  private readonly Action<bool> _changed;
  private bool _isSelected;

  public RotationPackOption(string id, string name, bool isSelected, Action<bool> changed)
  {
    Id = id;
    Name = name;
    _isSelected = isSelected;
    _changed = changed;
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  public string Id { get; }
  public string Name { get; }
  public bool IsSelected
  {
    get => _isSelected;
    set
    {
      if (_isSelected == value) return;
      _isSelected = value;
      PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
      _changed(value);
    }
  }
}

public sealed class DeviceClassificationOption : INotifyPropertyChanged
{
  private static readonly DeviceFamily[] Families = [DeviceFamily.ExternalMouse, DeviceFamily.Trackpad, DeviceFamily.UnknownPointer];
  private readonly Action<DeviceFamily> _changed;
  private readonly LocalizationService _localization;
  private DeviceFamily _family;
  private bool _isConnected = true;

  public DeviceClassificationOption(string id, DeviceFamily family, LocalizationService localization, Action<DeviceFamily> changed)
  {
    Id = id;
    _family = Families.Contains(family) ? family : DeviceFamily.UnknownPointer;
    _localization = localization;
    _changed = changed;
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  public string Id { get; }
  public string DisplayName => $"{_localization.EnumName(_family)} · {Id[..Math.Min(8, Id.Length)]}";
  public IReadOnlyList<string> FamilyOptions => Families.Select(family => _localization.EnumName(family)).ToArray();
  public int FamilyIndex
  {
    get => Array.IndexOf(Families, _family);
    set
    {
      if (value < 0 || value >= Families.Length || Families[value] == _family) return;
      _family = Families[value];
      PropertyChanged?.Invoke(this, new(nameof(FamilyIndex)));
      PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
      _changed(_family);
    }
  }
  public bool IsConnected
  {
    get => _isConnected;
    set { if (_isConnected == value) return; _isConnected = value; PropertyChanged?.Invoke(this, new(nameof(IsConnected))); PropertyChanged?.Invoke(this, new(nameof(ConnectionStatus))); }
  }
  public string ConnectionStatus => _localization.Get(IsConnected ? "DeviceConnected" : "DeviceDisconnected");
}

internal sealed class AsyncDelegateCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
  private bool _running;
  public event EventHandler? CanExecuteChanged;
  public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
  public async void Execute(object? parameter)
  {
    if (!CanExecute(parameter)) return;
    _running = true;
    CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    try { await execute(); }
    finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
  }
}
