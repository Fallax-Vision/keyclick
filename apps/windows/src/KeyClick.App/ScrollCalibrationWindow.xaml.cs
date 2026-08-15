using System.Globalization;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace KeyClick.App;

public partial class ScrollCalibrationWindow : Window
{
  private double _detents;
  public ScrollCalibrationWindow() => InitializeComponent();
  public double CentimetersPerDetent { get; private set; }

  private void ScrollSurface_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
  {
    _detents += Math.Abs(e.Delta) / 120d;
    DetentCount.Text = _detents.ToString("0.##", CultureInfo.CurrentUICulture);
    e.Handled = true;
  }

  private void Reset_Click(object sender, RoutedEventArgs e)
  {
    _detents = 0;
    DetentCount.Text = "0";
    ScrollSurface.Focus();
  }

  private void Apply_Click(object sender, RoutedEventArgs e)
  {
    if (!double.TryParse(KnownDistance.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var distance)
      || !FunStatsEngine.TryCalculateScrollCalibration(distance, _detents, out var calibrated))
    {
      MessageBox.Show(this, LocalizationService.Current.Get("FunCalibrationValidation"), LocalizationService.Current.Get("FunCalibrateScroll"),
        MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    CentimetersPerDetent = calibrated;
    DialogResult = true;
  }
}
