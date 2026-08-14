using System.Globalization;
using System.Text.Json.Serialization;

namespace KeyClick.Core;

public enum ThemeMode { System, Light, Dark }
public enum DisplayLanguageMode { System, English, French }
public enum DeviceFamily { Keyboard, ExternalMouse, Trackpad, UnknownPointer }
public enum InputKind { KeyboardKey, PointerButton, Wheel }
public enum InputPhase { Down, Up, WheelDetent }
public enum KeyboardSoundTiming { KeyDown, KeyUp }
public enum KeyVariant { Base, Shift, AltGr, Enabled, Disabled }
public enum SoundOutcome { Success, Failure, Authorized, Blocked }
public enum ShortcutScope { App, Global }
public enum ShortcutKind { Chord, Sequence }
public enum StatisticsPeriod { Today, SevenDays, ThirtyDays, ThisMonth, ThisYear, AllTime, Custom }
public enum StatisticsComparison { None, PreviousPeriod, PreviousYear }
public enum StatisticsCategory { Keyboard, Pointer, Scrolling }
public enum PackRotationInterval { OneMinute, TenMinutes, ThirtyMinutes, OneHour, OneDay, OneWeek, WindowsBoot, Custom }
public enum PackRotationPoolMode { AllPacks, SelectedPacks }
public enum DistributionMode { Installed, Portable }

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

public readonly record struct InputIdentity(
  InputKind Kind,
  int Code,
  bool Extended = false,
  DeviceFamily DeviceFamily = DeviceFamily.Keyboard,
  string? DeviceId = null)
{
  public string StableId => $"{Kind}:{DeviceFamily}:{Code}:{(Extended ? 1 : 0)}";
}

public readonly record struct InputActionEvent(
  InputIdentity Input,
  int VirtualKey,
  KeyVariant Variant,
  InputGroup Group,
  InputPhase Phase,
  long Timestamp,
  string? ForegroundExecutable = null,
  ShortcutStep? ShortcutStep = null);

public sealed record StatisticsQuery(
  DateTimeOffset StartUtc,
  DateTimeOffset EndUtc,
  StatisticsComparison Comparison = StatisticsComparison.None,
  DeviceFamily? DeviceFamily = null);

public sealed record StatisticsTrendPoint(
  DateTimeOffset BucketUtc,
  long KeyboardPresses,
  long PointerClicks,
  long VerticalScroll,
  long HorizontalScroll,
  long ActiveMilliseconds);

public sealed record StatisticsBreakdown(
  InputKind Kind,
  DeviceFamily DeviceFamily,
  int PhysicalCode,
  bool Extended,
  InputGroup Group,
  long Count);

public sealed record StatisticsSnapshot(
  StatisticsQuery Query,
  long KeyboardPresses,
  long TypingKeyPresses,
  long PointerClicks,
  long VerticalScroll,
  long HorizontalScroll,
  long ActiveMilliseconds,
  long KeyboardActiveMilliseconds,
  long PointerActiveMilliseconds,
  int PeakTypingKeysPerMinute,
  int PeakClicksPerFiveSeconds,
  int BusiestHour,
  IReadOnlyList<StatisticsTrendPoint> Trend,
  IReadOnlyList<StatisticsBreakdown> Breakdown,
  StatisticsSnapshot? Comparison = null)
{
  public double AverageKeysPerMinute => KeyboardActiveMilliseconds <= 0 ? 0 : TypingKeyPresses * 60000d / KeyboardActiveMilliseconds;
  public double AverageWordsPerMinute => AverageKeysPerMinute / 5d;
  public double PeakWordsPerMinute => PeakTypingKeysPerMinute / 5d;
  public double AverageClicksPerActiveSecond => PointerActiveMilliseconds <= 0 ? 0 : PointerClicks * 1000d / PointerActiveMilliseconds;
}

public sealed record InputDeviceDescriptor(string Id, DeviceFamily Family, bool Connected);

public sealed record StatisticsDeleteRequest(
  DateTimeOffset? StartUtc,
  DateTimeOffset? EndUtc,
  IReadOnlySet<StatisticsCategory> Categories,
  bool DeleteWellnessAchievements,
  bool CreateSafetyBackup = true);

public sealed record PackRotationPolicy
{
  public bool Enabled { get; init; }
  public PackRotationInterval Interval { get; init; } = PackRotationInterval.ThirtyMinutes;
  public int CustomMinutes { get; init; } = 30;
  public PackRotationPoolMode PoolMode { get; init; } = PackRotationPoolMode.AllPacks;
  public List<string> SelectedPackIds { get; init; } = [];
  public DateTimeOffset? NextDueUtc { get; set; }
  public long? LastWindowsBootTicks { get; set; }
}

public sealed record WellnessAchievement(
  string Id,
  string GoalKind,
  DateOnly LocalDate,
  long TargetSnapshot,
  long ActualValue,
  DateTimeOffset AchievedUtc);

