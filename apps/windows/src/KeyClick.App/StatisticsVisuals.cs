using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KeyClick.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using WpfCursors = System.Windows.Input.Cursors;

namespace KeyClick.App;

public sealed class ActivityChart : FrameworkElement
{
  public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
    nameof(Snapshot), typeof(StatisticsSnapshot), typeof(ActivityChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
  private static readonly Brush KeyboardBrush = new SolidColorBrush(Color.FromRgb(24, 169, 91));
  private static readonly Brush PointerBrush = new SolidColorBrush(Color.FromRgb(91, 111, 216));
  private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 1);

  public StatisticsSnapshot? Snapshot { get => (StatisticsSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

  protected override void OnRender(DrawingContext drawingContext)
  {
    base.OnRender(drawingContext);
    var area = new Rect(38, 12, Math.Max(0, ActualWidth - 50), Math.Max(0, ActualHeight - 38));
    for (var line = 0; line <= 4; line++)
    {
      var y = area.Top + area.Height * line / 4d;
      drawingContext.DrawLine(GridPen, new(area.Left, y), new(area.Right, y));
    }
    var points = Snapshot?.Trend;
    if (points is null || points.Count == 0)
    {
      DrawText(drawingContext, LocalizationService.Current.Get("NoStatisticsYet"), new(area.Left + 12, area.Top + area.Height / 2 - 8));
      return;
    }
    var max = Math.Max(1, points.Max(item => Math.Max(item.KeyboardPresses, item.PointerClicks)));
    DrawSeries(drawingContext, area, points.Select(item => item.KeyboardPresses).ToArray(), max, new Pen(KeyboardBrush, 2));
    DrawSeries(drawingContext, area, points.Select(item => item.PointerClicks).ToArray(), max, new Pen(PointerBrush, 2));
    drawingContext.DrawRectangle(KeyboardBrush, null, new(area.Left, area.Bottom + 10, 10, 10));
    DrawText(drawingContext, LocalizationService.Current.Get("Keyboard"), new(area.Left + 15, area.Bottom + 6));
    drawingContext.DrawRectangle(PointerBrush, null, new(area.Left + 105, area.Bottom + 10, 10, 10));
    DrawText(drawingContext, LocalizationService.Current.Get("Pointer"), new(area.Left + 120, area.Bottom + 6));
  }

  protected override void OnMouseMove(MouseEventArgs e)
  {
    base.OnMouseMove(e);
    if (Snapshot?.Trend is not { Count: > 0 } points) return;
    var areaWidth = Math.Max(1, ActualWidth - 50);
    var index = Math.Clamp((int)Math.Round((e.GetPosition(this).X - 38) / areaWidth * (points.Count - 1)), 0, points.Count - 1);
    var point = points[index];
    ToolTip = $"{point.BucketUtc.ToLocalTime():g}\n{LocalizationService.Current.Get("Keyboard")}: {point.KeyboardPresses:N0}\n{LocalizationService.Current.Get("Pointer")}: {point.PointerClicks:N0}";
  }

  private static void DrawSeries(DrawingContext context, Rect area, long[] values, long max, Pen pen)
  {
    if (values.Length == 1)
    {
      context.DrawEllipse(pen.Brush, null, new(area.Left, area.Bottom - area.Height * values[0] / max), 3, 3);
      return;
    }
    var geometry = new StreamGeometry();
    using (var stream = geometry.Open())
    {
      for (var index = 0; index < values.Length; index++)
      {
        var point = new Point(area.Left + area.Width * index / (values.Length - 1d), area.Bottom - area.Height * values[index] / max);
        if (index == 0) stream.BeginFigure(point, false, false); else stream.LineTo(point, true, false);
      }
    }
    context.DrawGeometry(null, pen, geometry);
  }

  private static void DrawText(DrawingContext context, string text, Point origin) => context.DrawText(
    new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Gray, 1.0), origin);
}

public sealed class KeyboardHeatmap : FrameworkElement
{
  public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
    nameof(Snapshot), typeof(StatisticsSnapshot), typeof(KeyboardHeatmap),
    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSnapshotChanged));
  public static readonly DependencyProperty TooltipsEnabledProperty = DependencyProperty.Register(
    nameof(TooltipsEnabled), typeof(bool), typeof(KeyboardHeatmap), new FrameworkPropertyMetadata(true, OnTooltipsEnabledChanged));
  private static readonly KeyLayout[] Keys =
  [
    new(0x01,0,0), new(0x3B,2,0), new(0x3C,3,0), new(0x3D,4,0), new(0x3E,5,0),
    new(0x3F,6.5,0), new(0x40,7.5,0), new(0x41,8.5,0), new(0x42,9.5,0),
    new(0x43,11,0), new(0x44,12,0), new(0x57,13,0), new(0x58,14,0),
    new(0xE037,16,0), new(0x46,17,0), new(0xE145,18,0),

    new(0x29,0,1.35), new(0x02,1,1.35), new(0x03,2,1.35), new(0x04,3,1.35), new(0x05,4,1.35),
    new(0x06,5,1.35), new(0x07,6,1.35), new(0x08,7,1.35), new(0x09,8,1.35), new(0x0A,9,1.35),
    new(0x0B,10,1.35), new(0x0C,11,1.35), new(0x0D,12,1.35), new(0x0E,13,1.35,2),
    new(0xE052,16,1.35), new(0xE047,17,1.35), new(0xE049,18,1.35),
    new(0x45,19.5,1.35), new(0xE035,20.5,1.35), new(0x37,21.5,1.35), new(0x4A,22.5,1.35),

    new(0x0F,0,2.35,1.5), new(0x10,1.5,2.35), new(0x11,2.5,2.35), new(0x12,3.5,2.35),
    new(0x13,4.5,2.35), new(0x14,5.5,2.35), new(0x15,6.5,2.35), new(0x16,7.5,2.35),
    new(0x17,8.5,2.35), new(0x18,9.5,2.35), new(0x19,10.5,2.35), new(0x1A,11.5,2.35),
    new(0x1B,12.5,2.35), new(0x2B,13.5,2.35,1.5),
    new(0xE053,16,2.35), new(0xE04F,17,2.35), new(0xE051,18,2.35),
    new(0x47,19.5,2.35), new(0x48,20.5,2.35), new(0x49,21.5,2.35), new(0x4E,22.5,2.35,1,2),

    new(0x3A,0,3.35,1.75), new(0x1E,1.75,3.35), new(0x1F,2.75,3.35), new(0x20,3.75,3.35),
    new(0x21,4.75,3.35), new(0x22,5.75,3.35), new(0x23,6.75,3.35), new(0x24,7.75,3.35),
    new(0x25,8.75,3.35), new(0x26,9.75,3.35), new(0x27,10.75,3.35), new(0x28,11.75,3.35),
    new(0x1C,12.75,3.35,2.25), new(0x4B,19.5,3.35), new(0x4C,20.5,3.35), new(0x4D,21.5,3.35),

    new(0x2A,0,4.35,2.25), new(0x2C,2.25,4.35), new(0x2D,3.25,4.35), new(0x2E,4.25,4.35),
    new(0x2F,5.25,4.35), new(0x30,6.25,4.35), new(0x31,7.25,4.35), new(0x32,8.25,4.35),
    new(0x33,9.25,4.35), new(0x34,10.25,4.35), new(0x35,11.25,4.35), new(0x36,12.25,4.35,2.75),
    new(0xE048,17,4.35), new(0x4F,19.5,4.35), new(0x50,20.5,4.35), new(0x51,21.5,4.35), new(0xE01C,22.5,4.35,1,2),

    new(0x1D,0,5.35,1.4), new(0xE05B,1.4,5.35,1.4), new(0x38,2.8,5.35,1.4), new(0x39,4.2,5.35,5.8),
    new(0xE038,10,5.35,1.4), new(0xE05C,11.4,5.35,1.4), new(0xE05D,12.8,5.35,1.4), new(0xE01D,14.2,5.35,1.4),
    new(0xE04B,16,5.35), new(0xE050,17,5.35), new(0xE04D,18,5.35), new(0x52,19.5,5.35,2), new(0x53,21.5,5.35)
  ];
  private readonly Popup _detailsPopup;
  private readonly TextBlock _detailsTitle;
  private readonly TextBlock _detailsBody;
  private int? _selectedCode;

  public KeyboardHeatmap()
  {
    Cursor = WpfCursors.Hand;
    _detailsTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) };
    _detailsTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
    _detailsBody = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 270 };
    _detailsBody.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
    var content = new StackPanel();
    content.Children.Add(_detailsTitle);
    content.Children.Add(_detailsBody);
    var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(13) };
    card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundBrush");
    card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
    card.Child = content;
    _detailsPopup = new Popup
    {
      AllowsTransparency = true,
      Child = card,
      Placement = PlacementMode.Relative,
      PlacementTarget = this,
      StaysOpen = false
    };
  }

  public StatisticsSnapshot? Snapshot { get => (StatisticsSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
  public bool TooltipsEnabled { get => (bool)GetValue(TooltipsEnabledProperty); set => SetValue(TooltipsEnabledProperty, value); }

  protected override void OnRender(DrawingContext context)
  {
    base.OnRender(context);
    var counts = Snapshot?.Breakdown.Where(item => item.Kind == InputKind.KeyboardKey).GroupBy(item => item.PhysicalCode).ToDictionary(group => group.Key, group => group.Sum(item => item.Count)) ?? [];
    var max = Math.Max(1, counts.Values.DefaultIfEmpty().Max());
    var (unit, gap, originX) = Geometry();
    foreach (var key in Keys)
    {
      counts.TryGetValue(key.Code, out var count);
      var intensity = Math.Sqrt(count / (double)max);
      var color = Color.FromRgb((byte)(42 - intensity * 16), (byte)(58 + intensity * 122), (byte)(53 + intensity * 42));
      var rect = KeyRect(key, unit, gap, originX);
      context.DrawRoundedRectangle(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)), 1), rect, 4, 4);
      var label = LocalizationService.Current.KeyNameFromScanCode(key.Code, key.Code > 0xFF);
      var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Clamp(unit * .22, 7, 9), Brushes.White, 1.0)
      { MaxTextWidth = Math.Max(4, rect.Width - 5), Trimming = TextTrimming.CharacterEllipsis };
      context.DrawText(text, new(rect.Left + 3, rect.Top + (rect.Height - text.Height) / 2));
    }
  }

  protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
  {
    base.OnMouseLeftButtonUp(e);
    if (!TooltipsEnabled || Snapshot is null)
    {
      CloseDetails();
      return;
    }

    var point = e.GetPosition(this);
    var (unit, gap, originX) = Geometry();
    var key = Keys.FirstOrDefault(item => KeyRect(item, unit, gap, originX).Contains(point));
    if (key is null)
    {
      CloseDetails();
      return;
    }
    if (_selectedCode == key.Code && _detailsPopup.IsOpen)
    {
      CloseDetails();
      e.Handled = true;
      return;
    }

    _selectedCode = key.Code;
    var rows = Snapshot.Breakdown.Where(item => item.Kind == InputKind.KeyboardKey && item.PhysicalCode == key.Code).ToArray();
    var count = rows.Sum(item => item.Count);
    var group = rows.OrderByDescending(item => item.Count).FirstOrDefault()?.Group;
    var share = Snapshot.KeyboardPresses <= 0 ? 0 : count * 100d / Snapshot.KeyboardPresses;
    var start = Snapshot.Query.StartUtc.ToLocalTime().Date;
    var end = Snapshot.Query.EndUtc.ToLocalTime().AddTicks(-1).Date;
    var period = start == end ? start.ToString("d", CultureInfo.CurrentUICulture) : $"{start.ToString("d", CultureInfo.CurrentUICulture)} – {end.ToString("d", CultureInfo.CurrentUICulture)}";
    var localization = LocalizationService.Current;
    var label = localization.KeyNameFromScanCode(key.Code, key.Code > 0xFF);
    var groupLabel = group is null ? "—" : localization.EnumName(group.Value);
    _detailsTitle.Text = label;
    _detailsBody.Text = $"{localization.Get("HeatmapTooltipPresses")}: {count.ToString("N0", CultureInfo.CurrentUICulture)}\n{localization.Get("HeatmapTooltipShare")}: {share:0.0}%\n{localization.Get("HeatmapTooltipGroup")}: {groupLabel}\n{localization.Get("HeatmapTooltipPeriod")}: {period}";
    var rect = KeyRect(key, unit, gap, originX);
    _detailsPopup.HorizontalOffset = Math.Min(rect.Left, Math.Max(0, ActualWidth - 290));
    _detailsPopup.VerticalOffset = rect.Bottom + 7;
    _detailsPopup.IsOpen = false;
    _detailsPopup.IsOpen = true;
    e.Handled = true;
  }

  private static void OnTooltipsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
  {
    if (sender is not KeyboardHeatmap heatmap) return;
    heatmap.Cursor = e.NewValue is true ? WpfCursors.Hand : WpfCursors.Arrow;
    if (e.NewValue is false) heatmap.CloseDetails();
  }

  private static void OnSnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
    ((KeyboardHeatmap)sender).CloseDetails();

  private (double Unit, double Gap, double OriginX) Geometry()
  {
    var unit = Math.Max(16, Math.Min(ActualWidth / 23.6, ActualHeight / 6.55));
    return (unit, Math.Clamp(unit * .08, 2, 4), Math.Max(0, (ActualWidth - unit * 23.5) / 2));
  }

  private static Rect KeyRect(KeyLayout key, double unit, double gap, double originX) =>
    new(originX + key.X * unit + gap / 2, key.Y * unit + gap / 2, key.Width * unit - gap, key.Height * unit - gap);

  private void CloseDetails()
  {
    _selectedCode = null;
    _detailsPopup.IsOpen = false;
  }

  private sealed record KeyLayout(int Code, double X, double Y, double Width = 1, double Height = 1);
}
