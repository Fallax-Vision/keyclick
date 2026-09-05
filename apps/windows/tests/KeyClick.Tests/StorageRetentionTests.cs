using KeyClick.Infrastructure.Windows;

namespace KeyClick.Tests;

public sealed class StorageRetentionTests
{
  [Fact]
  public async Task Runtime_cleanup_bounds_logs_by_age_count_file_size_and_total_size()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(Path.Combine(folder.Path, "state"));
    paths.EnsureCreated();
    for (var index = 0; index < 10; index++)
    {
      var path = Path.Combine(paths.Logs, $"recent-{index}.log");
      await File.WriteAllBytesAsync(path, new byte[1024]);
      File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-index));
    }
    var stale = Path.Combine(paths.Logs, "stale.log");
    await File.WriteAllTextAsync(stale, "old");
    File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-15));
    var oversized = Path.Combine(paths.Logs, "oversized.log");
    await using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write))
      stream.SetLength(StorageRetentionService.MaximumLogFileBytes + 1);

    await new StorageRetentionService(paths).ApplyAsync();

    var retained = new DirectoryInfo(paths.Logs).GetFiles("*.log");
    Assert.True(retained.Length <= StorageRetentionService.MaximumLogFiles);
    Assert.True(retained.Sum(file => file.Length) <= StorageRetentionService.MaximumTotalLogBytes);
    Assert.All(retained, file => Assert.True(file.Length <= StorageRetentionService.MaximumLogFileBytes));
    Assert.False(File.Exists(stale));
    Assert.False(File.Exists(oversized));
  }

  [Fact]
  public async Task Validated_backup_rotation_keeps_three_general_and_one_of_each_safety_role()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(Path.Combine(folder.Path, "state"));
    paths.EnsureCreated();
    await File.WriteAllTextAsync(paths.Database, "database");
    var backups = new BackupService(paths);

    for (var index = 0; index < 5; index++) await backups.CreateAsync();
    for (var index = 0; index < 2; index++) await backups.CreateAsync(BackupReason.PreUpdate);
    for (var index = 0; index < 2; index++) await backups.CreateAsync(BackupReason.PreDestructiveAction);

    var retained = new DirectoryInfo(paths.Backups).GetFiles("KeyClick-*.zip");
    Assert.Equal(5, retained.Length);
    Assert.Equal(3, retained.Count(file => file.Name.Contains("-general-", StringComparison.Ordinal)));
    Assert.Single(retained, file => file.Name.Contains("-pre-update-", StringComparison.Ordinal));
    Assert.Single(retained, file => file.Name.Contains("-pre-destructive-", StringComparison.Ordinal));
    foreach (var backup in retained) await backups.ValidateAsync(backup.FullName);
  }

  [Fact]
  public async Task Update_cleanup_keeps_one_package_and_removes_only_abandoned_transients()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(Path.Combine(folder.Path, "state"));
    paths.EnsureCreated();
    var newest = Path.Combine(paths.Updates, "KeyClick-new.exe");
    var older = Path.Combine(paths.Updates, "KeyClick-old.exe");
    await File.WriteAllTextAsync(newest, "new");
    await File.WriteAllTextAsync(older, "old");
    File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
    var stale = Path.Combine(paths.Updates, "abandoned.download");
    var recent = Path.Combine(paths.Updates, "active.download");
    await File.WriteAllTextAsync(stale, "stale");
    await File.WriteAllTextAsync(recent, "recent");
    File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

    await new StorageRetentionService(paths).ApplyAsync();

    Assert.True(File.Exists(newest));
    Assert.False(File.Exists(older));
    Assert.False(File.Exists(stale));
    Assert.True(File.Exists(recent));
  }

  [Fact]
  public async Task Diagnostic_writer_rejects_paths_and_bounds_content()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(Path.Combine(folder.Path, "state"));
    var retention = new StorageRetentionService(paths);

    await Assert.ThrowsAsync<ArgumentException>(() => retention.WriteDiagnosticAsync("../outside.log", "private"));
    await retention.WriteDiagnosticAsync("startup-error.log", new string('x', 600 * 1024));

    var diagnostic = new FileInfo(Path.Combine(paths.Logs, "startup-error.log"));
    Assert.True(diagnostic.Exists);
    Assert.True(diagnostic.Length <= StorageRetentionService.MaximumLogFileBytes);
  }

  private sealed class TemporaryFolder : IDisposable
  {
    public TemporaryFolder()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KeyClick.RetentionTests.{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
      if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
  }
}
