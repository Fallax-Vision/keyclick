using System.Globalization;
using System.Windows;
using System.Windows.Data;
using KeyClick.Core;
using Application = System.Windows.Application;
using ThemeMode = KeyClick.Core.ThemeMode;
using WpfBinding = System.Windows.Data.Binding;

namespace KeyClick.App;

public sealed class LocalizationService
{
  private readonly CultureInfo _deviceCulture;
  private ResourceDictionary? _activeDictionary;

  public LocalizationService(CultureInfo? deviceCulture = null)
  {
    _deviceCulture = deviceCulture ?? CultureInfo.CurrentUICulture;
    Current = this;
  }

  public static LocalizationService Current { get; private set; } = null!;
  public string EffectiveCode { get; private set; } = "en";
  public event EventHandler? LanguageChanged;

  public void Apply(DisplayLanguageMode preference)
  {
    var code = DisplayLanguageResolver.ResolveCode(preference, _deviceCulture);
    var culture = string.Equals(_deviceCulture.TwoLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase)
      ? _deviceCulture
      : CultureInfo.GetCultureInfo(code == "fr" ? "fr-FR" : "en-US");
    var dictionary = new ResourceDictionary
    {
      Source = new Uri($"/KeyClick.App;component/Resources/Strings.{code}.xaml", UriKind.Relative)
    };

    CultureInfo.CurrentUICulture = culture;
    CultureInfo.DefaultThreadCurrentUICulture = culture;
    EffectiveCode = code;

    var dictionaries = Application.Current.Resources.MergedDictionaries;
    dictionaries.Add(dictionary);
    if (_activeDictionary is not null) dictionaries.Remove(_activeDictionary);
    _activeDictionary = dictionary;
    LanguageChanged?.Invoke(this, EventArgs.Empty);
  }

