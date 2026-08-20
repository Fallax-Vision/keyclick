using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KeyClick.Updater;

public enum UpdatePackageKind
{
  Setup,
  Portable
}

public sealed record UpdateInfo(
  string Version,
  string DownloadUrl,
  string ChecksumUrl,
  string AssetName,
  long Size,
  string? LocalPath = null,
  string? LocalChecksumPath = null)
{
  public bool IsLocal => !string.IsNullOrWhiteSpace(LocalPath);
}

public static class UpdateAssetSelector
{
  public static UpdateInfo? Select(JsonElement release, string architecture, UpdatePackageKind packageKind = UpdatePackageKind.Setup)
  {
    if (!IsSupportedArchitecture(architecture) ||
        !release.TryGetProperty("tag_name", out var tagElement) ||
        tagElement.ValueKind != JsonValueKind.String ||
        !SemanticVersion.TryParse(tagElement.GetString(), out var version) ||
        !release.TryGetProperty("assets", out var assetsElement) ||
        assetsElement.ValueKind != JsonValueKind.Array) return null;

    var package = packageKind == UpdatePackageKind.Portable ? "Portable" : "Setup";
    var expected = $"KeyClick-{package}-Windows-{architecture}-{version}.exe";
    var checksumName = $"checksums-{version}.txt";
    JsonElement? executable = null;
    JsonElement? checksum = null;
    foreach (var asset in assetsElement.EnumerateArray())
    {
      if (!TryGetAssetName(asset, out var name)) continue;
      if (string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)) executable = asset;
      else if (string.Equals(name, checksumName, StringComparison.OrdinalIgnoreCase)) checksum = asset;
    }
    if (executable is null || checksum is null ||
        !TryGetDownloadUrl(executable.Value, out var downloadUrl) ||
        !TryGetDownloadUrl(checksum.Value, out var checksumUrl) ||
        !executable.Value.TryGetProperty("size", out var sizeElement) ||
        !sizeElement.TryGetInt64(out var size) || size is <= 0 or > UpdateService.MaximumAssetBytes) return null;

    return new(version.ToString(), downloadUrl, checksumUrl, expected, size);
  }

  private static bool IsSupportedArchitecture(string architecture) =>
    string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(architecture, "arm64", StringComparison.OrdinalIgnoreCase);

  private static bool TryGetAssetName(JsonElement asset, out string name)
  {
    name = string.Empty;
    return asset.ValueKind == JsonValueKind.Object &&
           asset.TryGetProperty("name", out var element) &&
           element.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(name = element.GetString() ?? string.Empty);
  }

  private static bool TryGetDownloadUrl(JsonElement asset, out string url)
  {
    url = string.Empty;
    return asset.TryGetProperty("browser_download_url", out var element) &&
           element.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(url = element.GetString() ?? string.Empty);
  }
}

public sealed class UpdateService
{
  internal const long MaximumAssetBytes = 350_000_000;
  private const int MaximumChecksumBytes = 1_048_576;
  private const string LatestRelease = "https://api.github.com/repos/Fallax-Vision/keyclick/releases/latest";
  private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
  {
    "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"
  };

  public async Task<UpdateInfo?> CheckAsync(
    string architecture,
    UpdatePackageKind packageKind = UpdatePackageKind.Setup,
    CancellationToken cancellationToken = default)
  {
    using var client = CreateClient();
    using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
    request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KeyClick", "1.0"));
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
    response.EnsureSuccessStatusCode();
    await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
    var update = UpdateAssetSelector.Select(document.RootElement, architecture, packageKind);
    if (update is not null)
    {
      ValidateUri(update.DownloadUrl);
      ValidateUri(update.ChecksumUrl);
    }
    return update;
  }

