using System.Text.Json;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;
using KeyClick.Updater;

namespace KeyClick.Tests;

public sealed class IntegrationContractTests
{
  [Fact]
  public void Action_result_validator_enforces_protocol_and_field_bounds()
  {
    Assert.Null(IntegrationRequestValidator.Validate(new(1, "action-result", SoundOutcome.Success, "input", "action", true)));
    Assert.Equal("unsupported-version", IntegrationRequestValidator.Validate(new(2, "action-result", SoundOutcome.Success, null, null, true)));
    Assert.Equal("unsupported-message", IntegrationRequestValidator.Validate(new(1, "other", SoundOutcome.Success, null, null, true)));
    Assert.Equal("field-too-long", IntegrationRequestValidator.Validate(new(1, "action-result", SoundOutcome.Success, new string('x', 129), null, true)));
  }

  [Fact]
  public void Rate_limiter_accepts_twenty_events_per_window()
  {
    var limiter = new SlidingRateLimiter(20);
    for (var index = 0; index < 20; index++) Assert.True(limiter.TryAccept(100, 1000));
    Assert.False(limiter.TryAccept(100, 1000));
    Assert.True(limiter.TryAccept(1101, 1000));
  }

  [Fact]
  public void Update_selector_requires_architecture_asset_and_checksums()
  {
    using var document = JsonDocument.Parse("""
      {
        "tag_name": "v1.2.3",
        "assets": [
          { "name": "KeyClick-Setup-Windows-x64-1.2.3.exe", "browser_download_url": "https://example.test/setup-x64", "size": 1234 },
          { "name": "KeyClick-Portable-Windows-x64-1.2.3.exe", "browser_download_url": "https://example.test/portable-x64", "size": 1235 },
          { "name": "checksums-1.2.3.txt", "browser_download_url": "https://example.test/checksums", "size": 100 }
        ]
      }
      """);

    var selected = UpdateAssetSelector.Select(document.RootElement, "x64");
    var portable = UpdateAssetSelector.Select(document.RootElement, "x64", UpdatePackageKind.Portable);

    Assert.NotNull(selected);
    Assert.Equal("1.2.3", selected.Version);
    Assert.Equal("KeyClick-Setup-Windows-x64-1.2.3.exe", selected.AssetName);
    Assert.Equal("KeyClick-Portable-Windows-x64-1.2.3.exe", portable?.AssetName);
    Assert.Null(UpdateAssetSelector.Select(document.RootElement, "arm64"));
  }

  [Fact]
  public void Update_selector_rejects_malformed_or_mismatched_release_metadata()
  {
    using var malformed = JsonDocument.Parse("""{ "tag_name": "v1.2.3", "assets": [{ "size": 100 }] }""");
    using var mismatched = JsonDocument.Parse("""
      {
        "tag_name": "v1.2.3",
        "assets": [
          { "name": "KeyClick-Setup-Windows-x64-1.2.2.exe", "browser_download_url": "https://example.test/setup", "size": 1234 },
          { "name": "checksums-1.2.3.txt", "browser_download_url": "https://example.test/checksums", "size": 100 }
        ]
      }
      """);

    Assert.Null(UpdateAssetSelector.Select(malformed.RootElement, "x64"));
    Assert.Null(UpdateAssetSelector.Select(mismatched.RootElement, "x64"));
  }

  [Theory]
  [InlineData("v1.2.0", "1.1.9", true)]
  [InlineData("1.1.0+build", "1.1.0", false)]
  [InlineData("1.1.0", "1.1.0-rc.1", true)]
  [InlineData("1.1.0-beta.2", "1.1.0-beta.11", false)]
  [InlineData("1.1.0-beta", "1.1.0-alpha.9", true)]
  [InlineData("1.01.0", "1.0.0", false)]
  [InlineData("1.0.9", "1.1.0", false)]
  public void Update_version_comparison_requires_a_strictly_newer_release(string candidate, string current, bool expected) =>
    Assert.Equal(expected, UpdateService.IsNewer(candidate, current));

  [Fact]
  public async Task Local_setup_is_staged_only_after_checksum_verification()
  {
    using var sourceFolder = new TempDirectory();
    using var destinationFolder = new TempDirectory();
    const string assetName = "KeyClick-Setup-Windows-x64-1.2.0.exe";
    var source = Path.Combine(sourceFolder.Path, assetName);
    var checksums = Path.Combine(sourceFolder.Path, "checksums-1.2.0.txt");
    await File.WriteAllBytesAsync(source, [1, 3, 3, 7, 9]);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source))).ToLowerInvariant();
    await File.WriteAllTextAsync(checksums, $"{hash}  {assetName}");
    var update = new UpdateInfo("1.2.0", string.Empty, string.Empty, assetName, new FileInfo(source).Length, source, checksums);

    var staged = await new UpdateService().DownloadVerifiedAsync(update, destinationFolder.Path);

    Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(staged));
    await File.WriteAllBytesAsync(source, [9, 9, 9]);
    await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateService().DownloadVerifiedAsync(update, destinationFolder.Path));
  }

  [Fact]
  public async Task Local_discovery_requires_matching_embedded_version_and_valid_checksum()
  {
    using var artifacts = new TempDirectory();
    var sourceAssembly = typeof(UpdateService).Assembly.Location;
    var productVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(sourceAssembly).ProductVersion;
    var version = productVersion!.TrimStart('v', 'V').Split('+', 2)[0];
    var assetName = $"KeyClick-Setup-Windows-x64-{version}.exe";
    var executable = Path.Combine(artifacts.Path, assetName);
    File.Copy(sourceAssembly, executable);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(executable))).ToLowerInvariant();
    await File.WriteAllTextAsync(Path.Combine(artifacts.Path, $"checksums-{version}.txt"), $"{hash}  {assetName}");

    var update = await new UpdateService().FindLocalAsync(artifacts.Path, "x64", "0.0.0");

    Assert.NotNull(update);
    Assert.Equal(version, update.Version);
    Assert.True(update.IsLocal);
  }

  [Fact]
  public async Task Update_staging_rejects_unsafe_asset_names_before_file_access()
  {
    using var destination = new TempDirectory();
    var update = new UpdateInfo("1.2.0", string.Empty, string.Empty, "..\\KeyClick.exe", 10, "missing.exe", "missing.txt");

    await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateService().DownloadVerifiedAsync(update, destination.Path));
  }

  private sealed class TempDirectory : IDisposable
  {
    public TempDirectory()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"keyclick-update-test-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
      if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
  }
}
