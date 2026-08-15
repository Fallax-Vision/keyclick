using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyClick.Core;

namespace KeyClick.App;

public sealed record FunFactDefinition(
  string Id,
  FunStatMetric Metric,
  string Category,
  string Kind,
  double Target,
  string TitleKey,
  string SourceKey,
  bool Approximate,
  string? Ladder = null,
  int Order = 0);

public sealed record FunStatsCatalogDocument(int Version, IReadOnlyList<FunFactDefinition> Facts);

public sealed record EvaluatedFunStat(
  string Id,
  FunStatMetric Metric,
  string Title,
  string Value,
  string Detail,
  string Source,
  double Progress,
  bool Approximate,
  bool IsCustom = false);

public enum FunStatVisualType { Linear, Route, Radial, Equivalence }

public sealed record FunStatTile(
  string Id,
  string Title,
  string Value,
  string Detail,
  string Source,
  double Progress,
  bool Approximate,
  bool IsDistance,
  bool IsRate,
  FunStatVisualType VisualType = FunStatVisualType.Linear,
  bool Animate = false);

public sealed class FunStatOption : INotifyPropertyChanged
{
  private readonly Action<FunStatOption, string> _changed;
  private bool _enabled;
  private bool _selected;

  public FunStatOption(string id, string title, string category, bool isCustom, bool enabled, bool selected, Action<FunStatOption, string> changed)
  {
    Id = id;
    Title = title;
    Category = category;
    IsCustom = isCustom;
    _enabled = enabled;
    _selected = selected;
    _changed = changed;
  }

  public string Id { get; }
  public string Title { get; }
  public string Category { get; }
  public bool IsCustom { get; }
  public bool IsEnabled
  {
    get => _enabled;
    set
    {
      if (_enabled == value) return;
      _enabled = value;
      Notify();
      _changed(this, nameof(IsEnabled));
    }
  }
  public bool IsSelected
  {
    get => _selected;
    set
    {
      if (_selected == value) return;
      _selected = value;
      Notify();
      _changed(this, nameof(IsSelected));
    }
  }

  public void SetEnabledSilently(bool value) { _enabled = value; Notify(nameof(IsEnabled)); }
  public void SetSelectedSilently(bool value) { _selected = value; Notify(nameof(IsSelected)); }

  public event PropertyChangedEventHandler? PropertyChanged;
  private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public static class FunStatsCatalog
{
  private static readonly Lazy<IReadOnlyList<FunFactDefinition>> Cached = new(Load);
  public static IReadOnlyList<FunFactDefinition> Facts => Cached.Value;

  private static IReadOnlyList<FunFactDefinition> Load()
  {
    var assembly = typeof(FunStatsCatalog).Assembly;
    var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("fun-stats.v1.json", StringComparison.Ordinal));
    using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("The Fun Stats catalog is missing.");
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
      Converters = { new JsonStringEnumConverter() }
    };
    var document = JsonSerializer.Deserialize<FunStatsCatalogDocument>(stream, options)
      ?? throw new InvalidDataException("The Fun Stats catalog is invalid.");
    if (document.Version != 1 || document.Facts.Count < 36 || document.Facts.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != document.Facts.Count)
      throw new InvalidDataException("The Fun Stats catalog contract is invalid.");
    return document.Facts;
  }
}

public sealed class FunStatsEngine
{
  private readonly AppSettings _settings;
  private readonly LocalizationService _localization;
  private readonly int _launchSeed = Random.Shared.Next();
  private readonly Dictionary<string, int> _cardClickSeeds = new(StringComparer.Ordinal);
  private int _manualSeed;

  public FunStatsEngine(AppSettings settings, LocalizationService localization)
  {
    _settings = settings;
    _localization = localization;
    var builtInIds = FunStatsCatalog.Facts.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
    var knownIds = builtInIds.Concat(settings.CustomFunStats.Select(item => item.Id)).ToHashSet(StringComparer.Ordinal);
    settings.SelectedFunStatIds = settings.SelectedFunStatIds.Where(knownIds.Contains).Take(12).ToList();
    settings.DisabledFunFactIds = settings.DisabledFunFactIds.Where(builtInIds.Contains).ToList();
  }