  public Task<UpdateInfo?> FindLocalAsync(
    string artifactsDirectory,
    string architecture,
    string currentVersion,
    UpdatePackageKind packageKind = UpdatePackageKind.Setup,
    CancellationToken cancellationToken = default) => Task.Run(async () =>
  {
    if (!Directory.Exists(artifactsDirectory) || !SemanticVersion.TryParse(currentVersion, out var current)) return null;
    var root = Path.GetFullPath(artifactsDirectory);
    var package = packageKind == UpdatePackageKind.Portable ? "Portable" : "Setup";
    var prefix = $"KeyClick-{package}-Windows-{architecture}-";
    UpdateInfo? selected = null;
    SemanticVersion? selectedVersion = null;
    foreach (var executable in Directory.EnumerateFiles(root, $"{prefix}*.exe", SearchOption.TopDirectoryOnly))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var assetName = Path.GetFileName(executable);
      var versionText = assetName[prefix.Length..^4];
      if (!SemanticVersion.TryParse(versionText, out var candidate) || candidate.CompareTo(current) <= 0 ||
          selectedVersion is not null && candidate.CompareTo(selectedVersion) <= 0) continue;

      var productVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion;
      if (!SemanticVersion.TryParse(productVersion, out var embeddedVersion) || embeddedVersion.CompareTo(candidate) != 0) continue;
      var checksums = Path.Combine(root, $"checksums-{candidate}.txt");
      if (!File.Exists(checksums)) continue;
      try
      {
        await VerifyFileAsync(executable, checksums, assetName, cancellationToken);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
      {
        continue;
      }
      selectedVersion = candidate;
      selected = new(candidate.ToString(), string.Empty, string.Empty, assetName, new FileInfo(executable).Length, executable, checksums);
    }
    return selected;
  }, cancellationToken);

  public async Task<string> DownloadVerifiedAsync(UpdateInfo update, string destinationDirectory, CancellationToken cancellationToken = default)
  {
    ValidateAssetName(update.AssetName);
    if (update.IsLocal) return await StageLocalVerifiedAsync(update, destinationDirectory, cancellationToken);
    if (update.Size is <= 0 or > MaximumAssetBytes) throw new InvalidDataException("The update asset size is invalid.");
    ValidateUri(update.DownloadUrl);
    ValidateUri(update.ChecksumUrl);
    using var client = CreateClient();
    Directory.CreateDirectory(destinationDirectory);
    using var checksumResponse = await GetWithApprovedRedirectsAsync(client, update.ChecksumUrl, cancellationToken);
    checksumResponse.EnsureSuccessStatusCode();
    var checksumText = await ReadBoundedTextAsync(checksumResponse.Content, cancellationToken);
    var expected = ParseChecksum(checksumText, update.AssetName);

    var destination = Path.Combine(destinationDirectory, update.AssetName);
    var temporary = destination + $".{Guid.NewGuid():N}.download";
    try
    {
      using var response = await GetWithApprovedRedirectsAsync(client, update.DownloadUrl, cancellationToken);
      response.EnsureSuccessStatusCode();
      if (response.Content.Headers.ContentLength is { } contentLength && contentLength != update.Size)
        throw new InvalidDataException("The update asset size does not match its release metadata.");
      await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
      await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
      {
        await input.CopyToAsync(output, cancellationToken);
        if (output.Length > MaximumAssetBytes) throw new InvalidDataException("The update asset is unexpectedly large.");
        if (output.Length != update.Size) throw new InvalidDataException("The downloaded update is incomplete.");
      }
      await VerifyHashAsync(temporary, expected, cancellationToken);
      File.Move(temporary, destination, true);
      return destination;
    }
    finally
    {
      if (File.Exists(temporary)) File.Delete(temporary);
    }
  }

