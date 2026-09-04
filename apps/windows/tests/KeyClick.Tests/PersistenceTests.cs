using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using Microsoft.Data.Sqlite;

namespace KeyClick.Tests;

public sealed class PersistenceTests
{
  [Fact]
  public async Task Migrations_are_idempotent_and_settings_overrides_round_trip()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using (var store = new SqliteAppStore(paths))
    {
      await store.InitializeAsync();
      var settings = new AppSettings
      {
        DisplayName = "Studio",
        MasterVolume = 0.42f,
        Theme = ThemeMode.Light,
        DisplayLanguage = DisplayLanguageMode.French,
        SoundPackViewMode = SoundPackViewMode.Grid
      };
      await store.SaveSettingsAsync(settings);
      var inputOverride = new InputOverride("clicky-switch", "KeyboardKey:Keyboard:30:0", KeyVariant.Shift, false, 0.6f, ["custom:abc"]);
      await store.SaveOverrideAsync(inputOverride);
      var groupMapping = new GroupMapping("clicky-switch", InputGroup.PointerPrimary, KeyVariant.Base, true, 0.4f, ["custom:pointer"], DeviceFamily.Trackpad);
      await store.SaveGroupMappingAsync(groupMapping);

      var loaded = await store.LoadSettingsAsync();
      var overrides = await store.LoadOverridesAsync("clicky-switch");
      Assert.Equal("Studio", loaded.DisplayName);
      Assert.Equal(0.42f, loaded.MasterVolume);
      Assert.Equal(ThemeMode.Light, loaded.Theme);
      Assert.Equal(DisplayLanguageMode.French, loaded.DisplayLanguage);
      Assert.Equal(SoundPackViewMode.Grid, loaded.SoundPackViewMode);
      var loadedOverride = Assert.Single(overrides);
      Assert.Equal(inputOverride.PackId, loadedOverride.PackId);
      Assert.Equal(inputOverride.InputId, loadedOverride.InputId);
      Assert.Equal(inputOverride.Variant, loadedOverride.Variant);
      Assert.Equal(inputOverride.Enabled, loadedOverride.Enabled);
      Assert.Equal(inputOverride.Volume, loadedOverride.Volume);
      Assert.Equal(inputOverride.SampleIds, loadedOverride.SampleIds);
      var loadedGroup = Assert.Single(await store.LoadGroupMappingsAsync("clicky-switch"));
      Assert.Equal(InputGroup.PointerPrimary, loadedGroup.Group);
      Assert.Equal(DeviceFamily.Trackpad, loadedGroup.DeviceFamily);
      Assert.Equal(0.4f, loadedGroup.Volume);
      Assert.Equal(4, (await store.LoadShortcutsAsync()).Count);
    }

    await using (var second = new SqliteAppStore(paths))
    {
      await second.InitializeAsync();
      Assert.Equal("Studio", (await second.LoadSettingsAsync()).DisplayName);
    }

    await using (var connection = new SqliteConnection($"Data Source={paths.Database};Pooling=False"))
    {
      await connection.OpenAsync();
      var command = connection.CreateCommand();
      command.CommandText = "PRAGMA journal_mode;";
      Assert.Equal("wal", (await command.ExecuteScalarAsync())?.ToString());
      command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
      Assert.Equal(6L, (long)(await command.ExecuteScalarAsync())!);
      command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='statistics_application_hourly';";
      Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
      command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='typing_challenge_results';";
      Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }
  }

  [Fact]
  public async Task Privacy_migration_removes_legacy_content_derived_custom_prompt_ids_only()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using (var store = new SqliteAppStore(paths))
    {
      await store.InitializeAsync();
    }

    await using (var connection = new SqliteConnection($"Data Source={paths.Database};Pooling=False"))
    {
      await connection.OpenAsync();
      var command = connection.CreateCommand();
      command.CommandText = """
        DELETE FROM schema_migrations WHERE version=6;
        INSERT INTO typing_challenge_prompts(id,title,prompt_text,language,difficulty,favorite,created_utc,updated_utc,revision)
        VALUES('saved-prompt','Saved','Local text','en','Medium',0,$now,$now,1);
        INSERT INTO typing_challenge_results
          (id,source_id,completed_utc,source,prompt_id,prompt_title,language,difficulty,run_mode,mistake_mode,
           duration_limit_seconds,active_ms,character_attempts,correct_characters,error_attempts,corrections,
           retained_characters,words,gross_wpm,net_wpm,accuracy_percent,consistency_percent,reference_completed,
           valid_for_streak,goal_wpm_snapshot,goal_accuracy_snapshot,revision)
        VALUES
          ('legacy','source',$now,'Custom','content-derived-fingerprint','Custom','en','Medium','PassageCompletion','Flow',
           NULL,1000,5,5,0,0,5,1,12,12,100,100,1,0,40,95,1),
          ('saved','source',$now,'Custom','saved-prompt','Saved','en','Medium','PassageCompletion','Flow',
           NULL,1000,5,5,0,0,5,1,12,12,100,100,1,0,40,95,1);
        """;
      command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
      await command.ExecuteNonQueryAsync();
    }

    await using (var migrated = new SqliteAppStore(paths))
    {
      await migrated.InitializeAsync();
    }

    await using var check = new SqliteConnection($"Data Source={paths.Database};Pooling=False");
    await check.OpenAsync();
    var query = check.CreateCommand();
    query.CommandText = "SELECT prompt_id FROM typing_challenge_results WHERE id='legacy';";
    Assert.Equal(DBNull.Value, await query.ExecuteScalarAsync());
    query.CommandText = "SELECT prompt_id FROM typing_challenge_results WHERE id='saved';";
    Assert.Equal("saved-prompt", await query.ExecuteScalarAsync());
  }

  [Theory]
  [InlineData(DisplayLanguageMode.System, "fr-FR", "fr")]
  [InlineData(DisplayLanguageMode.System, "fr-CA", "fr")]
  [InlineData(DisplayLanguageMode.System, "en-US", "en")]
  [InlineData(DisplayLanguageMode.System, "de-DE", "en")]
  [InlineData(DisplayLanguageMode.English, "fr-FR", "en")]
  [InlineData(DisplayLanguageMode.French, "en-US", "fr")]
  public void Display_language_uses_system_until_the_user_overrides_it(
    DisplayLanguageMode preference,
    string deviceCulture,
    string expected)
  {
    Assert.Equal(expected, DisplayLanguageResolver.ResolveCode(preference, System.Globalization.CultureInfo.GetCultureInfo(deviceCulture)));
  }

  private sealed class TemporaryFolder : IDisposable
  {
    public TemporaryFolder()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KeyClick.Tests.{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
  }
}
