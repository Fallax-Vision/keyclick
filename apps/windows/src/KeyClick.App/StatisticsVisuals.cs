using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KeyClick.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using WpfCursors = System.Windows.Input.Cursors;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace KeyClick.App;

public sealed class FunStatProgressBar : WpfProgressBar
{
  public static readonly DependencyProperty TargetValueProperty = DependencyProperty.Register(
    nameof(TargetValue), typeof(double), typeof(FunStatProgressBar), new PropertyMetadata(0d, OnPresentationChanged));
  public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
    nameof(Animate), typeof(bool), typeof(FunStatProgressBar), new PropertyMetadata(false, OnPresentationChanged));
  public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
    nameof(ReducedMotion), typeof(bool), typeof(FunStatProgressBar), new PropertyMetadata(false, OnPresentationChanged));
  private bool _presented;

  public FunStatProgressBar() => Loaded += (_, _) => Present();
  public double TargetValue { get => (double)GetValue(TargetValueProperty); set => SetValue(TargetValueProperty, value); }
  public bool Animate { get => (bool)GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
  public bool ReducedMotion { get => (bool)GetValue(ReducedMotionProperty); set => SetValue(ReducedMotionProperty, value); }

  private static void OnPresentationChanged(DependencyObject target, DependencyPropertyChangedEventArgs _) =>
    ((FunStatProgressBar)target).Present();

  private void Present()
  {
    if (!IsLoaded) return;
    var target = Math.Clamp(TargetValue, Minimum, Maximum);
    BeginAnimation(ValueProperty, null);
    if (!_presented && Animate && !ReducedMotion && SystemParameters.ClientAreaAnimation)
    {
      BeginAnimation(ValueProperty, new DoubleAnimation(Minimum, target, TimeSpan.FromMilliseconds(480))
      {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
      });
    }
    Value = target;
    _presented = true;
  }
}

public sealed class RadialProgress : FrameworkElement
{
  public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
    nameof(Progress), typeof(double), typeof(RadialProgress), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

  public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

  protected override Size MeasureOverride(Size availableSize) => new(50, 50);

