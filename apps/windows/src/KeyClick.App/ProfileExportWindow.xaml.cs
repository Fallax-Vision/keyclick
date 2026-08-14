using System.Windows;
using KeyClick.Core;
using MessageBox = System.Windows.MessageBox;

namespace KeyClick.App;

public partial class ProfileExportWindow : Window
{
  public ProfileExportWindow() => InitializeComponent();
  public ProfileExportOptions Options => new(Settings.IsChecked == true, Media.IsChecked == true, Statistics.IsChecked == true, Wellness.IsChecked == true, Protect.IsChecked == true ? Password.Password : null);
  private void Protect_Changed(object sender, RoutedEventArgs e) { if (PasswordFields is not null) PasswordFields.Visibility = Protect.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
  private void Continue_Click(object sender, RoutedEventArgs e)
  {
    if (Settings.IsChecked != true && Media.IsChecked != true && Statistics.IsChecked != true && Wellness.IsChecked != true) { MessageBox.Show(this, LocalizationService.Current.Get("SelectProfileSection")); return; }
    if (Protect.IsChecked == true && (Password.Password.Length < 8 || Password.Password != ConfirmPassword.Password)) { MessageBox.Show(this, LocalizationService.Current.Get("ProfilePasswordInvalid")); return; }
    DialogResult = true;
  }
}
