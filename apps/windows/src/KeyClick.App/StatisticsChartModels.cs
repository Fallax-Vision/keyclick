using System.ComponentModel;
using System.Runtime.CompilerServices;
using KeyClick.Core;

namespace KeyClick.App;

public sealed record StatisticsChartSeries(string Id, string Label);
public sealed record StatisticsChartPoint(DateTimeOffset Start, DateTimeOffset End, IReadOnlyDictionary<string, double> Values);
public sealed record StatisticsChartModel(
  StatisticsChartMetricFamily Family,
  StatisticsChartViewType ViewType,
  StatisticsTrendGranularity Granularity,
  string Unit,
  IReadOnlyList<StatisticsChartSeries> Series,
  IReadOnlyList<StatisticsChartPoint> Points,
  IReadOnlyList<StatisticsChartPoint> ComparisonPoints);

public sealed class LocalizedOption : INotifyPropertyChanged
{
  private string _label;
  private bool _isEnabled;

  public LocalizedOption(string label, bool isEnabled = true)
  {
    _label = label;
    _isEnabled = isEnabled;
  }

  public string Label { get => _label; private set { if (_label == value) return; _label = value; Notify(); } }
  public bool IsEnabled { get => _isEnabled; private set { if (_isEnabled == value) return; _isEnabled = value; Notify(); } }
  public event PropertyChangedEventHandler? PropertyChanged;

  public void Update(LocalizedOption source)
  {
    Label = source.Label;
    IsEnabled = source.IsEnabled;
  }

  private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public static class StatisticsTrendAggregator
{
  private sealed class Bucket
  {
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; set; }
    public long Keyboard { get; set; }
    public long Typing { get; set; }
    public long Pointer { get; set; }
    public long Vertical { get; set; }
    public long Horizontal { get; set; }
    public long Active { get; set; }
    public long KeyboardActive { get; set; }
    public long PointerActive { get; set; }
    public int PeakTyping { get; set; }
    public int PeakClicks { get; set; }
  }

  public static StatisticsChartModel Build(StatisticsSnapshot? snapshot, StatisticsChartMetricFamily family,
    StatisticsChartViewType viewType, StatisticsTrendGranularity requested, IReadOnlyCollection<string> enabledSeries,
    LocalizationService localization)
  {
    if (family == StatisticsChartMetricFamily.Rates && viewType == StatisticsChartViewType.Donut)
      viewType = StatisticsChartViewType.Line;
    var raw = snapshot?.Trend ?? [];
    var granularity = EffectiveGranularity(raw, requested);
    var points = Aggregate(raw, granularity, family);
    var comparison = Aggregate(snapshot?.Comparison?.Trend ?? [], granularity, family);
    var available = AvailableSeries(family, localization);
    var selected = available.Where(item => enabledSeries.Contains(item.Id, StringComparer.Ordinal)).ToArray();
    if (selected.Length == 0) selected = available.Take(2).ToArray();
    var unit = family switch
    {
      StatisticsChartMetricFamily.Rates => localization.Get("ChartUnitRate"),
      StatisticsChartMetricFamily.ActiveTime => localization.Get("ChartUnitMinutes"),
      _ => localization.Get("ChartUnitCount")
    };
    return new(family, viewType, granularity, unit, selected, points, comparison);
  }

  public static bool IsGranularityAvailable(IReadOnlyList<StatisticsTrendPoint> points, StatisticsTrendGranularity granularity) =>
    granularity == StatisticsTrendGranularity.Auto || CountBuckets(points, granularity) <= 500;

  private static StatisticsTrendGranularity EffectiveGranularity(IReadOnlyList<StatisticsTrendPoint> points, StatisticsTrendGranularity requested)
  {
    var value = requested == StatisticsTrendGranularity.Auto ? Automatic(points) : requested;
    while (CountBuckets(points, value) > 500 && value < StatisticsTrendGranularity.Monthly)
      value = (StatisticsTrendGranularity)((int)value + 1);
    return value;
  }

  private static StatisticsTrendGranularity Automatic(IReadOnlyList<StatisticsTrendPoint> points)
  {
    if (points.Count < 2) return StatisticsTrendGranularity.Hourly;
    var duration = points[^1].BucketUtc - points[0].BucketUtc;
    if (duration <= TimeSpan.FromDays(2)) return StatisticsTrendGranularity.Hourly;
    if (duration <= TimeSpan.FromDays(90)) return StatisticsTrendGranularity.Daily;
    if (duration <= TimeSpan.FromDays(730)) return StatisticsTrendGranularity.Weekly;
    return StatisticsTrendGranularity.Monthly;
  }

  private static int CountBuckets(IReadOnlyList<StatisticsTrendPoint> points, StatisticsTrendGranularity granularity) =>
    points.Select(point => BucketStart(point.BucketUtc, granularity)).Distinct().Count();

