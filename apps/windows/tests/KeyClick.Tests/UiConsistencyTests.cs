using System.Xml.Linq;

namespace KeyClick.Tests;

public sealed class UiConsistencyTests
{
  private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
  private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

  [Theory]
  [InlineData("Button")]
  [InlineData("TextBox")]
  [InlineData("PasswordBox")]
  [InlineData("ComboBox")]
  [InlineData("DatePicker")]
  public void Interactive_controls_share_theme_aware_geometry(string targetType)
  {
    var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "apps", "windows", "src", "KeyClick.App", "App.xaml"));
    var style = Assert.Single(document.Descendants(Presentation + "Style"), element =>
      element.Attribute("TargetType")?.Value == targetType && element.Attribute(Xaml + "Key") is null);
    var setters = style.Elements(Presentation + "Setter").ToArray();

    Assert.Contains(setters, setter => setter.Attribute("Property")?.Value == "MinHeight" && setter.Attribute("Value")?.Value == "40");
    Assert.Contains(setters, setter => setter.Attribute("Property")?.Value == "Background" && setter.Attribute("Value")?.Value == "{DynamicResource SurfaceBrush}");
    Assert.Contains(setters, setter => setter.Attribute("Property")?.Value == "Foreground" && setter.Attribute("Value")?.Value == "{DynamicResource TextBrush}");
    Assert.Contains(setters, setter => setter.Attribute("Property")?.Value == "Template");
  }

  [Fact]
  public void Pages_cards_and_accent_content_use_shared_layout_resources()
  {
    var repositoryRoot = FindRepositoryRoot();
    var appXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "App.xaml"));
    var mainXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml"));

    Assert.Contains("<CornerRadius x:Key=\"ControlCornerRadius\">9</CornerRadius>", appXaml);
    Assert.Contains("<CornerRadius x:Key=\"CardCornerRadius\">16</CornerRadius>", appXaml);
    Assert.Contains("x:Key=\"MetricCard\"", appXaml);
    Assert.Contains("x:Key=\"PageContent\"", appXaml);
    Assert.Equal(8, Count(mainXaml, "Style=\"{StaticResource PageContent}\""));
    Assert.Equal(8, Count(mainXaml, "Style=\"{StaticResource MetricCard}\""));
    Assert.DoesNotContain("Foreground=\"#", appXaml);
  }

  private static int Count(string value, string fragment)
  {
    var count = 0;
    for (var index = 0; (index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0; index += fragment.Length) count++;
    return count;
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeyClick.sln"))) directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the KeyClick repository root.");
  }
}
