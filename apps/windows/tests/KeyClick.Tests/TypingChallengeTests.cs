using System.IO.Compression;
using System.Text;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using Microsoft.Data.Sqlite;

namespace KeyClick.Tests;

public sealed class TypingChallengeTests
{
  [Fact]
  public void Reference_text_accepts_spaces_as_counted_text_elements()
  {
    var definition = new TypingChallengeDefinition("spaces", "Spaces", "a b", "en", TypingChallengeDifficulty.Easy, TypingChallengeSource.BuiltIn);
    var session = new TypingChallengeSession(definition, TypingChallengeRunMode.PassageCompletion, TypingChallengeMistakeMode.Flow);

    Assert.True(session.Input("a"));
    Assert.True(session.Input(" "));
    Assert.True(session.Input("b"));
    Assert.Equal("a b", session.ResponseText);
    Assert.Equal(3, session.CharacterAttempts);
    Assert.Equal(3, session.CorrectCharacters);
    Assert.True(session.ReferenceTextCompleted);
  }

  [Fact]
  public void New_settings_keep_challenges_out_of_normal_statistics_and_use_goal_defaults()
  {
    var settings = new AppSettings();
    Assert.False(settings.IncludeChallengeTypingInStatistics);
    Assert.False(settings.TypingChallengeDisclosureConfirmed);
    Assert.Equal(40, settings.TypingChallengeGoalWordsPerMinute);
    Assert.Equal(95, settings.TypingChallengeGoalAccuracy);
    Assert.NotEmpty(TypingChallengeCatalog.Filter("en", TypingChallengeDifficulty.Easy));
    Assert.NotEmpty(TypingChallengeCatalog.Filter("fr", TypingChallengeDifficulty.Hard));
  }

  [Fact]
  public void Strict_and_flow_modes_count_errors_without_persisting_a_response()
  {
    long clock = 0;
    var definition = new TypingChallengeDefinition("test", "Test", "abc", "en", TypingChallengeDifficulty.Easy, TypingChallengeSource.BuiltIn);
    var strict = new TypingChallengeSession(definition, TypingChallengeRunMode.PassageCompletion, TypingChallengeMistakeMode.Strict, timestamp: () => clock, frequency: 1000);
    Assert.False(strict.Input("x"));
    clock += 1000;
    Assert.True(strict.Input("a"));
    Assert.Equal("a", strict.ResponseText);
    Assert.Equal(1, strict.ErrorAttempts);
    Assert.Equal(2, strict.CharacterAttempts);

    var flow = new TypingChallengeSession(definition, TypingChallengeRunMode.PassageCompletion, TypingChallengeMistakeMode.Flow, timestamp: () => clock, frequency: 1000);
    Assert.True(flow.Input("x"));
    Assert.Equal("x", flow.ResponseText);
    Assert.Equal(1, flow.ErrorAttempts);
  }

  [Fact]
  public void Timer_starts_on_first_input_and_pause_time_is_excluded()
  {
    long clock = 5_000;
    var definition = new TypingChallengeDefinition("test", "Test", "abcdef", "en", TypingChallengeDifficulty.Easy, TypingChallengeSource.BuiltIn);
    var session = new TypingChallengeSession(definition, TypingChallengeRunMode.SinglePassageTimed, TypingChallengeMistakeMode.Flow, 15, () => clock, 1000);
    clock += 10_000;
    Assert.Equal(0, session.ActiveMilliseconds);
    session.Input("a");
    clock += 3_000;
    session.Pause();
    clock += 20_000;
    Assert.Equal(3_000, session.ActiveMilliseconds);
    session.Resume();
    clock += 2_000;
    Assert.Equal(5_000, session.ActiveMilliseconds);
    Assert.False(session.TimeExpired);
  }

  [Fact]
  public async Task Aggregate_results_samples_prompts_deletion_and_streaks_round_trip()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using var store = new SqliteAppStore(paths);
    await store.InitializeAsync();
    var service = new TypingChallengeService(store, store);
    var now = DateTimeOffset.UtcNow;
    var result = Result(now, "result-one");
    await service.SaveResultAsync(result);
    await store.SaveTypingPromptAsync(new("prompt-one", "Local prompt", "This source prompt was explicitly saved by the user.", "en", TypingChallengeDifficulty.Medium, false, now, now, 1));

    var loaded = Assert.Single(await service.QueryAsync(new(now.AddMinutes(-1), now.AddMinutes(1))));
    Assert.Equal(2, loaded.Samples.Count);
    Assert.Equal(72, loaded.NetWordsPerMinute);
    Assert.Single(await service.LoadPromptsAsync());
    var streaks = await service.GetStreaksAsync();
    Assert.Equal(1, streaks.ParticipationCurrent);
    Assert.Equal(1, streaks.PerformanceCurrent);

