using System.IO.Compression;
using KeyClick.Infrastructure.Windows;

namespace KeyClick.Tests;

public sealed class BackupTests
{
  [Fact]
  public async Task Backup_round_trip_contract_contains_database_and_media()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(folder.Path);
    paths.EnsureCreated();
    await File.WriteAllTextAsync(paths.Database, "database");
    await File.WriteAllTextAsync(System.IO.Path.Combine(paths.Sounds, "sample.wav"), "audio");
    var service = new BackupService(paths);

    var archivePath = await service.CreateAsync();
    await service.ValidateAsync(archivePath);

    using var archive = ZipFile.OpenRead(archivePath);
    Assert.Contains(archive.Entries, entry => entry.FullName == "data/keyclick.db");
    Assert.Contains(archive.Entries, entry => entry.FullName == "media/sounds/sample.wav");
  }

  [Fact]
  public async Task Restore_validation_rejects_path_traversal()
  {
    using var folder = new TemporaryFolder();
    var paths = new AppPaths(System.IO.Path.Combine(folder.Path, "state"));
    paths.EnsureCreated();
    var archivePath = System.IO.Path.Combine(folder.Path, "malicious.zip");
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
      archive.CreateEntry("data/keyclick.db");
      archive.CreateEntry("../outside.txt");
    }

    await Assert.ThrowsAsync<InvalidDataException>(() => new BackupService(paths).ValidateAsync(archivePath));
  }

  private sealed class TemporaryFolder : IDisposable
  {
    public TemporaryFolder()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KeyClick.BackupTests.{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }
    public string Path { get; }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
  }
}
