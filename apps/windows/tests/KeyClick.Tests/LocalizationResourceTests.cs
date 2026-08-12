using System.Xml.Linq;

namespace KeyClick.Tests;

public sealed class LocalizationResourceTests
{
  [Fact]
  public void LocalizationDictionariesHaveUniqueKeysAndMatchingLanguages()
  {
    var repositoryRoot = FindRepositoryRoot();
    var resourceDirectory = Path.Combine(repositoryRoot, "apps", "windows", "src", "KeyClick.App", "Resources");
    var files = Directory.GetFiles(resourceDirectory, "Strings.*.xaml").Order().ToArray();
    Assert.NotEmpty(files);

    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    string[]? expectedKeys = null;
    foreach (var file in files)
    {
      var keys = XDocument.Load(file).Descendants()
        .Select(element => element.Attribute(x + "Key")?.Value)
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Cast<string>()
        .ToArray();
      var duplicateKeys = keys.GroupBy(key => key).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
      Assert.True(duplicateKeys.Length == 0, $"{Path.GetFileName(file)} contains duplicate keys: {string.Join(", ", duplicateKeys)}");

      expectedKeys ??= keys.Order().ToArray();
      Assert.Equal(expectedKeys, keys.Order().ToArray());
    }
  }

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeyClick.sln")))
      directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the KeyClick repository root.");
  }
}
