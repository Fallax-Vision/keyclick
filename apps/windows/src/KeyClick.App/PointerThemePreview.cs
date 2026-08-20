using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KeyClick.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace KeyClick.App;

public sealed class PointerThemePreview : FrameworkElement
{
  public static readonly DependencyProperty ThemeProperty = DependencyProperty.Register(
    nameof(Theme), typeof(PointerThemeDefinition), typeof(PointerThemePreview),
    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
  public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(
    nameof(Role), typeof(string), typeof(PointerThemePreview),
    new FrameworkPropertyMetadata("Arrow", FrameworkPropertyMetadataOptions.AffectsRender));

  public PointerThemeDefinition? Theme
  {
    get => (PointerThemeDefinition?)GetValue(ThemeProperty);
    set => SetValue(ThemeProperty, value);
  }
  public string Role { get => (string)GetValue(RoleProperty); set => SetValue(RoleProperty, value); }

  protected override void OnRender(DrawingContext drawingContext)
  {
    base.OnRender(drawingContext);
    if (Theme is not { } theme || ActualWidth <= 0 || ActualHeight <= 0) return;
    var primary = Brush(theme.Primary);
    var secondary = Brush(theme.Secondary);
    var outline = Brush(theme.Outline);
    var outlinePen = Pen(outline, 2.4);
    var accentPen = Pen(secondary, 2.2);
    var scale = Math.Min(ActualWidth, ActualHeight) / 64d;
    drawingContext.PushTransform(new TranslateTransform((ActualWidth - 64 * scale) / 2, (ActualHeight - 64 * scale) / 2));
    drawingContext.PushTransform(new ScaleTransform(scale, scale));

    DrawMotif(drawingContext, theme.Style, secondary, accentPen);
    DrawRole(drawingContext, Role, theme.Style, primary, secondary, outline, outlinePen, accentPen);

    drawingContext.Pop();
    drawingContext.Pop();
  }

  private static void DrawRole(DrawingContext context, string role, string style, Brush primary, Brush secondary, Brush outline, Pen outlinePen, Pen accentPen)
  {
    if (role is "Arrow" or "Help" or "AppStarting")
    {
      context.DrawGeometry(primary, outlinePen, ArrowGeometry());
      DrawAccent(context, style, secondary, accentPen, outline);
      if (role == "Help")
      {
        context.DrawEllipse(secondary, Pen(outline, 1.4), new Point(50, 14), 9, 9);
        context.DrawLine(Pen(Brush("#FFFFFFFF"), 2), new Point(48, 11), new Point(51, 9));
        context.DrawLine(Pen(Brush("#FFFFFFFF"), 2), new Point(51, 9), new Point(53, 12));
        context.DrawLine(Pen(Brush("#FFFFFFFF"), 2), new Point(53, 12), new Point(50, 15));
        context.DrawEllipse(Brush("#FFFFFFFF"), null, new Point(50, 19), 1.2, 1.2);
      }
      if (role == "AppStarting") context.DrawEllipse(null, accentPen, new Point(50, 15), 10, 10);
      return;
    }
    if (role == "Wait")
    {
      context.DrawEllipse(primary, outlinePen, new Point(32, 32), 22, 22);
      context.DrawGeometry(null, accentPen, ArcGeometry(new Point(32, 10), new Point(50, 42), new Size(22, 22)));
      context.DrawEllipse(secondary, null, new Point(32, 10), 4, 4);
      return;
    }
    if (role == "IBeam")
    {
      context.DrawLine(outlinePen, new Point(32, 9), new Point(32, 55));
      context.DrawLine(outlinePen, new Point(21, 10), new Point(43, 10));
      context.DrawLine(outlinePen, new Point(21, 54), new Point(43, 54));
      context.DrawEllipse(secondary, null, new Point(32, 32), 3, 3);
      return;
    }
    if (role == "Crosshair")
    {
      context.DrawEllipse(null, accentPen, new Point(32, 32), 15, 15);
      context.DrawLine(outlinePen, new Point(32, 4), new Point(32, 60));
      context.DrawLine(outlinePen, new Point(4, 32), new Point(60, 32));
      return;
    }
    if (role == "NWPen")
    {
      var nib = Polygon(new Point(13, 51), new Point(21, 25), new Point(46, 7), new Point(55, 16), new Point(37, 41));
      context.DrawGeometry(primary, outlinePen, nib);
      context.DrawLine(accentPen, new Point(20, 45), new Point(48, 12));
      context.DrawEllipse(secondary, null, new Point(17, 48), 4, 4);
      return;
    }
    if (role == "No")
    {
      context.DrawEllipse(primary, outlinePen, new Point(32, 32), 23, 23);
      context.DrawLine(accentPen, new Point(16, 16), new Point(48, 48));
      return;
    }
    if (role == "Hand")
    {
      var hand = Polygon(new Point(17, 29), new Point(17, 11), new Point(25, 11), new Point(25, 25), new Point(31, 19), new Point(38, 22), new Point(44, 24), new Point(49, 33), new Point(42, 54), new Point(24, 50), new Point(10, 34));
      context.DrawGeometry(primary, outlinePen, hand);
      context.DrawLine(accentPen, new Point(25, 25), new Point(25, 42));
      return;
    }
    if (role == "UpArrow")
    {
      var arrow = Polygon(new Point(32, 6), new Point(14, 27), new Point(25, 27), new Point(25, 57), new Point(39, 57), new Point(39, 27), new Point(50, 27));
      context.DrawGeometry(primary, outlinePen, arrow);
      context.DrawLine(accentPen, new Point(32, 12), new Point(32, 49));
      return;
    }
    DrawResize(context, role, secondary, outlinePen);
  }

  private static void DrawResize(DrawingContext context, string role, Brush accent, Pen outlinePen)
  {
    if (role == "SizeAll")
    {
      context.DrawLine(outlinePen, new Point(32, 7), new Point(32, 57));
      context.DrawLine(outlinePen, new Point(7, 32), new Point(57, 32));
      foreach (var point in new[] { new Point(32, 7), new Point(32, 57), new Point(7, 32), new Point(57, 32) }) context.DrawEllipse(accent, null, point, 4, 4);
      return;
    }
    var start = role switch
    {
      "SizeWE" => new Point(7, 32),
      "SizeNWSE" => new Point(11, 11),
      "SizeNESW" => new Point(11, 53),
      _ => new Point(32, 7)
    };
    var end = role switch
    {
      "SizeWE" => new Point(57, 32),
      "SizeNWSE" => new Point(53, 53),
      "SizeNESW" => new Point(53, 11),
      _ => new Point(32, 57)
    };
    context.DrawLine(outlinePen, start, end);
    context.DrawEllipse(accent, null, start, 5, 5);
    context.DrawEllipse(accent, null, end, 5, 5);
  }

  private static void DrawMotif(DrawingContext context, string style, Brush accent, Pen accentPen)
  {
    switch (style)
    {
      case "orbital":
        context.DrawEllipse(null, accentPen, new Point(33, 30), 23, 16);
        context.DrawEllipse(accent, null, new Point(53, 25), 3, 3);
        break;
      case "neon":
        context.DrawLine(Pen(accent, 5, 0.22), new Point(7, 54), new Point(31, 31));
        break;
      case "organic":
        context.DrawEllipse(WithOpacity(accent, 0.22), null, new Point(35, 34), 24, 24);
        break;
      case "pixel":
        context.DrawRectangle(WithOpacity(accent, 0.35), null, new Rect(5, 47, 8, 8));
        context.DrawRectangle(WithOpacity(accent, 0.6), null, new Rect(14, 40, 6, 6));
        break;
      case "liquid":
        context.DrawEllipse(WithOpacity(accent, 0.5), null, new Point(11, 51), 5, 5);
        context.DrawEllipse(WithOpacity(accent, 0.28), null, new Point(5, 58), 3, 3);
        break;
      case "geometric":
        context.DrawLine(accentPen, new Point(5, 52), new Point(16, 41));
        context.DrawEllipse(accent, null, new Point(5, 52), 2.5, 2.5);
        break;
      case "playful":
        context.DrawLine(accentPen, new Point(4, 56), new Point(21, 39));
        context.DrawLine(accentPen, new Point(2, 47), new Point(14, 38));
        context.DrawEllipse(accent, null, new Point(5, 56), 2.5, 2.5);
        break;
    }
  }

  private static void DrawAccent(DrawingContext context, string style, Brush accent, Pen accentPen, Brush outline)
  {
    switch (style)
    {
      case "clean":
        context.DrawLine(accentPen, new Point(16, 13), new Point(16, 39));
        break;
      case "orbital":
        context.DrawEllipse(accent, Pen(outline, 1.4), new Point(20, 19), 4, 4);
        break;
      case "glass":
        context.DrawGeometry(WithOpacity(accent, 0.72), null, Polygon(new Point(17, 12), new Point(18, 40), new Point(31, 31)));
        context.DrawLine(accentPen, new Point(18, 40), new Point(31, 31));
        break;
      case "neon":
        context.DrawLine(accentPen, new Point(17, 13), new Point(17, 40));
        context.DrawLine(accentPen, new Point(17, 40), new Point(27, 31));
        break;
      case "organic":
        context.DrawEllipse(accent, Pen(outline, 1.2), new Point(20, 20), 4.5, 4.5);
        break;
      case "pixel":
        context.DrawRectangle(accent, null, new Rect(15, 15, 6, 21));
        break;
      case "folded":
        context.DrawGeometry(accent, Pen(outline, 1.2), Polygon(new Point(16, 11), new Point(18, 40), new Point(33, 31)));
        break;
      case "liquid":
        context.DrawEllipse(accent, null, new Point(20, 21), 5, 5);
        context.DrawEllipse(WithOpacity(accent, 0.7), null, new Point(25, 33), 3, 3);
        break;
      case "geometric":
        context.DrawLine(accentPen, new Point(18, 15), new Point(18, 38));
        context.DrawEllipse(accent, null, new Point(18, 26), 2.5, 2.5);
        context.DrawLine(accentPen, new Point(18, 26), new Point(29, 30));
        break;
      case "playful":
        context.DrawGeometry(accent, null, StarGeometry(new Point(21, 21), 6));
        break;
    }
  }

  private static StreamGeometry ArrowGeometry() => Polygon(
    new Point(12, 7), new Point(13, 50), new Point(23, 40), new Point(33, 58),
    new Point(41, 53), new Point(31, 36), new Point(49, 34));

  private static StreamGeometry Polygon(params Point[] points)
  {
    var geometry = new StreamGeometry();
    using var context = geometry.Open();
    context.BeginFigure(points[0], true, true);
    context.PolyLineTo(points.Skip(1).ToArray(), true, false);
    geometry.Freeze();
    return geometry;
  }

  private static StreamGeometry StarGeometry(Point center, double radius)
  {
    var points = Enumerable.Range(0, 10).Select(index =>
    {
      var angle = -Math.PI / 2 + index * Math.PI / 5;
      var length = index % 2 == 0 ? radius : radius * 0.42;
      return new Point(center.X + Math.Cos(angle) * length, center.Y + Math.Sin(angle) * length);
    }).ToArray();
    return Polygon(points);
  }

  private static StreamGeometry ArcGeometry(Point start, Point end, Size radius)
  {
    var geometry = new StreamGeometry();
    using var context = geometry.Open();
    context.BeginFigure(start, false, false);
    context.ArcTo(end, radius, 0, false, SweepDirection.Clockwise, true, false);
    geometry.Freeze();
    return geometry;
  }

  private static SolidColorBrush Brush(string value)
  {
    var parsed = uint.Parse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    var color = value.Length == 9
      ? Color.FromArgb((byte)(parsed >> 24), (byte)(parsed >> 16), (byte)(parsed >> 8), (byte)parsed)
      : Color.FromRgb((byte)(parsed >> 16), (byte)(parsed >> 8), (byte)parsed);
    var brush = new SolidColorBrush(color);
    brush.Freeze();
    return brush;
  }

  private static SolidColorBrush WithOpacity(Brush source, double opacity)
  {
    var color = ((SolidColorBrush)source).Color;
    var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B));
    brush.Freeze();
    return brush;
  }

  private static Pen Pen(Brush brush, double thickness, double opacity = 1)
  {
    var selected = opacity >= 1 ? brush : WithOpacity(brush, opacity);
    var pen = new Pen(selected, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    pen.Freeze();
    return pen;
  }
}
