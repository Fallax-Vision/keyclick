using System.Windows;

namespace KeyClick.App;

public partial class ChallengePrivacyWindow : Window
{
  public ChallengePrivacyWindow() => InitializeComponent();

  private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
  private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
