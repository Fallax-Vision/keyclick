using System.Globalization;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace KeyClick.App;

public partial class FunStatsManageWindow : Window
{
  private readonly StatisticsViewModel _viewModel;

  public FunStatsManageWindow(StatisticsViewModel viewModel)
  {
    _viewModel = viewModel;
    DataContext = viewModel;
    InitializeComponent();
  }

  private void MoveUp_Click(object sender, RoutedEventArgs e)
  {
    if ((sender as FrameworkElement)?.DataContext is FunStatOption option) _viewModel.MoveFunStat(option.Id, -1);
  }

  private void MoveDown_Click(object sender, RoutedEventArgs e)
  {
    if ((sender as FrameworkElement)?.DataContext is FunStatOption option) _viewModel.MoveFunStat(option.Id, 1);
  }

  private void RemoveCustom_Click(object sender, RoutedEventArgs e)
  {
    if ((sender as FrameworkElement)?.DataContext is not FunStatOption { IsCustom: true } option) return;
    if (MessageBox.Show(this, LocalizationService.Current.Get("FunRemoveCustomQuestion"), LocalizationService.Current.Get("Remove"),
      MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) _viewModel.RemoveCustomFunStat(option.Id);
  }

  private void AddCustom_Click(object sender, RoutedEventArgs e)
  {
    var parsed = double.TryParse(CustomTarget.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var target);
    var error = string.Empty;
    if (!parsed || !_viewModel.TryAddCustomFunStat(CustomLabel.Text, CustomMetric.SelectedIndex, target, out error))
    {
      MessageBox.Show(this, string.IsNullOrEmpty(error) ? LocalizationService.Current.Get("FunCustomValidationError") : error,
        LocalizationService.Current.Get("FunCustomTab"), MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    CustomLabel.Clear();
    CustomTarget.Text = "1000";
  }

  private void ResetScroll_Click(object sender, RoutedEventArgs e) => _viewModel.ResetScrollEstimate();

  private void CalibrateScroll_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new ScrollCalibrationWindow { Owner = this };
    if (dialog.ShowDialog() == true) _viewModel.ScrollCentimetersPerDetent = dialog.CentimetersPerDetent;
  }
}
