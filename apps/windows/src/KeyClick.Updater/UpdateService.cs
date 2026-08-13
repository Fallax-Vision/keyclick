using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace KeyClick.Updater;

public sealed record UpdateInfo(string Version, string DownloadUrl, string ChecksumUrl, string AssetName, long Size);

public static class UpdateAssetSelector
{
  public static UpdateInfo? Select(JsonElement release, string architecture)
  {
    var expected = $"KeyClick-Setup-Windows-{architecture}.exe";
    var assets = release.GetProperty("assets").EnumerateArray().ToArray();
    var executable = assets.FirstOrDefault(asset => string.Equals(asset.GetProperty("name").GetString(), expected, StringComparison.OrdinalIgnoreCase));
    var checksum = assets.FirstOrDefault(asset => string.Equals(asset.GetProperty("name").GetString(), "checksums.txt", StringComparison.OrdinalIgnoreCase));
    if (executable.ValueKind == JsonValueKind.Undefined || checksum.ValueKind == JsonValueKind.Undefined) return null;
    return new(
      release.GetProperty("tag_name").GetString() ?? string.Empty,
      executable.GetProperty("browser_download_url").GetString() ?? string.Empty,
      checksum.GetProperty("browser_download_url").GetString() ?? string.Empty,
      expected,
      executable.GetProperty("size").GetInt64());
  }
}

public sealed class UpdateService
{
  private const string LatestRelease = "https://api.github.com/repos/Fallax-Vision/keyclick/releases/latest";
  private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
  {
    "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"
  };

  public async Task<UpdateInfo?> CheckAsync(string architecture, CancellationToken cancellationToken = default)
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
    var update = UpdateAssetSelector.Select(document.RootElement, architecture);
    if (update is not null)
    {
      ValidateUri(update.DownloadUrl);
      ValidateUri(update.ChecksumUrl);
    }
    return update;
  }

  public async Task<string> DownloadVerifiedAsync(UpdateInfo update, string destinationDirectory, CancellationToken cancellationToken = default)
  {
    ValidateUri(update.DownloadUrl);
    ValidateUri(update.ChecksumUrl);
    using var client = CreateClient();
    Directory.CreateDirectory(destinationDirectory);
    using var checksumResponse = await GetWithApprovedRedirectsAsync(client, update.ChecksumUrl, cancellationToken);
    checksumResponse.EnsureSuccessStatusCode();
    var checksumText = await checksumResponse.Content.ReadAsStringAsync(cancellationToken);
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
      using var response = await GetWithApprovedRedirectsAsync(client, update.DownloadUrl, cancellationToken);
      response.EnsureSuccessStatusCode();
      if (response.Content.Headers.ContentLength is > 350_000_000) throw new InvalidDataException("The update asset is unexpectedly large.");
      await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
      await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await input.CopyToAsync(output, cancellationToken);
      await using var verify = File.OpenRead(temporary);
      var actual = await SHA256.HashDataAsync(verify, cancellationToken);
      if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), actual))
        throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
      File.Move(temporary, destination, true);
      return destination;
    }
    finally
    {
      if (File.Exists(temporary)) File.Delete(temporary);
    }
  }

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
}