  public IReadOnlyList<FunFactDefinition> EnabledFacts => FunStatsCatalog.Facts
    .Where(item => !_settings.DisabledFunFactIds.Contains(item.Id, StringComparer.Ordinal))
    .ToArray();

  public void RotateCard(string cardId)
  {
    _cardClickSeeds[cardId] = _cardClickSeeds.GetValueOrDefault(cardId) + 1;
  }

  public void Shuffle() => _manualSeed++;

  public static bool TryCalculateScrollCalibration(double knownCentimeters, double detents, out double centimetersPerDetent)
  {
    centimetersPerDetent = 0;
    if (!double.IsFinite(knownCentimeters) || !double.IsFinite(detents) || knownCentimeters <= 0 || detents <= 0) return false;
    var value = knownCentimeters / detents;
    if (value is < 0.01 or > 100) return false;
    centimetersPerDetent = value;
    return true;
  }

  public string CardFact(string cardId, StatisticsSnapshot? snapshot, DateTimeOffset now)
  {
    if (!_settings.FunStatsEnabled || !_settings.MetricCardFunFactsEnabled || snapshot is null) return string.Empty;
    var metrics = CardMetrics(cardId);
    var pool = EnabledFacts.Where(item => metrics.Contains(item.Metric)).ToArray();
    if (pool.Length == 0) return _localization.Get("FunStatsNoComparisons");
    var seed = RotationSeed(cardId, now);
    var definition = pool[StableIndex($"{cardId}|{seed}", pool.Length)];
    if (definition.Kind == "classification") definition = ClassificationFact(snapshot, definition.Metric) ?? definition;
    return Evaluate(definition, snapshot).Detail;
  }

  public IReadOnlyList<EvaluatedFunStat> Details(string cardId, StatisticsSnapshot? snapshot)
  {
    if (snapshot is null) return [];
    var metrics = CardMetrics(cardId);
    var definitions = EnabledFacts.Where(item => metrics.Contains(item.Metric)).ToList();
    definitions.RemoveAll(item => item.Kind == "classification");
    var classification = metrics.Select(metric => ClassificationFact(snapshot, metric)).FirstOrDefault(item => item is not null);
    if (classification is not null) definitions.Add(classification);
    return definitions.OrderBy(item => item.Metric).ThenBy(item => item.Order).Select(item => Evaluate(item, snapshot)).ToArray();
  }

  public IReadOnlyList<FunStatTile> Dashboard(StatisticsSnapshot? snapshot)
  {
    if (!_settings.FunStatsEnabled || snapshot is null) return [];
    var definitions = FunStatsCatalog.Facts.ToDictionary(item => item.Id, StringComparer.Ordinal);
    var custom = _settings.CustomFunStats.ToDictionary(item => item.Id, StringComparer.Ordinal);
    var tiles = new List<FunStatTile>();
    foreach (var id in _settings.SelectedFunStatIds.Take(12))
    {
      EvaluatedFunStat? evaluated = null;
      if (definitions.TryGetValue(id, out var definition) && !_settings.DisabledFunFactIds.Contains(id, StringComparer.Ordinal))
        evaluated = Evaluate(AdvanceMilestone(definition, snapshot), snapshot);
      else if (custom.TryGetValue(id, out var customDefinition))
        evaluated = Evaluate(customDefinition, snapshot);
      if (evaluated is null) continue;
      var isDistance = evaluated.Metric == FunStatMetric.ScrollDistanceCentimeters;
      var isRate = evaluated.Metric is FunStatMetric.AverageWordsPerMinute or FunStatMetric.PeakWordsPerMinute
        or FunStatMetric.AverageClicksPerSecond or FunStatMetric.PeakClicksPerSecond;
      var visualType = evaluated.Metric switch
      {
        FunStatMetric.ScrollDistanceCentimeters => FunStatVisualType.Route,
        FunStatMetric.AverageClicksPerSecond or FunStatMetric.PeakClicksPerSecond => FunStatVisualType.Equivalence,
        FunStatMetric.AverageWordsPerMinute or FunStatMetric.PeakWordsPerMinute => FunStatVisualType.Radial,
        _ => FunStatVisualType.Linear
      };
      tiles.Add(new(evaluated.Id, evaluated.Title, evaluated.Value, evaluated.Detail, evaluated.Source, evaluated.Progress,
        evaluated.Approximate, isDistance, isRate, visualType));
    }
    return tiles;
  }

