using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using KeyClick.Updater;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace KeyClick.Tests;

public sealed class StatisticsPrivacyAndProfileTests
{
  [Fact]
  public void New_install_defaults_are_cream_keys_key_down_and_independent_statistics()
  {
    var settings = new AppSettings();
    Assert.Equal(BuiltInCatalog.DefaultPackId, settings.ActivePackId);
    Assert.Equal(0.35f, settings.MasterVolume);
    Assert.Equal(KeyboardSoundTiming.KeyDown, settings.KeyboardSoundTiming);
    Assert.True(settings.KeyboardStatisticsEnabled);
    Assert.True(settings.PointerStatisticsEnabled);
    Assert.True(settings.ScrollingStatisticsEnabled);
    Assert.True(settings.SoundsEnabled);
    Assert.False(settings.WellnessEnabled);
    Assert.False(settings.PackRotation.Enabled);
  }

  [Fact]
  public async Task Existing_settings_without_timing_migrate_to_key_up()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using var store = new SqliteAppStore(paths);
    await store.InitializeAsync();
    await using (var connection = new SqliteConnection($"Data Source={paths.Database};Pooling=False"))
    {
      await connection.OpenAsync();
      var command = connection.CreateCommand();
      command.CommandText = "INSERT OR REPLACE INTO settings(key,value_json,updated_utc) VALUES('app',$json,$now);";
      command.Parameters.AddWithValue("$json", "{\"activePackId\":\"clicky-switch\",\"soundsEnabled\":true}");
      command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
      await command.ExecuteNonQueryAsync();
    }
    var loaded = await store.LoadSettingsAsync();
    Assert.Equal(KeyboardSoundTiming.KeyUp, loaded.KeyboardSoundTiming);
    Assert.Equal("clicky-switch", loaded.ActivePackId);
  }

  [Fact]
  public async Task Hourly_statistics_query_breakdown_delete_and_idempotent_transfer_work()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using var store = new SqliteAppStore(paths);
    await store.InitializeAsync();
    var bucket = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
    await store.MergeStatisticsAsync([
      new(new(bucket, InputKind.KeyboardKey, DeviceFamily.Keyboard, 0x1E, false, InputGroup.Letters), 10, 900, 900, 0, 10, 0, 1),
      new(new(bucket, InputKind.PointerButton, DeviceFamily.ExternalMouse, 1, false, InputGroup.PointerPrimary), 4, 400, 0, 400, 0, 4, 1),
      new(new(bucket, InputKind.Wheel, DeviceFamily.Trackpad, 6, false, InputGroup.Wheel), 3, 100, 0, 100, 0, 0, 1),
      new(new(bucket.AddDays(1), InputKind.PointerButton, DeviceFamily.Trackpad, 1, false, InputGroup.PointerPrimary), 7, 700, 0, 700, 0, 5, 1)
    ]);
    var query = new StatisticsQuery(bucket, bucket.AddHours(1));
    var snapshot = await store.QueryStatisticsAsync(query);
    Assert.Equal(10, snapshot.KeyboardPresses);
    Assert.Equal(4, snapshot.PointerClicks);
    Assert.Equal(3, snapshot.VerticalScroll);
    Assert.Contains(snapshot.Breakdown, item => item.PhysicalCode == 0x1E && item.Count == 10);

    var transfer = await store.ExportStatisticsAsync(false);
    await store.ImportStatisticsAsync(transfer, false);
    Assert.Equal(10, (await store.QueryStatisticsAsync(query)).KeyboardPresses);

    await store.DeleteStatisticsAsync(new(bucket, bucket.AddHours(1), new HashSet<StatisticsCategory> { StatisticsCategory.Pointer }, false));
    var afterPointerDelete = await store.QueryStatisticsAsync(query);
    Assert.Equal(10, afterPointerDelete.KeyboardPresses);
    Assert.Equal(0, afterPointerDelete.PointerClicks);
    Assert.Equal(7, (await store.QueryStatisticsAsync(new(bucket.AddDays(1), bucket.AddDays(1).AddHours(1)))).PointerClicks);

    await store.DeleteStatisticsAsync(new(bucket, bucket.AddHours(1), new HashSet<StatisticsCategory> { StatisticsCategory.Keyboard }, false));
    var afterDelete = await store.QueryStatisticsAsync(query);
    Assert.Equal(0, afterDelete.KeyboardPresses);
    Assert.Equal(0, afterDelete.PointerClicks);
  }

  [Fact]
  public async Task Statistics_shutdown_flushes_pending_aggregate_events()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using var store = new SqliteAppStore(paths);
    await store.InitializeAsync();
    var settings = new AppSettings { StatisticsDisclosureConfirmed = true };
    var service = new StatisticsService(store, settings);
    Assert.True(service.TryRecord(new(
      new(InputKind.KeyboardKey, 0x1E, DeviceFamily: DeviceFamily.Keyboard),
      0x41, KeyVariant.Base, InputGroup.Letters, InputPhase.Down, Stopwatch.GetTimestamp())));
    await service.DisposeAsync();

    var snapshot = await store.QueryStatisticsAsync(new(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1)));
    Assert.Equal(1, snapshot.KeyboardPresses);
  }

  [Fact]
  public async Task Password_protected_profile_round_trips_without_machine_paths()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    await using var store = new SqliteAppStore(paths);
    await store.InitializeAsync();
    await store.SaveSettingsAsync(new AppSettings
    {
      DisplayName = "Portable profile",
      LaunchAtStartup = true,
      OutputDeviceId = "machine-device",
      ExcludedExecutables = [@"C:\private\app.exe"],
      StatisticsExcludedExecutables = [@"C:\private\stats.exe"],
      AllowedIntegrationClients = [@"C:\private\client.exe"],
      PackRotation = new() { Enabled = true, NextDueUtc = DateTimeOffset.UtcNow.AddHours(1), LastWindowsBootTicks = 12345 }
    });
    var service = new ProfileTransferService(paths, store, store);
    var profile = Path.Combine(folder.Path, "test.keyclickprofile");
    await service.ExportAsync(profile, new(Password: "correct horse battery staple"));
    Assert.True(await service.RequiresPasswordAsync(profile));
    await Assert.ThrowsAsync<InvalidDataException>(() => service.PreviewAsync(profile, "wrong password"));
    var preview = await service.PreviewAsync(profile, "correct horse battery staple");
    Assert.Contains("settings-mappings", preview.Sections);
    using var targetFolder = new TemporaryFolder();
    var targetPaths = new AppPaths(targetFolder.Path);
    await using var targetStore = new SqliteAppStore(targetPaths);
    await targetStore.InitializeAsync();
    var imported = await new ProfileTransferService(targetPaths, targetStore, targetStore).ImportAsync(profile, "correct horse battery staple", false);
    Assert.Equal("Portable profile", imported.DisplayName);
    Assert.False(imported.LaunchAtStartup);
    Assert.Equal("default", imported.OutputDeviceId);
    Assert.Empty(imported.ExcludedExecutables);
    Assert.Empty(imported.StatisticsExcludedExecutables);
    Assert.Empty(imported.AllowedIntegrationClients);
    Assert.True(imported.PackRotation.Enabled);
    Assert.Null(imported.PackRotation.NextDueUtc);
    Assert.Null(imported.PackRotation.LastWindowsBootTicks);
  }

  [Fact]
  public void Updater_assembly_has_no_reference_to_input_or_statistics_assembly()
  {
    var references = typeof(UpdateService).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
    Assert.DoesNotContain("KeyClick.Core", references);
    Assert.DoesNotContain("KeyClick.Infrastructure.Windows", references);
    Assert.Empty(typeof(UpdateService).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
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
