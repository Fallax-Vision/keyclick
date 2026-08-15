using System.Reflection;
using System.Xml.Linq;
using KeyClick.App;
using KeyClick.Core;

namespace KeyClick.Tests;

public sealed class FunStatsTests
{
  [Fact]
  public void Offline_catalog_is_versioned_complete_unique_and_localized()
  {
    var root = FindRepositoryRoot();
    var facts = FunStatsCatalog.Facts;
    Assert.True(facts.Count >= 36);
    Assert.Equal(facts.Count, facts.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
    Assert.Contains(facts, item => item.Id == "clicks-worm-cells" && item.Target == 959 && !item.Approximate);
    Assert.Contains(facts, item => item.Id == "total-amoeba-cells" && item.Target == 1);
    Assert.Contains(facts, item => item.Id == "scroll-moon" && item.Target == 38_440_000_000 && item.Approximate);
    Assert.Contains(facts, item => item.Id == "scroll-mars" && item.Approximate);

    var schema = File.ReadAllText(Path.Combine(root, "shared", "specs", "fun-stats.v1.schema.json"));
    Assert.Contains("\"const\": 1", schema);
    Assert.Contains("\"minItems\": 36", schema);

    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    foreach (var language in new[] { "en", "fr" })
    {
      var resource = XDocument.Load(Path.Combine(root, "apps", "windows", "src", "KeyClick.App", "Resources", $"Strings.{language}.xaml"));
      var keys = resource.Descendants().Select(item => item.Attribute(x + "Key")?.Value).Where(item => item is not null).ToHashSet(StringComparer.Ordinal);
      Assert.All(facts, fact =>
      {
        Assert.Contains(fact.TitleKey, keys!);
        Assert.Contains(fact.SourceKey, keys!);
      });
    }
  }

  [Fact]
  public void Imported_fun_settings_are_bounded_validated_and_catalog_sanitized()
  {
    var settings = new AppSettings
    {
      ScrollCentimetersPerDetent = double.PositiveInfinity,
      FunFactRotation = (FunFactRotation)999,
      FunStatsCopyMode = (FunStatsCopyMode)999,
      HomeFunStatsPeriod = StatisticsPeriod.Custom,
      StatisticsChartMetricFamily = StatisticsChartMetricFamily.Rates,
      StatisticsChartViewType = StatisticsChartViewType.Donut,
      StatisticsTrendGranularity = (StatisticsTrendGranularity)999,
      SelectedFunStatIds = Enumerable.Range(0, 15).Select(index => $"safe-{index}").Append("unsafe id").ToList(),
      DisabledFunFactIds = ["keyboard-planets", "unknown-fact", "bad/id"],
      EnabledStatisticsChartSeries = ["bad-series"],
      CustomFunStats =
      [
        new() { Id = "custom-valid", Label = "  My milestone  ", Metric = FunStatMetric.PointerClicks, Target = 100 },
        new() { Id = "custom-invalid", Label = "", Metric = FunStatMetric.PointerClicks, Target = -1 }
      ]
    };

    settings.NormalizeFunStats();
    Assert.Equal(1.27, settings.ScrollCentimetersPerDetent, 2);
    Assert.Equal(FunFactRotation.OneHour, settings.FunFactRotation);
    Assert.Equal(FunStatsCopyMode.ImageOnly, settings.FunStatsCopyMode);
    Assert.Equal(StatisticsPeriod.AllTime, settings.HomeFunStatsPeriod);
    Assert.Equal(StatisticsTrendGranularity.Auto, settings.StatisticsTrendGranularity);
    Assert.Equal(StatisticsChartViewType.Line, settings.StatisticsChartViewType);
    Assert.Equal(12, settings.SelectedFunStatIds.Count);
    Assert.Single(settings.CustomFunStats);
    Assert.Equal("My milestone", settings.CustomFunStats[0].Label);
    Assert.Equal(new[] { "keyboard", "pointer", "vertical-scroll", "horizontal-scroll" }, settings.EnabledStatisticsChartSeries);

    _ = new FunStatsEngine(settings, new LocalizationService());
    Assert.Empty(settings.SelectedFunStatIds);
    Assert.Equal(new[] { "keyboard-planets" }, settings.DisabledFunFactIds);
  }

  [Fact]
  public void Legacy_settings_enable_fun_stats_and_receive_default_tiles_once()
  {
    var settings = new AppSettings
    {
      FunStatsEnabled = false,
      MetricCardFunFactsEnabled = false,
      FunStatsPreferencesVersion = 0,
      SelectedFunStatIds = []
    };

    settings.NormalizeFunStats();

    Assert.True(settings.FunStatsEnabled);
    Assert.True(settings.MetricCardFunFactsEnabled);
    Assert.Equal(AppSettings.CurrentFunStatsPreferencesVersion, settings.FunStatsPreferencesVersion);
    Assert.Equal(6, settings.SelectedFunStatIds.Count);

    settings.FunStatsEnabled = false;
    settings.MetricCardFunFactsEnabled = false;
    settings.NormalizeFunStats();
    Assert.False(settings.FunStatsEnabled);
    Assert.False(settings.MetricCardFunFactsEnabled);
  }

  [Fact]
  public void Built_in_milestones_advance_while_custom_milestones_stop_at_completion()
  {
    var settings = new AppSettings
    {
      ScrollCentimetersPerDetent = 1.27,
      SelectedFunStatIds = ["scroll-eiffel", "custom-clicks"],
      CustomFunStats = [new() { Id = "custom-clicks", Label = "Personal click goal", Metric = FunStatMetric.PointerClicks, Target = 100 }]
    };
    settings.NormalizeFunStats();
    var engine = new FunStatsEngine(settings, new LocalizationService());
    var snapshot = Snapshot(pointer: 250, vertical: 30_000);

    var tiles = engine.Dashboard(snapshot);
    Assert.Equal("scroll-burj-khalifa", tiles[0].Id);
    Assert.Equal(FunStatVisualType.Route, tiles[0].VisualType);
    Assert.Equal("custom-clicks", tiles[1].Id);
    Assert.Equal(FunStatVisualType.Linear, tiles[1].VisualType);
    Assert.Equal(1, tiles[1].Progress);
    Assert.Equal(38_100, engine.MetricValue(snapshot, FunStatMetric.ScrollDistanceCentimeters), 3);

    var zero = engine.Dashboard(Snapshot());
    Assert.All(zero, tile => Assert.True(double.IsFinite(tile.Progress) && tile.Progress is >= 0 and <= 1));
    var huge = Snapshot(pointer: long.MaxValue / 4, vertical: long.MaxValue / 4);
    Assert.All(engine.Dashboard(huge), tile => Assert.True(double.IsFinite(tile.Progress) && tile.Progress is >= 0 and <= 1));
  }

  [Fact]
  public void Rate_tiles_use_radial_and_equivalence_visuals()
  {
    var settings = new AppSettings
    {
      SelectedFunStatIds = ["average-wpm-casual", "average-cps-heartbeat"]
    };
    settings.NormalizeFunStats();
    var engine = new FunStatsEngine(settings, new LocalizationService());
    var snapshot = Snapshot(pointer: 60, typing: 100, keyboardActive: 60_000, pointerActive: 60_000);

    var tiles = engine.Dashboard(snapshot);

    Assert.Equal(FunStatVisualType.Radial, tiles[0].VisualType);
    Assert.Equal(FunStatVisualType.Equivalence, tiles[1].VisualType);
  }

  [Fact]
  public void Rotation_buckets_are_stable_within_the_selected_cadence()
  {
    var settings = new AppSettings { FunFactRotation = FunFactRotation.OneHour };
    var engine = new FunStatsEngine(settings, new LocalizationService());
    var method = typeof(FunStatsEngine).GetMethod("RotationSeed", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var first = new DateTimeOffset(2026, 8, 15, 10, 2, 0, TimeSpan.Zero);
    var sameHour = first.AddMinutes(40);
    var nextHour = first.AddHours(1);

    Assert.Equal(method.Invoke(engine, ["keyboard", first]), method.Invoke(engine, ["keyboard", sameHour]));
    Assert.NotEqual(method.Invoke(engine, ["keyboard", first]), method.Invoke(engine, ["keyboard", nextHour]));
  }

  [Fact]
  public void Scroll_calibration_accepts_only_safe_positive_estimates()
  {
    Assert.True(FunStatsEngine.TryCalculateScrollCalibration(25.4, 20, out var estimate));
    Assert.Equal(1.27, estimate, 3);
    Assert.False(FunStatsEngine.TryCalculateScrollCalibration(0, 20, out _));
    Assert.False(FunStatsEngine.TryCalculateScrollCalibration(1, 1000, out _));
    Assert.False(FunStatsEngine.TryCalculateScrollCalibration(1000, 1, out _));
    Assert.False(FunStatsEngine.TryCalculateScrollCalibration(double.NaN, 20, out _));
    var formatter = new FunStatsEngine(new AppSettings(), new LocalizationService());
    Assert.Equal("FunCentimetersFormat", formatter.FormatMetric(FunStatMetric.ScrollDistanceCentimeters, 50));
    Assert.Equal("FunMetersFormat", formatter.FormatMetric(FunStatMetric.ScrollDistanceCentimeters, 5_000));
    Assert.Equal("FunHectometersFormat", formatter.FormatMetric(FunStatMetric.ScrollDistanceCentimeters, 50_000));
    Assert.Equal("FunKilometersFormat", formatter.FormatMetric(FunStatMetric.ScrollDistanceCentimeters, 500_000));
  }

  [Fact]
  public void Share_cards_use_social_dimensions_and_contain_only_supplied_aggregate_tiles()
  {
    _ = new LocalizationService();
    var service = new FunStatsShareService();
    var tile = new FunStatTile("safe", "Clicks", "1,000", "Halfway there", "Offline source", 0.5, true, false, false);

    var landscape = service.RenderShareCard(Enumerable.Repeat(tile, 6).ToArray(), "All time", new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), false);
    var square = service.RenderShareCard(Enumerable.Repeat(tile, 7).ToArray(), "All time", new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), true);

    Assert.Equal(1200, landscape.PixelWidth);
    Assert.Equal(630, landscape.PixelHeight);
    Assert.Equal(1200, square.PixelWidth);
    Assert.Equal(1200, square.PixelHeight);
    Assert.True(CountAccentPixels(landscape, 700, 1180, 30, 130) > 20);
    Assert.Equal(0, CountAccentPixels(landscape, 30, 500, 100, 150));
    var imageOnly = service.CreateClipboardData(landscape, "Private aggregate caption", false);
    Assert.True(imageOnly.GetDataPresent(System.Windows.DataFormats.Bitmap));
    Assert.False(imageOnly.GetDataPresent(System.Windows.DataFormats.UnicodeText));
    var imageAndCaption = service.CreateClipboardData(landscape, "Private aggregate caption", true);
    Assert.True(imageAndCaption.GetDataPresent(System.Windows.DataFormats.Bitmap));
    Assert.Equal("Private aggregate caption", imageAndCaption.GetData(System.Windows.DataFormats.UnicodeText));

    var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "apps", "windows", "src", "KeyClick.App", "FunStatsShareService.cs"));
    Assert.DoesNotContain("ApplicationStatistics", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Executable", source, StringComparison.Ordinal);
    Assert.DoesNotContain("ForegroundExecutable", source, StringComparison.Ordinal);
    Assert.Contains("mode == FunStatsCopyMode.WholeAppView", source);
  }

  private static int CountAccentPixels(System.Windows.Media.Imaging.BitmapSource bitmap, int left, int right, int top, int bottom)
  {
    var stride = bitmap.PixelWidth * 4;
    var pixels = new byte[stride * bitmap.PixelHeight];
    bitmap.CopyPixels(pixels, stride, 0);
    var count = 0;
    for (var y = top; y < bottom; y++)
    {
      for (var x = left; x < right; x++)
      {
        var offset = y * stride + x * 4;
        var blue = pixels[offset];
        var green = pixels[offset + 1];
        var red = pixels[offset + 2];
        if (green > 110 && green > red * 1.35 && green > blue * 1.35) count++;
      }
    }
    return count;
  }

  [Fact]
  public void Trend_aggregation_calculates_counts_rates_active_time_and_comparisons()
  {
    var start = new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);
    var trend = new[]
    {
      Point(start, keyboard: 100, typing: 50, pointer: 10, active: 60_000, keyboardActive: 30_000, pointerActive: 10_000, peakTyping: 300, peakClicks: 25),
      Point(start.AddHours(1), keyboard: 200, typing: 100, pointer: 20, active: 30_000, keyboardActive: 30_000, pointerActive: 20_000, peakTyping: 500, peakClicks: 30)
    };
    var comparison = Snapshot(trend: [Point(start.AddDays(-1), keyboard: 40, typing: 20, pointer: 5)]);
    var snapshot = Snapshot(trend: trend, comparison: comparison);
    var localization = new LocalizationService();

    var counts = StatisticsTrendAggregator.Build(snapshot, StatisticsChartMetricFamily.Counts, StatisticsChartViewType.Bar,
      StatisticsTrendGranularity.Daily, ["keyboard", "pointer"], localization);
    Assert.Single(counts.Points);
    Assert.Equal(300, counts.Points[0].Values["keyboard"]);
    Assert.Equal(30, counts.Points[0].Values["pointer"]);
    Assert.Single(counts.ComparisonPoints);

    var rates = StatisticsTrendAggregator.Build(snapshot, StatisticsChartMetricFamily.Rates, StatisticsChartViewType.Donut,
      StatisticsTrendGranularity.Daily, ["average-wpm", "peak-wpm", "average-cps", "peak-cps"], localization);
    Assert.Equal(StatisticsChartViewType.Line, rates.ViewType);
    Assert.Equal(30, rates.Points[0].Values["average-wpm"], 3);
    Assert.Equal(100, rates.Points[0].Values["peak-wpm"], 3);
    Assert.Equal(1, rates.Points[0].Values["average-cps"], 3);
    Assert.Equal(6, rates.Points[0].Values["peak-cps"], 3);

    var active = StatisticsTrendAggregator.Build(snapshot, StatisticsChartMetricFamily.ActiveTime, StatisticsChartViewType.Line,
      StatisticsTrendGranularity.Daily, ["active", "keyboard-active", "pointer-active"], localization);
    Assert.Equal(1.5, active.Points[0].Values["active"], 3);
    Assert.Equal(1, active.Points[0].Values["keyboard-active"], 3);
    Assert.Equal(0.5, active.Points[0].Values["pointer-active"], 3);
  }

  [Fact]
  public void Trend_aggregation_enforces_the_five_hundred_point_limit()
  {
    var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var trend = Enumerable.Range(0, 501).Select(index => Point(start.AddHours(index), keyboard: 1)).ToArray();
    Assert.False(StatisticsTrendAggregator.IsGranularityAvailable(trend, StatisticsTrendGranularity.Hourly));

    var model = StatisticsTrendAggregator.Build(Snapshot(trend: trend), StatisticsChartMetricFamily.Counts,
      StatisticsChartViewType.Line, StatisticsTrendGranularity.Hourly, ["keyboard"], new LocalizationService());
    Assert.Equal(StatisticsTrendGranularity.Daily, model.Granularity);
    Assert.True(model.Points.Count <= 500);

    var monthlyTrend = new[] { Point(start, keyboard: 2), Point(start.AddMonths(1), keyboard: 3) };
    var monthly = StatisticsTrendAggregator.Build(Snapshot(trend: monthlyTrend), StatisticsChartMetricFamily.Counts,
      StatisticsChartViewType.Bar, StatisticsTrendGranularity.Monthly, ["keyboard"], new LocalizationService());
    Assert.Equal(2, monthly.Points.Count);
    Assert.Equal(new[] { 2d, 3d }, monthly.Points.Select(point => point.Values["keyboard"]));
  }

  private static StatisticsTrendPoint Point(DateTimeOffset bucket, long keyboard = 0, long typing = 0, long pointer = 0,
    long vertical = 0, long horizontal = 0, long active = 0, long keyboardActive = 0, long pointerActive = 0,
    int peakTyping = 0, int peakClicks = 0) =>
    new(bucket, keyboard, typing, pointer, vertical, horizontal, active, keyboardActive, pointerActive, peakTyping, peakClicks);

  private static StatisticsSnapshot Snapshot(long pointer = 0, long vertical = 0, IReadOnlyList<StatisticsTrendPoint>? trend = null,
    StatisticsSnapshot? comparison = null, long typing = 0, long keyboardActive = 0, long pointerActive = 0) => new(
      new(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow),
      0, typing, pointer, vertical, 0, 0, keyboardActive, pointerActive, 0, 0, 12, trend ?? [], [], comparison);

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeyClick.sln"))) directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the KeyClick repository root.");
  }
}
