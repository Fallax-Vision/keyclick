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
    try
    {
      var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder));
      Directory.CreateDirectory(root);
      if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase)) return Uninstall(root);

      using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
        ?? throw new InvalidOperationException("This KeyClick launcher does not contain an application payload. Use a packaged release build.");
      var version = SafeVersion(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0");
      var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
      payload.Position = 0;
      var versionDirectory = Path.GetFullPath(Path.Combine(root, $"app-v{version}"));
      EnsureChild(root, versionDirectory);
      InstallPayload(root, versionDirectory, payload, payloadHash);

      var launcher = Path.Combine(root, "KeyClick.exe");
      InstallStableLauncher(launcher);
      CreateShortcuts(launcher);

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
        !(restoreIndex >= 0 && (index == restoreIndex || index == restoreIndex + 1))).ToArray();
      Process.Start(new ProcessStartInfo(application) { UseShellExecute = true, Arguments = JoinArguments(forwarded), WorkingDirectory = versionDirectory });
      return 0;
    }
    catch (Exception exception)
    {
      Forms.MessageBox.Show($"KeyClick could not be installed or started.\n\n{exception.Message}", "KeyClick", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
      return 1;
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

  private static void InstallStableLauncher(string launcher)
  {
    var current = Environment.ProcessPath ?? throw new InvalidOperationException("The launcher path is unavailable.");
    if (Path.GetFullPath(current).Equals(Path.GetFullPath(launcher), StringComparison.OrdinalIgnoreCase)) return;
    if (File.Exists(launcher) && FilesEqual(current, launcher)) return;
    var pending = launcher + ".new";
    File.Copy(current, pending, true);
    File.Move(pending, launcher, true);
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

  private static void CreateShortcuts(string launcher)
  {
    var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KeyClick.lnk");
    var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "KeyClick.lnk");
    foreach (var shortcutPath in new[] { desktop, startMenu })
    {
      if (File.Exists(shortcutPath)) continue;
      Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
      var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut services are unavailable.");
      dynamic shell = Activator.CreateInstance(shellType)!;
      dynamic shortcut = shell.CreateShortcut(shortcutPath);
      shortcut.TargetPath = launcher;
      shortcut.WorkingDirectory = Path.GetDirectoryName(launcher);
      shortcut.IconLocation = launcher;
      shortcut.Description = "KeyClick key-up and pointer sound studio";
      shortcut.Save();
      Marshal.FinalReleaseComObject(shortcut);
      Marshal.FinalReleaseComObject(shell);
    }
  }

  private static int Uninstall(string root)
  {
    var choice = Forms.MessageBox.Show(
      "Remove KeyClick?\n\nYes: remove the app and all local data.\nNo: remove the app but preserve settings, sounds, backups, and logs.\nCancel: do nothing.",
      "Uninstall KeyClick", Forms.MessageBoxButtons.YesNoCancel, Forms.MessageBoxIcon.Question);
    if (choice == Forms.DialogResult.Cancel) return 0;

    foreach (var process in Process.GetProcessesByName("KeyClick.App"))
    {
      try
      {
        var path = process.MainModule?.FileName;
        if (path is not null && IsChild(root, path)) { process.CloseMainWindow(); if (!process.WaitForExit(1500)) process.Kill(); }
      }
      catch { }
      finally { process.Dispose(); }
    }

    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) key?.DeleteValue("KeyClick", false);
    DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KeyClick.lnk"));
    DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "KeyClick.lnk"));

    if (choice == Forms.DialogResult.Yes)
    {
      foreach (var entry in Directory.EnumerateFileSystemEntries(root))
      {
        if (Path.GetFullPath(entry).Equals(Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase)) continue;
        if (Directory.Exists(entry)) DeleteValidatedDirectory(root, entry); else File.Delete(entry);
      }
    }
    else
    {
      foreach (var directory in Directory.EnumerateDirectories(root, "app-v*")) DeleteValidatedDirectory(root, directory);
    }

    var current = Environment.ProcessPath!;
    MoveFileEx(current, null, 4);
    if (choice == Forms.DialogResult.Yes) MoveFileEx(root, null, 4);
    Forms.MessageBox.Show(choice == Forms.DialogResult.Yes ? "KeyClick will finish removing itself after restart." : "KeyClick was removed. Your local data was preserved.", "KeyClick", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
    return 0;
  }

  private static void WaitForAppExit(string root)
  {
    var deadline = DateTime.UtcNow.AddSeconds(12);
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
      if (!running) return;
      Thread.Sleep(150);
    }
    throw new InvalidOperationException("The current KeyClick process did not close in time. Try the update again.");
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

  private static void DeleteShortcut(string path) { if (File.Exists(path)) File.Delete(path); }

  private static void DeleteValidatedDirectory(string root, string directory)
  {
    var full = Path.GetFullPath(directory);
    EnsureChild(root, full);
    Directory.Delete(full, true);
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

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}