  public string Get(string key)
  {
    if (_activeDictionary?[key] is string value) return value.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);
    return key;
  }

  public string Format(string key, params object?[] values) => string.Format(CultureInfo.CurrentUICulture, Get(key), values);

  public string EnumName(object value) => value switch
  {
    ThemeMode.System => Get("ThemeSystem"),
    ThemeMode.Light => Get("ThemeLight"),
    ThemeMode.Dark => Get("ThemeDark"),
    DisplayLanguageMode.System => Get("LanguageSystem"),
    DisplayLanguageMode.English => Get("LanguageEnglish"),
    DisplayLanguageMode.French => Get("LanguageFrench"),
    KeyVariant.Base => Get("VariantBase"),
    KeyVariant.Shift => Get("VariantShift"),
    KeyVariant.AltGr => Get("VariantAltGr"),
    KeyVariant.Enabled => Get("VariantEnabled"),
    KeyVariant.Disabled => Get("VariantDisabled"),
    KeyboardSoundTiming.KeyDown => Get("SoundOnKeyDown"),
    KeyboardSoundTiming.KeyUp => Get("SoundOnKeyUp"),
    PackRotationInterval.OneMinute => Get("RotationOneMinute"),
    PackRotationInterval.TenMinutes => Get("RotationTenMinutes"),
    PackRotationInterval.ThirtyMinutes => Get("RotationThirtyMinutes"),
    PackRotationInterval.OneHour => Get("RotationOneHour"),
    PackRotationInterval.OneDay => Get("RotationOneDay"),
    PackRotationInterval.OneWeek => Get("RotationOneWeek"),
    PackRotationInterval.WindowsBoot => Get("RotationWindowsBoot"),
    PackRotationInterval.Custom => Get("RotationCustom"),
    PackRotationPoolMode.AllPacks => Get("RotationAllPacks"),
    PackRotationPoolMode.SelectedPacks => Get("RotationSelectedPacks"),
    ShortcutScope.App => Get("ScopeApp"),
    ShortcutScope.Global => Get("ScopeGlobal"),
    ShortcutKind.Chord => Get("KindChord"),
    ShortcutKind.Sequence => Get("KindSequence"),
    DeviceFamily.Keyboard => Get("DeviceKeyboard"),
    DeviceFamily.ExternalMouse => Get("DeviceExternalMouse"),
    DeviceFamily.Trackpad => Get("DeviceTrackpad"),
    DeviceFamily.UnknownPointer => Get("DeviceUnknownPointer"),
    InputGroup.Letters => Get("GroupLetters"),
    InputGroup.Numbers => Get("GroupNumbers"),
    InputGroup.Punctuation => Get("GroupPunctuation"),
    InputGroup.Modifiers => Get("GroupModifiers"),
    InputGroup.Navigation => Get("GroupNavigation"),
    InputGroup.FunctionAndMedia => Get("GroupFunctionMedia"),
    InputGroup.Numpad => Get("GroupNumpad"),
    InputGroup.Locks => Get("GroupLocks"),
    InputGroup.Space => Get("GroupSpace"),
    InputGroup.Enter => Get("GroupEnter"),
    InputGroup.Editing => Get("GroupEditing"),
    InputGroup.PointerPrimary => Get("GroupPointerPrimary"),
    InputGroup.PointerSecondary => Get("GroupPointerSecondary"),
    InputGroup.PointerAuxiliary => Get("GroupPointerAuxiliary"),
    InputGroup.Wheel => Get("GroupWheel"),
    InputGroup.Outcomes => Get("GroupOutcomes"),
    _ => value.ToString() ?? string.Empty
  };

  public SoundPackDefinition LocalizePack(SoundPackDefinition pack)
  {
    var suffix = pack.Id.Replace('-', '_');
    return pack with
    {
      Name = Get($"PackName_{suffix}"),
      Family = pack.Family == "Balanced" ? Get("PackFamilyBalanced") : Get("PackFamilyKeyboard"),
      Description = Get($"PackDescription_{suffix}")
    };
  }

  public string ShortcutName(ShortcutBinding binding)
  {
    var key = $"ShortcutName_{binding.CommandId.Replace('-', '_')}";
    var value = Get(key);
    return value == key ? binding.Name : value;
  }

  public string KeyName(int virtualKey) => virtualKey switch
  {
    0x08 => Get("KeyBackspace"),
    0x09 => Get("KeyTab"),
    0x0D => Get("KeyEnter"),
    0x1B => Get("KeyEscape"),
    0x20 => Get("KeySpace"),
    0x21 => Get("KeyPageUp"),
    0x22 => Get("KeyPageDown"),
    0x23 => Get("KeyEnd"),
    0x24 => Get("KeyHome"),
    0x25 => Get("KeyLeft"),
    0x26 => Get("KeyUp"),
    0x27 => Get("KeyRight"),
    0x28 => Get("KeyDown"),
    0x2E => Get("KeyDelete"),
    _ => KeyNames.Display(virtualKey)
  };

  public string KeyNameFromScanCode(int scanCode, bool extended)
  {
    var value = scanCode & 0xFF;
    if (extended)
    {
      return scanCode switch
      {
        0xE037 => Get("KeyPrintScreen"), 0xE145 => Get("KeyPause"), 0xE052 => Get("KeyInsert"),
        0xE047 => KeyName(0x24), 0xE049 => KeyName(0x21), 0xE053 => KeyName(0x2E),
        0xE04F => KeyName(0x23), 0xE051 => KeyName(0x22), 0xE048 => KeyName(0x26),
        0xE04B => KeyName(0x25), 0xE050 => KeyName(0x28), 0xE04D => KeyName(0x27),
        0xE035 => "Num /", 0xE01C => Get("KeyNumpadEnter"), 0xE038 => "AltGr", 0xE01D => "Ctrl",
        0xE05B or 0xE05C => Get("KeyWindows"), 0xE05D => Get("KeyMenu"),
        _ => $"SC {scanCode:X}"
      };
    }
    return value switch
    {
      0x01 => KeyName(0x1B), 0x0E => KeyName(0x08), 0x0F => KeyName(0x09), 0x1C => KeyName(0x0D), 0x39 => KeyName(0x20),
      >= 0x3B and <= 0x44 => $"F{value - 0x3A}", 0x57 => "F11", 0x58 => "F12",
      0x1E => "A", 0x30 => "B", 0x2E => "C", 0x20 => "D", 0x12 => "E", 0x21 => "F", 0x22 => "G", 0x23 => "H", 0x17 => "I", 0x24 => "J", 0x25 => "K", 0x26 => "L", 0x32 => "M", 0x31 => "N", 0x18 => "O", 0x19 => "P", 0x10 => "Q", 0x13 => "R", 0x1F => "S", 0x14 => "T", 0x16 => "U", 0x2F => "V", 0x11 => "W", 0x2D => "X", 0x15 => "Y", 0x2C => "Z",
      >= 0x02 and <= 0x0A => (value - 1).ToString(CultureInfo.InvariantCulture), 0x0B => "0",
      0x29 => "`", 0x0C => "-", 0x0D => "=", 0x1A => "[", 0x1B => "]", 0x2B => "\\",
      0x27 => ";", 0x28 => "'", 0x33 => ",", 0x34 => ".", 0x35 => "/",
      0x2A or 0x36 => Get("ShortcutShift"), 0x1D => "Ctrl", 0x38 => "Alt", 0x3A => "Caps",
      0x46 => Get("KeyScrollLock"), 0x45 => Get("KeyNumLock"), 0x37 => "Num *", 0x4A => "Num -", 0x4E => "Num +",
      0x47 => "Num 7", 0x48 => "Num 8", 0x49 => "Num 9", 0x4B => "Num 4", 0x4C => "Num 5", 0x4D => "Num 6",
      0x4F => "Num 1", 0x50 => "Num 2", 0x51 => "Num 3", 0x52 => "Num 0", 0x53 => "Num .",
      _ => $"SC {scanCode:X}"
    };
  }

  public string Gesture(IReadOnlyList<ShortcutStep> steps) => string.Join(Get("SequenceSeparator"), steps.Select(step =>
    string.Join("+", new[]
    {
      step.Control ? "Ctrl" : null,
      step.Alt ? "Alt" : null,
      step.Shift ? Get("ShortcutShift") : null,
      step.Windows ? "Win" : null,
      KeyName(step.VirtualKey)
    }.Where(value => value is not null))));
}

public sealed class LocalizedEnumConverter : IValueConverter
{
  public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => LocalizationService.Current.EnumName(value);
  public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => WpfBinding.DoNothing;
}

public sealed class LocalizedGestureConverter : IValueConverter
{
  public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is ShortcutBinding binding
    ? LocalizationService.Current.Gesture(binding.Steps)
    : string.Empty;

  public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => WpfBinding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
  public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool flag && !flag;
  public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool flag && !flag;
}
