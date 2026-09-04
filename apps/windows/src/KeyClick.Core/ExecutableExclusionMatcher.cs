namespace KeyClick.Core;

public static class ExecutableExclusionMatcher
{
  public static bool Matches(IEnumerable<string> exclusions, string? executablePath)
  {
    if (string.IsNullOrWhiteSpace(executablePath)) return false;
    var executable = executablePath.AsSpan().Trim();
    var separator = Math.Max(executable.LastIndexOf('\\'), executable.LastIndexOf('/'));
    var fileName = separator < 0 ? executable : executable[(separator + 1)..];
    foreach (var exclusion in exclusions)
    {
      if (string.IsNullOrWhiteSpace(exclusion)) continue;
      var candidate = exclusion.AsSpan().Trim();
      if (candidate.Equals(executable, StringComparison.OrdinalIgnoreCase) ||
          fileName.Length > 0 && candidate.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return true;
    }
    return false;
  }
}
