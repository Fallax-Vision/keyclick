using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;

namespace KeyClick.App;

public sealed class PointerStudioViewModel : INotifyPropertyChanged, IDisposable
{
  private readonly AppSettings _appSettings;
  private readonly PointerAppearanceService _appearance;
  private readonly PointerEffectsService _effects;
  private readonly PointerSuppressionService _suppression;
  private readonly PointerActionService _actions;
  private readonly LocalizationService _localization;
  private readonly Action _save;
  private readonly Func<Task> _refreshDevices;
  private readonly SynchronizationContext? _uiContext;
  private string _status = string.Empty;
  private string? _preparedCursorPath;
  private bool _reducedMotion;

  public PointerStudioViewModel(
    AppSettings appSettings,
    PointerAppearanceService appearance,
    PointerEffectsService effects,
    PointerSuppressionService suppression,
    PointerActionService actions,
    LocalizationService localization,
    Action save,
    Func<Task> refreshDevices)
  {
    _appSettings = appSettings;
    _appSettings.PointerStudio.Normalize();
    _appearance = appearance;
    _effects = effects;
    _suppression = suppression;
    _actions = actions;
    _localization = localization;
    _save = save;
    _refreshDevices = refreshDevices;
    _uiContext = SynchronizationContext.Current;
    Catalog = PointerStudioCatalogLoader.Load();
    Themes = new(Catalog.Themes.Select(theme => new PointerThemeOption(theme, localization, id => _appSettings.PointerStudio.FavoriteThemeIds.Contains(id, StringComparer.Ordinal))));
    RolePreviews = new(Catalog.Roles.Select(role => new PointerRoleOption(role, RoleNameKey(role), localization)));
    LeftClick = new(_appSettings.PointerStudio.LeftClick, "PointerLeftClick", localization, () => Changed());
    RightClick = new(_appSettings.PointerStudio.RightClick, "PointerRightClick", localization, () => Changed());
    MiddleClick = new(_appSettings.PointerStudio.MiddleClick, "PointerMiddleClick", localization, () => Changed());
    AuxiliaryClick = new(_appSettings.PointerStudio.AuxiliaryClick, "PointerAuxiliaryClick", localization, () => Changed());
    ClickChannels = [LeftClick, RightClick, MiddleClick, AuxiliaryClick];
    ApplyCommand = new AsyncDelegateCommand(ApplyAsync);
    RestorePreviousCommand = new AsyncDelegateCommand(RestorePreviousAsync);
    RestoreDefaultsCommand = new AsyncDelegateCommand(RestoreDefaultsAsync);
    ToggleFavoriteCommand = new DelegateCommand(ToggleFavorite);
    FindPointerCommand = new DelegateCommand(_effects.FindPointer);
    RefreshDevicesCommand = new AsyncDelegateCommand(_refreshDevices);
    OpenMouseSettingsCommand = new DelegateCommand(() => OpenSettings("ms-settings:mousetouchpad"));
    OpenTouchpadSettingsCommand = new DelegateCommand(() => OpenSettings("ms-settings:devices-touchpad"));
    PanicCommand = new DelegateCommand(Panic);
    _suppression.ActionRequested += ExecuteAction;
    _suppression.PanicTriggered += Panic;
    _effects.HealthChanged += EffectsHealthChanged;
    var recovered = _appearance.RecoverExperimentalIfNeeded(Settings);
    if (recovered || Settings.ExperimentalReplacementEnabled || Settings.ExperimentalSuppressionEnabled)
    {
      Settings.DeactivateExperimentalModes();
      _save();
      if (recovered) Status = _localization.Get("PointerRecoveredStatus");
    }
    Reconfigure();
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  public event Action<PointerActionKind>? KeyClickActionRequested;
  public event Action<string?, PointerThemeScope>? AppCursorChanged;
  public PointerStudioCatalog Catalog { get; }
  public ObservableCollection<PointerThemeOption> Themes { get; }
  public ObservableCollection<PointerRoleOption> RolePreviews { get; }
  public PointerStudioSettings Settings => _appSettings.PointerStudio;
  public ClickIndicatorOption LeftClick { get; }
  public ClickIndicatorOption RightClick { get; }
  public ClickIndicatorOption MiddleClick { get; }
  public ClickIndicatorOption AuxiliaryClick { get; }
  public IReadOnlyList<ClickIndicatorOption> ClickChannels { get; }
  public ICommand ApplyCommand { get; }
  public ICommand RestorePreviousCommand { get; }
  public ICommand RestoreDefaultsCommand { get; }
  public ICommand ToggleFavoriteCommand { get; }
  public ICommand FindPointerCommand { get; }
  public ICommand RefreshDevicesCommand { get; }
  public ICommand OpenMouseSettingsCommand { get; }
  public ICommand OpenTouchpadSettingsCommand { get; }
  public ICommand PanicCommand { get; }
  public IReadOnlyList<string> ScopeOptions => Options<PointerThemeScope>();
  public IReadOnlyList<string> SizeOptions => Options<PointerCursorSize>();
  public IReadOnlyList<string> VariantOptions => Options<PointerThemeVariant>();
  public IReadOnlyList<string> MotionModeOptions => Options<PointerMotionMode>();
  public IReadOnlyList<string> MotionPresetOptions => Options<PointerMotionPreset>();
  public IReadOnlyList<string> ActionOptions => Options<PointerActionKind>();
  public PointerThemeOption SelectedTheme => Themes[Math.Clamp(SelectedThemeIndex, 0, Themes.Count - 1)];
  public string SelectedThemeDescription => SelectedTheme.Description;
  public string FavoriteButtonText => Settings.FavoriteThemeIds.Contains(SelectedTheme.Definition.Id, StringComparer.Ordinal)
    ? _localization.Get("PointerRemoveFavorite") : _localization.Get("PointerAddFavorite");
  public string ExperimentalStatus => Settings.ExperimentalReplacementEnabled || Settings.ExperimentalSuppressionEnabled
    ? _localization.Get("PointerExperimentalActive") : _localization.Get("PointerExperimentalOff");
  public string PerformanceStatus => _effects.IsRunning ? _localization.Get("PointerPerformanceActive") : _localization.Get("PointerPerformanceSleeping");
  public string Status { get => _status; private set { if (_status == value) return; _status = value; Notify(); } }

  public int SelectedThemeIndex
  {
    get { var index = Themes.ToList().FindIndex(item => item.Definition.Id == Settings.ThemeId); return index < 0 ? 0 : index; }
    set
    {
      if (value < 0 || value >= Themes.Count || Themes[value].Definition.Id == Settings.ThemeId) return;
      Settings.ThemeId = Themes[value].Definition.Id;
      Changed(nameof(SelectedThemeIndex), nameof(SelectedTheme), nameof(SelectedThemeDescription), nameof(FavoriteButtonText));
    }
  }
  public int ScopeIndex { get => (int)Settings.Scope; set { if (Enum.IsDefined(typeof(PointerThemeScope), value)) { Settings.Scope = (PointerThemeScope)value; Changed(); } } }
  public int SizeIndex { get => (int)Settings.Size; set { if (Enum.IsDefined(typeof(PointerCursorSize), value)) { Settings.Size = (PointerCursorSize)value; Changed(); } } }
  public int VariantIndex { get => (int)Settings.Variant; set { if (Enum.IsDefined(typeof(PointerThemeVariant), value)) { Settings.Variant = (PointerThemeVariant)value; Changed(); } } }
  public int MotionModeIndex { get => (int)Settings.MotionMode; set { if (Enum.IsDefined(typeof(PointerMotionMode), value)) { Settings.MotionMode = (PointerMotionMode)value; Changed(); } } }
  public int MotionPresetIndex { get => (int)Settings.MotionPreset; set { if (Enum.IsDefined(typeof(PointerMotionPreset), value)) { Settings.MotionPreset = (PointerMotionPreset)value; Changed(); } } }
  public bool Enabled { get => Settings.Enabled; set { Settings.Enabled = value; Changed(); } }
  public bool EnhancePointerPrecision { get => Settings.EnhancePointerPrecision; set { Settings.EnhancePointerPrecision = value; Changed(); } }
  public bool NativeShadow { get => Settings.NativeShadow; set { Settings.NativeShadow = value; Changed(); } }
  public bool MotionEffectsEnabled { get => Settings.MotionEffectsEnabled; set { Settings.MotionEffectsEnabled = value; Changed(); } }
  public bool ClickIndicatorsEnabled { get => Settings.ClickIndicatorsEnabled; set { Settings.ClickIndicatorsEnabled = value; Changed(); } }
  public bool AdaptivePerformance { get => Settings.AdaptivePerformance; set { Settings.AdaptivePerformance = value; Changed(); } }
  public bool PauseOnBatterySaver { get => Settings.PauseOnBatterySaver; set { Settings.PauseOnBatterySaver = value; Changed(); } }
  public bool PauseInFullscreen { get => Settings.PauseInFullscreen; set { Settings.PauseInFullscreen = value; Changed(); } }
  public bool PauseInRemoteSession { get => Settings.PauseInRemoteSession; set { Settings.PauseInRemoteSession = value; Changed(); } }
  public bool ShakeToEnlarge { get => Settings.ShakeToEnlarge; set { Settings.ShakeToEnlarge = value; Changed(); } }
  public bool ExperimentalReplacementEnabled { get => Settings.ExperimentalReplacementEnabled; set { Settings.ExperimentalReplacementEnabled = value; if (!value && Settings.MotionMode == PointerMotionMode.FullReplacement) Settings.MotionMode = PointerMotionMode.Companion; Changed(nameof(ExperimentalReplacementEnabled), nameof(MotionModeIndex), nameof(ExperimentalStatus), nameof(HasExperimentalControls)); } }
  public bool ExperimentalSuppressionEnabled { get => Settings.ExperimentalSuppressionEnabled; set { Settings.ExperimentalSuppressionEnabled = value; Changed(nameof(ExperimentalSuppressionEnabled), nameof(ExperimentalStatus), nameof(HasExperimentalControls)); } }
  public bool HasExperimentalControls => Settings.ExperimentalReplacementEnabled || Settings.ExperimentalSuppressionEnabled;
  public int GlobalLeftActionIndex { get => (int)GetGlobalBinding(PointerButtonKind.Left); set => SetGlobalBinding(PointerButtonKind.Left, value); }
  public int GlobalRightActionIndex { get => (int)GetGlobalBinding(PointerButtonKind.Right); set => SetGlobalBinding(PointerButtonKind.Right, value); }
  public int GlobalMiddleActionIndex { get => (int)GetGlobalBinding(PointerButtonKind.Middle); set => SetGlobalBinding(PointerButtonKind.Middle, value); }
  public int GlobalX1ActionIndex { get => (int)GetGlobalBinding(PointerButtonKind.X1); set => SetGlobalBinding(PointerButtonKind.X1, value); }
  public int GlobalX2ActionIndex { get => (int)GetGlobalBinding(PointerButtonKind.X2); set => SetGlobalBinding(PointerButtonKind.X2, value); }
  public double WindowsPointerSpeed { get => Settings.WindowsPointerSpeed; set { Settings.WindowsPointerSpeed = Math.Clamp((int)Math.Round(value), 1, 20); Changed(); } }
  public double PointerTrails { get => Settings.PointerTrails; set { Settings.PointerTrails = Math.Clamp((int)Math.Round(value), 0, 16); Changed(); } }
  public double VisualScale { get => Settings.VisualScale; set { Settings.VisualScale = Math.Clamp(value, 0.5, 3); Changed(); } }
  public double Smoothing { get => Settings.Smoothing; set { Settings.Smoothing = Math.Clamp(value, 0, 1); Changed(); } }
  public double SpringStrength { get => Settings.SpringStrength; set { Settings.SpringStrength = Math.Clamp(value, 0.05, 1); Changed(); } }
  public double Damping { get => Settings.Damping; set { Settings.Damping = Math.Clamp(value, 0.05, 1); Changed(); } }
  public double TrailLength { get => Settings.TrailLength; set { Settings.TrailLength = Math.Clamp((int)Math.Round(value), 0, 24); Changed(); } }
  public double ShakeSensitivity { get => Settings.ShakeSensitivity; set { Settings.ShakeSensitivity = Math.Clamp(value, 0.1, 1); Changed(); } }
  public double ShakeScale { get => Settings.ShakeScale; set { Settings.ShakeScale = Math.Clamp(value, 1.1, 4); Changed(); } }
  public double ShadowOpacity { get => Settings.ShadowOpacity; set { Settings.ShadowOpacity = Math.Clamp(value, 0, 1); Changed(); } }
  public double ShadowSoftness { get => Settings.ShadowSoftness; set { Settings.ShadowSoftness = Math.Clamp(value, 0, 30); Changed(); } }

  public void SetReducedMotion(bool value) { if (_reducedMotion == value) return; _reducedMotion = value; Reconfigure(); }
  public void HandleMovement(PointerMovementSignal signal) => _effects.SignalMovement(signal);

  public async Task OnPageOpenedAsync()
  {
    await _refreshDevices();
    if (!Settings.Enabled || Settings.Scope != PointerThemeScope.SystemWide || _appearance.OwnsSystemTheme(Settings.ThemeId)) return;
    Settings.Enabled = false;
    _appearance.ClearExperimentalMarker();
    Reconfigure();
    _save();
    Status = _localization.Get("PointerExternalSchemeDetected");
    Notify(nameof(Enabled));
  }

  public void HandleInput(InputActionEvent input)
  {
    if (input.Input.Kind == InputKind.PointerButton && input.Phase == InputPhase.Up)
      _effects.SignalClick(input.Input.Code);
    if (input.Phase is not (InputPhase.Up or InputPhase.WheelDetent) || !TryButton(input, out var button)) return;
    var binding = Settings.ButtonBindings.FirstOrDefault(item => item.DeviceId == input.Input.DeviceId && item.Button == button)
      ?? Settings.ButtonBindings.FirstOrDefault(item => item.DeviceId == "*" && item.Button == button && !item.SuppressOriginal);
    if (binding is not null && binding.Action != PointerActionKind.None) ExecuteAction(binding.Action);
  }

  public void SetBinding(string deviceId, PointerButtonKind button, PointerActionKind action, bool suppress)
  {
    Settings.ButtonBindings.RemoveAll(binding => binding.DeviceId == deviceId && binding.Button == button);
    if (action != PointerActionKind.None) Settings.ButtonBindings.Add(new() { DeviceId = deviceId, Button = button, Action = action, SuppressOriginal = suppress && deviceId == "*" });
    Changed();
  }

  public PointerActionKind GetBinding(string deviceId, PointerButtonKind button) =>
    Settings.ButtonBindings.FirstOrDefault(binding => binding.DeviceId == deviceId && binding.Button == button)?.Action ?? PointerActionKind.None;

  private PointerActionKind GetGlobalBinding(PointerButtonKind button) =>
    Settings.ButtonBindings.FirstOrDefault(binding => binding.DeviceId == "*" && binding.Button == button && binding.SuppressOriginal)?.Action ?? PointerActionKind.None;

  private void SetGlobalBinding(PointerButtonKind button, int value)
  {
    if (!Enum.IsDefined(typeof(PointerActionKind), value)) return;
    SetBinding("*", button, (PointerActionKind)value, true);
    NotifyMany(nameof(GlobalLeftActionIndex), nameof(GlobalRightActionIndex), nameof(GlobalMiddleActionIndex), nameof(GlobalX1ActionIndex), nameof(GlobalX2ActionIndex));
  }

  public void RefreshLocalization()
  {
    foreach (var theme in Themes) theme.RefreshLocalization();
    foreach (var role in RolePreviews) role.RefreshLocalization();
    foreach (var channel in ClickChannels) channel.RefreshLocalization();
    NotifyMany(nameof(ScopeOptions), nameof(SizeOptions), nameof(VariantOptions), nameof(MotionModeOptions), nameof(MotionPresetOptions), nameof(ActionOptions), nameof(SelectedThemeDescription), nameof(FavoriteButtonText), nameof(ExperimentalStatus), nameof(PerformanceStatus));
  }

  private async Task ApplyAsync()
  {
    Settings.Enabled = true;
    Settings.Normalize();
    PointerApplyResult result;
    if (Settings.Scope == PointerThemeScope.SystemWide)
    {
      result = await Task.Run(() => _appearance.ApplyTheme(SelectedTheme.Definition, Settings));
      AppCursorChanged?.Invoke(null, Settings.Scope);
    }
    else
    {
      result = await Task.Run(() => _appearance.PrepareTheme(SelectedTheme.Definition, Settings));
      _preparedCursorPath = result.CursorPath;
      if (result.Success) AppCursorChanged?.Invoke(result.CursorPath, Settings.Scope);
    }
    if (Settings.ExperimentalReplacementEnabled || Settings.ExperimentalSuppressionEnabled) _appearance.MarkExperimentalActive();
    else _appearance.ClearExperimentalMarker();
    Reconfigure();
    _save();
    Status = result.Success ? _localization.Get("PointerAppliedStatus") : _localization.Format("PointerApplyFailedFormat", result.Error ?? "Unknown error");
    NotifyMany(nameof(Enabled), nameof(PerformanceStatus));
  }

  private async Task RestorePreviousAsync()
  {
    var result = await Task.Run(() => _appearance.RestorePrevious(Settings));
    AppCursorChanged?.Invoke(null, PointerThemeScope.SystemWide);
    Status = result.Success ? _localization.Get("PointerRestoredStatus") : _localization.Format("PointerApplyFailedFormat", result.Error ?? "Unknown error");
  }

  private async Task RestoreDefaultsAsync()
  {
    var result = await Task.Run(_appearance.RestoreWindowsDefaults);
    AppCursorChanged?.Invoke(null, PointerThemeScope.SystemWide);
    Status = result.Success ? _localization.Get("PointerDefaultsRestoredStatus") : _localization.Format("PointerApplyFailedFormat", result.Error ?? "Unknown error");
  }

  private void ToggleFavorite()
  {
    var id = SelectedTheme.Definition.Id;
    if (Settings.FavoriteThemeIds.Contains(id, StringComparer.Ordinal)) Settings.FavoriteThemeIds.RemoveAll(item => item == id);
    else if (Settings.FavoriteThemeIds.Count < 10) Settings.FavoriteThemeIds.Add(id);
    foreach (var theme in Themes) theme.RefreshFavorite();
    Changed(nameof(FavoriteButtonText));
  }

  public void Panic()
  {
    Settings.ExperimentalReplacementEnabled = false;
    Settings.ExperimentalSuppressionEnabled = false;
    Settings.MotionMode = PointerMotionMode.Companion;
    _appearance.ClearExperimentalMarker();
    Reconfigure();
    _save();
    Status = _localization.Get("PointerPanicStatus");
    NotifyMany(nameof(ExperimentalReplacementEnabled), nameof(ExperimentalSuppressionEnabled), nameof(MotionModeIndex), nameof(ExperimentalStatus), nameof(HasExperimentalControls), nameof(GlobalLeftActionIndex), nameof(GlobalRightActionIndex), nameof(GlobalMiddleActionIndex), nameof(GlobalX1ActionIndex), nameof(GlobalX2ActionIndex));
  }

  private void Changed([CallerMemberName] string? property = null, params string[] additional)
  {
    Settings.Normalize();
    Notify(property!);
    foreach (var name in additional) Notify(name);
    _save();
    Reconfigure();
  }

  private void Reconfigure()
  {
    _effects.Configure(Settings, SelectedTheme.Definition, _reducedMotion);
    if (!_suppression.Configure(Settings) && Settings.ExperimentalSuppressionEnabled)
    {
      Settings.ExperimentalSuppressionEnabled = false;
      Settings.ButtonBindings.RemoveAll(binding => binding.SuppressOriginal);
      Status = _localization.Get("PointerPanicHotkeyUnavailable");
      NotifyMany(nameof(ExperimentalSuppressionEnabled), nameof(ExperimentalStatus), nameof(HasExperimentalControls));
      _save();
    }
    Notify(nameof(PerformanceStatus));
  }

  private void ExecuteAction(PointerActionKind action)
  {
    if (_actions.Execute(action)) return;
    KeyClickActionRequested?.Invoke(action);
  }

  private static bool TryButton(InputActionEvent input, out PointerButtonKind button)
  {
    button = input.Input.Kind == InputKind.Wheel ? input.Input.Code switch
    {
      6 => PointerButtonKind.WheelUp, 7 => PointerButtonKind.WheelDown, 8 => PointerButtonKind.WheelLeft, _ => PointerButtonKind.WheelRight
    } : input.Input.Code switch { 3 => PointerButtonKind.Middle, 4 => PointerButtonKind.X1, _ => PointerButtonKind.X2 };
    return input.Input.Kind == InputKind.Wheel || input.Input.Kind == InputKind.PointerButton && input.Input.Code is 3 or 4 or 5;
  }

  private IReadOnlyList<string> Options<T>() where T : struct, Enum => Enum.GetValues<T>().Select(value => _localization.EnumName(value)).ToArray();
  private static string RoleNameKey(string role) => role switch
  {
    "Arrow" => "PointerRoleArrow",
    "Help" => "PointerRoleHelp",
    "AppStarting" => "PointerRoleWorking",
    "Wait" => "PointerRoleBusy",
    "Crosshair" => "PointerRolePrecision",
    "IBeam" => "PointerRoleText",
    "NWPen" => "PointerRoleHandwriting",
    "No" => "PointerRoleUnavailable",
    "SizeNS" => "PointerRoleResizeVertical",
    "SizeWE" => "PointerRoleResizeHorizontal",
    "SizeNWSE" => "PointerRoleResizeDiagonalDown",
    "SizeNESW" => "PointerRoleResizeDiagonalUp",
    "SizeAll" => "PointerRoleMove",
    "UpArrow" => "PointerRoleAlternate",
    _ => "PointerRoleLink"
  };
  private static void OpenSettings(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
  private void EffectsHealthChanged(string message)
  {
    if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext)) Status = message;
    else _uiContext.Post(_ => Status = message, null);
  }
  private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
  private void NotifyMany(params string[] properties) { foreach (var property in properties) PropertyChanged?.Invoke(this, new(property)); }