  public async Task LaunchVerifiedAsync(UpdateInfo update, string stagedPath, string arguments, CancellationToken cancellationToken = default)
  {
    ValidateAssetName(update.AssetName);
    var fullPath = Path.GetFullPath(stagedPath);
    if (!string.Equals(Path.GetFileName(fullPath), update.AssetName, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The staged update path is invalid.");
    string expected;
    if (update.IsLocal)
    {
      expected = ParseChecksum(await File.ReadAllTextAsync(update.LocalChecksumPath!, cancellationToken), update.AssetName);
    }
    else
    {
      ValidateUri(update.ChecksumUrl);
      using var client = CreateClient();
      using var response = await GetWithApprovedRedirectsAsync(client, update.ChecksumUrl, cancellationToken);
      response.EnsureSuccessStatusCode();
      expected = ParseChecksum(await ReadBoundedTextAsync(response.Content, cancellationToken), update.AssetName);
    }
    await using var locked = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
    var actual = await SHA256.HashDataAsync(locked, cancellationToken);
    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), actual))
      throw new InvalidDataException("The staged update changed after verification.");
    using var process = Process.Start(new ProcessStartInfo(fullPath, arguments) { UseShellExecute = true })
      ?? throw new InvalidOperationException("Windows could not start the verified update.");
  }

  public static bool IsNewer(string candidate, string current) =>
    SemanticVersion.TryParse(candidate, out var candidateValue) &&
    SemanticVersion.TryParse(current, out var currentValue) &&
    candidateValue.CompareTo(currentValue) > 0;

  private static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

  private static async Task<HttpResponseMessage> GetWithApprovedRedirectsAsync(HttpClient client, string value, CancellationToken cancellationToken)
  {
    var current = new Uri(value, UriKind.Absolute);
    for (var redirects = 0; redirects <= 5; redirects++)
    {
      ValidateUri(current.AbsoluteUri);
      var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      if ((int)response.StatusCode is < 300 or >= 400) return response;
      var location = response.Headers.Location;
      response.Dispose();
      if (location is null) throw new InvalidDataException("The update server returned an invalid redirect.");
      current = location.IsAbsoluteUri ? location : new Uri(current, location);
    }
    throw new InvalidDataException("The update server returned too many redirects.");
  }

  private static void ValidateUri(string value)
  {
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
      throw new InvalidDataException("The update URL is not an approved GitHub HTTPS host.");
  }

  private static void ValidateAssetName(string assetName)
  {
    if (string.IsNullOrWhiteSpace(assetName) || !string.Equals(Path.GetFileName(assetName), assetName, StringComparison.Ordinal) ||
        !assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The update asset name is invalid.");
  }

  private static async Task<string> StageLocalVerifiedAsync(UpdateInfo update, string destinationDirectory, CancellationToken cancellationToken)
  {
    var source = Path.GetFullPath(update.LocalPath ?? throw new InvalidDataException("The local update path is missing."));
    var checksums = Path.GetFullPath(update.LocalChecksumPath ?? throw new InvalidDataException("The local checksum path is missing."));
    if (!string.Equals(Path.GetFileName(source), update.AssetName, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(checksums), StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("The local update files are not from the same approved artifact folder.");
    if (new FileInfo(source).Length > MaximumAssetBytes) throw new InvalidDataException("The local update asset is unexpectedly large.");
    var expected = ParseChecksum(await File.ReadAllTextAsync(checksums, cancellationToken), update.AssetName);

    Directory.CreateDirectory(destinationDirectory);
    var destination = Path.Combine(destinationDirectory, update.AssetName);
    var temporary = destination + $".{Guid.NewGuid():N}.stage";
    try
    {
      await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
      {
        var actual = await SHA256.HashDataAsync(input, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), actual))
          throw new InvalidDataException("The local update failed SHA-256 verification.");
        input.Position = 0;
        await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
      }
      await VerifyHashAsync(temporary, expected, cancellationToken);
      File.Move(temporary, destination, true);
      return destination;
    }
    finally
    {
      if (File.Exists(temporary)) File.Delete(temporary);
    }
  }

  private static async Task VerifyFileAsync(string path, string checksumPath, string assetName, CancellationToken cancellationToken)
  {
    if (!File.Exists(path) || !File.Exists(checksumPath)) throw new FileNotFoundException("The local update or its checksum file is missing.");
    var expected = ParseChecksum(await File.ReadAllTextAsync(checksumPath, cancellationToken), assetName);
    await VerifyHashAsync(path, expected, cancellationToken);
  }

  private static async Task<string> ReadBoundedTextAsync(HttpContent content, CancellationToken cancellationToken)
  {
    if (content.Headers.ContentLength is > MaximumChecksumBytes)
      throw new InvalidDataException("The update checksum manifest is unexpectedly large.");
    await using var input = await content.ReadAsStreamAsync(cancellationToken);
    using var output = new MemoryStream();
    var buffer = new byte[81920];
    while (true)
    {
      var read = await input.ReadAsync(buffer, cancellationToken);
      if (read == 0) break;
      if (output.Length + read > MaximumChecksumBytes)
        throw new InvalidDataException("The update checksum manifest is unexpectedly large.");
      await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
    }
    return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
  }

  private static async Task VerifyHashAsync(string path, string expected, CancellationToken cancellationToken)
  {
    await using var input = File.OpenRead(path);
    var actual = await SHA256.HashDataAsync(input, cancellationToken);
    if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), actual))
      throw new InvalidDataException("The update failed SHA-256 verification.");
  }

  private static string ParseChecksum(string checksumText, string assetName)
  {
    var expected = checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
      .Where(parts => parts.Length >= 2 && string.Equals(parts[^1].TrimStart('*'), assetName, StringComparison.OrdinalIgnoreCase))
      .Select(parts => parts[0].ToLowerInvariant())
      .FirstOrDefault();
    if (expected is null || expected.Length != 64 || expected.Any(character => !Uri.IsHexDigit(character)))
      throw new InvalidDataException("The update does not provide a valid checksum for this architecture.");
    return expected;
  }
}

