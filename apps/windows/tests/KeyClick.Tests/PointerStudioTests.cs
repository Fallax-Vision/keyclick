using System.Text.Json;
using KeyClick.Core;
using KeyClick.Infrastructure.Windows;

namespace KeyClick.Tests;

public sealed class PointerStudioTests
{
  [Fact]
  public void New_install_defaults_are_safe_and_effects_are_dormant()
  {
    var settings = new PointerStudioSettings();

    settings.Normalize();

    Assert.Equal(PointerThemeScope.SystemWide, settings.Scope);
    Assert.False(settings.Enabled);
    Assert.False(settings.MotionEffectsEnabled);
    Assert.False(settings.ClickIndicatorsEnabled);
    Assert.False(settings.ExperimentalReplacementEnabled);
    Assert.False(settings.ExperimentalSuppressionEnabled);
    Assert.Empty(settings.ButtonBindings);
  }

  [Fact]
  public void Imported_pointer_settings_are_normalized_and_experimental_state_is_not_reactivated()
  {
    var settings = new PointerStudioSettings
    {
      ThemeId = "not-a-theme",
      WindowsPointerSpeed = 200,
      VisualScale = double.NaN,
      TrailLength = 500,
      ExperimentalReplacementEnabled = true,
      ExperimentalSuppressionEnabled = true,
      MotionMode = PointerMotionMode.FullReplacement,
      ButtonBindings =
      [
        new() { DeviceId = new string('a', 32), Button = PointerButtonKind.X1, Action = PointerActionKind.ShowDesktop },
        new() { DeviceId = "*", Button = PointerButtonKind.Right, Action = PointerActionKind.DisableButton, SuppressOriginal = true },
        new() { DeviceId = "raw hid path", Button = PointerButtonKind.X2, Action = PointerActionKind.ToggleSounds }
      ]
    };

    settings.Normalize(profileImport: true);

    Assert.Equal("meridian", settings.ThemeId);
    Assert.Equal(20, settings.WindowsPointerSpeed);
    Assert.Equal(1, settings.VisualScale);
    Assert.Equal(24, settings.TrailLength);
    Assert.Equal(PointerMotionMode.Companion, settings.MotionMode);
    Assert.False(settings.ExperimentalReplacementEnabled);
    Assert.False(settings.ExperimentalSuppressionEnabled);
    Assert.All(settings.ButtonBindings, binding => Assert.Equal("*", binding.DeviceId));
    Assert.DoesNotContain(settings.ButtonBindings, binding => binding.Action == PointerActionKind.ToggleSounds);
  }

  [Fact]
  public void Click_indicator_values_and_binding_collections_are_bounded()
  {
    var settings = new PointerStudioSettings
    {
      LeftClick = new()
      {
        Color = "javascript:red",
        Opacity = -5,
        Size = 1000,
        Intensity = double.PositiveInfinity,
        ParticleCount = 500,
        DurationMilliseconds = 9000
      },
      ButtonBindings = Enumerable.Range(0, 100)
        .Select(index => new PointerButtonBinding
        {
          DeviceId = "*",
          Button = (PointerButtonKind)(index % Enum.GetValues<PointerButtonKind>().Length),
          Action = PointerActionKind.ShowDesktop,
          SuppressOriginal = true
        }).ToList()
    };

    settings.Normalize();

    Assert.Equal("#24C85A", settings.LeftClick.Color);
    Assert.InRange(settings.LeftClick.Opacity, 0.1, 1);
    Assert.Equal(120, settings.LeftClick.Size);
    Assert.Equal(0.65, settings.LeftClick.Intensity);
    Assert.Equal(48, settings.LeftClick.ParticleCount);
    Assert.Equal(1200, settings.LeftClick.DurationMilliseconds);
    Assert.True(settings.ButtonBindings.Count <= Enum.GetValues<PointerButtonKind>().Length);
  }

  [Fact]
  public void Experimental_modes_can_be_deactivated_without_preserving_suppression_or_recovery_state()
  {
    var settings = new PointerStudioSettings
    {
      ExperimentalReplacementEnabled = true,
      ExperimentalSuppressionEnabled = true,
      MotionMode = PointerMotionMode.FullReplacement,
      RecoverySnapshotCaptured = true,
      PreviousCursorScheme = new() { ["Arrow"] = @"\\server\cursor.cur" },
      ButtonBindings = [new() { DeviceId = "*", Button = PointerButtonKind.Left, Action = PointerActionKind.DisableButton, SuppressOriginal = true }]
    };

    settings.DeactivateExperimentalModes();

    Assert.False(settings.ExperimentalReplacementEnabled);
    Assert.False(settings.ExperimentalSuppressionEnabled);
    Assert.Equal(PointerMotionMode.Companion, settings.MotionMode);
    Assert.False(settings.RecoverySnapshotCaptured);
    Assert.Empty(settings.PreviousCursorScheme);
    Assert.Empty(settings.ButtonBindings);
  }

  [Fact]
  public void Bundled_catalog_contains_original_complete_theme_and_role_sets()
  {
    var catalog = LoadCatalog();

    Assert.Empty(catalog.Validate());
    Assert.True(catalog.Themes.Count >= 10);
    Assert.True(catalog.Roles.Count >= 15);
    Assert.All(catalog.Themes, theme =>
    {
      Assert.Contains("KeyClick original", theme.Provenance, StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("flaticon", theme.Provenance, StringComparison.OrdinalIgnoreCase);
    });
  }

  [Fact]
  public void Deterministic_compiler_generates_valid_cursor_files_for_every_theme_and_size()
  {
    using var folder = new TemporaryFolder();
    var service = new PointerAppearanceService(new AppPaths(folder.Path, DistributionMode.Portable));
    var catalog = LoadCatalog();

    foreach (var theme in catalog.Themes)
    foreach (var size in Enum.GetValues<PointerCursorSize>())
    {
      var result = service.PrepareTheme(theme, new PointerStudioSettings { ThemeId = theme.Id, Size = size });
      Assert.True(result.Success, result.Error);
      Assert.NotNull(result.CursorPath);
      var directory = Path.GetDirectoryName(result.CursorPath)!;
      var cursors = Directory.GetFiles(directory, "*.cur");
      Assert.Equal(catalog.Roles.Count, cursors.Length);
      foreach (var cursor in cursors)
      {
        var bytes = File.ReadAllBytes(cursor);
        Assert.True(bytes.Length > 64);
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(2, BitConverter.ToUInt16(bytes, 2));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 4));
        Assert.True(BitConverter.ToUInt16(bytes, 10) < 64);
        Assert.True(BitConverter.ToUInt16(bytes, 12) < 64);
      }
      Assert.False(File.ReadAllBytes(Path.Combine(directory, "Arrow.cur")).SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "Hand.cur"))));
      Assert.False(File.ReadAllBytes(Path.Combine(directory, "Arrow.cur")).SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "IBeam.cur"))));
    }
  }

  private static PointerStudioCatalog LoadCatalog()
  {
    var path = Path.Combine(FindRepositoryRoot(), "shared", "fixtures", "pointer-studio.v1.json");
    return JsonSerializer.Deserialize<PointerStudioCatalog>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
  }

  private static string FindRepositoryRoot()
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "KeyClick.sln"))) current = current.Parent;
    return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
  }

  private sealed class TemporaryFolder : IDisposable
  {
    public TemporaryFolder()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"keyclick-pointer-{Guid.NewGuid():N}");
      Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public void Dispose() => Directory.Delete(Path, true);
  }
}
