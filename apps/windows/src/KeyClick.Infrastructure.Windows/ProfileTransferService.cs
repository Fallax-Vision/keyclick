using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyClick.Core;

namespace KeyClick.Infrastructure.Windows;

public sealed class ProfileTransferService(AppPaths paths, IAppStore appStore, IStatisticsStore statisticsStore, ITypingChallengeStore? challengeStore = null)
{
  private const int SchemaVersion = 2;
  private const long MaxProfileBytes = 500L * 1024 * 1024;
  private static readonly byte[] Header = Encoding.ASCII.GetBytes("KCPROF1\0");
  private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
  {
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
  };

  public async Task ExportAsync(string destination, ProfileExportOptions options, CancellationToken cancellationToken = default)
  {
    if (options.ChallengePrompts && string.IsNullOrWhiteSpace(options.Password))
      throw new InvalidDataException("Saved typing prompts require a password-protected profile.");
    var sections = new List<string>();
    var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    if (options.SettingsAndMappings)
    {
      sections.Add("settings-mappings");
      var settings = Sanitize(await appStore.LoadSettingsAsync(cancellationToken));
      var packIds = BuiltInCatalog.Packs.Select(pack => pack.Id).Concat(InstalledPackIds()).Distinct(StringComparer.Ordinal).ToArray();
      var overrides = new List<InputOverride>();
      var groups = new List<GroupMapping>();
      foreach (var packId in packIds)
      {
        overrides.AddRange(await appStore.LoadOverridesAsync(packId, cancellationToken));
        groups.AddRange(await appStore.LoadGroupMappingsAsync(packId, cancellationToken));
      }
      var payload = new SettingsPayload(settings, overrides, groups, await appStore.LoadShortcutsAsync(cancellationToken));
      entries["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
    }
    if (options.Statistics || options.WellnessAchievements)
    {
      if (options.Statistics) sections.Add("statistics");
      if (options.WellnessAchievements) sections.Add("wellness-achievements");
      entries["statistics.json"] = JsonSerializer.SerializeToUtf8Bytes(
        await statisticsStore.ExportStatisticsAsync(options.WellnessAchievements, cancellationToken), Json);
    }
    if (options.CustomPacksAndAudio)
    {
      sections.Add("custom-packs-audio");
      AddMedia(entries, paths.Packs, "media/packs", [".json"], cancellationToken);
      AddMedia(entries, paths.Sounds, "media/sounds", [".wav", ".mp3", ".ogg"], cancellationToken);
    }
    if (options.ChallengeHistory || options.ChallengePrompts)
    {
      if (challengeStore is null) throw new InvalidOperationException("Typing challenge transfer is unavailable.");
      if (options.ChallengeHistory) sections.Add("challenge-history");
      if (options.ChallengePrompts) sections.Add("challenge-prompts");
      entries["challenges.json"] = JsonSerializer.SerializeToUtf8Bytes(
        await challengeStore.ExportTypingChallengesAsync(options.ChallengeHistory, options.ChallengePrompts, cancellationToken), Json);
    }

    var hashes = entries.ToDictionary(item => item.Key, item => Convert.ToHexString(SHA256.HashData(item.Value)).ToLowerInvariant(), StringComparer.Ordinal);
    var manifest = new ProfileManifest(SchemaVersion, DateTimeOffset.UtcNow, typeof(ProfileTransferService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0", sections, !string.IsNullOrEmpty(options.Password), hashes);
    entries["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
    var archive = CreateArchive(entries);
    var output = string.IsNullOrEmpty(options.Password) ? WrapPlain(archive) : Encrypt(archive, options.Password);
    if (output.LongLength > MaxProfileBytes) throw new InvalidDataException("The profile is larger than the 500 MB safety limit.");
    await File.WriteAllBytesAsync(destination, output, cancellationToken);
  }

  public async Task<bool> RequiresPasswordAsync(string source, CancellationToken cancellationToken = default)
  {
    var buffer = new byte[Header.Length + 1];
    await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (await stream.ReadAsync(buffer, cancellationToken) != buffer.Length || !buffer.AsSpan(0, Header.Length).SequenceEqual(Header))
      throw new InvalidDataException("The profile header is invalid.");
    return buffer[Header.Length] == 1;
  }

  public async Task<ProfileImportPreview> PreviewAsync(string source, string? password, CancellationToken cancellationToken = default)
  {
    var archive = await ReadArchiveAsync(source, password, cancellationToken);
    using var zip = new ZipArchive(new MemoryStream(archive, false), ZipArchiveMode.Read);
    ValidateEntries(zip);
    var manifest = await ReadJsonAsync<ProfileManifest>(zip, "manifest.json", cancellationToken) ?? throw new InvalidDataException("The profile manifest is missing.");
    if (manifest.SchemaVersion is < 1 or > SchemaVersion) throw new InvalidDataException("This profile schema version is not supported.");
    VerifyHashes(zip, manifest);
    var mediaCount = zip.Entries.Count(entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal));
    long buckets = 0;
    if (manifest.Sections.Contains("statistics", StringComparer.Ordinal) && zip.GetEntry("statistics.json") is not null)
      buckets = (await ReadJsonAsync<StatisticsTransferBundle>(zip, "statistics.json", cancellationToken))?.Summaries.Count ?? 0;
    long challengeResults = 0;
    long savedPrompts = 0;
    if ((manifest.Sections.Contains("challenge-history", StringComparer.Ordinal) || manifest.Sections.Contains("challenge-prompts", StringComparer.Ordinal))
      && zip.GetEntry("challenges.json") is not null)
    {
      var challenges = await ReadJsonAsync<TypingChallengeTransferBundle>(zip, "challenges.json", cancellationToken);
      challengeResults = challenges?.Results.Count ?? 0;
      savedPrompts = challenges?.Prompts.Count ?? 0;
    }
    return new(manifest, manifest.Sections, mediaCount, buckets, manifest.PasswordProtected, challengeResults, savedPrompts);
  }

  public async Task<AppSettings> ImportAsync(string source, string? password, bool useImportedMediaOnConflict, CancellationToken cancellationToken = default)
  {
    var archive = await ReadArchiveAsync(source, password, cancellationToken);
    using var zip = new ZipArchive(new MemoryStream(archive, false), ZipArchiveMode.Read);
    ValidateEntries(zip);
    var manifest = await ReadJsonAsync<ProfileManifest>(zip, "manifest.json", cancellationToken) ?? throw new InvalidDataException("The profile manifest is missing.");
    if (manifest.SchemaVersion is < 1 or > SchemaVersion) throw new InvalidDataException("This profile schema version is not supported.");
    VerifyHashes(zip, manifest);

    var local = await appStore.LoadSettingsAsync(cancellationToken);
    if (manifest.Sections.Contains("settings-mappings", StringComparer.Ordinal))
    {
      var payload = await ReadJsonAsync<SettingsPayload>(zip, "settings.json", cancellationToken) ?? throw new InvalidDataException("The settings section is invalid.");
      local = MergeTransferable(local, payload.Settings);
      await appStore.SaveSettingsAsync(local, cancellationToken);
      foreach (var value in payload.Overrides) await appStore.SaveOverrideAsync(value, cancellationToken);
      foreach (var value in payload.Groups) await appStore.SaveGroupMappingAsync(value, cancellationToken);
      foreach (var value in payload.Shortcuts) await appStore.SaveShortcutAsync(value, cancellationToken);
    }
    if (manifest.Sections.Contains("statistics", StringComparer.Ordinal) || manifest.Sections.Contains("wellness-achievements", StringComparer.Ordinal))
    {
      var bundle = await ReadJsonAsync<StatisticsTransferBundle>(zip, "statistics.json", cancellationToken) ?? throw new InvalidDataException("The statistics section is invalid.");
      await statisticsStore.ImportStatisticsAsync(bundle, manifest.Sections.Contains("wellness-achievements", StringComparer.Ordinal), cancellationToken);
    }
    if (manifest.Sections.Contains("custom-packs-audio", StringComparer.Ordinal))
      await ImportMediaAsync(zip, useImportedMediaOnConflict, cancellationToken);
    if (manifest.Sections.Contains("challenge-history", StringComparer.Ordinal) || manifest.Sections.Contains("challenge-prompts", StringComparer.Ordinal))
    {
      if (challengeStore is null) throw new InvalidOperationException("Typing challenge transfer is unavailable.");
      var bundle = await ReadJsonAsync<TypingChallengeTransferBundle>(zip, "challenges.json", cancellationToken)
        ?? throw new InvalidDataException("The typing challenge section is invalid.");
      await challengeStore.ImportTypingChallengesAsync(bundle,
        manifest.Sections.Contains("challenge-history", StringComparer.Ordinal),
        manifest.Sections.Contains("challenge-prompts", StringComparer.Ordinal), cancellationToken);
    }
    return local;
  }

  private IEnumerable<string> InstalledPackIds()
  {
    if (!Directory.Exists(paths.Packs)) yield break;
    foreach (var file in Directory.EnumerateFiles(paths.Packs, "*.json").Take(100))
    {
      SoundPackDefinition? pack = null;
      try { pack = JsonSerializer.Deserialize<SoundPackDefinition>(File.ReadAllText(file), Json); } catch { }
      if (pack is { IsCustom: true }) yield return pack.Id;
    }
  }

  private static AppSettings Sanitize(AppSettings settings)
  {
    settings.NormalizeFunStats();
    settings.LaunchAtStartup = false;
    settings.OutputDeviceId = "default";
    settings.ExcludedExecutables = [];
    settings.StatisticsExcludedExecutables = [];
    settings.AllowedIntegrationClients = [];
    settings.DeviceClassifications = [];
    settings.PackRotation = settings.PackRotation with { NextDueUtc = null, LastWindowsBootTicks = null };
    return settings;
  }

  private static AppSettings MergeTransferable(AppSettings local, AppSettings imported)
  {
    local.DisplayName = imported.DisplayName;
    local.SoundsEnabled = imported.SoundsEnabled;
    local.KeyboardEnabled = imported.KeyboardEnabled;
    local.PointerEnabled = imported.PointerEnabled;
    local.WheelEnabled = imported.WheelEnabled;
    local.ResultSoundsEnabled = imported.ResultSoundsEnabled;
    local.CloseToTray = imported.CloseToTray;
    local.PauseInFullscreen = imported.PauseInFullscreen;
    local.ReducedMotion = imported.ReducedMotion;
    local.NormalizeImports = imported.NormalizeImports;
    local.Theme = imported.Theme;
    local.DisplayLanguage = imported.DisplayLanguage;
    local.KeyboardSoundTiming = imported.KeyboardSoundTiming;
    local.SoundPackViewMode = imported.SoundPackViewMode;
    local.ActivePackId = imported.ActivePackId;
    local.MasterVolume = imported.MasterVolume;
    local.KeyboardVolume = imported.KeyboardVolume;
    local.PointerVolume = imported.PointerVolume;
    local.ResultVolume = imported.ResultVolume;
    local.SequenceTimeoutMs = imported.SequenceTimeoutMs;
    local.KeyboardStatisticsEnabled = imported.KeyboardStatisticsEnabled;
    local.PointerStatisticsEnabled = imported.PointerStatisticsEnabled;
    local.ScrollingStatisticsEnabled = imported.ScrollingStatisticsEnabled;
    local.IncludeChallengeTypingInStatistics = imported.IncludeChallengeTypingInStatistics;
    imported.NormalizeFunStats();
    local.FunStatsEnabled = imported.FunStatsEnabled;
    local.MetricCardFunFactsEnabled = imported.MetricCardFunFactsEnabled;
    local.FunFactRotation = imported.FunFactRotation;
    local.FunStatsCopyMode = imported.FunStatsCopyMode;
    local.ScrollCentimetersPerDetent = imported.ScrollCentimetersPerDetent;
    local.HomeFunStatsPeriod = imported.HomeFunStatsPeriod;
    local.SelectedFunStatIds = [.. imported.SelectedFunStatIds];
    local.DisabledFunFactIds = [.. imported.DisabledFunFactIds];
    local.CustomFunStats = imported.CustomFunStats.Select(item => new CustomFunStatDefinition
    {
      Id = item.Id,
      Label = item.Label,
      Metric = item.Metric,
      Target = item.Target
    }).ToList();
    local.StatisticsChartMetricFamily = imported.StatisticsChartMetricFamily;
    local.StatisticsChartViewType = imported.StatisticsChartViewType;
    local.StatisticsTrendGranularity = imported.StatisticsTrendGranularity;
    local.EnabledStatisticsChartSeries = [.. imported.EnabledStatisticsChartSeries];
    local.TypingChallengeGoalWordsPerMinute = imported.TypingChallengeGoalWordsPerMinute;
    local.TypingChallengeGoalAccuracy = imported.TypingChallengeGoalAccuracy;
    local.FavoriteTypingChallengeIds = [.. imported.FavoriteTypingChallengeIds];
    local.WellnessEnabled = imported.WellnessEnabled;
    local.BreakReminderEnabled = imported.BreakReminderEnabled;
    local.BreakReminderActiveMinutes = imported.BreakReminderActiveMinutes;
    local.BreakReminderRestMinutes = imported.BreakReminderRestMinutes;
    local.KeyboardGoalEnabled = imported.KeyboardGoalEnabled;
    local.PointerGoalEnabled = imported.PointerGoalEnabled;
    local.ActiveMinutesGoalEnabled = imported.ActiveMinutesGoalEnabled;
    local.KeyboardDailyGoal = imported.KeyboardDailyGoal;
    local.PointerDailyGoal = imported.PointerDailyGoal;
    local.ActiveMinutesDailyGoal = imported.ActiveMinutesDailyGoal;
    local.PackRotation = imported.PackRotation with { NextDueUtc = null, LastWindowsBootTicks = null };
    return local;
  }

  private static void AddMedia(Dictionary<string, byte[]> entries, string directory, string prefix, IReadOnlyCollection<string> extensions, CancellationToken cancellationToken)
  {
    if (!Directory.Exists(directory)) return;
    foreach (var file in Directory.EnumerateFiles(directory).Take(512))
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || new FileInfo(file).Length > 300L * 1024 * 1024) continue;
      entries[$"{prefix}/{Path.GetFileName(file)}"] = File.ReadAllBytes(file);
    }
  }

  private static byte[] CreateArchive(IReadOnlyDictionary<string, byte[]> entries)
  {
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
      foreach (var item in entries)
      {
        var entry = archive.CreateEntry(item.Key, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(item.Value);
      }
    return output.ToArray();
  }

  private static byte[] WrapPlain(byte[] archive)
  {
    var result = new byte[Header.Length + 1 + archive.Length];
    Header.CopyTo(result, 0);
    result[Header.Length] = 0;
    archive.CopyTo(result, Header.Length + 1);
    return result;
  }

  private static byte[] Encrypt(byte[] archive, string password)
  {
    var salt = RandomNumberGenerator.GetBytes(16);
    var nonce = RandomNumberGenerator.GetBytes(12);
    var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200000, HashAlgorithmName.SHA256, 32);
    var cipher = new byte[archive.Length];
    var tag = new byte[16];
    using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, archive, cipher, tag, Header);
    CryptographicOperations.ZeroMemory(key);
    var result = new byte[Header.Length + 1 + salt.Length + nonce.Length + tag.Length + cipher.Length];
    var offset = 0;
    Header.CopyTo(result, offset); offset += Header.Length;
    result[offset++] = 1;
    salt.CopyTo(result, offset); offset += salt.Length;
    nonce.CopyTo(result, offset); offset += nonce.Length;
    tag.CopyTo(result, offset); offset += tag.Length;
    cipher.CopyTo(result, offset);
    return result;
  }