  public void Dispose()
  {
    _suppression.ActionRequested -= ExecuteAction;
    _suppression.PanicTriggered -= Panic;
    _effects.HealthChanged -= EffectsHealthChanged;
  }
}

public sealed class PointerThemeOption(PointerThemeDefinition definition, LocalizationService localization, Func<string, bool> isFavorite) : INotifyPropertyChanged
{
  public event PropertyChangedEventHandler? PropertyChanged;
  public PointerThemeDefinition Definition { get; } = definition;
  public string Name => localization.Get(Definition.NameKey);
  public string Description => localization.Get(Definition.DescriptionKey);
  public string FavoriteMark => isFavorite(Definition.Id) ? "★" : string.Empty;
  public void RefreshLocalization() { PropertyChanged?.Invoke(this, new(nameof(Name))); PropertyChanged?.Invoke(this, new(nameof(Description))); }
  public void RefreshFavorite() => PropertyChanged?.Invoke(this, new(nameof(FavoriteMark)));
}

public sealed class PointerRoleOption(string role, string nameKey, LocalizationService localization) : INotifyPropertyChanged
{
  public event PropertyChangedEventHandler? PropertyChanged;
  public string Role { get; } = role;
  public string Name => localization.Get(nameKey);
  public void RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Name)));
}