  public EvaluatedFunStat Evaluate(FunFactDefinition definition, StatisticsSnapshot snapshot)
  {
    var value = MetricValue(snapshot, definition.Metric);
    var title = _localization.Get(definition.TitleKey);
    var prefix = definition.Approximate ? "≈ " : string.Empty;
    string detail;
    if (definition.Kind == "classification")
      detail = _localization.Format("FunClassificationFormat", title);
    else if (definition.Kind == "equivalence" && definition.Metric is FunStatMetric.AverageClicksPerSecond or FunStatMetric.PeakClicksPerSecond)
      detail = _localization.Format("FunTempoFormat", value * 60, title);
    else if (value >= definition.Target)
      detail = _localization.Format("FunCompletedMultipleFormat", prefix, value / definition.Target, title);
    else
      detail = _localization.Format("FunProgressTowardFormat", prefix, value / definition.Target, title);
    return new(definition.Id, definition.Metric, title, FormatMetric(definition.Metric, value), detail,
      _localization.Get(definition.SourceKey), Math.Clamp(value / definition.Target, 0, 1), definition.Approximate);
  }

  public EvaluatedFunStat Evaluate(CustomFunStatDefinition definition, StatisticsSnapshot snapshot)
  {
    var value = MetricValue(snapshot, definition.Metric);
    var detail = value >= definition.Target
      ? _localization.Format("FunCustomCompletedFormat", value / definition.Target)
      : _localization.Format("FunProgressTowardFormat", string.Empty, value / definition.Target, definition.Label);
    return new(definition.Id, definition.Metric, definition.Label, FormatMetric(definition.Metric, value), detail,
      _localization.Get("FunSourceCustom"), Math.Clamp(value / definition.Target, 0, 1), false, true);
  }

  public double MetricValue(StatisticsSnapshot snapshot, FunStatMetric metric) => metric switch
  {
    FunStatMetric.KeyboardPresses => snapshot.KeyboardPresses,
    FunStatMetric.PointerClicks => snapshot.PointerClicks,
    FunStatMetric.TotalActions => snapshot.KeyboardPresses + snapshot.PointerClicks + snapshot.VerticalScroll + snapshot.HorizontalScroll,
    FunStatMetric.ScrollingDetents => snapshot.VerticalScroll + snapshot.HorizontalScroll,
    FunStatMetric.ScrollDistanceCentimeters => (snapshot.VerticalScroll + snapshot.HorizontalScroll) * _settings.ScrollCentimetersPerDetent,
    FunStatMetric.ActiveMinutes => snapshot.ActiveMilliseconds / 60000d,
    FunStatMetric.TypingWords => snapshot.TypingKeyPresses / 5d,
    FunStatMetric.AverageWordsPerMinute => snapshot.AverageWordsPerMinute,
    FunStatMetric.PeakWordsPerMinute => snapshot.PeakWordsPerMinute,
    FunStatMetric.AverageClicksPerSecond => snapshot.AverageClicksPerActiveSecond,
    FunStatMetric.PeakClicksPerSecond => snapshot.PeakClicksPerFiveSeconds / 5d,
    FunStatMetric.BusiestHour => snapshot.BusiestHour,
    _ => 0
  };

