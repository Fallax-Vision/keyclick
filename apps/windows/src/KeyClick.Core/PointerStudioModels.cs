namespace KeyClick.Core;

public enum PointerThemeScope { SystemWide, KeyClickOnly, PreviewOnly }
public enum PointerCursorSize { Small, Standard, Large, ExtraLarge }
public enum PointerThemeVariant { Automatic, Light, Dark, HighContrast }
public enum PointerMotionMode { Companion, FullReplacement }
public enum PointerMotionPreset { None, WindTail, Elastic, Comet, Liquid, Glow, ShakeFinder }
public enum PointerClickIndicatorStyle { None, Ring, Ripple, TinyExplosion, Sparkles, RadialTicks, Pulse }
public enum PointerButtonKind { Middle, X1, X2, WheelUp, WheelDown, WheelLeft, WheelRight, Left, Right }
public enum PointerActionKind
{
  None,
  ToggleSounds,
  PreviousSoundPack,
  NextSoundPack,
  TogglePointerEffects,
  NextPointerTheme,
  ShowHideKeyClick,
  BrowserBack,
  BrowserForward,
  MediaPlayPause,
  VolumeUp,
  VolumeDown,
  ShowDesktop,
  DisableButton,
  LeftClick,
  RightClick,
  MiddleClick
}

public sealed class PointerClickIndicatorSettings
{
  public bool Enabled { get; set; } = true;
  public PointerClickIndicatorStyle Style { get; set; } = PointerClickIndicatorStyle.Ring;
  public string Color { get; set; } = "#24C85A";
  public double Opacity { get; set; } = 0.9;
  public double Size { get; set; } = 28;
  public double Intensity { get; set; } = 0.65;
  public int ParticleCount { get; set; } = 12;
  public int DurationMilliseconds { get; set; } = 280;

  public void Normalize()
  {
    if (!Enum.IsDefined(Style)) Style = PointerClickIndicatorStyle.Ring;
    Color = PointerStudioSettings.NormalizeColor(Color, "#24C85A");
    Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.1, 1) : 0.9;
    Size = double.IsFinite(Size) ? Math.Clamp(Size, 8, 120) : 28;
    Intensity = double.IsFinite(Intensity) ? Math.Clamp(Intensity, 0.1, 1) : 0.65;
    ParticleCount = Math.Clamp(ParticleCount, 2, 48);
    DurationMilliseconds = Math.Clamp(DurationMilliseconds, 80, 1200);
  }

  public PointerClickIndicatorSettings Copy() => new()
  {
    Enabled = Enabled,
    Style = Style,
    Color = Color,
    Opacity = Opacity,
    Size = Size,
    Intensity = Intensity,
    ParticleCount = ParticleCount,
    DurationMilliseconds = DurationMilliseconds
  };
}

public sealed class PointerButtonBinding
{
  public string DeviceId { get; set; } = "*";
  public PointerButtonKind Button { get; set; }
  public PointerActionKind Action { get; set; }
  public bool SuppressOriginal { get; set; }
}

public sealed class PointerStudioSettings
{
  public const int CurrentVersion = 1;
  private static readonly HashSet<string> ThemeIds =
  [
    "meridian", "orbit", "prism", "neon-vector", "soft-drop", "pixel-nova", "paper-fold", "liquid", "circuit", "comet"
  ];

