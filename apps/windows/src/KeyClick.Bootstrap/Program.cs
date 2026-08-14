using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace KeyClick.Bootstrap;

internal static class Program
{
  private const string ProductFolder = "KeyClick";
  private const string PayloadResource = "KeyClick.payload.zip";
  private const string AppExecutable = "KeyClick.App.exe";

  [STAThread]
  private static int Main(string[] args)
  {
    var uninstalling = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
    try
    {
      var portable = IsPortableBuild;
      var root = ResolveRoot(args, portable);
      var firstSetup = !portable && !File.Exists(Path.Combine(root, "KeyClick.exe"));
      var shortcutSelection = firstSetup ? PromptForShortcuts() : null;
      if (firstSetup && shortcutSelection is null) return 0;
      Directory.CreateDirectory(root);
      if (uninstalling) return Uninstall(root);

      using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
        ?? throw new InvalidOperationException("This KeyClick launcher does not contain an application payload. Use a packaged release build.");
      var version = SafeVersion(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0");
      var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
      payload.Position = 0;
      var versionDirectory = Path.GetFullPath(Path.Combine(root, $"app-v{version}"));
      EnsureChild(root, versionDirectory);
      InstallPayload(root, versionDirectory, payload, payloadHash);

      var launcher = portable ? Environment.ProcessPath! : Path.Combine(root, "KeyClick.exe");
      if (!portable)
      {
        var shellIconsChanged = InstallStableLauncher(launcher);
        shellIconsChanged |= CreateShortcuts(
          launcher,
          shortcutSelection?.Desktop ?? File.Exists(DesktopShortcutPath()),
          shortcutSelection?.StartMenu ?? File.Exists(StartMenuShortcutPath()));
        RegisterUninstall(launcher);
        if (shellIconsChanged) RefreshShellIcons();
      }

      var application = Path.Combine(versionDirectory, AppExecutable);
      if (!File.Exists(application)) throw new FileNotFoundException("The packaged KeyClick application is incomplete.", application);
      var restoreIndex = Array.FindIndex(args, value => value.Equals("--restore-backup", StringComparison.OrdinalIgnoreCase));
      if (args.Contains("--update", StringComparer.OrdinalIgnoreCase) || restoreIndex >= 0) WaitForAppExit(root);
      if (restoreIndex >= 0)
      {
        if (restoreIndex + 1 >= args.Length) throw new InvalidDataException("A restore archive path is required.");
        ApplyRestore(root, args[restoreIndex + 1]);
      }
      var forwarded = args.Where((value, index) =>
        !value.Equals("--update", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("--use-installed-data", StringComparison.OrdinalIgnoreCase) &&
        !(restoreIndex >= 0 && (index == restoreIndex || index == restoreIndex + 1))).ToArray();
      var appArguments = forwarded.Concat(["--data-root", root, "--launcher", launcher]).ToList();
      if (portable) appArguments.Add("--distribution-portable");
      Process.Start(new ProcessStartInfo(application) { UseShellExecute = true, Arguments = JoinArguments(appArguments), WorkingDirectory = versionDirectory });
      return 0;
    }
    catch (Exception exception)
    {
      var action = uninstalling ? "uninstalled" : "installed or started";
      Forms.MessageBox.Show($"KeyClick could not be {action}.\n\n{exception.Message}", "KeyClick", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
      return 1;
    }
  }

  private static bool IsPortableBuild
  {
    get
    {
#if PORTABLE_BUILD
      return true;
#else
      return false;
#endif
    }
  }

  private static string ResolveRoot(string[] args, bool portable)
  {
    var installed = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder));
    if (!portable || args.Contains("--use-installed-data", StringComparer.OrdinalIgnoreCase)) return installed;
    var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
    var local = Path.GetFullPath(Path.Combine(executableDirectory, "KeyClickData"));
    try
    {
      Directory.CreateDirectory(local);
      var probe = Path.Combine(local, $".write-test-{Guid.NewGuid():N}");
      File.WriteAllText(probe, string.Empty);
      File.Delete(probe);
      return local;
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
    {
      var choice = Forms.MessageBox.Show(
        "This folder is not writable, so portable KeyClick cannot use KeyClickData beside the app.\n\nUse the installed %LOCALAPPDATA%\\KeyClick data store instead?",
        "KeyClick portable data", Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Warning);
      if (choice == Forms.DialogResult.Yes) return installed;
      throw new InvalidOperationException("KeyClick exited without creating or changing a data store.");
    }
  }

  private static void InstallPayload(string root, string destination, Stream payload, string payloadHash)
  {
    var marker = Path.Combine(destination, ".payload-sha256");
    if (File.Exists(marker) && string.Equals(File.ReadAllText(marker).Trim(), payloadHash, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(destination, AppExecutable))) return;

    var temporary = Path.GetFullPath(Path.Combine(root, $".install-{Guid.NewGuid():N}"));
    EnsureChild(root, temporary);
    Directory.CreateDirectory(temporary);
    try
    {
      using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
      foreach (var entry in archive.Entries)
      {
        var output = Path.GetFullPath(Path.Combine(temporary, entry.FullName));
        EnsureChild(temporary, output);
        if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var source = entry.Open();
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
      }
      File.WriteAllText(Path.Combine(temporary, ".payload-sha256"), payloadHash);

      if (Directory.Exists(destination)) DeleteValidatedDirectory(root, destination);
      Directory.Move(temporary, destination);
    }
    finally
    {
      if (Directory.Exists(temporary)) DeleteValidatedDirectory(root, temporary);
    }
  }

  private static bool InstallStableLauncher(string launcher)
  {
    var current = Environment.ProcessPath ?? throw new InvalidOperationException("The launcher path is unavailable.");
    if (Path.GetFullPath(current).Equals(Path.GetFullPath(launcher), StringComparison.OrdinalIgnoreCase)) return false;
    if (File.Exists(launcher) && FilesEqual(current, launcher)) return false;
    var pending = launcher + ".new";
    File.Copy(current, pending, true);
    File.Move(pending, launcher, true);
    return true;
  }

  private static bool FilesEqual(string left, string right)
  {
    var leftInfo = new FileInfo(left);
    var rightInfo = new FileInfo(right);
    if (leftInfo.Length != rightInfo.Length) return false;
    using var leftStream = File.OpenRead(left);
    using var rightStream = File.OpenRead(right);
    return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
  }

  private static bool CreateShortcuts(string launcher, bool desktopRequested, bool startMenuRequested)
  {
    var workingDirectory = Path.GetDirectoryName(launcher)!;
    var iconLocation = $"{launcher},0";
    var changed = false;
    var shortcuts = new List<string>();
    if (desktopRequested) shortcuts.Add(DesktopShortcutPath());
    if (startMenuRequested) shortcuts.Add(StartMenuShortcutPath());
    foreach (var shortcutPath in shortcuts)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
      var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut services are unavailable.");
      dynamic shell = Activator.CreateInstance(shellType)!;
      dynamic shortcut = shell.CreateShortcut(shortcutPath);
      try
      {
        if (File.Exists(shortcutPath) &&
            string.Equals((string)shortcut.TargetPath, launcher, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)shortcut.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)shortcut.IconLocation, iconLocation, StringComparison.OrdinalIgnoreCase)) continue;

        shortcut.TargetPath = launcher;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = iconLocation;
        shortcut.Description = "KeyClick keyboard and pointer sound studio";
        shortcut.Save();
        changed = true;
      }
      finally
      {
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
      }
    }
    return changed;
  }

  private static ShortcutSelection? PromptForShortcuts()
  {
    var french = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase);
    using var form = new Forms.Form
    {
      Text = french ? "Installer KeyClick" : "Install KeyClick",
      Width = 470,
      Height = 255,
      FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
      MaximizeBox = false,
      MinimizeBox = false,
      StartPosition = Forms.FormStartPosition.CenterScreen,
      ShowIcon = true
    };
    try { form.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
    var heading = new Forms.Label
    {
      Text = french ? "Choisissez les raccourcis à créer pour votre compte." : "Choose the shortcuts to create for your account.",
      AutoSize = false,
      Dock = Forms.DockStyle.Top,
      Height = 55,
      Padding = new Forms.Padding(20, 20, 20, 0)
    };
    var desktop = new Forms.CheckBox
    {
      Text = french ? "Créer un raccourci sur le Bureau" : "Create a Desktop shortcut",
      Checked = true,
      AutoSize = true,
      Location = new System.Drawing.Point(24, 76)
    };
    var startMenu = new Forms.CheckBox
    {
      Text = french ? "Créer un raccourci dans le menu Démarrer" : "Create a Start Menu shortcut",
      Checked = true,
      AutoSize = true,
      Location = new System.Drawing.Point(24, 110)
    };
    var install = new Forms.Button
    {
      Text = french ? "Installer" : "Install",
      DialogResult = Forms.DialogResult.OK,
      AutoSize = true,
      MinimumSize = new System.Drawing.Size(90, 34),
      Location = new System.Drawing.Point(335, 164)
    };
    var cancel = new Forms.Button
    {
      Text = french ? "Annuler" : "Cancel",
      DialogResult = Forms.DialogResult.Cancel,
      AutoSize = true,
      MinimumSize = new System.Drawing.Size(90, 34),
      Location = new System.Drawing.Point(235, 164)
    };
    form.Controls.AddRange([heading, desktop, startMenu, cancel, install]);
    form.AcceptButton = install;
    form.CancelButton = cancel;
    return form.ShowDialog() == Forms.DialogResult.OK ? new(desktop.Checked, startMenu.Checked) : null;
  }

  private static string DesktopShortcutPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KeyClick.lnk");
  private static string StartMenuShortcutPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "KeyClick.lnk");

  private static void RegisterUninstall(string launcher)
  {
    using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KeyClick", true);
    key.SetValue("DisplayName", "KeyClick", RegistryValueKind.String);
    key.SetValue("DisplayVersion", SafeVersion(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"), RegistryValueKind.String);
    key.SetValue("Publisher", "Fallax Vision", RegistryValueKind.String);
    key.SetValue("DisplayIcon", launcher, RegistryValueKind.String);
    key.SetValue("UninstallString", $"\"{launcher}\" --uninstall", RegistryValueKind.String);
    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
  }

  private static void RefreshShellIcons() => SHChangeNotify(0x08000000, 0, 0, 0);

  private static int Uninstall(string root)
  {
    var choice = Forms.MessageBox.Show(
      "Remove KeyClick?\n\nYes: remove the app and all local data.\nNo: remove the app but preserve settings, sounds, backups, and logs.\nCancel: do nothing.",
      "Uninstall KeyClick", Forms.MessageBoxButtons.YesNoCancel, Forms.MessageBoxIcon.Question);
    if (choice == Forms.DialogResult.Cancel) return 0;

    StopRunningApp(root);

    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) key?.DeleteValue("KeyClick", false);
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KeyClick", false);
    DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KeyClick.lnk"));
    DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "KeyClick.lnk"));

    if (choice == Forms.DialogResult.Yes)
    {
      foreach (var entry in Directory.EnumerateFileSystemEntries(root))
      {
        if (Path.GetFullPath(entry).Equals(Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)) continue;
        if (Directory.Exists(entry)) DeleteValidatedDirectory(root, entry); else DeleteFile(entry);
      }
    }
    else
    {
      foreach (var directory in Directory.EnumerateDirectories(root, "app-v*")) DeleteValidatedDirectory(root, directory);
      var updates = Path.Combine(root, "updates");
      if (Directory.Exists(updates)) DeleteValidatedDirectory(root, updates);
    }

    var current = Environment.ProcessPath!;
    MoveFileEx(current, null, 4);
    if (choice == Forms.DialogResult.Yes) MoveFileEx(root, null, 4);
    Forms.MessageBox.Show(choice == Forms.DialogResult.Yes ? "KeyClick will finish removing itself after restart." : "KeyClick was removed. Your local data was preserved.", "KeyClick", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
    return 0;
  }

  private static void WaitForAppExit(string root)
  {
    if (WaitForAppExit(root, TimeSpan.FromSeconds(12))) return;
    throw new InvalidOperationException("The current KeyClick process did not close in time. Try the update again.");
  }

  private static bool WaitForAppExit(string root, TimeSpan timeout)
  {
    var deadline = DateTime.UtcNow.Add(timeout);
    while (DateTime.UtcNow < deadline)
    {
      var running = Process.GetProcessesByName("KeyClick.App").Any(process =>
      {
        using (process)
        {
          try { return process.MainModule?.FileName is string path && IsChild(root, path); }
          catch { return false; }
        }
      });
      if (!running) return true;
      Thread.Sleep(150);
    }
    return false;
  }

  internal static void StopRunningApp(string root)
  {
    var instanceId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)).AsSpan(0, 8));
    if (EventWaitHandle.TryOpenExisting($@"Local\KeyClick.Shutdown.{instanceId}", out var shutdownEvent))
    {
      using (shutdownEvent) shutdownEvent.Set();
      if (WaitForAppExit(root, TimeSpan.FromSeconds(8))) return;
    }

    foreach (var process in Process.GetProcessesByName("KeyClick.App"))
    {
      try
      {
        var path = process.MainModule?.FileName;
        if (path is null || !IsChild(root, path)) continue;
        process.CloseMainWindow();
        if (process.WaitForExit(3000)) continue;
        process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(5000)) throw new InvalidOperationException("KeyClick is still running. Close it and try uninstalling again.");
      }
      catch (InvalidOperationException) when (process.HasExited) { }
      finally { process.Dispose(); }
    }

    if (!WaitForAppExit(root, TimeSpan.FromSeconds(2)))
      throw new InvalidOperationException("KeyClick is still running. Close it and try uninstalling again.");
  }

  private static void ApplyRestore(string root, string archivePath)
  {
    var archiveInfo = new FileInfo(archivePath);
    if (!archiveInfo.Exists) throw new FileNotFoundException("The selected backup no longer exists.", archivePath);
    if (archiveInfo.Length > 500L * 1024 * 1024) throw new InvalidDataException("Backup archives must be 500 MB or smaller.");
    var temporary = Path.GetFullPath(Path.Combine(root, $".restore-{Guid.NewGuid():N}"));
    EnsureChild(root, temporary);
    Directory.CreateDirectory(temporary);
    try
    {
      using var archive = ZipFile.OpenRead(archivePath);
      long expanded = 0;
      foreach (var entry in archive.Entries)
      {
        var normalized = entry.FullName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) ||
            !(normalized.StartsWith("data/", StringComparison.Ordinal) || normalized.StartsWith("media/", StringComparison.Ordinal)))
          throw new InvalidDataException("The archive is not a valid KeyClick backup.");
        expanded += entry.Length;
        if (expanded > 1024L * 1024 * 1024) throw new InvalidDataException("The expanded backup is unexpectedly large.");
        var destination = Path.GetFullPath(Path.Combine(temporary, normalized));
        EnsureChild(temporary, destination);
        if (normalized.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: false);
      }
      if (!File.Exists(Path.Combine(temporary, "data", "keyclick.db"))) throw new InvalidDataException("The backup does not contain a KeyClick database.");

      foreach (var folderName in new[] { "data", "media" })
      {
        var source = Path.Combine(temporary, folderName);
        var destination = Path.GetFullPath(Path.Combine(root, folderName));
        EnsureChild(root, destination);
        if (Directory.Exists(destination)) DeleteValidatedDirectory(root, destination);
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else Directory.CreateDirectory(destination);
      }
    }
    finally
    {
      if (Directory.Exists(temporary)) DeleteValidatedDirectory(root, temporary);
    }
  }

  private static void DeleteShortcut(string path) { if (File.Exists(path)) DeleteFile(path); }

  private static void DeleteFile(string path) => DeleteWithRetry(() => File.Delete(path));

  private static void DeleteValidatedDirectory(string root, string directory)
  {
    var full = Path.GetFullPath(directory);
    EnsureChild(root, full);
    DeleteWithRetry(() => Directory.Delete(full, true));
  }

  private static void DeleteWithRetry(Action delete)
  {
    for (var attempt = 0; ; attempt++)
    {
      try { delete(); return; }
      catch (Exception exception) when (attempt < 5 && exception is IOException or UnauthorizedAccessException)
      {
        Thread.Sleep(250);
      }
    }
  }

  private static void EnsureChild(string root, string candidate)
  {
    if (!IsChild(root, candidate)) throw new InvalidOperationException("A package entry attempted to escape the KeyClick application directory.");
  }

  private static bool IsChild(string root, string candidate)
  {
    var rootWithSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    return Path.GetFullPath(candidate).StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
  }

  private static string SafeVersion(string version)
  {
    var value = version.Split('+')[0];
    if (value.Length is < 1 or > 40 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
      throw new InvalidOperationException("The package version is invalid.");
    return value;
  }

  private static string JoinArguments(IEnumerable<string> args) => string.Join(' ', args.Select(value => $"\"{value.Replace("\"", "\\\"")}\""));

  private sealed record ShortcutSelection(bool Desktop, bool StartMenu);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);

  [DllImport("shell32.dll")]
  private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
