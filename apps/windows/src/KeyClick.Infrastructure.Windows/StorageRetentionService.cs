using System.Text;

namespace KeyClick.Infrastructure.Windows;

public enum BackupReason
{
  General,
  PreUpdate,
  PreDestructiveAction
}

public sealed class StorageRetentionService(AppPaths paths)
{
  public const int MaximumLogFiles = 7;
  public const int MaximumLogAge = 14;
  public const long MaximumLogFileBytes = 5L * 1024 * 1024;
  public const long MaximumTotalLogBytes = 25L * 1024 * 1024;
  public const int MaximumGeneralBackups = 3;
  public const int MaximumPendingUpdates = 1;
  private const int MaximumDiagnosticCharacters = 512 * 1024;
  private static readonly TimeSpan TransientFileLifetime = TimeSpan.FromDays(1);

  public Task ApplyAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
  {
    TryRun(() => PruneLogs(cancellationToken));
    TryRun(() => PruneBackups(cancellationToken));
    TryRun(() => PruneUpdates(cancellationToken));
  }, cancellationToken);

  public Task PruneBackupsAsync(CancellationToken cancellationToken = default) =>
    Task.Run(() => TryRun(() => PruneBackups(cancellationToken)), cancellationToken);

  public async Task WriteDiagnosticAsync(string fileName, string content, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(fileName) ||
        !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
        !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
      throw new ArgumentException("The diagnostic file name is invalid.", nameof(fileName));

    Directory.CreateDirectory(paths.Logs);
    var bounded = content.Length <= MaximumDiagnosticCharacters ? content : content[..MaximumDiagnosticCharacters];
    await File.WriteAllTextAsync(Path.Combine(paths.Logs, fileName), bounded, new UTF8Encoding(false), cancellationToken);
    await Task.Run(() => TryRun(() => PruneLogs(cancellationToken)), cancellationToken);
  }

  private void PruneLogs(CancellationToken cancellationToken)
  {
    var files = ManagedFiles(paths.Logs, "*.log").ToList();
    var cutoff = DateTime.UtcNow.AddDays(-MaximumLogAge);
    foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff || file.Length > MaximumLogFileBytes))
    {
      cancellationToken.ThrowIfCancellationRequested();
      TryDelete(file);
    }

    files = ManagedFiles(paths.Logs, "*.log").OrderByDescending(file => file.LastWriteTimeUtc).ToList();
    foreach (var file in files.Skip(MaximumLogFiles))
    {
      cancellationToken.ThrowIfCancellationRequested();
      TryDelete(file);
    }

    files = ManagedFiles(paths.Logs, "*.log").OrderByDescending(file => file.LastWriteTimeUtc).ToList();
    var retainedBytes = files.Sum(file => file.Length);
    for (var index = files.Count - 1; index >= 0 && retainedBytes > MaximumTotalLogBytes; index--)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var file = files[index];
      var length = file.Length;
      if (TryDelete(file)) retainedBytes -= length;
    }
  }

  private void PruneBackups(CancellationToken cancellationToken)
  {
    var backups = ManagedFiles(paths.Backups, "KeyClick-*.zip")
      .OrderByDescending(file => file.LastWriteTimeUtc)
      .ToList();
    if (backups.Count <= 1) return;

    var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Retain(backups.Where(file => IsReason(file, "pre-update")), 1, retained);
    Retain(backups.Where(file => IsReason(file, "pre-destructive")), 1, retained);
    Retain(backups.Where(file => !IsReason(file, "pre-update") && !IsReason(file, "pre-destructive")), MaximumGeneralBackups, retained);
    retained.Add(backups[0].FullName);

    foreach (var backup in backups.Where(file => !retained.Contains(file.FullName)))
    {
      cancellationToken.ThrowIfCancellationRequested();
      TryDelete(backup);
    }
  }

  private void PruneUpdates(CancellationToken cancellationToken)
  {
    var updates = ManagedFiles(paths.Updates, "*.exe").OrderByDescending(file => file.LastWriteTimeUtc).ToList();
    foreach (var update in updates.Skip(MaximumPendingUpdates))
    {
      cancellationToken.ThrowIfCancellationRequested();
      TryDelete(update);
    }

    var cutoff = DateTime.UtcNow.Subtract(TransientFileLifetime);
    foreach (var pattern in new[] { "*.download", "*.stage", "*.tmp" })
    {
      foreach (var file in ManagedFiles(paths.Updates, pattern).Where(file => file.LastWriteTimeUtc < cutoff))
      {
        cancellationToken.ThrowIfCancellationRequested();
        TryDelete(file);
      }
    }

    foreach (var directory in ManagedDirectories(paths.Updates).Where(directory => directory.LastWriteTimeUtc < cutoff))
    {
      cancellationToken.ThrowIfCancellationRequested();
      TryDelete(directory);
    }
  }

  private static IEnumerable<FileInfo> ManagedFiles(string directory, string pattern)
  {
    if (!Directory.Exists(directory)) yield break;
    var root = new DirectoryInfo(directory);
    if ((root.Attributes & FileAttributes.ReparsePoint) != 0) yield break;
    foreach (var file in root.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
    {
      if ((file.Attributes & FileAttributes.ReparsePoint) == 0) yield return file;
    }
  }

  private static IEnumerable<DirectoryInfo> ManagedDirectories(string directory)
  {
    if (!Directory.Exists(directory)) yield break;
    var root = new DirectoryInfo(directory);
    if ((root.Attributes & FileAttributes.ReparsePoint) != 0) yield break;
    foreach (var child in root.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
    {
      if ((child.Attributes & FileAttributes.ReparsePoint) == 0) yield return child;
    }
  }

  private static bool IsReason(FileInfo file, string reason) =>
    file.Name.Contains($"-{reason}-", StringComparison.OrdinalIgnoreCase);

  private static void Retain(IEnumerable<FileInfo> files, int count, ISet<string> retained)
  {
    foreach (var file in files.Take(count)) retained.Add(file.FullName);
  }

  private static bool TryDelete(FileInfo file)
  {
    try { file.Delete(); return true; }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
  }

  private static void TryDelete(DirectoryInfo directory)
  {
    try { directory.Delete(true); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
  }

  private static void TryRun(Action action)
  {
    try { action(); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
  }
}