internal sealed partial class SemanticVersion : IComparable<SemanticVersion>
{
  private SemanticVersion(string major, string minor, string patch, string? prerelease)
  {
    Major = major;
    Minor = minor;
    Patch = patch;
    Prerelease = prerelease;
  }

  private string Major { get; }
  private string Minor { get; }
  private string Patch { get; }
  private string? Prerelease { get; }

  public static bool TryParse(string? value, out SemanticVersion version)
  {
    version = null!;
    if (string.IsNullOrWhiteSpace(value)) return false;
    var normalized = value.Trim();
    if (normalized.StartsWith('v') || normalized.StartsWith('V')) normalized = normalized[1..];
    var match = SemVerRegex().Match(normalized);
    if (!match.Success) return false;
    version = new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value,
      match.Groups[4].Success ? match.Groups[4].Value : null);
    return true;
  }

  public int CompareTo(SemanticVersion? other)
  {
    if (other is null) return 1;
    var comparison = CompareNumeric(Major, other.Major);
    if (comparison != 0) return comparison;
    comparison = CompareNumeric(Minor, other.Minor);
    if (comparison != 0) return comparison;
    comparison = CompareNumeric(Patch, other.Patch);
    if (comparison != 0) return comparison;
    if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
    if (other.Prerelease is null) return -1;
    var left = Prerelease.Split('.');
    var right = other.Prerelease.Split('.');
    for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
    {
      var leftNumeric = left[index].All(char.IsDigit);
      var rightNumeric = right[index].All(char.IsDigit);
      comparison = leftNumeric && rightNumeric
        ? CompareNumeric(left[index], right[index])
        : leftNumeric != rightNumeric
          ? leftNumeric ? -1 : 1
          : string.CompareOrdinal(left[index], right[index]);
      if (comparison != 0) return comparison;
    }
    return left.Length.CompareTo(right.Length);
  }

  public override string ToString() => $"{Major}.{Minor}.{Patch}{(Prerelease is null ? string.Empty : $"-{Prerelease}")}";

  private static int CompareNumeric(string left, string right) =>
    left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

  [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")]
  private static partial Regex SemVerRegex();
}