  protected override void OnRender(DrawingContext context)
  {
    base.OnRender(context);
    var progress = Math.Clamp(Progress, 0, 1);
    var center = new Point(ActualWidth / 2, ActualHeight / 2);
    var radius = Math.Max(4, Math.Min(ActualWidth, ActualHeight) / 2 - 5);
    var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.LimeGreen;
    var track = TryFindResource("BorderBrush") as Brush ?? Brushes.Gray;
    var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
    context.DrawEllipse(null, new Pen(track, 5), center, radius, radius);
    DrawProgressArc(context, center, radius, progress, new Pen(accent, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    var formatted = new FormattedText($"{progress:P0}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
      new Typeface("Segoe UI"), 10, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
  }

  private static void DrawProgressArc(DrawingContext context, Point center, double radius, double progress, Pen pen)
  {
    if (progress <= 0) return;
    if (progress >= .9999)
    {
      context.DrawEllipse(null, pen, center, radius, radius);
      return;
    }
    var start = new Point(center.X, center.Y - radius);
    var angle = progress * Math.PI * 2 - Math.PI / 2;
    var end = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
    var geometry = new StreamGeometry();
    using (var stream = geometry.Open())
    {
      stream.BeginFigure(start, false, false);
      stream.ArcTo(end, new Size(radius, radius), 0, progress > .5, SweepDirection.Clockwise, true, false);
    }
    context.DrawGeometry(null, pen, geometry);
  }
}

public sealed class ActivityChart : FrameworkElement
{
  public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
    nameof(Model), typeof(StatisticsChartModel), typeof(ActivityChart),
    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnModelChanged));
  public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
    nameof(ReducedMotion), typeof(bool), typeof(ActivityChart), new PropertyMetadata(false));
  private static readonly Brush[] SeriesBrushes =
  [
    new SolidColorBrush(Color.FromRgb(24, 169, 91)),
    new SolidColorBrush(Color.FromRgb(91, 111, 216)),
    new SolidColorBrush(Color.FromRgb(226, 144, 50)),
    new SolidColorBrush(Color.FromRgb(183, 92, 211))
  ];
  private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 1);
  private readonly Popup _hoverPopup;
  private readonly TextBlock _hoverText;
  private int _hoverIndex = -1;
  private int _hoverSeries = -1;
  private Point _hoverPosition;
  private bool _pointerHoverActive;
  private bool _presented;

  public ActivityChart()
  {
    Focusable = true;
    Cursor = WpfCursors.Cross;
    _hoverText = new TextBlock { Margin = new Thickness(10, 7, 10, 7), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
    _hoverPopup = new Popup
    {
      PlacementTarget = this,
      Placement = PlacementMode.Relative,
      AllowsTransparency = true,
      StaysOpen = true,
      Child = new Border
      {
        Background = new SolidColorBrush(Color.FromArgb(245, 28, 31, 36)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(170, 128, 128, 128)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        Child = _hoverText
      }
    };
    MouseLeave += (_, _) => CloseHover();
    Unloaded += (_, _) => CloseHover();
    KeyDown += ActivityChart_KeyDown;
  }

  public StatisticsChartModel? Model { get => (StatisticsChartModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
  public bool ReducedMotion { get => (bool)GetValue(ReducedMotionProperty); set => SetValue(ReducedMotionProperty, value); }

  protected override void OnRender(DrawingContext drawingContext)
  {
    base.OnRender(drawingContext);
    var area = ChartArea();
    if (Model is not { Points.Count: > 0, Series.Count: > 0 } model)
    {
      DrawText(drawingContext, LocalizationService.Current.Get("NoStatisticsYet"), new(area.Left + 12, area.Top + area.Height / 2 - 8));
      return;
    }
    if (model.ViewType == StatisticsChartViewType.Donut)
      DrawDonut(drawingContext, area, model);
    else
      DrawCartesian(drawingContext, area, model);
    DrawLegend(drawingContext, area, model);
  }

  protected override void OnMouseMove(MouseEventArgs e)
  {
    base.OnMouseMove(e);
    if (Model is not { Points.Count: > 0, Series.Count: > 0 } model)
    {
      CloseHover();
      return;
    }
    Focus();
    UpdatePointerHover(e.GetPosition(this), model);
  }

  private void UpdatePointerHover(Point position, StatisticsChartModel model)
  {
    _pointerHoverActive = true;
    _hoverPosition = position;
    if (model.ViewType == StatisticsChartViewType.Donut)
      SelectDonut(position, model);
    else
      _hoverIndex = Math.Clamp((int)Math.Round((position.X - ChartArea().Left) / Math.Max(1, ChartArea().Width) * (model.Points.Count - 1)), 0, model.Points.Count - 1);
    ShowHover(position, model);
    InvalidateVisual();
  }

  private void DrawCartesian(DrawingContext context, Rect area, StatisticsChartModel model)
  {
    for (var line = 0; line <= 4; line++)
    {
      var y = area.Top + area.Height * line / 4d;
      context.DrawLine(GridPen, new(area.Left, y), new(area.Right, y));
    }
    var currentMaximum = model.Points.SelectMany(point => model.Series.Select(series => point.Values.GetValueOrDefault(series.Id))).DefaultIfEmpty().Max();
    var comparisonMaximum = model.ComparisonPoints.SelectMany(point => model.Series.Select(series => point.Values.GetValueOrDefault(series.Id))).DefaultIfEmpty().Max();
    var max = Math.Max(1, Math.Max(currentMaximum, comparisonMaximum));
    if (model.ViewType == StatisticsChartViewType.Bar)
      DrawBars(context, area, model, max);
    else
    {
      for (var seriesIndex = 0; seriesIndex < model.Series.Count; seriesIndex++)
      {
        var brush = SeriesBrushes[seriesIndex % SeriesBrushes.Length];
        DrawLineSeries(context, area, model.Points, model.Series[seriesIndex].Id, max, new Pen(brush, 2));
        if (model.ComparisonPoints.Count > 0)
        {
          var comparisonBrush = brush.Clone();
          comparisonBrush.Opacity = .55;
          DrawLineSeries(context, area, model.ComparisonPoints, model.Series[seriesIndex].Id, max,
            new Pen(comparisonBrush, 1) { DashStyle = DashStyles.Dash });
        }
      }
    }
    DrawText(context, max.ToString(model.Family == StatisticsChartMetricFamily.Counts ? "N0" : "0.#", CultureInfo.CurrentUICulture), new(0, area.Top));
    if (_hoverIndex >= 0 && _hoverIndex < model.Points.Count)
    {
      var x = model.Points.Count == 1 ? area.Left + area.Width / 2 : area.Left + area.Width * _hoverIndex / (model.Points.Count - 1d);
      context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(120, 190, 190, 190)), 1), new(x, area.Top), new(x, area.Bottom));
      for (var seriesIndex = 0; seriesIndex < model.Series.Count; seriesIndex++)
      {
        var value = model.Points[_hoverIndex].Values.GetValueOrDefault(model.Series[seriesIndex].Id);
        var y = area.Bottom - area.Height * value / max;
        context.DrawEllipse(SeriesBrushes[seriesIndex % SeriesBrushes.Length], new Pen(Brushes.White, 1), new(x, y), 4, 4);
      }
    }
  }

  private static void DrawLineSeries(DrawingContext context, Rect area, IReadOnlyList<StatisticsChartPoint> points, string seriesId, double max, Pen pen)
  {
    if (points.Count == 1)
    {
      var value = points[0].Values.GetValueOrDefault(seriesId);
      context.DrawEllipse(pen.Brush, null, new(area.Left + area.Width / 2, area.Bottom - area.Height * value / max), 3, 3);
      return;
    }
    var geometry = new StreamGeometry();
    using (var stream = geometry.Open())
    {
      for (var index = 0; index < points.Count; index++)
      {
        var value = points[index].Values.GetValueOrDefault(seriesId);
        var point = new Point(area.Left + area.Width * index / (points.Count - 1d), area.Bottom - area.Height * value / max);
        if (index == 0) stream.BeginFigure(point, false, false); else stream.LineTo(point, true, false);
      }
    }
    context.DrawGeometry(null, pen, geometry);
  }

  private static void DrawBars(DrawingContext context, Rect area, StatisticsChartModel model, double max)
  {
    var groupWidth = area.Width / Math.Max(1, model.Points.Count);
    var barWidth = Math.Max(1, Math.Min(22, groupWidth * .75 / model.Series.Count));
    for (var pointIndex = 0; pointIndex < model.Points.Count; pointIndex++)
    {
      var center = area.Left + groupWidth * (pointIndex + .5);
      for (var seriesIndex = 0; seriesIndex < model.Series.Count; seriesIndex++)
      {
        var value = model.Points[pointIndex].Values.GetValueOrDefault(model.Series[seriesIndex].Id);
        var height = area.Height * value / max;
        var left = center - barWidth * model.Series.Count / 2 + seriesIndex * barWidth;
        if (pointIndex < model.ComparisonPoints.Count)
        {
          var comparisonValue = model.ComparisonPoints[pointIndex].Values.GetValueOrDefault(model.Series[seriesIndex].Id);
          var comparisonHeight = area.Height * comparisonValue / max;
          var comparisonBrush = SeriesBrushes[seriesIndex % SeriesBrushes.Length].Clone();
          comparisonBrush.Opacity = .55;
          var comparisonPen = new Pen(comparisonBrush, 1) { DashStyle = DashStyles.Dash };
          context.DrawRoundedRectangle(null, comparisonPen,
            new Rect(left, area.Bottom - comparisonHeight, Math.Max(1, barWidth - 1), comparisonHeight), 2, 2);
        }
        var opacity = pointIndex == model.Points.Count - 1 || pointIndex == 0 ? .95 : .78;
        context.PushOpacity(opacity);
        context.DrawRoundedRectangle(SeriesBrushes[seriesIndex % SeriesBrushes.Length], null,
          new Rect(left, area.Bottom - height, Math.Max(1, barWidth - 1), height), 2, 2);
        context.Pop();
      }
    }
  }

  private void DrawDonut(DrawingContext context, Rect area, StatisticsChartModel model)
  {
    var center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
    var radius = Math.Max(10, Math.Min(area.Width, area.Height) * .38);
    var totals = model.Series.Select(series => model.Points.Sum(point => point.Values.GetValueOrDefault(series.Id))).ToArray();
    var total = Math.Max(1, totals.Sum());
    var start = -90d;
    for (var index = 0; index < totals.Length; index++)
    {
      var sweep = totals[index] / total * 360;
      DrawArc(context, center, radius, start, sweep, new Pen(SeriesBrushes[index % SeriesBrushes.Length], Math.Max(12, radius * .28)));
      start += sweep;
    }
    context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(55, 128, 128, 128)), 1), center, radius * .72, radius * .72);
    DrawCenteredText(context, total.ToString(model.Family == StatisticsChartMetricFamily.Counts ? "N0" : "0.#", CultureInfo.CurrentUICulture), center, 18);
    if (_hoverSeries >= 0 && _hoverSeries < totals.Length)
      context.DrawEllipse(null, new Pen(SeriesBrushes[_hoverSeries % SeriesBrushes.Length], 3), center, radius * 1.18, radius * 1.18);
  }

  private static void DrawArc(DrawingContext context, Point center, double radius, double startDegrees, double sweepDegrees, Pen pen)
  {
    if (sweepDegrees <= 0) return;
    if (sweepDegrees >= 359.999)
    {
      context.DrawEllipse(null, pen, center, radius, radius);
      return;
    }
    var start = PointOnCircle(center, radius, startDegrees);
    var end = PointOnCircle(center, radius, startDegrees + sweepDegrees);
    var geometry = new StreamGeometry();
    using (var stream = geometry.Open())
    {
      stream.BeginFigure(start, false, false);
      stream.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
    }
    context.DrawGeometry(null, pen, geometry);
  }

  private void DrawLegend(DrawingContext context, Rect area, StatisticsChartModel model)
  {
    var x = area.Left;
    var y = area.Bottom + 9;
    for (var index = 0; index < model.Series.Count; index++)
    {
      context.DrawRectangle(SeriesBrushes[index % SeriesBrushes.Length], null, new Rect(x, y + 2, 10, 10));
      var text = Formatted(model.Series[index].Label, 11);
      context.DrawText(text, new Point(x + 15, y));
      x += 25 + text.Width;
      if (x > area.Right - 100) { x = area.Left; y += 18; }
    }
  }

  private void SelectDonut(Point position, StatisticsChartModel model)
  {
    var area = ChartArea();
    var center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
    var angle = Math.Atan2(position.Y - center.Y, position.X - center.X) * 180 / Math.PI + 90;
    if (angle < 0) angle += 360;
    var totals = model.Series.Select(series => model.Points.Sum(point => point.Values.GetValueOrDefault(series.Id))).ToArray();
    var total = Math.Max(1, totals.Sum());
    var cursor = 0d;
    _hoverSeries = 0;
    for (var index = 0; index < totals.Length; index++)
    {
      cursor += totals[index] / total * 360;
      if (angle <= cursor) { _hoverSeries = index; break; }
    }
  }

  private void ShowHover(Point position, StatisticsChartModel model)
  {
    _hoverPosition = position;
    if (model.ViewType == StatisticsChartViewType.Donut)
    {
      var index = Math.Clamp(_hoverSeries, 0, model.Series.Count - 1);
      var total = model.Points.Sum(point => point.Values.GetValueOrDefault(model.Series[index].Id));
      var all = Math.Max(1, model.Series.Sum(series => model.Points.Sum(point => point.Values.GetValueOrDefault(series.Id))));
      _hoverText.Text = $"{model.Series[index].Label}\n{FormatValue(total, model.Family)} · {total / all:P1}";
      if (model.ComparisonPoints.Count > 0)
      {
        var comparison = model.ComparisonPoints.Sum(point => point.Values.GetValueOrDefault(model.Series[index].Id));
        var delta = comparison == 0 ? (total == 0 ? 0 : 100) : (total - comparison) * 100 / comparison;
        _hoverText.Text += $"\n{LocalizationService.Current.Format("ChartComparisonDeltaFormat", delta)}";
      }
    }
    else
    {
      _hoverIndex = Math.Clamp(_hoverIndex, 0, model.Points.Count - 1);
      var point = model.Points[_hoverIndex];
      var lines = new List<string> { FormatRange(point.Start, point.End, model.Granularity) };
      for (var index = 0; index < model.Series.Count; index++)
      {
        var series = model.Series[index];
        var line = $"{series.Label}: {FormatValue(point.Values.GetValueOrDefault(series.Id), model.Family)}";
        if (_hoverIndex < model.ComparisonPoints.Count)
          line += $"  ({LocalizationService.Current.Get("ChartPrevious")}: {FormatValue(model.ComparisonPoints[_hoverIndex].Values.GetValueOrDefault(series.Id), model.Family)})";
        lines.Add(line);
      }
      _hoverText.Text = string.Join(Environment.NewLine, lines);
    }
    _hoverPopup.HorizontalOffset = Math.Clamp(position.X + 14, 0, Math.Max(0, ActualWidth - 310));
    _hoverPopup.VerticalOffset = Math.Clamp(position.Y + 14, 0, Math.Max(0, ActualHeight - 130));
    _hoverPopup.IsOpen = true;
  }

  private void ActivityChart_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
  {
    if (Model is not { Points.Count: > 0 } model) return;
    if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
    if (model.ViewType == StatisticsChartViewType.Donut)
    {
      _pointerHoverActive = false;
      _hoverSeries = e.Key switch
      {
        Key.Home => 0,
        Key.End => model.Series.Count - 1,
        Key.Left => Math.Max(0, _hoverSeries < 0 ? model.Series.Count - 1 : _hoverSeries - 1),
        _ => Math.Min(model.Series.Count - 1, _hoverSeries + 1)
      };
      ShowHover(new Point(ActualWidth / 2, ActualHeight / 2), model);
      InvalidateVisual();
      e.Handled = true;
      return;
    }
    _pointerHoverActive = false;
    _hoverIndex = e.Key switch
    {
      Key.Home => 0,
      Key.End => model.Points.Count - 1,
      Key.Left => Math.Max(0, _hoverIndex < 0 ? model.Points.Count - 1 : _hoverIndex - 1),
      _ => Math.Min(model.Points.Count - 1, _hoverIndex + 1)
    };
    var area = ChartArea();
    var x = model.Points.Count == 1 ? area.Left + area.Width / 2 : area.Left + area.Width * _hoverIndex / (model.Points.Count - 1d);
    ShowHover(new Point(x, area.Top + 12), model);
    InvalidateVisual();
    e.Handled = true;
  }

  private void CloseHover()
  {
    _hoverPopup.IsOpen = false;
    _hoverIndex = -1;
    _hoverSeries = -1;
    _pointerHoverActive = false;
    InvalidateVisual();
  }

  private Rect ChartArea() => new(46, 12, Math.Max(0, ActualWidth - 58), Math.Max(0, ActualHeight - 58));
  private static Point PointOnCircle(Point center, double radius, double degrees)
  {
    var radians = degrees * Math.PI / 180;
    return new(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
  }
  private static string FormatRange(DateTimeOffset start, DateTimeOffset end, StatisticsTrendGranularity granularity) => granularity switch
  {
    StatisticsTrendGranularity.Hourly => start.ToString("g", CultureInfo.CurrentUICulture),
    StatisticsTrendGranularity.Daily => start.ToString("D", CultureInfo.CurrentUICulture),
    _ => $"{start:d} – {end.AddTicks(-1):d}"
  };
  private static string FormatValue(double value, StatisticsChartMetricFamily family) => value.ToString(family == StatisticsChartMetricFamily.Counts ? "N0" : "0.#", CultureInfo.CurrentUICulture);
  private static FormattedText Formatted(string text, double size) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Gray, 1.0);
  private static void DrawCenteredText(DrawingContext context, string text, Point center, double size)
  {
    var formatted = Formatted(text, size);
    context.DrawText(formatted, new(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
  }
  private static void OnModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
  {
    if (sender is not ActivityChart chart) return;
    if (args.NewValue is not StatisticsChartModel { Points.Count: > 0, Series.Count: > 0 } model)
    {
      chart.CloseHover();
      return;
    }
    if (chart._pointerHoverActive)
    {
      if (chart.IsMouseOver)
        chart.UpdatePointerHover(Mouse.GetPosition(chart), model);
      else
        chart.CloseHover();
    }
    else if (chart._hoverPopup.IsOpen)
    {
      chart.ShowHover(chart._hoverPosition, model);
      chart.InvalidateVisual();
    }
    if (chart._presented) return;
    chart._presented = true;
    if (chart.ReducedMotion || !SystemParameters.ClientAreaAnimation) return;
    chart.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
    {
      EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
    });
  }

  private static void DrawText(DrawingContext context, string text, Point origin) => context.DrawText(
    new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Gray, 1.0), origin);
}

