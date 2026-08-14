using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class TypingChallengeService(ITypingChallengeStore store, IStatisticsStore statisticsStore)
{
  private int _sessionActive;

  public bool IsSessionActive => Volatile.Read(ref _sessionActive) != 0;
  public void SetSessionActive(bool active) => Volatile.Write(ref _sessionActive, active ? 1 : 0);

  public async Task<TypingChallengeResult> CreateResultAsync(TypingChallengeSession session, string? savedPromptId,
    string promptTitle, double goalWordsPerMinute, double goalAccuracy, CancellationToken cancellationToken = default)
  {
    var sourceId = await statisticsStore.GetStatisticsSourceIdAsync(cancellationToken);
    var source = session.Definition?.Source ?? TypingChallengeSource.FreeWriting;
    var promptId = source switch
    {
      TypingChallengeSource.BuiltIn => session.Definition?.Id,
      TypingChallengeSource.Custom when !string.IsNullOrEmpty(savedPromptId) => savedPromptId,
      TypingChallengeSource.Custom => session.Definition is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.Definition.Text))).ToLowerInvariant(),
      _ => null
    };
    var referenceCompleted = source != TypingChallengeSource.FreeWriting && session.ReferenceTextCompleted;
    var valid = referenceCompleted || session.ActiveMilliseconds >= 15_000 && session.CharacterAttempts >= 25;
    return new(
      Guid.NewGuid().ToString("N"), sourceId, DateTimeOffset.UtcNow, source, promptId, promptTitle,
      session.Definition?.Language ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
      session.Definition?.Difficulty ?? TypingChallengeDifficulty.Medium,
      session.RunMode, session.MistakeMode, session.DurationLimitSeconds, session.ActiveMilliseconds,
      session.CharacterAttempts, session.CorrectCharacters, session.ErrorAttempts, session.Corrections,
      session.RetainedCharacters, session.Words, session.GrossWordsPerMinute, session.NetWordsPerMinute,
      source == TypingChallengeSource.FreeWriting ? 0 : session.AccuracyPercent, session.ConsistencyPercent,
      referenceCompleted, valid, Math.Max(0, goalWordsPerMinute), Math.Clamp(goalAccuracy, 0, 100), 1, session.CreateSamples());
  }

  public Task<IReadOnlyList<TypingChallengeResult>> QueryAsync(TypingChallengeQuery query, CancellationToken cancellationToken = default) =>
    store.QueryTypingChallengeResultsAsync(query, cancellationToken);

  public Task<IReadOnlyList<SavedTypingPrompt>> LoadPromptsAsync(CancellationToken cancellationToken = default) =>
    store.LoadSavedTypingPromptsAsync(cancellationToken);

  public Task SavePromptAsync(SavedTypingPrompt prompt, CancellationToken cancellationToken = default) =>
    store.SaveTypingPromptAsync(prompt, cancellationToken);

  public Task DeletePromptAsync(string promptId, CancellationToken cancellationToken = default) =>
    store.DeleteTypingPromptAsync(promptId, cancellationToken);

  public Task DeleteAsync(TypingChallengeDeleteRequest request, CancellationToken cancellationToken = default) =>
    store.DeleteTypingChallengeResultsAsync(request, cancellationToken);

  public async Task<TypingChallengeResult> SaveResultAsync(TypingChallengeResult result, CancellationToken cancellationToken = default)
  {
    await store.SaveTypingChallengeResultAsync(result, cancellationToken);
    if (!result.ValidForStreak) return result;
    var date = DateOnly.FromDateTime(result.CompletedUtc.ToLocalTime().DateTime);
    await store.SaveTypingChallengeAchievementAsync(new(
      $"participation:{date:O}", "participation", date, result.Id,
      result.GoalWordsPerMinuteSnapshot, result.GoalAccuracySnapshot, result.CompletedUtc), cancellationToken);
    if (result.Source != TypingChallengeSource.FreeWriting
      && result.NetWordsPerMinute >= result.GoalWordsPerMinuteSnapshot
      && result.AccuracyPercent >= result.GoalAccuracySnapshot)
      await store.SaveTypingChallengeAchievementAsync(new(
        $"performance:{date:O}", "performance", date, result.Id,
        result.GoalWordsPerMinuteSnapshot, result.GoalAccuracySnapshot, result.CompletedUtc), cancellationToken);
    return result;
  }

  public async Task<TypingChallengeStreakSnapshot> GetStreaksAsync(CancellationToken cancellationToken = default)
  {
    var values = await store.LoadTypingChallengeAchievementsAsync(cancellationToken);
    var participation = CalculateStreak(values.Where(value => value.Kind == "participation").Select(value => value.LocalDate));
    var performance = CalculateStreak(values.Where(value => value.Kind == "performance").Select(value => value.LocalDate));
    return new(participation.Current, participation.Longest, performance.Current, performance.Longest);
  }

  public async Task<TypingChallengeComparison> CompareAsync(TypingChallengeResult current, StatisticsSnapshot? normalStatistics,
    TypingChallengeResult? selectedResult = null, CancellationToken cancellationToken = default)
  {
    var history = await store.QueryTypingChallengeResultsAsync(new(DateTimeOffset.UnixEpoch, current.CompletedUtc), cancellationToken);
    var similar = history.FirstOrDefault(value => value.Source == current.Source && value.RunMode == current.RunMode
      && value.MistakeMode == current.MistakeMode && value.DurationLimitSeconds == current.DurationLimitSeconds
      && (current.PromptId is null || value.PromptId == current.PromptId));
    var best = history.Where(value => value.Source == current.Source && value.RunMode == current.RunMode)
      .OrderByDescending(value => value.NetWordsPerMinute).FirstOrDefault();
    return new(current, similar, best, selectedResult, normalStatistics);
  }

  public async Task ExportCsvAsync(TypingChallengeQuery query, string path, CancellationToken cancellationToken = default)
  {
    var rows = await store.QueryTypingChallengeResultsAsync(query, cancellationToken);
    var output = new StringBuilder("completed_utc,source,prompt_id,language,difficulty,run_mode,mistake_mode,duration_ms,characters,words,gross_wpm,net_wpm,accuracy,errors,corrections,consistency\n");
    foreach (var row in rows)
      output.AppendLine(string.Join(',', row.CompletedUtc.ToUniversalTime().ToString("O"), row.Source, Csv(row.PromptId), Csv(row.Language), row.Difficulty,
        row.RunMode, row.MistakeMode, row.ActiveMilliseconds, row.RetainedCharacters, row.Words,
        row.GrossWordsPerMinute.ToString("0.##", CultureInfo.InvariantCulture), row.NetWordsPerMinute.ToString("0.##", CultureInfo.InvariantCulture),
        row.AccuracyPercent.ToString("0.##", CultureInfo.InvariantCulture), row.ErrorAttempts, row.Corrections,
        row.ConsistencyPercent.ToString("0.##", CultureInfo.InvariantCulture)));
    await File.WriteAllTextAsync(path, output.ToString(), new UTF8Encoding(false), cancellationToken);
  }

  private static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

  private static (int Current, int Longest) CalculateStreak(IEnumerable<DateOnly> dates)
  {
    var values = dates.Distinct().Order().ToArray();
    if (values.Length == 0) return (0, 0);
    var longest = 1;
    var run = 1;
    for (var index = 1; index < values.Length; index++)
    {
      run = values[index].DayNumber == values[index - 1].DayNumber + 1 ? run + 1 : 1;
      longest = Math.Max(longest, run);
    }
    var today = DateOnly.FromDateTime(DateTime.Today);
    var latest = values[^1];
    var current = latest == today || latest == today.AddDays(-1) ? 1 : 0;
    if (current > 0)
      for (var index = values.Length - 1; index > 0 && values[index].DayNumber == values[index - 1].DayNumber + 1; index--) current++;
    return (current, longest);
  }
}
