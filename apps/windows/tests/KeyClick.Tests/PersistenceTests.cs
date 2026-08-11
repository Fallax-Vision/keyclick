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
        DisplayLanguage = DisplayLanguageMode.French
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
    }
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
