using KeyClick.Core;

namespace KeyClick.Tests;

public sealed class ApplicationStatisticsPresentationTests
{
  [Theory]
  [InlineData("brave", "Brave", "brave.exe")]
  [InlineData("chrome", "Chrome", "chrome.exe")]
  [InlineData("vlc", "VLC", "vlc.exe")]
  [InlineData("msedge", "Microsoft Edge", "msedge.exe")]
  [InlineData("sample_app", "Sample App", "sample_app.exe")]
  public void Application_rows_expose_a_friendly_primary_name_and_executable_detail(string storedName, string friendlyName, string executableName)
  {
    var row = new ApplicationStatisticsRow("local-id", storedName, 1, 2, 3, 4);

    Assert.Equal(friendlyName, row.FriendlyName);
    Assert.Equal(executableName, row.ExecutableName);
  }
}
