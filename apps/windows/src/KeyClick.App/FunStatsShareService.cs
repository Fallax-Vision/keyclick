using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KeyClick.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace KeyClick.App;

public sealed class FunStatsShareService
{
  public BitmapSource RenderShareCard(IReadOnlyList<FunStatTile> source, string period, DateTimeOffset generated, bool dark)
  {
    var tiles = source.Take(12).ToArray();
    var width = 1200;
    var height = tiles.Length <= 6 ? 630 : 1200;
    var background = new SolidColorBrush(dark ? Color.FromRgb(18, 20, 24) : Color.FromRgb(246, 248, 250));
    var card = new SolidColorBrush(dark ? Color.FromRgb(28, 31, 36) : Colors.White);
    var border = new SolidColorBrush(dark ? Color.FromRgb(66, 72, 82) : Color.FromRgb(205, 211, 220));
    var text = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(25, 29, 36));
    var muted = new SolidColorBrush(dark ? Color.FromRgb(177, 184, 195) : Color.FromRgb(91, 100, 114));
    var accent = new SolidColorBrush(Color.FromRgb(40, 196, 116));
    var visual = new DrawingVisual();
    using (var context = visual.RenderOpen())
    {
      context.DrawRectangle(background, null, new Rect(0, 0, width, height));
      DrawText(context, "KeyClick", 48, FontWeights.SemiBold, text, new Point(62, 42), width - 124);
      const double headerRight = 62;
      const double headerBlockWidth = 500;
      var headerBlockLeft = width - headerRight - headerBlockWidth;
      DrawText(context, LocalizationService.Current.Get("FunStats"), 28, FontWeights.SemiBold, accent,
        new Point(headerBlockLeft, 44), headerBlockWidth, TextAlignment.Right);
      DrawText(context, $"{period}  •  {generated.LocalDateTime:d}", 21, FontWeights.Normal, muted,
        new Point(headerBlockLeft, 86), headerBlockWidth, TextAlignment.Right);

      var columns = 3;
      var rows = Math.Max(1, (int)Math.Ceiling(tiles.Length / (double)columns));
      var left = 56d;
      var top = 160d;
      var gap = 18d;
      var cardWidth = (width - left * 2 - gap * (columns - 1)) / columns;
      var cardHeight = Math.Max(118, (height - top - 58 - gap * (rows - 1)) / rows);
      for (var index = 0; index < tiles.Length; index++)
      {
        var column = index % columns;
        var row = index / columns;
        var bounds = new Rect(left + column * (cardWidth + gap), top + row * (cardHeight + gap), cardWidth, cardHeight);
        context.DrawRoundedRectangle(card, new Pen(border, 1), bounds, 18, 18);
        var tile = tiles[index];
        DrawText(context, tile.Title, 19, FontWeights.SemiBold, muted, new Point(bounds.Left + 24, bounds.Top + 20), bounds.Width - 48);
        DrawText(context, tile.Value, 31, FontWeights.SemiBold, text, new Point(bounds.Left + 24, bounds.Top + 52), bounds.Width - 48);
        DrawText(context, tile.Detail, 16, FontWeights.Normal, muted, new Point(bounds.Left + 24, bounds.Top + 94), bounds.Width - 48);
        var track = new Rect(bounds.Left + 24, bounds.Bottom - 23, bounds.Width - 48, 7);
        context.DrawRoundedRectangle(border, null, track, 4, 4);
        context.DrawRoundedRectangle(accent, null, new Rect(track.Left, track.Top, track.Width * Math.Clamp(tile.Progress, 0, 1), track.Height), 4, 4);
      }
    }
    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
  }

  public BitmapSource CaptureElement(FrameworkElement element)
  {
    var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
    var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(element);
    bitmap.Freeze();
    return bitmap;
  }

  public void Copy(IReadOnlyList<FunStatTile> tiles, string period, string caption, FunStatsCopyMode mode, Window window, bool dark)
  {
    var image = mode == FunStatsCopyMode.WholeAppView
      ? CaptureElement(window)
      : RenderShareCard(tiles, period, DateTimeOffset.Now, dark);
    var data = CreateClipboardData(image, caption, mode == FunStatsCopyMode.ImageAndCaption);
    System.Windows.Clipboard.SetDataObject(data, true);
  }

  public DataObject CreateClipboardData(BitmapSource image, string caption, bool includeCaption)
  {
    var data = new DataObject();
    data.SetData(DataFormats.Bitmap, image);
    if (includeCaption) data.SetData(DataFormats.UnicodeText, caption);
    return data;
  }

  private static void DrawText(DrawingContext context, string value, double size, FontWeight weight, Brush brush, Point origin,
    double maxWidth, TextAlignment alignment = TextAlignment.Left)
  {
    var formatted = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
      new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, 1)
    {
      MaxTextWidth = Math.Max(1, maxWidth),
      TextAlignment = alignment,
      Trimming = TextTrimming.CharacterEllipsis
    };
    context.DrawText(formatted, origin);
  }
}
