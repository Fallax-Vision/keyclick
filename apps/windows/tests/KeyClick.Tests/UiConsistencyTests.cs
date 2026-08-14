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

  [Fact]
  public void Navigation_and_statistics_controls_have_icons_clear_selection_and_heatmap_filters()
  {
    var repositoryRoot = FindRepositoryRoot();
    var appXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "App.xaml"));
    var mainXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml"));

    Assert.Equal(8, Count(mainXaml, "Style=\"{StaticResource NavButton}\""));
    Assert.Equal(8, Count(mainXaml, "Style=\"{StaticResource NavigationIcon}\""));
    Assert.Contains("GroupName=\"PrimaryNavigation\"", mainXaml);
    Assert.DoesNotContain("ActiveMarker", appXaml);
    Assert.Contains("<Setter Property=\"Margin\" Value=\"0,0,15,0\" />", appXaml);
    Assert.Contains("Property=\"IsChecked\" Value=\"True\"", appXaml);
    Assert.Contains("Value=\"{DynamicResource SelectionBrush}\"", appXaml);
    Assert.Contains("x:Name=\"StatisticsSectionTabs\"", mainXaml);
    Assert.Contains("CornerRadius=\"11\"", appXaml);
    Assert.Contains("<Setter Property=\"Margin\" Value=\"0,0,10,0\" />", appXaml);
    Assert.Contains("<Color x:Key=\"AccentTextColor\">#000000</Color>", appXaml);
    Assert.Contains("ItemsSource=\"{Binding HeatmapPeriodOptions}\"", mainXaml);
    Assert.Contains("TooltipsEnabled=\"{Binding HeatmapTooltipsEnabled}\"", mainXaml);
    Assert.Contains("Snapshot=\"{Binding HeatmapSnapshot}\"", mainXaml);
    Assert.Equal(2, Count(mainXaml, "<local:KeyboardHeatmap Snapshot=\"{Binding HeatmapSnapshot}\""));
    Assert.Contains("DataContext=\"{Binding Statistics}\"", mainXaml);
    Assert.Contains("Style=\"{StaticResource HelpIconButton}\"", mainXaml);
    Assert.Contains("Height=\"218\" Margin=\"0,18,0,0\"", mainXaml);
    Assert.DoesNotContain("Text=\"{DynamicResource KeyboardHeatmapHelp}\" Style=\"{StaticResource MutedText}\"", mainXaml);

    var visuals = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "StatisticsVisuals.cs"));
    Assert.Contains("OnMouseLeftButtonUp", visuals);
    Assert.DoesNotContain("_hoveredCode", visuals);
  }

  [Fact]
  public void Sound_pack_items_use_one_hoverable_card_container()
  {
    var repositoryRoot = FindRepositoryRoot();
    var appXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "App.xaml"));
    var mainXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml"));

    Assert.Contains("x:Key=\"CardListBoxItem\"", appXaml);
    Assert.Contains("x:Name=\"PackContainer\"", appXaml);
    Assert.Contains("TargetName=\"PackContainer\" Property=\"Background\" Value=\"{DynamicResource SurfaceHoverBrush}\"", appXaml);
    Assert.Contains("ItemContainerStyle=\"{StaticResource CardListBoxItem}\"", mainXaml);
    Assert.DoesNotContain("<Border Margin=\"0,0,0,12\" MinHeight=\"98\">", mainXaml);
    Assert.DoesNotContain("Binding=\"{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}\"", mainXaml);
  }

  [Fact]
  public void Uninstaller_waits_for_graceful_or_forced_app_exit_before_deleting_payloads()
  {
    var repositoryRoot = FindRepositoryRoot();
    var app = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "App.xaml.cs"));
    var bootstrap = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.Bootstrap", "Program.cs"));

    Assert.Contains("Local\\KeyClick.Shutdown.", app);
    Assert.Contains("Local\\KeyClick.Shutdown.", bootstrap);
    Assert.Contains("StopRunningApp(root);", bootstrap);
    Assert.Contains("process.Kill(entireProcessTree: true);", bootstrap);
    Assert.Contains("process.WaitForExit(5000)", bootstrap);
    Assert.Contains("DeleteWithRetry", bootstrap);
    Assert.DoesNotContain("if (!process.WaitForExit(1500)) process.Kill();", bootstrap);
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
