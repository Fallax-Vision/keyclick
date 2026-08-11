using System.Text.Json;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;

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
          { "name": "KeyClick-Windows-x64.exe", "browser_download_url": "https://example.test/x64", "size": 1234 },
          { "name": "checksums.txt", "browser_download_url": "https://example.test/checksums", "size": 100 }
        ]
      }
      """);

    var selected = UpdateAssetSelector.Select(document.RootElement, "x64");

    Assert.NotNull(selected);
    Assert.Equal("v1.2.3", selected.Version);
    Assert.Null(UpdateAssetSelector.Select(document.RootElement, "arm64"));
  }
}
