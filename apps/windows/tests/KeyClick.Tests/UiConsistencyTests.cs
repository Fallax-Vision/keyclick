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
    Assert.Equal(9, Count(mainXaml, "Style=\"{StaticResource PageContent}\""));
    Assert.Equal(16, Count(mainXaml, "Style=\"{StaticResource MetricCard}\""));
    Assert.DoesNotContain("Foreground=\"#", appXaml);
  }

  [Fact]
  public void Navigation_and_statistics_controls_have_icons_clear_selection_and_heatmap_filters()
  {
    var repositoryRoot = FindRepositoryRoot();
    var appXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "App.xaml"));
    var mainXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml"));

    Assert.Equal(9, Count(mainXaml, "Style=\"{StaticResource NavButton}\""));
    Assert.Equal(9, Count(mainXaml, "Style=\"{StaticResource NavigationIcon}\""));
    Assert.Contains("GroupName=\"PrimaryNavigation\"", mainXaml);
    Assert.DoesNotContain("ActiveMarker", appXaml);
    Assert.Contains("<Setter Property=\"Margin\" Value=\"0,0,15,0\" />", appXaml);
    Assert.Contains("Property=\"IsChecked\" Value=\"True\"", appXaml);
    Assert.Contains("Value=\"{DynamicResource SelectionBrush}\"", appXaml);
    Assert.Contains("x:Name=\"StatisticsSectionTabs\"", mainXaml);
    Assert.Contains("x:Key=\"StatisticsSectionTabControl\"", appXaml);
    Assert.Contains("<UniformGrid Rows=\"1\" IsItemsHost=\"True\" />", appXaml);
    Assert.Contains("HorizontalAlignment=\"{TemplateBinding HorizontalContentAlignment}\"", appXaml);
    Assert.Contains("VerticalAlignment=\"{TemplateBinding VerticalContentAlignment}\"", appXaml);
    Assert.Contains("CornerRadius=\"9\"", appXaml);
    Assert.Contains("<Setter Property=\"Margin\" Value=\"3\" />", appXaml);
    Assert.Contains("<Setter Property=\"MinHeight\" Value=\"38\" />", appXaml);
    Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\" />", appXaml);
    Assert.Contains("<Setter Property=\"Panel.ZIndex\" Value=\"1\" />", appXaml);
    Assert.Contains("<Color x:Key=\"AccentTextColor\">#000000</Color>", appXaml);
    Assert.Contains("<Setter Property=\"Foreground\" Value=\"Black\" />", appXaml);
    Assert.Contains("<Setter Property=\"TextElement.Foreground\" Value=\"Black\" />", appXaml);
    Assert.Contains("<Style TargetType=\"AccessText\"><Setter Property=\"Foreground\" Value=\"Black\" /></Style>", appXaml);
    Assert.Contains("Text=\"{Binding VersionText}\" Style=\"{StaticResource MutedText}\"", mainXaml);
    Assert.Contains("HorizontalAlignment=\"Stretch\" TextAlignment=\"Center\"", mainXaml);
    Assert.DoesNotContain("Text=\"{Binding StatusMessage}\" Style=\"{StaticResource MutedText}\"", mainXaml);
    Assert.Contains("ItemsSource=\"{Binding HeatmapPeriodOptions}\"", mainXaml);
    Assert.Contains("TooltipsEnabled=\"{Binding HeatmapTooltipsEnabled}\"", mainXaml);
    Assert.Contains("Snapshot=\"{Binding HeatmapSnapshot}\"", mainXaml);
    Assert.Equal(2, Count(mainXaml, "<local:KeyboardHeatmap Snapshot=\"{Binding HeatmapSnapshot}\""));
    Assert.Contains("DataContext=\"{Binding Statistics}\"", mainXaml);
    Assert.Contains("Style=\"{StaticResource HelpIconButton}\"", mainXaml);
    Assert.Contains("Height=\"218\" Margin=\"0,18,0,0\"", mainXaml);
    Assert.DoesNotContain("Text=\"{DynamicResource KeyboardHeatmapHelp}\" Style=\"{StaticResource MutedText}\"", mainXaml);
    Assert.Contains("Text=\"{DynamicResource StatisticsApplications}\"", mainXaml);
    Assert.Contains("ItemsSource=\"{Binding ApplicationRows}\"", mainXaml);
    Assert.DoesNotContain("RefreshStatistics_Click", mainXaml);
    Assert.Contains("Grid.Column=\"1\" Orientation=\"Horizontal\" VerticalAlignment=\"Bottom\"", mainXaml);
    Assert.Contains("GroupName=\"ThemeSelection\"", mainXaml);
    Assert.DoesNotContain("ItemsSource=\"{Binding ThemeModes}\"", mainXaml);
    Assert.Equal(5, Count(mainXaml, "Style=\"{StaticResource StatisticsTabIcon}\""));
    Assert.Contains("ItemsSource=\"{Binding ApplicationRows}\"", mainXaml);
    Assert.Contains("Text=\"{Binding FriendlyName}\" FontSize=\"18\"", mainXaml);
    Assert.Contains("Text=\"{Binding ExecutableName}\"", mainXaml);
    Assert.Contains("x:Key=\"InsetListBoxItem\"", appXaml);

    var visuals = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "StatisticsVisuals.cs"));
    Assert.Contains("OnMouseLeftButtonUp", visuals);
    Assert.Contains("StaysOpen = true", visuals);
    Assert.Contains("_detailsPopup.Closed +=", visuals);
    Assert.Contains("heatmap.UpdateDetails(key);", visuals);
    Assert.Contains("heatmap.PositionDetails(key);", visuals);
    Assert.Contains("OwnerWindow_PreviewMouseDown", visuals);
    Assert.Contains("OwnerWindow_Deactivated", visuals);
    Assert.Contains("scrollViewer.ScrollChanged += AncestorScrollViewer_ScrollChanged", visuals);
    Assert.Contains("RepositionOpenDetails(true)", visuals);
    Assert.Contains("if (_detailsPopup.IsOpen && !IsMouseOver) CloseDetails();", visuals);
    Assert.Contains("var preferAbove =", visuals);
    Assert.Contains("var popupX = Math.Clamp", visuals);
    Assert.DoesNotContain("_selectedCode == key.Code", visuals);
    Assert.DoesNotContain("_hoveredCode", visuals);

    var codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml.cs"));
    Assert.Contains("else if (e.Key == Key.Space)", codeBehind);
    Assert.Contains("_spaceEnteredOnKeyDown && e.Text == \" \"", codeBehind);
    Assert.Contains("new ChallengePrivacyWindow { Owner = this }", codeBehind);

    var challengePrivacy = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "ChallengePrivacyWindow.xaml"));
    Assert.Contains("Style=\"{StaticResource Card}\"", challengePrivacy);
    Assert.Contains("Style=\"{StaticResource DialogAccentButton}\"", challengePrivacy);
  }

  [Fact]
  public void Shortcut_rows_present_gesture_to_action_hierarchy()
  {
    var repositoryRoot = FindRepositoryRoot();
    var mainXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml"));

    Assert.Contains("Text=\"{DynamicResource ShortcutListHeading}\"", mainXaml);
    Assert.Contains("Text=\"{Binding Converter={StaticResource LocalizedGestureConverter}}\"", mainXaml);
    Assert.Contains("Text=\"&#x2192;\"", mainXaml);
    Assert.Contains("Text=\"{Binding Name}\" FontSize=\"17\"", mainXaml);
    Assert.Contains("ItemContainerStyle=\"{StaticResource InsetListBoxItem}\"", mainXaml);
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
    Assert.Contains("Property=\"ItemContainerStyle\" Value=\"{StaticResource CardListBoxItem}\"", mainXaml);
    Assert.DoesNotContain("<Border Margin=\"0,0,0,12\" MinHeight=\"98\">", mainXaml);
    Assert.DoesNotContain("Binding=\"{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}\"", mainXaml);
  }

  [Fact]
  public void Sound_pack_list_supports_grid_view_and_forwards_wheel_scrolling()
  {
    var repositoryRoot = FindRepositoryRoot();
    var appXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "App.xaml"));
    var mainXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml"));
    var codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml.cs"));

    Assert.Contains("x:Name=\"SoundPacksScrollViewer\"", mainXaml);
    Assert.Contains("PreviewMouseWheel=\"SoundPackList_PreviewMouseWheel\"", mainXaml);
    Assert.Contains("GroupName=\"SoundPackView\"", mainXaml);
    Assert.Contains("IsChecked=\"{Binding SoundPackGridView, Mode=TwoWay}\"", mainXaml);
    Assert.Contains("IsChecked=\"{Binding SoundPackListView, Mode=TwoWay}\"", mainXaml);
    Assert.Contains("<UniformGrid Columns=\"2\" />", mainXaml);
    Assert.Contains("x:Key=\"SoundPackViewButton\"", appXaml);
    Assert.Contains("x:Key=\"GridCardListBoxItem\"", appXaml);
    Assert.Contains("SoundPacksScrollViewer.RaiseEvent", codeBehind);
    Assert.Contains("RoutedEvent = UIElement.MouseWheelEvent", codeBehind);
  }

  [Fact]
  public void Visible_statistics_refresh_live_and_offer_recent_periods_without_a_manual_refresh_action()
  {
    var repositoryRoot = FindRepositoryRoot();
    var viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "StatisticsViewModel.cs"));
    var mainXaml = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml"));
    var codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "MainWindow.xaml.cs"));

    Assert.Contains("Task.Delay(TimeSpan.FromSeconds(1)", viewModel);
    Assert.Contains("PeriodLastThirtyMinutes", viewModel);
    Assert.Contains("PeriodLastHour", viewModel);
    Assert.Contains("PeriodLastFiveHours", viewModel);
    Assert.Contains("SetApplicationsVisible", codeBehind);
    Assert.DoesNotContain("RefreshStatistics_Click", codeBehind);
    Assert.DoesNotContain("Content=\"{DynamicResource Refresh}\"", mainXaml);
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