  public int Version { get; set; }
  public bool Enabled { get; set; }
  public PointerThemeScope Scope { get; set; } = PointerThemeScope.SystemWide;
  public string ThemeId { get; set; } = "meridian";
  public PointerCursorSize Size { get; set; } = PointerCursorSize.Standard;
  public PointerThemeVariant Variant { get; set; } = PointerThemeVariant.Automatic;
  public List<string> FavoriteThemeIds { get; set; } = ["meridian", "orbit", "prism"];
  public int WindowsPointerSpeed { get; set; } = 10;
  public bool EnhancePointerPrecision { get; set; } = true;
  public int PointerTrails { get; set; }
  public bool NativeShadow { get; set; } = true;
  public bool MotionEffectsEnabled { get; set; }
  public PointerMotionMode MotionMode { get; set; } = PointerMotionMode.Companion;
  public PointerMotionPreset MotionPreset { get; set; } = PointerMotionPreset.WindTail;
  public double VisualScale { get; set; } = 1;
  public string ShadowColor { get; set; } = "#66000000";
  public double ShadowOpacity { get; set; } = 0.35;
  public double ShadowSoftness { get; set; } = 10;
  public double ShadowOffsetX { get; set; } = 2;
  public double ShadowOffsetY { get; set; } = 3;
  public double Smoothing { get; set; } = 0.55;
  public double SpringStrength { get; set; } = 0.6;
  public double Damping { get; set; } = 0.72;
  public int TrailLength { get; set; } = 8;
  public double ShakeSensitivity { get; set; } = 0.6;
  public bool ShakeToEnlarge { get; set; } = true;
  public double ShakeScale { get; set; } = 2;
  public bool ClickIndicatorsEnabled { get; set; }
  public PointerClickIndicatorSettings LeftClick { get; set; } = new() { Style = PointerClickIndicatorStyle.Ripple, Color = "#24C85A" };
  public PointerClickIndicatorSettings RightClick { get; set; } = new() { Style = PointerClickIndicatorStyle.Sparkles, Color = "#6D83FF" };
  public PointerClickIndicatorSettings MiddleClick { get; set; } = new() { Style = PointerClickIndicatorStyle.RadialTicks, Color = "#FFB020" };
  public PointerClickIndicatorSettings AuxiliaryClick { get; set; } = new() { Style = PointerClickIndicatorStyle.TinyExplosion, Color = "#E45CFF" };
  public bool AdaptivePerformance { get; set; } = true;
  public bool PauseOnBatterySaver { get; set; } = true;
  public bool PauseInFullscreen { get; set; } = true;
  public bool PauseInRemoteSession { get; set; } = true;
  public bool ExperimentalReplacementEnabled { get; set; }
  public bool ExperimentalSuppressionEnabled { get; set; }
  public List<PointerButtonBinding> ButtonBindings { get; set; } = [];
  public Dictionary<string, string?> PreviousCursorScheme { get; set; } = [];
  public int PreviousPointerSpeed { get; set; } = 10;
  public bool PreviousEnhancePointerPrecision { get; set; } = true;
  public int PreviousPointerTrails { get; set; }
  public bool PreviousNativeShadow { get; set; } = true;
  public bool RecoverySnapshotCaptured { get; set; }

  public void Normalize(bool profileImport = false)
  {
    Version = CurrentVersion;
    if (!Enum.IsDefined(Scope)) Scope = PointerThemeScope.SystemWide;
    if (!ThemeIds.Contains(ThemeId)) ThemeId = "meridian";
    if (!Enum.IsDefined(Size)) Size = PointerCursorSize.Standard;
    if (!Enum.IsDefined(Variant)) Variant = PointerThemeVariant.Automatic;
    if (!Enum.IsDefined(MotionMode)) MotionMode = PointerMotionMode.Companion;
    if (!Enum.IsDefined(MotionPreset)) MotionPreset = PointerMotionPreset.WindTail;
    FavoriteThemeIds = (FavoriteThemeIds ?? []).Where(ThemeIds.Contains).Distinct(StringComparer.Ordinal).Take(10).ToList();
    WindowsPointerSpeed = Math.Clamp(WindowsPointerSpeed, 1, 20);
    PointerTrails = Math.Clamp(PointerTrails, 0, 16);
    VisualScale = Finite(VisualScale, 0.5, 3, 1);
    ShadowColor = NormalizeColor(ShadowColor, "#66000000");
    ShadowOpacity = Finite(ShadowOpacity, 0, 1, 0.35);
    ShadowSoftness = Finite(ShadowSoftness, 0, 30, 10);
    ShadowOffsetX = Finite(ShadowOffsetX, -20, 20, 2);
    ShadowOffsetY = Finite(ShadowOffsetY, -20, 20, 3);
    Smoothing = Finite(Smoothing, 0, 1, 0.55);
    SpringStrength = Finite(SpringStrength, 0.05, 1, 0.6);
    Damping = Finite(Damping, 0.05, 1, 0.72);
    TrailLength = Math.Clamp(TrailLength, 0, 24);
    ShakeSensitivity = Finite(ShakeSensitivity, 0.1, 1, 0.6);
    ShakeScale = Finite(ShakeScale, 1.1, 4, 2);
    LeftClick ??= new(); RightClick ??= new(); MiddleClick ??= new(); AuxiliaryClick ??= new();
    LeftClick.Normalize(); RightClick.Normalize(); MiddleClick.Normalize(); AuxiliaryClick.Normalize();
    ButtonBindings = (ButtonBindings ?? [])
      .Where(binding => binding is not null && IsValidDeviceId(binding.DeviceId) && Enum.IsDefined(binding.Button) && Enum.IsDefined(binding.Action))
      .Select(binding => new PointerButtonBinding
      {
        DeviceId = SafeDeviceId(binding.DeviceId),
        Button = binding.Button,
        Action = binding.Action,
        SuppressOriginal = binding.SuppressOriginal && binding.DeviceId == "*"
      })
      .GroupBy(binding => (binding.DeviceId, binding.Button))
      .Select(group => group.First())
      .Take(64)
      .ToList();
    PreviousCursorScheme = (PreviousCursorScheme ?? [])
      .Where(item => item.Key is { Length: > 0 and <= 32 })
      .Take(32)
      .ToDictionary(item => item.Key, item => item.Value is { Length: <= 1024 } ? item.Value : null, StringComparer.OrdinalIgnoreCase);
    PreviousPointerSpeed = Math.Clamp(PreviousPointerSpeed, 1, 20);
    PreviousPointerTrails = Math.Clamp(PreviousPointerTrails, 0, 16);
    if (profileImport)
    {
      ExperimentalReplacementEnabled = false;
      ExperimentalSuppressionEnabled = false;
      MotionMode = PointerMotionMode.Companion;
      ButtonBindings.RemoveAll(binding => binding.DeviceId != "*");
      PreviousCursorScheme.Clear();
      RecoverySnapshotCaptured = false;
    }
  }

