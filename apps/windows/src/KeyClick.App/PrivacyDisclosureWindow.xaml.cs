using System.Windows;

namespace KeyClick.App;

public partial class PrivacyDisclosureWindow : Window
{
  public PrivacyDisclosureWindow() => InitializeComponent();

  public bool KeyboardStatisticsEnabled => KeyboardToggle.IsChecked == true;
  public bool PointerStatisticsEnabled => PointerToggle.IsChecked == true;

  private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
  private void NotNow_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
