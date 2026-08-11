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
