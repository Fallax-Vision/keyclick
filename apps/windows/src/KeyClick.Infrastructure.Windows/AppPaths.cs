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
    Launcher = launcherOverride ?? Path.Combine(Root, "KeyClick.exe");
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
}
