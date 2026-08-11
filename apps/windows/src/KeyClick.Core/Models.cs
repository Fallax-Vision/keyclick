using System.Globalization;
using System.Text.Json.Serialization;

namespace KeyClick.Core;

public enum ThemeMode { System, Light, Dark }
public enum DisplayLanguageMode { System, English, French }
public enum DeviceFamily { Keyboard, ExternalMouse, Trackpad, UnknownPointer }
public enum InputKind { KeyboardKey, PointerButton, Wheel }
public enum KeyVariant { Base, Shift, AltGr, Enabled, Disabled }
public enum SoundOutcome { Success, Failure, Authorized, Blocked }
public enum ShortcutScope { App, Global }
public enum ShortcutKind { Chord, Sequence }

public enum InputGroup
{
  Letters,
  Numbers,
  Punctuation,
  Modifiers,
  Navigation,
  FunctionAndMedia,
  Numpad,
  Locks,
  Space,
  Enter,
  Editing,
  PointerPrimary,
  PointerSecondary,
  PointerAuxiliary,
  Wheel,
  Outcomes
}

public sealed record InputIdentity(
  InputKind Kind,
  int Code,
  bool Extended = false,
  DeviceFamily DeviceFamily = DeviceFamily.Keyboard,
  string? DeviceId = null)
{
  public string StableId => $"{Kind}:{DeviceFamily}:{Code}:{(Extended ? 1 : 0)}";
}

public readonly record struct InputReleaseEvent(
  InputIdentity Input,
  int VirtualKey,
  KeyVariant Variant,
  InputGroup Group,
  long Timestamp,
  string? ForegroundExecutable = null,
  ShortcutStep? ShortcutStep = null);

public readonly record struct SoundTrigger(
  string SampleId,
  float Gain,
  long InputTimestamp,
  SoundOutcome? Outcome = null);

public sealed record AudioOutputDevice(string Id, string Name)
{
  public override string ToString() => Name;
}

public sealed record SoundPackDefinition(
  string Id,
  string Name,
  string Family,
  string Description,
  float BaseFrequency,
  float Noise,
  float Decay,
  float Brightness,
  string AccentHex)
{
  public override string ToString() => Name;
}

public sealed record InputOverride(
  string PackId,
  string InputId,
  KeyVariant Variant,
  bool Enabled,
  float? Volume,
  IReadOnlyList<string> SampleIds);

public sealed record GroupMapping(
  string PackId,
  InputGroup Group,
  KeyVariant Variant,
  bool Enabled,
  float Volume,
  IReadOnlyList<string> SampleIds,
  DeviceFamily? DeviceFamily = null);

public sealed record ResolvedSound(bool Enabled, float Gain, IReadOnlyList<string> SampleIds, bool IsOverride);

public sealed record ShortcutStep(bool Control, bool Alt, bool Shift, bool Windows, int VirtualKey)
{
  public string Display => string.Join("+", new[]
  {
    Control ? "Ctrl" : null,
    Alt ? "Alt" : null,
    Shift ? "Shift" : null,
    Windows ? "Win" : null,
    KeyNames.Display(VirtualKey)
  }.Where(value => value is not null));
}

public sealed record ShortcutBinding(
  string CommandId,
  string Name,
  ShortcutScope Scope,
  ShortcutKind Kind,
  IReadOnlyList<ShortcutStep> Steps,
  bool Enabled = true)
{
  public string Gesture => string.Join(", then ", Steps.Select(step => step.Display));
}

public sealed class AppSettings
{
  public string DisplayName { get; set; } = "KeyClick";
  public bool SoundsEnabled { get; set; } = true;
  public bool KeyboardEnabled { get; set; } = true;
  public bool PointerEnabled { get; set; } = true;
  public bool WheelEnabled { get; set; } = true;
  public bool ResultSoundsEnabled { get; set; } = true;
  public bool LaunchAtStartup { get; set; }
  public bool StartMinimized { get; set; }
  public bool CloseToTray { get; set; } = true;
  public bool PauseInFullscreen { get; set; }
  public bool ReducedMotion { get; set; }
  public bool IntegrationApiEnabled { get; set; }
  public bool NormalizeImports { get; set; } = true;
  public ThemeMode Theme { get; set; } = ThemeMode.System;
  public DisplayLanguageMode DisplayLanguage { get; set; } = DisplayLanguageMode.System;
  public string ActivePackId { get; set; } = "clicky-switch";
  public string OutputDeviceId { get; set; } = "default";
  public float MasterVolume { get; set; } = 0.70f;
  public float KeyboardVolume { get; set; } = 1.0f;
  public float PointerVolume { get; set; } = 0.80f;
  public float ResultVolume { get; set; } = 1.0f;
  public int SequenceTimeoutMs { get; set; } = 1200;
  public List<string> ExcludedExecutables { get; set; } = [];
  public List<string> AllowedIntegrationClients { get; set; } = [];
}

public static class DisplayLanguageResolver
{
  public static string ResolveCode(DisplayLanguageMode preference, CultureInfo deviceCulture) => preference switch
  {
    DisplayLanguageMode.English => "en",
    DisplayLanguageMode.French => "fr",
    _ => string.Equals(deviceCulture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en"
  };
}

public sealed record IntegrationResultRequest(
  int Version,
  string Type,
  SoundOutcome Outcome,
  string? InputId,
  string? ActionId,
  bool PlayResultSound);

public sealed record IntegrationResultResponse(
  int Version,
  bool Accepted,
  string? Error = null);

public static class KeyNames
{
  public static string Display(int virtualKey) => virtualKey switch
  {
    0x08 => "Backspace",
    0x09 => "Tab",
    0x0D => "Enter",
    0x1B => "Esc",
    0x20 => "Space",
    0x21 => "Page Up",
    0x22 => "Page Down",
    0x23 => "End",
    0x24 => "Home",
    0x25 => "Left",
    0x26 => "Up",
    0x27 => "Right",
    0x28 => "Down",
    0x2E => "Delete",
    >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
    >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
    >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
    _ => $"VK {virtualKey:X2}"
  };
}