public sealed record WellnessSnapshot(
  long KeyboardPressesToday,
  long PointerClicksToday,
  long ActiveMinutesToday,
  int KeyboardCurrentStreak,
  int KeyboardLongestStreak,
  int PointerCurrentStreak,
  int PointerLongestStreak,
  int ActiveCurrentStreak,
  int ActiveLongestStreak);

public sealed record ProfileManifest(
  int SchemaVersion,
  DateTimeOffset CreatedUtc,
  string ApplicationVersion,
  IReadOnlyList<string> Sections,
  bool PasswordProtected,
  IReadOnlyDictionary<string, string> FileHashes);

public sealed record StatisticsTransferInput(
  string SourceId,
  DateTimeOffset BucketUtc,
  InputKind Kind,
  DeviceFamily DeviceFamily,
  int PhysicalCode,
  bool Extended,
  InputGroup Group,
  long Count,
  long Revision);

public sealed record StatisticsTransferSummary(
  string SourceId,
  DateTimeOffset BucketUtc,
  long KeyboardPresses,
  long TypingKeyPresses,
  long PointerClicks,
  long VerticalScroll,
  long HorizontalScroll,
  long ActiveMilliseconds,
  long KeyboardActiveMilliseconds,
  long PointerActiveMilliseconds,
  int PeakTypingKeysPerMinute,
  int PeakClicksPerFiveSeconds,
  long Revision);

public sealed record StatisticsTransferBundle(
  IReadOnlyList<string> SourceIds,
  IReadOnlyList<StatisticsTransferInput> Inputs,
  IReadOnlyList<StatisticsTransferSummary> Summaries,
  IReadOnlyList<WellnessAchievement> Achievements);

public sealed record ProfileExportOptions(
  bool SettingsAndMappings = true,
  bool CustomPacksAndAudio = false,
  bool Statistics = false,
  bool WellnessAchievements = false,
  string? Password = null);

public sealed record ProfileImportPreview(
  ProfileManifest Manifest,
  IReadOnlyList<string> Sections,
  int MediaFileCount,
  long StatisticsBucketCount,
  bool RequiresPassword);

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
  string AccentHex,
  bool IsCustom = false,
  Dictionary<string, string[]>? SamplePools = null)
{
  public override string ToString() => Name;

  public IReadOnlyList<string> SamplesFor(InputGroup group, KeyVariant variant)
  {
    if (SamplePools is null) return BuiltInCatalog.SamplesFor(Id, group, variant).ToArray();
    if (SamplePools.TryGetValue(PoolKey(group, variant), out var exact) && exact.Length > 0) return exact;
    if (SamplePools.TryGetValue(PoolKey(group, KeyVariant.Base), out var basePool) && basePool.Length > 0) return basePool;
    return SamplePools.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value).FirstOrDefault(pool => pool.Length > 0) ?? [];
  }

  public IEnumerable<string> AllSampleIds() => SamplePools?.Values.SelectMany(pool => pool).Distinct(StringComparer.Ordinal) ?? [];

  public static string PoolKey(InputGroup group, KeyVariant variant) => $"{group}:{variant}".ToLowerInvariant();
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
  public bool StatisticsDisclosureConfirmed { get; set; }
  public bool KeyboardStatisticsEnabled { get; set; } = true;
  public bool PointerStatisticsEnabled { get; set; } = true;
  public bool ScrollingStatisticsEnabled { get; set; } = true;
  public bool WellnessEnabled { get; set; }
  public bool BreakReminderEnabled { get; set; }
  public int BreakReminderActiveMinutes { get; set; } = 60;
  public int BreakReminderRestMinutes { get; set; } = 10;
  public bool KeyboardGoalEnabled { get; set; }
  public bool PointerGoalEnabled { get; set; }
  public bool ActiveMinutesGoalEnabled { get; set; }
  public int KeyboardDailyGoal { get; set; } = 1000;
  public int PointerDailyGoal { get; set; } = 500;
  public int ActiveMinutesDailyGoal { get; set; } = 60;
  public ThemeMode Theme { get; set; } = ThemeMode.System;
  public DisplayLanguageMode DisplayLanguage { get; set; } = DisplayLanguageMode.System;
  public KeyboardSoundTiming KeyboardSoundTiming { get; set; } = KeyboardSoundTiming.KeyDown;
  public string ActivePackId { get; set; } = BuiltInCatalog.DefaultPackId;
  public string OutputDeviceId { get; set; } = "default";
  public float MasterVolume { get; set; } = 0.35f;
  public float KeyboardVolume { get; set; } = 1.0f;
  public float PointerVolume { get; set; } = 0.80f;
  public float ResultVolume { get; set; } = 1.0f;
  public int SequenceTimeoutMs { get; set; } = 1200;
  public List<string> ExcludedExecutables { get; set; } = [];
  public List<string> StatisticsExcludedExecutables { get; set; } = [];
  public List<string> AllowedIntegrationClients { get; set; } = [];
  public Dictionary<string, DeviceFamily> DeviceClassifications { get; set; } = [];
  public PackRotationPolicy PackRotation { get; set; } = new();
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
