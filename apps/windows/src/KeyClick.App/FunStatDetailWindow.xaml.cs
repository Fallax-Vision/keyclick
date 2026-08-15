using System.Windows;

namespace KeyClick.App;

public partial class FunStatDetailWindow : Window
{
  public FunStatDetailWindow(string title, string value, IReadOnlyList<EvaluatedFunStat> facts)
  {
    InitializeComponent();
    DataContext = new { Title = title, Value = value, Facts = facts };
  }
}
