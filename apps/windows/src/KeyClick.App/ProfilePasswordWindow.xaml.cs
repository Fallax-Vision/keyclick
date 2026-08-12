using System.Windows;
namespace KeyClick.App;
public partial class ProfilePasswordWindow : Window
{
  public ProfilePasswordWindow() => InitializeComponent();
  public string Password => PasswordField.Password;
  private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