  public void DeactivateExperimentalModes()
  {
    ExperimentalReplacementEnabled = false;
    ExperimentalSuppressionEnabled = false;
    MotionMode = PointerMotionMode.Companion;
    ButtonBindings.RemoveAll(binding => binding.SuppressOriginal);
    PreviousCursorScheme.Clear();
    RecoverySnapshotCaptured = false;
  }

  public PointerClickIndicatorSettings IndicatorFor(int buttonCode) => buttonCode switch
  {
    1 => LeftClick,
    2 => RightClick,
    3 => MiddleClick,
    _ => AuxiliaryClick
  };

  public static string NormalizeColor(string? value, string fallback)
  {
    if (value is null || value.Length is not (7 or 9) || value[0] != '#' || !value.AsSpan(1).ToString().All(Uri.IsHexDigit)) return fallback;
    return value.ToUpperInvariant();
  }

  private static bool IsValidDeviceId(string? value) => value == "*" || value is { Length: >= 8 and <= 64 } && value.All(char.IsAsciiHexDigit);
  private static string SafeDeviceId(string? value) => value!;
  private static double Finite(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed record PointerThemeDefinition(
  string Id,
  string NameKey,
  string DescriptionKey,
  string Style,
  string Primary,
  string Secondary,
  string Outline,
  string Provenance);

public sealed record PointerStudioCatalog(int Version, IReadOnlyList<string> Roles, IReadOnlyList<PointerThemeDefinition> Themes)
{
  public IReadOnlyList<string> Validate()
  {
    var errors = new List<string>();
    if (Version != 1) errors.Add("catalog-version");
    if (Themes.Count < 10) errors.Add("theme-count");
    if (Themes.Select(theme => theme.Id).Distinct(StringComparer.Ordinal).Count() != Themes.Count) errors.Add("duplicate-theme-id");
    if (Themes.Any(theme => string.IsNullOrWhiteSpace(theme.Provenance) || theme.Provenance.Contains("flaticon", StringComparison.OrdinalIgnoreCase))) errors.Add("theme-provenance");
    if (Roles.Count < 15 || Roles.Distinct(StringComparer.Ordinal).Count() != Roles.Count) errors.Add("cursor-roles");
    return errors;
  }
}

public readonly record struct PointerMovementSignal(int DeltaX, int DeltaY, long Timestamp);

public sealed record NativePointerSettings(int Speed, bool EnhancePointerPrecision, int Trails, bool Shadow);

public sealed record PointerApplyResult(bool Success, string? CursorPath = null, string? Error = null);
