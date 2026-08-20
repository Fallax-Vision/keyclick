using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class SoundPackImportException(string resourceKey, params object?[] arguments) : Exception(resourceKey)
{
  public string ResourceKey { get; } = resourceKey;
  public object?[] Arguments { get; } = arguments;
}

public sealed class SoundPackImportService(AppPaths paths, AudioImportService audioImporter)
{
  public const long MaxArchiveBytes = 250L * 1024 * 1024;
  public const long MaxExpandedBytes = 300L * 1024 * 1024;
  public const int MaxArchiveEntries = 256;
  public const int MaxAudioFiles = 128;
  private const int MaxManifestBytes = 256 * 1024;
  private static readonly Regex PackId = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
  private static readonly Regex Accent = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
  private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".keyclickpack", ".zip" };
  private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".ogg" };
  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

  public Task<IReadOnlyList<SoundPackDefinition>> LoadInstalledAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<SoundPackDefinition>>(() =>
  {
    paths.EnsureCreated();
    var results = new List<SoundPackDefinition>();
    foreach (var file in Directory.EnumerateFiles(paths.Packs, "*.json").Take(100))
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        if (new FileInfo(file).Length > 1024 * 1024) continue;
        var pack = JsonSerializer.Deserialize<SoundPackDefinition>(File.ReadAllText(file), Json);
        if (pack is null || !IsInstalledPackValid(pack)) continue;
        results.Add(pack);
      }
      catch (JsonException) { }
      catch (IOException) { }
    }
    return results.OrderBy(pack => pack.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
  }, cancellationToken);

  public Task<SoundPackDefinition> ImportAsync(string archivePath, bool normalize, CancellationToken cancellationToken = default) => Task.Run(async () =>
  {
    var source = new FileInfo(archivePath);
    if (!source.Exists) throw new SoundPackImportException("SoundPackFileMissing");
    if (!ArchiveExtensions.Contains(source.Extension)) throw new SoundPackImportException("SoundPackChooseArchive");
    if (source.Length > MaxArchiveBytes) throw new SoundPackImportException("SoundPackArchiveTooLarge");
    paths.EnsureCreated();

    var staging = Path.GetFullPath(Path.Combine(paths.Root, $".pack-import-{Guid.NewGuid():N}"));
    EnsureChild(paths.Root, staging);
    Directory.CreateDirectory(staging);
    try
    {
      using var archive = OpenArchive(source.FullName);
      if (archive.Entries.Count is 0 or > MaxArchiveEntries) throw new SoundPackImportException("SoundPackArchiveInvalid");
      long expanded = 0;
      var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
      foreach (var entry in archive.Entries)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeArchivePath(entry.FullName);
        if (!IsSafeRelativePath(normalized)) throw new SoundPackImportException("SoundPackPathInvalidFormat", entry.FullName);
        if (entry.Length > MaxExpandedBytes - expanded || !entries.TryAdd(normalized, entry)) throw new SoundPackImportException("SoundPackArchiveInvalid");
        expanded += entry.Length;
      }

      if (!entries.TryGetValue("pack.json", out var manifestEntry) || manifestEntry.Length is <= 0 or > MaxManifestBytes)
        throw new SoundPackImportException("SoundPackManifestMissing");
      var manifest = await ReadManifestAsync(manifestEntry, cancellationToken);
      ValidateManifest(manifest);
      if (BuiltInCatalog.Packs.Any(pack => string.Equals(pack.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
        throw new SoundPackImportException("SoundPackIdReservedFormat", manifest.Id);

      var pools = new Dictionary<string, string[]>(StringComparer.Ordinal);
      var importedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var audioCount = 0;
      foreach (var groupEntry in manifest.Groups)
      {
        if (!Enum.TryParse<InputGroup>(groupEntry.Key, true, out var group))
          throw new SoundPackImportException("SoundPackGroupInvalidFormat", groupEntry.Key);
        if (groupEntry.Value is null || groupEntry.Value.Count == 0)
          throw new SoundPackImportException("SoundPackManifestInvalid");

        foreach (var variantEntry in groupEntry.Value)
        {
          if (!TryParseVariant(variantEntry.Key, out var variant))
            throw new SoundPackImportException("SoundPackVariantInvalidFormat", variantEntry.Key);
          if (variantEntry.Value is null || variantEntry.Value.Length is 0 or > 8)
            throw new SoundPackImportException("SoundPackManifestInvalid");

          var sampleIds = new List<string>();
          foreach (var relativePath in variantEntry.Value)
          {
            audioCount++;
            if (audioCount > MaxAudioFiles) throw new SoundPackImportException("SoundPackArchiveInvalid");
            var normalized = NormalizeArchivePath(relativePath?.Trim() ?? string.Empty);
            if (!IsSafeRelativePath(normalized) || normalized.Length > 160 || !AudioExtensions.Contains(Path.GetExtension(normalized)))
              throw new SoundPackImportException("SoundPackPathInvalidFormat", relativePath);
            if (!entries.TryGetValue(normalized, out var audioEntry) || audioEntry.Length is <= 0 or > AudioImportService.MaxFileBytes)
              throw new SoundPackImportException("SoundPackAudioMissingFormat", relativePath);

            if (!importedFiles.TryGetValue(normalized, out var sampleId))
            {
              var temporary = Path.Combine(staging, $"audio-{importedFiles.Count:D3}{Path.GetExtension(normalized).ToLowerInvariant()}");
              await using (var input = audioEntry.Open())
              await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, cancellationToken);
              try
              {
                var imported = await audioImporter.ImportAsync(temporary, normalize, cancellationToken);
                sampleId = $"custom:{imported.Id}";
              }
              catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
              {
                throw new SoundPackImportException("SoundPackAudioInvalidFormat", relativePath);
              }
              importedFiles[normalized] = sampleId;
            }
            sampleIds.Add(sampleId);
          }
          pools[SoundPackDefinition.PoolKey(group, variant)] = sampleIds.Distinct(StringComparer.Ordinal).ToArray();
        }
      }

      var pack = new SoundPackDefinition(
        manifest.Id,
        manifest.Name.Trim(),
        manifest.Family?.Trim() ?? string.Empty,
        manifest.Description?.Trim() ?? string.Empty,
        0, 0, 0, 0,
        string.IsNullOrWhiteSpace(manifest.Accent) ? "#35E04B" : manifest.Accent,
        true,
        pools);
      await SavePackAsync(pack, cancellationToken);
      return pack;
    }
    finally
    {
      if (Directory.Exists(staging)) Directory.Delete(staging, true);
    }
  }, cancellationToken);

  private static ZipArchive OpenArchive(string path)
  {
    try { return ZipFile.OpenRead(path); }
    catch (InvalidDataException) { throw new SoundPackImportException("SoundPackArchiveInvalid"); }
  }

  private static async Task<PackageManifest> ReadManifestAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
  {
    try
    {
      await using var stream = entry.Open();
      return await JsonSerializer.DeserializeAsync<PackageManifest>(stream, Json, cancellationToken)
        ?? throw new SoundPackImportException("SoundPackManifestInvalid");
    }
    catch (JsonException) { throw new SoundPackImportException("SoundPackManifestInvalid"); }
  }

  private static void ValidateManifest(PackageManifest manifest)
  {
    if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 64 || !PackId.IsMatch(manifest.Id) ||
        string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Trim().Length > 80 ||
        (manifest.Family?.Length ?? 0) > 80 || (manifest.Description?.Length ?? 0) > 240 ||
        (!string.IsNullOrWhiteSpace(manifest.Accent) && !Accent.IsMatch(manifest.Accent)) ||
        manifest.Groups is null || manifest.Groups.Count is 0 or > 16)
      throw new SoundPackImportException("SoundPackManifestInvalid");
  }

  private async Task SavePackAsync(SoundPackDefinition pack, CancellationToken cancellationToken)
  {
    var destination = Path.GetFullPath(Path.Combine(paths.Packs, $"{pack.Id}.json"));
    EnsureChild(paths.Packs, destination);
    var temporary = destination + $".{Guid.NewGuid():N}.tmp";
    try
    {
      await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(pack, Json), cancellationToken);
      File.Move(temporary, destination, true);
    }
    finally
    {
      if (File.Exists(temporary)) File.Delete(temporary);
    }
  }

  private bool IsInstalledPackValid(SoundPackDefinition? pack) =>
    pack is not null && pack.IsCustom && pack.Id is { Length: > 0 and <= 64 } && PackId.IsMatch(pack.Id) &&
    !BuiltInCatalog.Packs.Any(item => string.Equals(item.Id, pack.Id, StringComparison.OrdinalIgnoreCase)) &&
    !string.IsNullOrWhiteSpace(pack.Name) && pack.SamplePools is { Count: > 0 } &&
    pack.AllSampleIds().All(IsInstalledSampleValid);

  private bool IsInstalledSampleValid(string sampleId)
  {
    if (!CustomSampleId.IsValid(sampleId)) return false;
    return File.Exists(Path.Combine(paths.Sounds, CustomSampleId.FileName(sampleId)));
  }

  private static bool TryParseVariant(string value, out KeyVariant variant)
  {
    if (string.Equals(value, "altGr", StringComparison.OrdinalIgnoreCase)) { variant = KeyVariant.AltGr; return true; }
    return Enum.TryParse(value, true, out variant);
  }

  private static string NormalizeArchivePath(string path) => path.Replace('\\', '/');

  private static bool IsSafeRelativePath(string path) =>
    !string.IsNullOrWhiteSpace(path) && !path.StartsWith('/') && !path.Contains(':') && !Path.IsPathFullyQualified(path) &&
    !path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");

  private static void EnsureChild(string root, string candidate)
  {
    var rootWithSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(candidate).StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
      throw new SoundPackImportException("SoundPackArchiveInvalid");
  }

  private sealed class PackageManifest
  {
    public int Version { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Family { get; init; }
    public string? Description { get; init; }
    public string? Accent { get; init; }
    public Dictionary<string, Dictionary<string, string[]>> Groups { get; init; } = [];
  }
}