  private static async Task<byte[]> ReadArchiveAsync(string source, string? password, CancellationToken cancellationToken)
  {
    var file = new FileInfo(source);
    if (!file.Exists || file.Length is <= 9 or > MaxProfileBytes) throw new InvalidDataException("The profile file is missing or outside the size limit.");
    var content = await File.ReadAllBytesAsync(source, cancellationToken);
    if (!content.AsSpan(0, Header.Length).SequenceEqual(Header)) throw new InvalidDataException("The profile header is invalid.");
    if (content[Header.Length] == 0) return content[(Header.Length + 1)..];
    if (content[Header.Length] != 1 || string.IsNullOrEmpty(password) || content.Length < Header.Length + 46) throw new InvalidDataException("This profile requires the correct password.");
    var offset = Header.Length + 1;
    var salt = content.AsSpan(offset, 16); offset += 16;
    var nonce = content.AsSpan(offset, 12); offset += 12;
    var tag = content.AsSpan(offset, 16); offset += 16;
    var cipher = content.AsSpan(offset);
    var plain = new byte[cipher.Length];
    var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200000, HashAlgorithmName.SHA256, 32);
    try { using var aes = new AesGcm(key, 16); aes.Decrypt(nonce, cipher, tag, plain, Header); }
    catch (AuthenticationTagMismatchException) { throw new InvalidDataException("The profile password is wrong or the file was modified."); }
    finally { CryptographicOperations.ZeroMemory(key); }
    return plain;
  }

  private static void ValidateEntries(ZipArchive archive)
  {
    if (archive.Entries.Count is 0 or > 1024) throw new InvalidDataException("The profile has an invalid number of entries.");
    long total = 0;
    foreach (var entry in archive.Entries)
    {
      var normalized = entry.FullName.Replace('\\', '/');
      if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) || Path.IsPathRooted(normalized)) throw new InvalidDataException("The profile contains an unsafe path.");
      total += entry.Length;
      if (entry.Length > 300L * 1024 * 1024 || total > MaxProfileBytes) throw new InvalidDataException("The expanded profile exceeds its safety limit.");
    }
  }

  private static void VerifyHashes(ZipArchive archive, ProfileManifest manifest)
  {
    foreach (var item in manifest.FileHashes)
    {
      var entry = archive.GetEntry(item.Key) ?? throw new InvalidDataException("A profile entry is missing.");
      using var stream = entry.Open();
      var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
      if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(item.Value.ToLowerInvariant())))
        throw new InvalidDataException("A profile entry failed hash verification.");
    }
  }

  private async Task ImportMediaAsync(ZipArchive archive, bool overwrite, CancellationToken cancellationToken)
  {
    foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal)))
    {
      cancellationToken.ThrowIfCancellationRequested();
      var pack = entry.FullName.StartsWith("media/packs/", StringComparison.Ordinal);
      var allowed = pack
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json" }
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".ogg" };
      if (!allowed.Contains(Path.GetExtension(entry.Name)) || entry.Name != Path.GetFileName(entry.Name)) throw new InvalidDataException("The profile contains an unsupported media file.");
      var directory = pack ? paths.Packs : paths.Sounds;
      Directory.CreateDirectory(directory);
      var destination = Path.GetFullPath(Path.Combine(directory, entry.Name));
      if (!destination.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The profile media path is unsafe.");
      if (File.Exists(destination) && !overwrite) continue;
      await using var input = entry.Open();
      await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
      await input.CopyToAsync(output, cancellationToken);
    }
  }

  private static async Task<T?> ReadJsonAsync<T>(ZipArchive archive, string name, CancellationToken cancellationToken)
  {
    var entry = archive.GetEntry(name);
    if (entry is null) return default;
    await using var stream = entry.Open();
    return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken);
  }

  private sealed record SettingsPayload(
    AppSettings Settings,
    IReadOnlyList<InputOverride> Overrides,
    IReadOnlyList<GroupMapping> Groups,
    IReadOnlyList<ShortcutBinding> Shortcuts);
}