  private static IReadOnlyList<StatisticsChartPoint> Aggregate(IReadOnlyList<StatisticsTrendPoint> points,
    StatisticsTrendGranularity granularity, StatisticsChartMetricFamily family)
  {
    var buckets = new SortedDictionary<DateTimeOffset, Bucket>();
    foreach (var point in points)
    {
      var start = BucketStart(point.BucketUtc, granularity);
      if (!buckets.TryGetValue(start, out var bucket))
      {
        bucket = new() { Start = start, End = BucketEnd(start, granularity) };
        buckets.Add(start, bucket);
      }
      bucket.Keyboard += point.KeyboardPresses;
      bucket.Typing += point.TypingKeyPresses;
      bucket.Pointer += point.PointerClicks;
      bucket.Vertical += point.VerticalScroll;
      bucket.Horizontal += point.HorizontalScroll;
      bucket.Active += point.ActiveMilliseconds;
      bucket.KeyboardActive += point.KeyboardActiveMilliseconds;
      bucket.PointerActive += point.PointerActiveMilliseconds;
      bucket.PeakTyping = Math.Max(bucket.PeakTyping, point.PeakTypingKeysPerMinute);
      bucket.PeakClicks = Math.Max(bucket.PeakClicks, point.PeakClicksPerFiveSeconds);
    }
    return buckets.Values.Select(bucket => new StatisticsChartPoint(bucket.Start, bucket.End, Values(bucket, family))).ToArray();
  }

  private static IReadOnlyDictionary<string, double> Values(Bucket bucket, StatisticsChartMetricFamily family) => family switch
  {
    StatisticsChartMetricFamily.Rates => new Dictionary<string, double>(StringComparer.Ordinal)
    {
      ["average-wpm"] = bucket.KeyboardActive <= 0 ? 0 : bucket.Typing * 60000d / bucket.KeyboardActive / 5,
      ["peak-wpm"] = bucket.PeakTyping / 5d,
      ["average-cps"] = bucket.PointerActive <= 0 ? 0 : bucket.Pointer * 1000d / bucket.PointerActive,
      ["peak-cps"] = bucket.PeakClicks / 5d
    },
    StatisticsChartMetricFamily.ActiveTime => new Dictionary<string, double>(StringComparer.Ordinal)
    {
      ["active"] = bucket.Active / 60000d,
      ["keyboard-active"] = bucket.KeyboardActive / 60000d,
      ["pointer-active"] = bucket.PointerActive / 60000d
    },
    _ => new Dictionary<string, double>(StringComparer.Ordinal)
    {
      ["keyboard"] = bucket.Keyboard,
      ["pointer"] = bucket.Pointer,
      ["vertical-scroll"] = bucket.Vertical,
      ["horizontal-scroll"] = bucket.Horizontal
    }
  };

  private static StatisticsChartSeries[] AvailableSeries(StatisticsChartMetricFamily family, LocalizationService localization) => family switch
  {
    StatisticsChartMetricFamily.Rates =>
    [
      new("average-wpm", localization.Get("ChartAverageWpm")), new("peak-wpm", localization.Get("ChartPeakWpm")),
      new("average-cps", localization.Get("ChartAverageCps")), new("peak-cps", localization.Get("ChartPeakCps"))
    ],
    StatisticsChartMetricFamily.ActiveTime =>
    [
      new("active", localization.Get("ChartActiveTime")), new("keyboard-active", localization.Get("ChartKeyboardActive")),
      new("pointer-active", localization.Get("ChartPointerActive"))
    ],
    _ =>
    [
      new("keyboard", localization.Get("Keyboard")), new("pointer", localization.Get("Pointer")),
      new("vertical-scroll", localization.Get("ChartVerticalScroll")), new("horizontal-scroll", localization.Get("ChartHorizontalScroll"))
    ]
  };

  private static DateTimeOffset BucketStart(DateTimeOffset utc, StatisticsTrendGranularity granularity)
  {
    var local = utc.ToLocalTime();
    var date = local.Date;
    DateTime start = granularity switch
    {
      StatisticsTrendGranularity.Monthly => new(date.Year, date.Month, 1),
      StatisticsTrendGranularity.Weekly => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
      StatisticsTrendGranularity.Daily => date,
      _ => new(date.Year, date.Month, date.Day, local.Hour, 0, 0)
    };
    return new(start, TimeZoneInfo.Local.GetUtcOffset(start));
  }

  private static DateTimeOffset BucketEnd(DateTimeOffset start, StatisticsTrendGranularity granularity) => granularity switch
  {
    StatisticsTrendGranularity.Monthly => start.AddMonths(1),
    StatisticsTrendGranularity.Weekly => start.AddDays(7),
    StatisticsTrendGranularity.Daily => start.AddDays(1),
    _ => start.AddHours(1)
  };
}
