using System.Windows;
using KeyClick.Core;
namespace KeyClick.App;
public partial class ProfileImportWindow : Window
{
  public ProfileImportWindow(ProfileImportPreview preview)
  {
    InitializeComponent();
    Summary.Text = LocalizationService.Current.Format("ProfilePreviewFormat", string.Join(", ", preview.Sections), preview.MediaFileCount, preview.StatisticsBucketCount);
  }
  public bool UseImportedMedia => UseImported.IsChecked == true;
  private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
