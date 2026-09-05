using System.IO.Compression;
using Microsoft.Win32;

namespace KeyClick.Infrastructure.Windows;

public sealed class StartupService(AppPaths paths)
{
  private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

  public bool IsEnabled()
  {
    using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
    return key?.GetValue("KeyClick") is string;
  }

  public void SetEnabled(bool enabled)
  {
    using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
    if (enabled) key.SetValue("KeyClick", $"\"{paths.Launcher}\" --startup");
    else key.DeleteValue("KeyClick", false);
  }
}

public sealed class BackupService(AppPaths paths)
{
  public const long MaxRestoreBytes = 1024L * 1024 * 1024;
  public const int MaxRestoreEntries = 10_000;
  private readonly StorageRetentionService _retention = new(paths);

  public async Task<string> CreateAsync(BackupReason reason = BackupReason.General, CancellationToken cancellationToken = default)
  {
    paths.EnsureCreated();
    var reasonName = reason switch
    {
      BackupReason.PreUpdate => "pre-update",
      BackupReason.PreDestructiveAction => "pre-destructive",
      _ => "general"
    };
    var output = Path.Combine(paths.Backups, $"KeyClick-{reasonName}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.zip");
    try
    {
      await Task.Run(() =>
      {
        using var archive = ZipFile.Open(output, ZipArchiveMode.Create);
        AddDirectory(archive, paths.Data, "data");
        AddDirectory(archive, paths.Media, "media");
      }, cancellationToken);
      await ValidateAsync(output, cancellationToken);
      await _retention.PruneBackupsAsync(cancellationToken);
      return output;
    }
    catch
    {
      await Task.Run(() =>
      {
        try { File.Delete(output); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
      }, CancellationToken.None);
      throw;
    }
  }

  public Task ValidateAsync(string archivePath, CancellationToken cancellationToken = default) => Task.Run(() =>
  {
    var file = new FileInfo(archivePath);
    if (!file.Exists) throw new FileNotFoundException("The selected backup no longer exists.", archivePath);
    if (file.Length > 500L * 1024 * 1024) throw new InvalidDataException("Backup archives must be 500 MB or smaller.");
    using var archive = ZipFile.OpenRead(archivePath);
    if (archive.Entries.Count is 0 or > MaxRestoreEntries) throw new InvalidDataException("The backup contains an invalid number of entries.");
    long expanded = 0;
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in archive.Entries)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var normalized = entry.FullName.Replace('\\', '/');
      if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) ||
          !(normalized.StartsWith("data/", StringComparison.Ordinal) || normalized.StartsWith("media/", StringComparison.Ordinal)) ||
          !names.Add(normalized) || IsUnsafeRecoveryEntry(normalized))
        throw new InvalidDataException("The archive is not a valid KeyClick backup.");
      expanded += entry.Length;
      if (expanded > MaxRestoreBytes) throw new InvalidDataException("The expanded backup is unexpectedly large.");
    }
    if (!archive.Entries.Any(entry => entry.FullName.Replace('\\', '/').Equals("data/keyclick.db", StringComparison.OrdinalIgnoreCase)))
      throw new InvalidDataException("The backup does not contain a KeyClick database.");
  }, cancellationToken);

  private static void AddDirectory(ZipArchive archive, string directory, string root)
  {
    if (!Directory.Exists(directory)) return;
    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
    {
      if (file.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || file.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)) continue;
      var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
      if (IsUnsafeRecoveryEntry($"{root}/{relative}")) continue;
      archive.CreateEntryFromFile(file, $"{root}/{relative}", CompressionLevel.Optimal);
    }
  }

  private static bool IsUnsafeRecoveryEntry(string name) =>
    name.Equals("data/pointer-experimental-active", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("data/pointer-recovery.json", StringComparison.OrdinalIgnoreCase);
}
