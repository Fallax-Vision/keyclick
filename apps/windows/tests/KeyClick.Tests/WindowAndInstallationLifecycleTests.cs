using KeyClick.App;
using KeyClick.Bootstrap;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;

namespace KeyClick.Tests;

public sealed class WindowAndInstallationLifecycleTests
{
  [Theory]
  [InlineData(true, false, false, true)]
  [InlineData(false, false, false, false)]
  [InlineData(true, true, false, false)]
  [InlineData(true, false, true, false)]
  public void Window_close_hides_only_when_close_to_tray_is_enabled(
    bool closeToTray, bool allowClose, bool appIsExiting, bool expected)
  {
    Assert.Equal(expected, MainWindow.ShouldHideOnClose(closeToTray, allowClose, appIsExiting));
  }

  [Fact]
  public void Installed_code_and_launcher_use_program_files_while_user_data_stays_local()
  {
    var expectedInstallRoot = Path.GetFullPath(Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KeyClick"));
    var expectedDataRoot = Path.GetFullPath(Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyClick"));

    Assert.Equal(expectedInstallRoot, Program.InstalledApplicationRoot(), ignoreCase: true);
    Assert.Equal(expectedDataRoot, Program.InstalledDataRoot(), ignoreCase: true);
    Assert.False(string.Equals(expectedInstallRoot, expectedDataRoot, StringComparison.OrdinalIgnoreCase));

    var paths = new AppPaths();
    Assert.Equal(expectedDataRoot, paths.Root, ignoreCase: true);
    Assert.Equal(Path.Combine(expectedInstallRoot, "KeyClick.exe"), paths.Launcher, ignoreCase: true);
  }

  [Fact]
  public void Program_files_migration_removes_only_legacy_code_from_the_data_root()
  {
    using var folder = new TempDirectory();
    var dataRoot = Path.Combine(folder.Path, "LocalAppData", "KeyClick");
    var installRoot = Path.Combine(folder.Path, "ProgramFiles", "KeyClick");
    var legacyPayload = Path.Combine(dataRoot, "app-v1.5.1");
    var data = Path.Combine(dataRoot, "data");
    var media = Path.Combine(dataRoot, "media");
    var updates = Path.Combine(dataRoot, "updates");
    Directory.CreateDirectory(legacyPayload);
    Directory.CreateDirectory(data);
    Directory.CreateDirectory(media);
    Directory.CreateDirectory(updates);
    File.WriteAllText(Path.Combine(legacyPayload, "KeyClick.App.exe"), "legacy");
    File.WriteAllText(Path.Combine(dataRoot, "KeyClick.exe"), "legacy launcher");
    File.WriteAllText(Path.Combine(data, "keyclick.db"), "private aggregate data");
    File.WriteAllText(Path.Combine(media, "custom.wav"), "custom media");
    File.WriteAllText(Path.Combine(updates, "download.exe"), "cached update");

    var paths = new AppPaths(dataRoot, DistributionMode.Installed, Path.Combine(installRoot, "KeyClick.exe"));
    paths.CleanupLegacyApplicationFiles();

    Assert.False(Directory.Exists(legacyPayload));
    Assert.False(File.Exists(Path.Combine(dataRoot, "KeyClick.exe")));
    Assert.True(File.Exists(Path.Combine(data, "keyclick.db")));
    Assert.True(File.Exists(Path.Combine(media, "custom.wav")));
    Assert.True(File.Exists(Path.Combine(updates, "download.exe")));
  }

  [Fact]
  public void Privileged_bootstrap_delegates_user_data_cleanup_and_rejects_privileged_restore()
  {
    var root = FindRepositoryRoot();
    var bootstrap = File.ReadAllText(Path.Combine(root, "apps", "windows", "src", "KeyClick.Bootstrap", "Program.cs"));

    Assert.Contains("restoreRequested && !portable && (externalInstall || IsProcessElevated())", bootstrap);
    Assert.Contains("Backups can be restored only by the installed, unelevated KeyClick launcher", bootstrap);
    Assert.Contains("LaunchUserDataPurge", bootstrap);
    Assert.Contains("User data cleanup must run without administrator privileges", bootstrap);
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeyClick.sln"))) directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
  }

  private sealed class TempDirectory : IDisposable
  {
    public TempDirectory()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"keyclick-install-test-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
      if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
  }
}
