using System.Windows;
using System.Windows.Input;
using KeyClick.Core;
using MessageBox = System.Windows.MessageBox;

namespace KeyClick.App;

public partial class ShortcutEditorWindow : Window
{
  private static LocalizationService L => LocalizationService.Current;
  private readonly ShortcutBinding _original;
  private readonly List<ShortcutStep> _steps = [];
  private bool _recording;

  public ShortcutEditorWindow(ShortcutBinding binding)
  {
    InitializeComponent();
    _original = binding;
    ActionName.Text = L.ShortcutName(binding);
    ScopeBox.ItemsSource = Enum.GetValues<ShortcutScope>().Select(value => L.EnumName(value));
    KindBox.ItemsSource = Enum.GetValues<ShortcutKind>().Select(value => L.EnumName(value));
    ScopeBox.SelectedIndex = (int)binding.Scope;
    KindBox.SelectedIndex = (int)binding.Kind;
    EnabledBox.IsChecked = binding.Enabled;
    _steps.AddRange(binding.Steps);
    UpdateGesture();
  }

  public ShortcutBinding? Result { get; private set; }

  private void Record_Click(object sender, RoutedEventArgs e)
  {
    _steps.Clear();
    _recording = true;
    GestureText.Text = L.Get("PressShortcut");
    RecordButton.Content = L.Get("Recording");
    Focus();
  }

  private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
  {
    if (!_recording) return;
    e.Handled = true;
    var key = e.Key == Key.System ? e.SystemKey : e.Key;
    if (key == Key.Escape) { _recording = false; RecordButton.Content = L.Get("RecordGesture"); UpdateGesture(); return; }
    if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
    var modifiers = Keyboard.Modifiers;
    var step = new ShortcutStep(
      modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Alt), modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Windows),
      KeyInterop.VirtualKeyFromKey(key));
    _steps.Add(step);
    var required = KindBox.SelectedIndex == (int)ShortcutKind.Sequence ? 2 : 1;
    if (_steps.Count >= required) { _recording = false; RecordButton.Content = L.Get("RecordAgain"); }
    UpdateGesture();
  }

  private void ScopeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
  {
    if (ScopeBox.SelectedIndex == (int)ShortcutScope.Global && KindBox.SelectedIndex == (int)ShortcutKind.Chord && _steps.Count == 1 && !HasModifier(_steps[0])) GestureText.Text = L.Get("GlobalModifierRequired");
  }

  private void KindBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
  {
    if (KindBox.SelectedIndex == (int)ShortcutKind.Chord && _steps.Count > 1) _steps.RemoveRange(1, _steps.Count - 1);
    UpdateGesture();
  }

  private void Save_Click(object sender, RoutedEventArgs e)
  {
    if (_steps.Count == 0) { MessageBox.Show(this, L.Get("RecordShortcutFirst"), L.Get("ShortcutRequired"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
    var scope = (ShortcutScope)ScopeBox.SelectedIndex;
    var kind = (ShortcutKind)KindBox.SelectedIndex;
    if (kind == ShortcutKind.Sequence && _steps.Count != 2) { MessageBox.Show(this, L.Get("RecordSequenceBothSteps"), L.Get("SecondStepRequired"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
    if (scope == ShortcutScope.Global && kind == ShortcutKind.Chord && !HasModifier(_steps[0])) { MessageBox.Show(this, L.Get("GlobalChordRule"), L.Get("ModifierRequired"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
    Result = _original with { Scope = scope, Kind = kind, Steps = _steps.ToArray(), Enabled = EnabledBox.IsChecked == true };
    DialogResult = true;
  }

  private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
  private void UpdateGesture() => GestureText.Text = _steps.Count == 0 ? L.Get("NotAssigned") : L.Gesture(_steps);
  private static bool HasModifier(ShortcutStep step) => step.Control || step.Alt || step.Shift || step.Windows;
}
