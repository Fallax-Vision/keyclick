using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
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

  public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
  {
    paths.EnsureCreated();
    var output = Path.Combine(paths.Backups, $"KeyClick-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
    await Task.Run(() =>
    {
      using var archive = ZipFile.Open(output, ZipArchiveMode.Create);
      AddDirectory(archive, paths.Data, "data");
      AddDirectory(archive, paths.Media, "media");
    }, cancellationToken);
    return output;
  }

  public Task ValidateAsync(string archivePath, CancellationToken cancellationToken = default) => Task.Run(() =>
  {
    var file = new FileInfo(archivePath);
    if (!file.Exists) throw new FileNotFoundException("The selected backup no longer exists.", archivePath);
    if (file.Length > 500L * 1024 * 1024) throw new InvalidDataException("Backup archives must be 500 MB or smaller.");
    using var archive = ZipFile.OpenRead(archivePath);
    long expanded = 0;
    foreach (var entry in archive.Entries)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var normalized = entry.FullName.Replace('\\', '/');
      if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) ||
          !(normalized.StartsWith("data/", StringComparison.Ordinal) || normalized.StartsWith("media/", StringComparison.Ordinal)))
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
      archive.CreateEntryFromFile(file, $"{root}/{relative}", CompressionLevel.Optimal);
    }
  }
}

public sealed record UpdateInfo(string Version, string DownloadUrl, string ChecksumUrl, string AssetName, long Size);

public static class UpdateAssetSelector
{
  public static UpdateInfo? Select(JsonElement release, string architecture)
  {
    var expected = $"KeyClick-Windows-{architecture}.exe";
    var assets = release.GetProperty("assets").EnumerateArray().ToArray();
    var executable = assets.FirstOrDefault(asset => string.Equals(asset.GetProperty("name").GetString(), expected, StringComparison.OrdinalIgnoreCase));
    var checksum = assets.FirstOrDefault(asset => string.Equals(asset.GetProperty("name").GetString(), "checksums.txt", StringComparison.OrdinalIgnoreCase));
    if (executable.ValueKind == JsonValueKind.Undefined || checksum.ValueKind == JsonValueKind.Undefined) return null;
    return new UpdateInfo(
      release.GetProperty("tag_name").GetString() ?? string.Empty,
      executable.GetProperty("browser_download_url").GetString() ?? string.Empty,
      checksum.GetProperty("browser_download_url").GetString() ?? string.Empty,
      expected,
      executable.GetProperty("size").GetInt64());
  }
}

public sealed class UpdateService(HttpClient httpClient)
{
  private const string LatestRelease = "https://api.github.com/repos/Fallax-Vision/keyclick/releases/latest";

  public async Task<UpdateInfo?> CheckAsync(string architecture, CancellationToken cancellationToken = default)
  {
    using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
    request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KeyClick", "1.0"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
    response.EnsureSuccessStatusCode();
    await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
    var root = document.RootElement;
    return UpdateAssetSelector.Select(root, architecture);
  }

  public async Task<string> DownloadVerifiedAsync(UpdateInfo update, string destinationDirectory, CancellationToken cancellationToken = default)
  {
    Directory.CreateDirectory(destinationDirectory);
    var checksumText = await httpClient.GetStringAsync(update.ChecksumUrl, cancellationToken);
    var expected = checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
      .Where(parts => parts.Length >= 2 && string.Equals(parts[^1].TrimStart('*'), update.AssetName, StringComparison.OrdinalIgnoreCase))
      .Select(parts => parts[0].ToLowerInvariant())
      .FirstOrDefault();
    if (expected is null || expected.Length != 64 || expected.Any(character => !Uri.IsHexDigit(character)))
      throw new InvalidDataException("The release does not provide a valid checksum for this architecture.");

    var destination = Path.Combine(destinationDirectory, update.AssetName);
    var temporary = destination + $".{Guid.NewGuid():N}.download";
    try
    {
      using var response = await httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      response.EnsureSuccessStatusCode();
      if (response.Content.Headers.ContentLength is > 350_000_000) throw new InvalidDataException("The update asset is unexpectedly large.");
      await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
      await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await input.CopyToAsync(output, cancellationToken);
      await using var verify = File.OpenRead(temporary);
      var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)).ToLowerInvariant();
      if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual)))
        throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
      File.Move(temporary, destination, true);
      return destination;
    }
    finally
    {
      if (File.Exists(temporary)) File.Delete(temporary);
    }
  }
}