public sealed class ClickIndicatorOption : INotifyPropertyChanged
{
  private readonly PointerClickIndicatorSettings _settings;
  private readonly string _titleKey;
  private readonly LocalizationService _localization;
  private readonly Action _changed;
  public ClickIndicatorOption(PointerClickIndicatorSettings settings, string titleKey, LocalizationService localization, Action changed) { _settings = settings; _titleKey = titleKey; _localization = localization; _changed = changed; }
  public event PropertyChangedEventHandler? PropertyChanged;
  public string Title => _localization.Get(_titleKey);
  public IReadOnlyList<string> StyleOptions => Enum.GetValues<PointerClickIndicatorStyle>().Select(value => _localization.EnumName(value)).ToArray();
  public bool Enabled { get => _settings.Enabled; set { _settings.Enabled = value; Changed(); } }
  public int StyleIndex { get => (int)_settings.Style; set { if (Enum.IsDefined(typeof(PointerClickIndicatorStyle), value)) { _settings.Style = (PointerClickIndicatorStyle)value; Changed(); } } }
  public string Color { get => _settings.Color; set { _settings.Color = PointerStudioSettings.NormalizeColor(value, _settings.Color); Changed(); } }
  public double Size { get => _settings.Size; set { _settings.Size = Math.Clamp(value, 8, 120); Changed(); } }
  public double Opacity { get => _settings.Opacity; set { _settings.Opacity = Math.Clamp(value, 0.1, 1); Changed(); } }
  public double Intensity { get => _settings.Intensity; set { _settings.Intensity = Math.Clamp(value, 0.1, 1); Changed(); } }
  public double Duration { get => _settings.DurationMilliseconds; set { _settings.DurationMilliseconds = Math.Clamp((int)Math.Round(value), 80, 1200); Changed(); } }
  private void Changed([CallerMemberName] string? property = null) { _settings.Normalize(); PropertyChanged?.Invoke(this, new(property)); _changed(); }
  public void RefreshLocalization()
  {
    PropertyChanged?.Invoke(this, new(nameof(Title)));
    PropertyChanged?.Invoke(this, new(nameof(StyleOptions)));
  }
}

internal static class PointerStudioCatalogLoader
{
  public static PointerStudioCatalog Load()
  {
    var assembly = Assembly.GetExecutingAssembly();
    var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("pointer-studio.v1.json", StringComparison.Ordinal));
    using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("The Pointer Studio catalog is unavailable.");
    var catalog = JsonSerializer.Deserialize<PointerStudioCatalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
      ?? throw new InvalidDataException("The Pointer Studio catalog is invalid.");
    var errors = catalog.Validate();
    if (errors.Count > 0) throw new InvalidDataException($"The Pointer Studio catalog failed validation: {string.Join(", ", errors)}.");
    return catalog;
  }
}