  public string FormatMetric(FunStatMetric metric, double value) => metric switch
  {
    FunStatMetric.ScrollDistanceCentimeters => FormatDistance(value),
    FunStatMetric.ActiveMinutes => value >= 60
      ? _localization.Format("FunHoursFormat", value / 60)
      : _localization.Format("FunMinutesFormat", value),
    FunStatMetric.AverageWordsPerMinute or FunStatMetric.PeakWordsPerMinute => _localization.Format("WpmFormat", value),
    FunStatMetric.AverageClicksPerSecond or FunStatMetric.PeakClicksPerSecond => _localization.Format("CpsFormat", value),
    FunStatMetric.BusiestHour => DateTime.Today.AddHours(value).ToString("h tt", CultureInfo.CurrentUICulture),
    FunStatMetric.TypingWords => _localization.Format("FunWordsFormat", value),
    _ => value.ToString("N0", CultureInfo.CurrentUICulture)
  };

  private string FormatDistance(double centimeters)
  {
    if (centimeters < 100) return _localization.Format("FunCentimetersFormat", centimeters);
    if (centimeters < 10000) return _localization.Format("FunMetersFormat", centimeters / 100);
    if (centimeters < 100000) return _localization.Format("FunHectometersFormat", centimeters / 10000);
    return _localization.Format("FunKilometersFormat", centimeters / 100000);
  }

  private FunFactDefinition AdvanceMilestone(FunFactDefinition definition, StatisticsSnapshot snapshot)
  {
    if (definition.Kind != "milestone" || string.IsNullOrEmpty(definition.Ladder)) return definition;
    var value = MetricValue(snapshot, definition.Metric);
    if (value < definition.Target) return definition;
    return EnabledFacts.Where(item => item.Ladder == definition.Ladder && item.Target > value).OrderBy(item => item.Target).FirstOrDefault()
      ?? EnabledFacts.Where(item => item.Ladder == definition.Ladder).OrderByDescending(item => item.Target).FirstOrDefault()
      ?? definition;
  }

  private FunFactDefinition? ClassificationFact(StatisticsSnapshot snapshot, FunStatMetric metric)
  {
    if (metric != FunStatMetric.BusiestHour) return null;
    var hour = snapshot.BusiestHour;
    return EnabledFacts.Where(item => item.Metric == metric && item.Kind == "classification" && hour < item.Target)
      .OrderBy(item => item.Target).FirstOrDefault()
      ?? EnabledFacts.Where(item => item.Metric == metric && item.Kind == "classification").OrderByDescending(item => item.Target).FirstOrDefault();
  }

  private long RotationSeed(string cardId, DateTimeOffset now) => _settings.FunFactRotation switch
  {
    FunFactRotation.TenMinutes => now.ToUnixTimeSeconds() / 600,
    FunFactRotation.OneHour => now.ToUnixTimeSeconds() / 3600,
    FunFactRotation.Daily => DateOnly.FromDateTime(now.LocalDateTime).DayNumber,
    FunFactRotation.AppLaunch => _launchSeed,
    FunFactRotation.CardClick => _cardClickSeeds.GetValueOrDefault(cardId),
    _ => _manualSeed
  };

  private static int StableIndex(string value, int length)
  {
    uint hash = 2166136261;
    foreach (var character in value)
    {
      hash ^= character;
      hash *= 16777619;
    }
    return (int)(hash % length);
  }

  private static IReadOnlySet<FunStatMetric> CardMetrics(string cardId) => cardId switch
  {
    "keyboard" => new HashSet<FunStatMetric> { FunStatMetric.KeyboardPresses, FunStatMetric.TypingWords },
    "pointer" => new HashSet<FunStatMetric> { FunStatMetric.PointerClicks },
    "active" => new HashSet<FunStatMetric> { FunStatMetric.ActiveMinutes },
    "scrolling" => new HashSet<FunStatMetric> { FunStatMetric.ScrollingDetents, FunStatMetric.ScrollDistanceCentimeters },
    "average-typing" => new HashSet<FunStatMetric> { FunStatMetric.AverageWordsPerMinute },
    "peak-typing" => new HashSet<FunStatMetric> { FunStatMetric.PeakWordsPerMinute },
    "average-clicking" => new HashSet<FunStatMetric> { FunStatMetric.AverageClicksPerSecond, FunStatMetric.PeakClicksPerSecond },
    "busiest-hour" => new HashSet<FunStatMetric> { FunStatMetric.BusiestHour },
    _ => new HashSet<FunStatMetric>()
  };
}
