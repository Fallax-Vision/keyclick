using System.Text.RegularExpressions;

namespace KeyClick.Tests;

public sealed class ReleaseWorkflowSecurityTests
{
  [Fact]
  public void Release_workflow_uses_immutable_dependencies_and_least_privilege_jobs()
  {
    var root = FindRepositoryRoot();
    var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
    var toolManifest = File.ReadAllText(Path.Combine(root, ".config", "dotnet-tools.json"));

    Assert.DoesNotContain("'${{ github.ref_name }}'", workflow, StringComparison.Ordinal);
    Assert.Contains("VERSION_TAG: ${{ github.ref_name }}", workflow);
    Assert.Contains("$env:VERSION_TAG.TrimStart('v')", workflow);
    Assert.DoesNotContain("dotnet tool install", workflow, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("dotnet tool restore", workflow);
    Assert.Contains("dotnet tool run dotnet-CycloneDX --", workflow);
    Assert.Contains("\"version\": \"6.2.0\"", toolManifest);
    Assert.Contains("build:\n    runs-on: windows-2025\n    permissions:\n      contents: read", workflow.Replace("\r\n", "\n"));
    Assert.Contains("release:\n    needs: build", workflow.Replace("\r\n", "\n"));
    Assert.Contains("retention-days: 14", workflow);
    Assert.Contains("Prune-GitHubReleaseAssets.ps1", workflow);
    Assert.Contains("-KeepReleaseVersions 3 -Apply -Confirm:$false", workflow);

    foreach (Match action in Regex.Matches(workflow, @"uses:\s+[^\s@]+@(?<reference>[^\s#]+)"))
      Assert.Matches("^[0-9a-f]{40}$", action.Groups["reference"].Value);
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeyClick.sln"))) directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
  }
}
