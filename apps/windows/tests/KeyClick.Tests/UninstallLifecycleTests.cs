using System.Diagnostics;
using KeyClick.Bootstrap;

namespace KeyClick.Tests;

public sealed class UninstallLifecycleTests
{
  [Fact]
  public void Uninstaller_forces_a_stuck_app_to_exit_before_deleting_its_payload()
  {
    using var root = new TempDirectory();
    var payload = Path.Combine(root.Path, "app-vtest");
    Directory.CreateDirectory(payload);
    var application = Path.Combine(payload, "KeyClick.App.exe");
    File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), application);
    using var process = Process.Start(new ProcessStartInfo(application, "/d /c ping 127.0.0.1 -n 30 > nul")
    {
      CreateNoWindow = true,
      UseShellExecute = false
    }) ?? throw new InvalidOperationException("Could not start the uninstall lifecycle fixture.");

    Assert.False(process.WaitForExit(150));
    Program.StopRunningApp(root.Path);

    Assert.True(process.WaitForExit(1000));
    Directory.Delete(payload, true);
  }

  private sealed class TempDirectory : IDisposable
  {
    public TempDirectory()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"keyclick-uninstall-test-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
      if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
  }
}
