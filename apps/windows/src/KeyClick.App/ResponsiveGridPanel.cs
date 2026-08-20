using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace KeyClick.App;

public sealed class ResponsiveGridPanel : Panel
{
  public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
    nameof(MinItemWidth), typeof(double), typeof(ResponsiveGridPanel),
    new FrameworkPropertyMetadata(180d, FrameworkPropertyMetadataOptions.AffectsMeasure));
  public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
    nameof(ItemHeight), typeof(double), typeof(ResponsiveGridPanel),
    new FrameworkPropertyMetadata(188d, FrameworkPropertyMetadataOptions.AffectsMeasure));

  public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
  public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

  protected override Size MeasureOverride(Size availableSize)
  {
    if (InternalChildren.Count == 0) return new(0, 0);
    var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Max(MinItemWidth, ActualWidth);
    var columns = ColumnsFor(width);
    var itemWidth = width / columns;
    foreach (UIElement child in InternalChildren) child.Measure(new(itemWidth, ItemHeight));
    return new(width, Math.Ceiling(InternalChildren.Count / (double)columns) * ItemHeight);
  }

  protected override Size ArrangeOverride(Size finalSize)
  {
    if (InternalChildren.Count == 0) return finalSize;
    var columns = ColumnsFor(finalSize.Width);
    var rows = (int)Math.Ceiling(InternalChildren.Count / (double)columns);
    for (var row = 0; row < rows; row++)
    {
      var first = row * columns;
      var count = Math.Min(columns, InternalChildren.Count - first);
      var width = finalSize.Width / count;
      for (var column = 0; column < count; column++)
        InternalChildren[first + column].Arrange(new Rect(column * width, row * ItemHeight, width, ItemHeight));
    }
    return new(finalSize.Width, rows * ItemHeight);
  }

  private int ColumnsFor(double width) => Math.Max(1, Math.Min(InternalChildren.Count, (int)Math.Floor(Math.Max(MinItemWidth, width) / Math.Max(1, MinItemWidth))));
}