    await using (var connection = new SqliteConnection($"Data Source={paths.Database};Pooling=False"))
    {
      await connection.OpenAsync();
      var command = connection.CreateCommand();
      command.CommandText = "PRAGMA table_info(typing_challenge_results);";
      var names = new List<string>();
      await using var reader = await command.ExecuteReaderAsync();
      while (await reader.ReadAsync()) names.Add(reader.GetString(1));
      Assert.DoesNotContain(names, value => value.Contains("response", StringComparison.OrdinalIgnoreCase) || value.Contains("typed_text", StringComparison.OrdinalIgnoreCase));
    }

    await service.DeleteAsync(new(new HashSet<string> { result.Id }, null, null, true));
    Assert.Empty(await service.QueryAsync(new(now.AddMinutes(-1), now.AddMinutes(1))));
    Assert.Single(await service.LoadPromptsAsync());
  }

  [Fact]
  public async Task Saved_prompts_require_password_for_profile_transfer_and_challenge_csv_has_no_content()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using var store = new SqliteAppStore(paths);
    await store.InitializeAsync();
    var now = DateTimeOffset.UtcNow;
    await store.SaveTypingChallengeResultAsync(Result(now, "history"));
    const string secret = "Sensitive source prompt that must stay inside a protected profile.";
    await store.SaveTypingPromptAsync(new("prompt", "Sensitive prompt", secret, "en", TypingChallengeDifficulty.Medium, false, now, now, 1));
    var profiles = new ProfileTransferService(paths, store, store, store);
    await Assert.ThrowsAsync<InvalidDataException>(() => profiles.ExportAsync(Path.Combine(folder.Path, "plain.keyclickprofile"), new(ChallengePrompts: true)));

    var protectedProfile = Path.Combine(folder.Path, "protected.keyclickprofile");
    await profiles.ExportAsync(protectedProfile, new(ChallengeHistory: true, ChallengePrompts: true, Password: "correct horse battery staple"));
    Assert.True(await profiles.RequiresPasswordAsync(protectedProfile));
    var preview = await profiles.PreviewAsync(protectedProfile, "correct horse battery staple");
    Assert.Equal(1, preview.ChallengeResultCount);
    Assert.Equal(1, preview.SavedPromptCount);

    var csv = Path.Combine(folder.Path, "history.csv");
    await new TypingChallengeService(store, store).ExportCsvAsync(new(now.AddMinutes(-1), now.AddMinutes(1)), csv);
    var content = await File.ReadAllTextAsync(csv);
    Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
    Assert.DoesNotContain("Sensitive prompt", content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Profile_v2_reader_remains_compatible_with_a_v1_manifest()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using var store = new SqliteAppStore(paths);
    await store.InitializeAsync();
    var profile = Path.Combine(folder.Path, "legacy.keyclickprofile");
    var manifest = """
      {"schemaVersion":1,"createdUtc":"2026-08-14T00:00:00Z","applicationVersion":"1.3.0","sections":[],"passwordProtected":false,"fileHashes":{}}
      """;
    await using var archiveStream = new MemoryStream();
    using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, true))
    {
      var entry = archive.CreateEntry("manifest.json");
      await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
      await writer.WriteAsync(manifest);
    }
    var header = Encoding.ASCII.GetBytes("KCPROF1\0");
    await using (var output = new FileStream(profile, FileMode.Create, FileAccess.Write))
    {
      await output.WriteAsync(header);
      output.WriteByte(0);
      archiveStream.Position = 0;
      await archiveStream.CopyToAsync(output);
    }
    var preview = await new ProfileTransferService(paths, store, store, store).PreviewAsync(profile, null);
    Assert.Equal(1, preview.Manifest.SchemaVersion);
    Assert.Empty(preview.Sections);
  }

  private static TypingChallengeResult Result(DateTimeOffset completed, string id) => new(
    id, "source", completed, TypingChallengeSource.BuiltIn, "en-easy-steady-rain", "Steady rain", "en",
    TypingChallengeDifficulty.Easy, TypingChallengeRunMode.PassageCompletion, TypingChallengeMistakeMode.Flow,
    null, 30_000, 190, 183, 7, 4, 180, 32, 76, 72, 96.32, 91, true, true, 40, 95, 1,
    [new(0, 90, 86, 4, 68.8), new(1, 100, 94, 6, 75.2)]);

  private sealed class TemporaryFolder : IDisposable
  {
    public TemporaryFolder()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KeyClick.Challenges.{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
  }
}
