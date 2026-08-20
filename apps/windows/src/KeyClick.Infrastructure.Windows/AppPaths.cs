using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class AppPaths
{
  public AppPaths(string? rootOverride = null, DistributionMode mode = DistributionMode.Installed, string? launcherOverride = null)
  {
    Root = rootOverride ?? Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "KeyClick");
    Data = Path.Combine(Root, "data");
    Media = Path.Combine(Root, "media");
    Sounds = Path.Combine(Media, "sounds");
    Packs = Path.Combine(Media, "packs");
    Logs = Path.Combine(Root, "logs");
    Backups = Path.Combine(Root, "backups");
    Updates = Path.Combine(Root, "updates");
    Database = Path.Combine(Data, "keyclick.db");
    var launcherRoot = mode == DistributionMode.Installed && rootOverride is null
      ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KeyClick")
      : Root;
    Launcher = launcherOverride ?? Path.Combine(launcherRoot, "KeyClick.exe");
    Mode = mode;
  }

  public string Root { get; }
  public string Data { get; }
  public string Media { get; }
  public string Sounds { get; }
  public string Packs { get; }
  public string Logs { get; }
  public string Backups { get; }
  public string Updates { get; }
  public string Database { get; }
  public string Launcher { get; }
  public DistributionMode Mode { get; }

  public void EnsureCreated()
  {
    foreach (var directory in new[] { Root, Data, Media, Sounds, Packs, Logs, Backups, Updates })
    {
      Directory.CreateDirectory(directory);
    }
  }

  public void CleanupLegacyApplicationFiles()
  {
    if (Mode != DistributionMode.Installed ||
        string.Equals(Path.GetDirectoryName(Launcher), Root, StringComparison.OrdinalIgnoreCase) ||
        (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) return;
    foreach (var directory in Directory.EnumerateDirectories(Root, "app-v*")) TryDeleteDirectory(directory);
    foreach (var fileName in new[] { "KeyClick.exe", "KeyClick.exe.new" })
    {
      var path = Path.Combine(Root, fileName);
      try { if (File.Exists(path)) File.Delete(path); }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
  }

  private static void TryDeleteDirectory(string path)
  {
    try { DeleteDirectoryTree(path); }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
  }

  private static void DeleteDirectoryTree(string path)
  {
    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
    {
      Directory.Delete(path, false);
      return;
    }
    foreach (var entry in Directory.EnumerateFileSystemEntries(path))
    {
      if (Directory.Exists(entry)) DeleteDirectoryTree(entry); else File.Delete(entry);
    }
    Directory.Delete(path, false);
  }
}