public sealed class TypingSpeedChart : FrameworkElement
{
  public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
    nameof(Samples), typeof(IReadOnlyList<TypingChallengeSample>), typeof(TypingSpeedChart),
    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
  private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 1);
  private static readonly Pen SpeedPen = new(new SolidColorBrush(Color.FromRgb(24, 169, 91)), 2);

  public IReadOnlyList<TypingChallengeSample>? Samples { get => (IReadOnlyList<TypingChallengeSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

  protected override void OnRender(DrawingContext drawingContext)
  {
    base.OnRender(drawingContext);
    var area = new Rect(38, 12, Math.Max(0, ActualWidth - 50), Math.Max(0, ActualHeight - 36));
    for (var line = 0; line <= 4; line++)
    {
      var y = area.Top + area.Height * line / 4d;
      drawingContext.DrawLine(GridPen, new(area.Left, y), new(area.Right, y));
    }
    if (Samples is not { Count: > 0 } samples)
    {
      DrawChartText(drawingContext, LocalizationService.Current.Get("NoStatisticsYet"), new(area.Left + 12, area.Top + area.Height / 2 - 8));
      return;
    }
    var max = Math.Max(1, samples.Max(value => value.NetWordsPerMinute));
    if (samples.Count == 1)
      drawingContext.DrawEllipse(SpeedPen.Brush, null, new(area.Left + area.Width / 2, area.Bottom - area.Height * samples[0].NetWordsPerMinute / max), 4, 4);
    else
    {
      var geometry = new StreamGeometry();
      using var stream = geometry.Open();
      for (var index = 0; index < samples.Count; index++)
      {
        var point = new Point(area.Left + area.Width * index / (samples.Count - 1d), area.Bottom - area.Height * samples[index].NetWordsPerMinute / max);
        if (index == 0) stream.BeginFigure(point, false, false); else stream.LineTo(point, true, false);
      }
      drawingContext.DrawGeometry(null, SpeedPen, geometry);
    }
    DrawChartText(drawingContext, $"{max:0} WPM", new(0, area.Top));
  }

  protected override void OnMouseMove(MouseEventArgs e)
  {
    base.OnMouseMove(e);
    if (Samples is not { Count: > 0 } samples) return;
    var index = Math.Clamp((int)Math.Round((e.GetPosition(this).X - 38) / Math.Max(1, ActualWidth - 50) * (samples.Count - 1)), 0, samples.Count - 1);
    var sample = samples[index];
    ToolTip = LocalizationService.Current.Format("ChallengeChartTooltipFormat", sample.IntervalIndex * 5, (sample.IntervalIndex + 1) * 5, sample.NetWordsPerMinute, sample.Errors);
  }

  private static void DrawChartText(DrawingContext context, string text, Point origin) => context.DrawText(
    new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.Gray, 1.0), origin);
}

