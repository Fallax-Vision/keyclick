using System.Windows;
using System.Windows.Controls;
using KeyClick.Core;
using MessageBox = System.Windows.MessageBox;

namespace KeyClick.App;

public partial class DeleteStatisticsWindow : Window
{
  public DeleteStatisticsWindow() => InitializeComponent();

  public StatisticsDeleteRequest Request
  {
    get
    {
      var (start, end) = Range();
      var categories = new HashSet<StatisticsCategory>();
      if (KeyboardCategory.IsChecked == true) categories.Add(StatisticsCategory.Keyboard);
      if (PointerCategory.IsChecked == true) categories.Add(StatisticsCategory.Pointer);
      if (ScrollingCategory.IsChecked == true) categories.Add(StatisticsCategory.Scrolling);
      return new(start, end, categories, AchievementsCategory.IsChecked == true, SafetyBackup.IsChecked == true);
    }
  }

  private void Period_SelectionChanged(object sender, SelectionChangedEventArgs e)
  {
    var visibility = Period.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    if (CustomDates is not null) CustomDates.Visibility = visibility;
    if (CustomRangeHelp is not null) CustomRangeHelp.Visibility = visibility;
  }

  private void Delete_Click(object sender, RoutedEventArgs e)
  {
    if (KeyboardCategory.IsChecked != true && PointerCategory.IsChecked != true && ScrollingCategory.IsChecked != true && AchievementsCategory.IsChecked != true)
    {
      MessageBox.Show(this, LocalizationService.Current.Get("SelectDeleteCategory"), LocalizationService.Current.Get("DeleteStatistics"), MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    if (Period.SelectedIndex == 4 && (StartDate.SelectedDate is null || EndDate.SelectedDate is null))
    {
      MessageBox.Show(this, LocalizationService.Current.Get("DeleteDateRangeRequired"), LocalizationService.Current.Get("DeleteStatistics"), MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    if (Period.SelectedIndex == 4 && StartDate.SelectedDate!.Value.Date > EndDate.SelectedDate!.Value.Date)
    {
      MessageBox.Show(this, LocalizationService.Current.Get("DeleteDateRangeInvalid"), LocalizationService.Current.Get("DeleteStatistics"), MessageBoxButton.OK, MessageBoxImage.Information);
      return;
    }
    DialogResult = true;
  }

  private (DateTimeOffset? Start, DateTimeOffset? End) Range()
  {
    var today = DateTime.Today;
    return Period.SelectedIndex switch
    {
      1 => (ToUtc(today), ToUtc(today.AddDays(1))),
      2 => (ToUtc(today.AddDays(-6)), ToUtc(today.AddDays(1))),
      3 => (ToUtc(today.AddDays(-29)), ToUtc(today.AddDays(1))),
      4 => (ToUtc(StartDate.SelectedDate!.Value.Date), ToUtc(EndDate.SelectedDate!.Value.Date.AddDays(1))),
      _ => (null, null)
    };
  }

  private static DateTimeOffset ToUtc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local), TimeSpan.Zero);
}