public sealed class KeyboardHeatmap : FrameworkElement
{
  private const double LayoutWidth = 23.6;
  private const double LayoutHeight = 6.55;
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
  private readonly MouseButtonEventHandler _outsideClickHandler;
  private readonly List<ScrollViewer> _scrollOwners = [];
  private Window? _ownerWindow;
  private int? _selectedCode;

  public KeyboardHeatmap()
  {
    Cursor = WpfCursors.Hand;
    _outsideClickHandler = OwnerWindow_PreviewMouseDown;
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
      StaysOpen = true
    };
    _detailsPopup.Closed += (_, _) => _selectedCode = null;
    Loaded += KeyboardHeatmap_Loaded;
    Unloaded += KeyboardHeatmap_Unloaded;
    SizeChanged += KeyboardHeatmap_SizeChanged;
  }

  public StatisticsSnapshot? Snapshot { get => (StatisticsSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
  public bool TooltipsEnabled { get => (bool)GetValue(TooltipsEnabledProperty); set => SetValue(TooltipsEnabledProperty, value); }

  protected override Size MeasureOverride(Size availableSize)
  {
    var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : LayoutWidth * 36;
    return new Size(width, width * LayoutHeight / LayoutWidth);
  }

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
      var cornerRadius = unit * .1;
      context.DrawRoundedRectangle(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)), unit * .025), rect, cornerRadius, cornerRadius);
      var label = LocalizationService.Current.KeyNameFromScanCode(key.Code, key.Code > 0xFF);
      var textInset = unit * .08;
      var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), unit * .22, Brushes.White, 1.0)
      { MaxTextWidth = Math.Max(0, rect.Width - textInset * 2), Trimming = TextTrimming.CharacterEllipsis };
      context.DrawText(text, new(rect.Left + textInset, rect.Top + (rect.Height - text.Height) / 2));
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
    if ((_ownerWindow ?? Window.GetWindow(this))?.IsActive != true)
    {
      CloseDetails();
      return;
    }
    _selectedCode = key.Code;
    UpdateDetails(key);
    PositionDetails(key);
    _detailsPopup.IsOpen = true;
    e.Handled = true;
  }

  private void UpdateDetails(KeyLayout key)
  {
    if (Snapshot is null) return;
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
  }

  private static void OnTooltipsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
  {
    if (sender is not KeyboardHeatmap heatmap) return;
    heatmap.Cursor = e.NewValue is true ? WpfCursors.Hand : WpfCursors.Arrow;
    if (e.NewValue is false) heatmap.CloseDetails();
  }

  private static void OnSnapshotChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
  {
    var heatmap = (KeyboardHeatmap)sender;
    if (!heatmap._detailsPopup.IsOpen || heatmap._selectedCode is not { } selectedCode) return;
    var key = Keys.FirstOrDefault(item => item.Code == selectedCode);
    if (key is null) return;
    heatmap.UpdateDetails(key);
    heatmap.PositionDetails(key);
  }

  private void KeyboardHeatmap_Loaded(object sender, RoutedEventArgs e)
  {
    var window = Window.GetWindow(this);
    if (ReferenceEquals(window, _ownerWindow)) return;
    DetachOutsideClickHandler();
    _ownerWindow = window;
    if (_ownerWindow is null) return;
    _ownerWindow.AddHandler(UIElement.PreviewMouseDownEvent, _outsideClickHandler, true);
    _ownerWindow.Deactivated += OwnerWindow_Deactivated;
    _ownerWindow.SizeChanged += OwnerWindow_SizeChanged;
    AttachScrollHandlers();
  }

  private void KeyboardHeatmap_Unloaded(object sender, RoutedEventArgs e)
  {
    DetachScrollHandlers();
    DetachOutsideClickHandler();
    CloseDetails();
  }

  private void OwnerWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
  {
    if (_detailsPopup.IsOpen && !IsMouseOver) CloseDetails();
  }

  private void OwnerWindow_Deactivated(object? sender, EventArgs e) => CloseDetails();

  private void OwnerWindow_SizeChanged(object sender, SizeChangedEventArgs e) => RepositionOpenDetails();

  private void KeyboardHeatmap_SizeChanged(object sender, SizeChangedEventArgs e) => RepositionOpenDetails();

  private void AncestorScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) => RepositionOpenDetails(true);

  private void AttachScrollHandlers()
  {
    DetachScrollHandlers();
    DependencyObject? current = this;
    while ((current = VisualTreeHelper.GetParent(current)) is not null)
    {
      if (current is not ScrollViewer scrollViewer || _scrollOwners.Contains(scrollViewer)) continue;
      scrollViewer.ScrollChanged += AncestorScrollViewer_ScrollChanged;
      _scrollOwners.Add(scrollViewer);
    }
  }

  private void DetachScrollHandlers()
  {
    foreach (var scrollViewer in _scrollOwners) scrollViewer.ScrollChanged -= AncestorScrollViewer_ScrollChanged;
    _scrollOwners.Clear();
  }

  private void DetachOutsideClickHandler()
  {
    if (_ownerWindow is null) return;
    _ownerWindow.RemoveHandler(UIElement.PreviewMouseDownEvent, _outsideClickHandler);
    _ownerWindow.Deactivated -= OwnerWindow_Deactivated;
    _ownerWindow.SizeChanged -= OwnerWindow_SizeChanged;
    _ownerWindow = null;
  }

  private void RepositionOpenDetails(bool forceNativeReposition = false)
  {
    if (!_detailsPopup.IsOpen || _selectedCode is not { } selectedCode) return;
    var key = Keys.FirstOrDefault(item => item.Code == selectedCode);
    if (key is not null) PositionDetails(key, forceNativeReposition);
  }

  private void PositionDetails(KeyLayout key, bool forceNativeReposition = false)
  {
    var bounds = (_ownerWindow?.Content as FrameworkElement) ?? _ownerWindow;
    if (bounds is null || bounds.ActualWidth <= 0 || bounds.ActualHeight <= 0) return;

    _detailsPopup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    var popupSize = _detailsPopup.Child.DesiredSize;
    var (unit, gap, originX) = Geometry();
    var keyBounds = KeyRect(key, unit, gap, originX);
    var heatmapOrigin = TranslatePoint(new Point(0, 0), bounds);
    var keyLeft = heatmapOrigin.X + keyBounds.Left;
    var keyTop = heatmapOrigin.Y + keyBounds.Top;
    var keyBottom = heatmapOrigin.Y + keyBounds.Bottom;
    const double windowMargin = 8;
    const double popupGap = 7;

    var minX = windowMargin;
    var maxX = Math.Max(minX, bounds.ActualWidth - popupSize.Width - windowMargin);
    var popupX = Math.Clamp(keyLeft + (keyBounds.Width - popupSize.Width) / 2, minX, maxX);
    var belowY = keyBottom + popupGap;
    var aboveY = keyTop - popupSize.Height - popupGap;
    var canPlaceBelow = belowY + popupSize.Height <= bounds.ActualHeight - windowMargin;
    var canPlaceAbove = aboveY >= windowMargin;
    var preferAbove = keyTop + keyBounds.Height / 2 > bounds.ActualHeight / 2;
    var popupY = preferAbove && canPlaceAbove
      ? aboveY
      : canPlaceBelow
        ? belowY
        : canPlaceAbove
          ? aboveY
          : Math.Clamp(belowY, windowMargin, Math.Max(windowMargin, bounds.ActualHeight - popupSize.Height - windowMargin));

    var horizontalOffset = popupX - heatmapOrigin.X;
    var verticalOffset = popupY - heatmapOrigin.Y;
    if (forceNativeReposition)
    {
      // Popup content lives in a separate native window. Nudging an offset forces WPF
      // to follow a placement target that moved because an ancestor scrolled.
      _detailsPopup.HorizontalOffset = horizontalOffset + 0.01;
      _detailsPopup.VerticalOffset = verticalOffset + 0.01;
    }
    _detailsPopup.HorizontalOffset = horizontalOffset;
    _detailsPopup.VerticalOffset = verticalOffset;
  }

  private (double Unit, double Gap, double OriginX) Geometry()
  {
    var unit = Math.Max(0, Math.Min(ActualWidth / LayoutWidth, ActualHeight / LayoutHeight));
    return (unit, unit * .08, Math.Max(0, (ActualWidth - unit * 23.5) / 2));
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
