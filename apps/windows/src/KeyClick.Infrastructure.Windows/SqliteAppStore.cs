using System.Text.Json;
using System.Text.Json.Serialization;
using KeyClick.Core;
using Microsoft.Data.Sqlite;

namespace KeyClick.Infrastructure.Windows;

public sealed class SqliteAppStore(AppPaths paths) : IAppStore, IStatisticsStore
{
  private readonly SemaphoreSlim _writeGate = new(1, 1);
  private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
  {
    Converters = { new JsonStringEnumConverter() }
  };
  private SqliteConnection? _connection;

  public async Task InitializeAsync(CancellationToken cancellationToken = default)
  {
    paths.EnsureCreated();
    _connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
      DataSource = paths.Database,
      Mode = SqliteOpenMode.ReadWriteCreate,
      Cache = SqliteCacheMode.Shared,
      Pooling = false
    }.ToString());
    await _connection.OpenAsync(cancellationToken);

    var migration = _connection.CreateCommand();
    migration.CommandText = """
      PRAGMA journal_mode=WAL;
      PRAGMA synchronous=NORMAL;
      PRAGMA foreign_keys=ON;
      CREATE TABLE IF NOT EXISTS schema_migrations (
        version INTEGER PRIMARY KEY,
        applied_utc TEXT NOT NULL
      );
      CREATE TABLE IF NOT EXISTS settings (
        key TEXT PRIMARY KEY,
        value_json TEXT NOT NULL,
        updated_utc TEXT NOT NULL
      );
      CREATE TABLE IF NOT EXISTS input_overrides (
        pack_id TEXT NOT NULL,
        input_id TEXT NOT NULL,
        variant TEXT NOT NULL,
        enabled INTEGER NOT NULL,
        volume REAL NULL,
        sample_ids_json TEXT NOT NULL,
        PRIMARY KEY (pack_id, input_id, variant)
      );
      CREATE TABLE IF NOT EXISTS shortcuts (
        command_id TEXT PRIMARY KEY,
        binding_json TEXT NOT NULL,
        updated_utc TEXT NOT NULL
      );
      CREATE TABLE IF NOT EXISTS group_mappings (
        pack_id TEXT NOT NULL,
        input_group TEXT NOT NULL,
        variant TEXT NOT NULL,
        enabled INTEGER NOT NULL,
        volume REAL NOT NULL,
        sample_ids_json TEXT NOT NULL,
        PRIMARY KEY (pack_id, input_group, variant)
      );
      CREATE TABLE IF NOT EXISTS device_profiles (
        device_id TEXT PRIMARY KEY,
        display_name TEXT NOT NULL,
        family TEXT NOT NULL,
        enabled INTEGER NOT NULL DEFAULT 1
      );
      CREATE TABLE IF NOT EXISTS group_mappings_v2 (
        pack_id TEXT NOT NULL,
        input_group TEXT NOT NULL,
        variant TEXT NOT NULL,
        device_family TEXT NOT NULL,
        enabled INTEGER NOT NULL,
        volume REAL NOT NULL,
        sample_ids_json TEXT NOT NULL,
        PRIMARY KEY (pack_id, input_group, variant, device_family)
      );
      CREATE TABLE IF NOT EXISTS integration_clients (
        executable_path TEXT PRIMARY KEY,
        enabled INTEGER NOT NULL DEFAULT 1,
        added_utc TEXT NOT NULL
      );
      CREATE TABLE IF NOT EXISTS app_rules (
        executable_path TEXT PRIMARY KEY,
        sounds_enabled INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS statistics_sources (
        source_id TEXT PRIMARY KEY,
        created_utc TEXT NOT NULL,
        platform TEXT NOT NULL
      );
      CREATE TABLE IF NOT EXISTS statistics_input_hourly (
        source_id TEXT NOT NULL,
        bucket_utc TEXT NOT NULL,
        input_kind TEXT NOT NULL,
        device_family TEXT NOT NULL,
        physical_code INTEGER NOT NULL,
        extended INTEGER NOT NULL,
        input_group TEXT NOT NULL,
        count INTEGER NOT NULL,
        revision INTEGER NOT NULL,
        PRIMARY KEY (source_id, bucket_utc, input_kind, device_family, physical_code, extended, input_group),
        FOREIGN KEY (source_id) REFERENCES statistics_sources(source_id) ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS statistics_hourly_summaries (
        source_id TEXT NOT NULL,
        bucket_utc TEXT NOT NULL,
        keyboard_presses INTEGER NOT NULL DEFAULT 0,
        typing_key_presses INTEGER NOT NULL DEFAULT 0,
        pointer_clicks INTEGER NOT NULL DEFAULT 0,
        vertical_scroll INTEGER NOT NULL DEFAULT 0,
        horizontal_scroll INTEGER NOT NULL DEFAULT 0,
        active_ms INTEGER NOT NULL DEFAULT 0,
        keyboard_active_ms INTEGER NOT NULL DEFAULT 0,
        pointer_active_ms INTEGER NOT NULL DEFAULT 0,
        peak_typing_keys_60s INTEGER NOT NULL DEFAULT 0,
        peak_clicks_5s INTEGER NOT NULL DEFAULT 0,
        revision INTEGER NOT NULL,
        PRIMARY KEY (source_id, bucket_utc),
        FOREIGN KEY (source_id) REFERENCES statistics_sources(source_id) ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS wellness_achievements (
        id TEXT PRIMARY KEY,
        goal_kind TEXT NOT NULL,
        local_date TEXT NOT NULL,
        target_snapshot INTEGER NOT NULL,
        actual_value INTEGER NOT NULL,
        achieved_utc TEXT NOT NULL
      );
      CREATE INDEX IF NOT EXISTS ix_statistics_input_range ON statistics_input_hourly(bucket_utc, input_kind);
      CREATE INDEX IF NOT EXISTS ix_statistics_summary_range ON statistics_hourly_summaries(bucket_utc);
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(3, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      """;
    await migration.ExecuteNonQueryAsync(cancellationToken);

    if ((await LoadShortcutsAsync(cancellationToken)).Count == 0)
    {
      foreach (var shortcut in BuiltInCatalog.DefaultShortcuts)
      {
        await SaveShortcutAsync(shortcut, cancellationToken);
      }
    }
  }

  public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
  {
    var connection = RequireConnection();
    var command = connection.CreateCommand();
    command.CommandText = "SELECT value_json FROM settings WHERE key = 'app';";
    var result = await command.ExecuteScalarAsync(cancellationToken) as string;
    if (result is null) return new AppSettings();
    var settings = JsonSerializer.Deserialize<AppSettings>(result, _json) ?? new AppSettings();
    using var document = JsonDocument.Parse(result);
    if (!document.RootElement.TryGetProperty("keyboardSoundTiming", out _)) settings.KeyboardSoundTiming = KeyboardSoundTiming.KeyUp;
    return settings;
  }

  public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
    ExecuteWriteAsync("""
      INSERT INTO settings(key, value_json, updated_utc)
      VALUES('app', $value, $updated)
      ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_utc=excluded.updated_utc;
      """, command =>
    {
      command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings, _json));
      command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
    }, cancellationToken);

  public async Task<IReadOnlyList<InputOverride>> LoadOverridesAsync(string packId, CancellationToken cancellationToken = default)
  {
    var command = RequireConnection().CreateCommand();
    command.CommandText = "SELECT input_id, variant, enabled, volume, sample_ids_json FROM input_overrides WHERE pack_id=$pack;";
    command.Parameters.AddWithValue("$pack", packId);
    var results = new List<InputOverride>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      results.Add(new InputOverride(
        packId,
        reader.GetString(0),
        Enum.Parse<KeyVariant>(reader.GetString(1)),
        reader.GetBoolean(2),
        reader.IsDBNull(3) ? null : reader.GetFloat(3),
        JsonSerializer.Deserialize<string[]>(reader.GetString(4), _json) ?? []));
    }
    return results;
  }

  public Task SaveOverrideAsync(InputOverride inputOverride, CancellationToken cancellationToken = default) =>
    ExecuteWriteAsync("""
      INSERT INTO input_overrides(pack_id,input_id,variant,enabled,volume,sample_ids_json)
      VALUES($pack,$input,$variant,$enabled,$volume,$samples)
      ON CONFLICT(pack_id,input_id,variant) DO UPDATE SET
        enabled=excluded.enabled, volume=excluded.volume, sample_ids_json=excluded.sample_ids_json;
      """, command =>
    {
      command.Parameters.AddWithValue("$pack", inputOverride.PackId);
      command.Parameters.AddWithValue("$input", inputOverride.InputId);
      command.Parameters.AddWithValue("$variant", inputOverride.Variant.ToString());
      command.Parameters.AddWithValue("$enabled", inputOverride.Enabled);
      command.Parameters.AddWithValue("$volume", (object?)inputOverride.Volume ?? DBNull.Value);
      command.Parameters.AddWithValue("$samples", JsonSerializer.Serialize(inputOverride.SampleIds, _json));
    }, cancellationToken);

  public Task RemoveOverrideAsync(string packId, string inputId, KeyVariant variant, CancellationToken cancellationToken = default) =>
    ExecuteWriteAsync(
      "DELETE FROM input_overrides WHERE pack_id=$pack AND input_id=$input AND variant=$variant;",
      command =>
      {
        command.Parameters.AddWithValue("$pack", packId);
        command.Parameters.AddWithValue("$input", inputId);
        command.Parameters.AddWithValue("$variant", variant.ToString());
      }, cancellationToken);

  public async Task<IReadOnlyList<GroupMapping>> LoadGroupMappingsAsync(string packId, CancellationToken cancellationToken = default)
  {
    var command = RequireConnection().CreateCommand();
    command.CommandText = "SELECT input_group,variant,device_family,enabled,volume,sample_ids_json FROM group_mappings_v2 WHERE pack_id=$pack;";
    command.Parameters.AddWithValue("$pack", packId);
    var results = new List<GroupMapping>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      var family = reader.GetString(2);
      results.Add(new GroupMapping(
        packId,
        Enum.Parse<InputGroup>(reader.GetString(0)),
        Enum.Parse<KeyVariant>(reader.GetString(1)),
        reader.GetBoolean(3),
        reader.GetFloat(4),
        JsonSerializer.Deserialize<string[]>(reader.GetString(5), _json) ?? [],
        family == "*" ? null : Enum.Parse<DeviceFamily>(family)));
    }
    return results;
  }

  public Task SaveGroupMappingAsync(GroupMapping mapping, CancellationToken cancellationToken = default) =>
    ExecuteWriteAsync("""
      INSERT INTO group_mappings_v2(pack_id,input_group,variant,device_family,enabled,volume,sample_ids_json)
      VALUES($pack,$group,$variant,$family,$enabled,$volume,$samples)
      ON CONFLICT(pack_id,input_group,variant,device_family) DO UPDATE SET
        enabled=excluded.enabled, volume=excluded.volume, sample_ids_json=excluded.sample_ids_json;
      """, command =>
    {
      command.Parameters.AddWithValue("$pack", mapping.PackId);
      command.Parameters.AddWithValue("$group", mapping.Group.ToString());
      command.Parameters.AddWithValue("$variant", mapping.Variant.ToString());
      command.Parameters.AddWithValue("$family", mapping.DeviceFamily?.ToString() ?? "*");
      command.Parameters.AddWithValue("$enabled", mapping.Enabled);
      command.Parameters.AddWithValue("$volume", mapping.Volume);
      command.Parameters.AddWithValue("$samples", JsonSerializer.Serialize(mapping.SampleIds, _json));
    }, cancellationToken);

  public Task RemoveGroupMappingAsync(string packId, InputGroup group, KeyVariant variant, DeviceFamily? deviceFamily, CancellationToken cancellationToken = default) =>
    ExecuteWriteAsync("DELETE FROM group_mappings_v2 WHERE pack_id=$pack AND input_group=$group AND variant=$variant AND device_family=$family;", command =>
    {
      command.Parameters.AddWithValue("$pack", packId);
      command.Parameters.AddWithValue("$group", group.ToString());
      command.Parameters.AddWithValue("$variant", variant.ToString());
      command.Parameters.AddWithValue("$family", deviceFamily?.ToString() ?? "*");
    }, cancellationToken);

  public async Task<IReadOnlyList<ShortcutBinding>> LoadShortcutsAsync(CancellationToken cancellationToken = default)
  {
    var command = RequireConnection().CreateCommand();
    command.CommandText = "SELECT binding_json FROM shortcuts ORDER BY command_id;";
    var results = new List<ShortcutBinding>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
      var binding = JsonSerializer.Deserialize<ShortcutBinding>(reader.GetString(0), _json);
      if (binding is not null) results.Add(binding);
    }
    return results;
  }

  public Task SaveShortcutAsync(ShortcutBinding binding, CancellationToken cancellationToken = default) =>
    ExecuteWriteAsync("""
      INSERT INTO shortcuts(command_id,binding_json,updated_utc) VALUES($id,$json,$updated)
      ON CONFLICT(command_id) DO UPDATE SET binding_json=excluded.binding_json, updated_utc=excluded.updated_utc;
      """, command =>
    {
      command.Parameters.AddWithValue("$id", binding.CommandId);
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(binding, _json));
      command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
    }, cancellationToken);

  public Task CheckpointAsync(CancellationToken cancellationToken = default) =>
    ExecuteWriteAsync("PRAGMA wal_checkpoint(FULL);", _ => { }, cancellationToken);

  public async Task<string> GetStatisticsSourceIdAsync(CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var select = RequireConnection().CreateCommand();
      select.CommandText = "SELECT source_id FROM statistics_sources ORDER BY created_utc LIMIT 1;";
      if (await select.ExecuteScalarAsync(cancellationToken) is string existing) return existing;
      var sourceId = Guid.NewGuid().ToString("N");
      var insert = RequireConnection().CreateCommand();
      insert.CommandText = "INSERT INTO statistics_sources(source_id,created_utc,platform) VALUES($id,$created,'windows');";
      insert.Parameters.AddWithValue("$id", sourceId);
      insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
      await insert.ExecuteNonQueryAsync(cancellationToken);
      return sourceId;
    }
    finally { _writeGate.Release(); }
  }

  public async Task MergeStatisticsAsync(IReadOnlyCollection<StatisticsAggregateDelta> deltas, CancellationToken cancellationToken = default)
  {
    if (deltas.Count == 0) return;
    var sourceId = await GetStatisticsSourceIdAsync(cancellationToken);
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      await using var transaction = await RequireConnection().BeginTransactionAsync(cancellationToken);
      foreach (var delta in deltas)
      {
        var bucket = delta.Key.BucketUtc.ToUniversalTime().ToString("O");
        var input = RequireConnection().CreateCommand();
        input.Transaction = (SqliteTransaction)transaction;
        input.CommandText = """
          INSERT INTO statistics_input_hourly(source_id,bucket_utc,input_kind,device_family,physical_code,extended,input_group,count,revision)
          VALUES($source,$bucket,$kind,$family,$code,$extended,$group,$count,$revision)
          ON CONFLICT(source_id,bucket_utc,input_kind,device_family,physical_code,extended,input_group) DO UPDATE SET
            count=count+excluded.count, revision=MAX(revision,excluded.revision);
          """;
        input.Parameters.AddWithValue("$source", sourceId);
        input.Parameters.AddWithValue("$bucket", bucket);
        input.Parameters.AddWithValue("$kind", delta.Key.Kind.ToString());
        input.Parameters.AddWithValue("$family", delta.Key.DeviceFamily.ToString());
        input.Parameters.AddWithValue("$code", delta.Key.PhysicalCode);
        input.Parameters.AddWithValue("$extended", delta.Key.Extended);
        input.Parameters.AddWithValue("$group", delta.Key.Group.ToString());
        input.Parameters.AddWithValue("$count", delta.Count);
        input.Parameters.AddWithValue("$revision", delta.Revision);
        await input.ExecuteNonQueryAsync(cancellationToken);

        var keyboard = delta.Key.Kind == InputKind.KeyboardKey ? delta.Count : 0;
        var typing = delta.Key.Kind == InputKind.KeyboardKey && IsTypingGroup(delta.Key.Group) ? delta.Count : 0;
        var pointer = delta.Key.Kind == InputKind.PointerButton ? delta.Count : 0;
        var vertical = delta.Key.Kind == InputKind.Wheel && delta.Key.PhysicalCode is 6 or 7 ? delta.Count : 0;
        var horizontal = delta.Key.Kind == InputKind.Wheel && delta.Key.PhysicalCode is 8 or 9 ? delta.Count : 0;
        var summary = RequireConnection().CreateCommand();
        summary.Transaction = (SqliteTransaction)transaction;
        summary.CommandText = """
          INSERT INTO statistics_hourly_summaries(source_id,bucket_utc,keyboard_presses,typing_key_presses,pointer_clicks,vertical_scroll,horizontal_scroll,active_ms,keyboard_active_ms,pointer_active_ms,peak_typing_keys_60s,peak_clicks_5s,revision)
          VALUES($source,$bucket,$keyboard,$typing,$pointer,$vertical,$horizontal,$active,$keyboard_active,$pointer_active,$peak_typing,$peak_clicks,$revision)
          ON CONFLICT(source_id,bucket_utc) DO UPDATE SET
            keyboard_presses=keyboard_presses+excluded.keyboard_presses,
            typing_key_presses=typing_key_presses+excluded.typing_key_presses,
            pointer_clicks=pointer_clicks+excluded.pointer_clicks,
            vertical_scroll=vertical_scroll+excluded.vertical_scroll,
            horizontal_scroll=horizontal_scroll+excluded.horizontal_scroll,
            active_ms=active_ms+excluded.active_ms,
            keyboard_active_ms=keyboard_active_ms+excluded.keyboard_active_ms,
            pointer_active_ms=pointer_active_ms+excluded.pointer_active_ms,
            peak_typing_keys_60s=MAX(peak_typing_keys_60s,excluded.peak_typing_keys_60s),
            peak_clicks_5s=MAX(peak_clicks_5s,excluded.peak_clicks_5s),
            revision=MAX(revision,excluded.revision);
          """;
        summary.Parameters.AddWithValue("$source", sourceId);
        summary.Parameters.AddWithValue("$bucket", bucket);
        summary.Parameters.AddWithValue("$keyboard", keyboard);
        summary.Parameters.AddWithValue("$typing", typing);
        summary.Parameters.AddWithValue("$pointer", pointer);
        summary.Parameters.AddWithValue("$vertical", vertical);
        summary.Parameters.AddWithValue("$horizontal", horizontal);
        summary.Parameters.AddWithValue("$active", delta.ActiveMilliseconds);
        summary.Parameters.AddWithValue("$keyboard_active", delta.KeyboardActiveMilliseconds);
        summary.Parameters.AddWithValue("$pointer_active", delta.PointerActiveMilliseconds);
        summary.Parameters.AddWithValue("$peak_typing", delta.PeakTypingKeysPerMinute);
        summary.Parameters.AddWithValue("$peak_clicks", delta.PeakClicksPerFiveSeconds);
        summary.Parameters.AddWithValue("$revision", delta.Revision);
        await summary.ExecuteNonQueryAsync(cancellationToken);
      }
      await transaction.CommitAsync(cancellationToken);
    }
    finally { _writeGate.Release(); }
  }

  public async Task<StatisticsSnapshot> QueryStatisticsAsync(StatisticsQuery query, CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var current = await QueryStatisticsCoreAsync(query with { Comparison = StatisticsComparison.None }, cancellationToken);
      if (query.Comparison == StatisticsComparison.None) return current;
      var comparisonRange = ComparisonRange(query);
      var comparison = await QueryStatisticsCoreAsync(comparisonRange, cancellationToken);
      return current with { Query = query, Comparison = comparison };
    }
    finally { _writeGate.Release(); }
  }

  public async Task DeleteStatisticsAsync(StatisticsDeleteRequest request, CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      await using var transaction = await RequireConnection().BeginTransactionAsync(cancellationToken);
      var range = RangeClause(request.StartUtc, request.EndUtc);
      foreach (var category in request.Categories)
      {
        var delete = RequireConnection().CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = $"DELETE FROM statistics_input_hourly WHERE {range.Sql} AND input_kind=$kind;";
        AddRangeParameters(delete, request.StartUtc, request.EndUtc);
        delete.Parameters.AddWithValue("$kind", category switch
        {
          StatisticsCategory.Keyboard => InputKind.KeyboardKey.ToString(),
          StatisticsCategory.Pointer => InputKind.PointerButton.ToString(),
          _ => InputKind.Wheel.ToString()
        });
        await delete.ExecuteNonQueryAsync(cancellationToken);
      }

      var reset = new List<string>();
      if (request.Categories.Contains(StatisticsCategory.Keyboard))
        reset.AddRange(["keyboard_presses=0", "typing_key_presses=0", "keyboard_active_ms=0", "peak_typing_keys_60s=0"]);
      if (request.Categories.Contains(StatisticsCategory.Pointer))
        reset.AddRange(["pointer_clicks=0", "pointer_active_ms=0", "peak_clicks_5s=0"]);
      if (request.Categories.Contains(StatisticsCategory.Scrolling))
        reset.AddRange(["vertical_scroll=0", "horizontal_scroll=0"]);
      if (request.Categories.Count == 3) reset.Add("active_ms=0");
      if (reset.Count > 0)
      {
        var update = RequireConnection().CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = $"UPDATE statistics_hourly_summaries SET {string.Join(',', reset)} WHERE {range.Sql};";
        AddRangeParameters(update, request.StartUtc, request.EndUtc);
        await update.ExecuteNonQueryAsync(cancellationToken);
      }
      if (request.DeleteWellnessAchievements)
      {
        var wellness = RequireConnection().CreateCommand();
        wellness.Transaction = (SqliteTransaction)transaction;
        wellness.CommandText = request.StartUtc is null && request.EndUtc is null
          ? "DELETE FROM wellness_achievements;"
          : "DELETE FROM wellness_achievements WHERE achieved_utc >= COALESCE($start, achieved_utc) AND achieved_utc < COALESCE($end, '9999-12-31T23:59:59Z');";
        wellness.Parameters.AddWithValue("$start", (object?)request.StartUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        wellness.Parameters.AddWithValue("$end", (object?)request.EndUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        await wellness.ExecuteNonQueryAsync(cancellationToken);
      }
      await transaction.CommitAsync(cancellationToken);
    }
    finally { _writeGate.Release(); }
  }

  public async Task<IReadOnlyList<WellnessAchievement>> LoadWellnessAchievementsAsync(CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var command = RequireConnection().CreateCommand();
      command.CommandText = "SELECT id,goal_kind,local_date,target_snapshot,actual_value,achieved_utc FROM wellness_achievements ORDER BY local_date;";
      var results = new List<WellnessAchievement>();
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
        results.Add(new(reader.GetString(0), reader.GetString(1), DateOnly.Parse(reader.GetString(2)), reader.GetInt64(3), reader.GetInt64(4), DateTimeOffset.Parse(reader.GetString(5))));
      return results;
    }
    finally { _writeGate.Release(); }
  }

  public Task SaveWellnessAchievementAsync(WellnessAchievement achievement, CancellationToken cancellationToken = default) => ExecuteWriteAsync("""
    INSERT OR IGNORE INTO wellness_achievements(id,goal_kind,local_date,target_snapshot,actual_value,achieved_utc)
    VALUES($id,$kind,$date,$target,$actual,$achieved);
    """, command =>
  {
    command.Parameters.AddWithValue("$id", achievement.Id);
    command.Parameters.AddWithValue("$kind", achievement.GoalKind);
    command.Parameters.AddWithValue("$date", achievement.LocalDate.ToString("O"));
    command.Parameters.AddWithValue("$target", achievement.TargetSnapshot);
    command.Parameters.AddWithValue("$actual", achievement.ActualValue);
    command.Parameters.AddWithValue("$achieved", achievement.AchievedUtc.ToUniversalTime().ToString("O"));
  }, cancellationToken);

  public async Task<StatisticsTransferBundle> ExportStatisticsAsync(bool includeWellness, CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var sources = new List<string>();
      var sourceCommand = RequireConnection().CreateCommand();
      sourceCommand.CommandText = "SELECT source_id FROM statistics_sources ORDER BY source_id;";
      await using (var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken))
        while (await reader.ReadAsync(cancellationToken)) sources.Add(reader.GetString(0));

      var inputs = new List<StatisticsTransferInput>();
      var inputCommand = RequireConnection().CreateCommand();
      inputCommand.CommandText = "SELECT source_id,bucket_utc,input_kind,device_family,physical_code,extended,input_group,count,revision FROM statistics_input_hourly ORDER BY source_id,bucket_utc;";
      await using (var reader = await inputCommand.ExecuteReaderAsync(cancellationToken))
        while (await reader.ReadAsync(cancellationToken)) inputs.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)), Enum.Parse<InputKind>(reader.GetString(2)), Enum.Parse<DeviceFamily>(reader.GetString(3)), reader.GetInt32(4), reader.GetBoolean(5), Enum.Parse<InputGroup>(reader.GetString(6)), reader.GetInt64(7), reader.GetInt64(8)));

      var summaries = new List<StatisticsTransferSummary>();
      var summaryCommand = RequireConnection().CreateCommand();
      summaryCommand.CommandText = "SELECT source_id,bucket_utc,keyboard_presses,typing_key_presses,pointer_clicks,vertical_scroll,horizontal_scroll,active_ms,keyboard_active_ms,pointer_active_ms,peak_typing_keys_60s,peak_clicks_5s,revision FROM statistics_hourly_summaries ORDER BY source_id,bucket_utc;";
      await using (var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken))
        while (await reader.ReadAsync(cancellationToken)) summaries.Add(new(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt32(10), reader.GetInt32(11), reader.GetInt64(12)));

      var achievements = includeWellness ? await LoadAchievementsWithoutLockAsync(cancellationToken) : [];
      return new(sources, inputs, summaries, achievements);
    }
    finally { _writeGate.Release(); }
  }

  public async Task ImportStatisticsAsync(StatisticsTransferBundle bundle, bool includeWellness, CancellationToken cancellationToken = default)
  {
    if (bundle.Inputs.Count > 5_000_000 || bundle.Summaries.Count > 1_000_000) throw new InvalidDataException("The profile contains too many statistics buckets.");
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      await using var transaction = await RequireConnection().BeginTransactionAsync(cancellationToken);
      foreach (var sourceId in bundle.SourceIds.Distinct(StringComparer.Ordinal))
      {
        if (sourceId.Length is < 16 or > 128) throw new InvalidDataException("The profile has an invalid statistics source ID.");
        var source = RequireConnection().CreateCommand();
        source.Transaction = (SqliteTransaction)transaction;
        source.CommandText = "INSERT OR IGNORE INTO statistics_sources(source_id,created_utc,platform) VALUES($id,$created,'profile');";
        source.Parameters.AddWithValue("$id", sourceId);
        source.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await source.ExecuteNonQueryAsync(cancellationToken);
      }
      foreach (var row in bundle.Inputs)
      {
        var command = RequireConnection().CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
          INSERT INTO statistics_input_hourly(source_id,bucket_utc,input_kind,device_family,physical_code,extended,input_group,count,revision)
          VALUES($source,$bucket,$kind,$family,$code,$extended,$group,$count,$revision)
          ON CONFLICT(source_id,bucket_utc,input_kind,device_family,physical_code,extended,input_group) DO UPDATE SET
            count=excluded.count,revision=excluded.revision WHERE excluded.revision > revision;
          """;
        command.Parameters.AddWithValue("$source", row.SourceId);
        command.Parameters.AddWithValue("$bucket", row.BucketUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$kind", row.Kind.ToString());
        command.Parameters.AddWithValue("$family", row.DeviceFamily.ToString());
        command.Parameters.AddWithValue("$code", row.PhysicalCode);
        command.Parameters.AddWithValue("$extended", row.Extended);
        command.Parameters.AddWithValue("$group", row.Group.ToString());
        command.Parameters.AddWithValue("$count", Math.Max(0, row.Count));
        command.Parameters.AddWithValue("$revision", row.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
      }
      foreach (var row in bundle.Summaries)
      {
        var command = RequireConnection().CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
          INSERT INTO statistics_hourly_summaries(source_id,bucket_utc,keyboard_presses,typing_key_presses,pointer_clicks,vertical_scroll,horizontal_scroll,active_ms,keyboard_active_ms,pointer_active_ms,peak_typing_keys_60s,peak_clicks_5s,revision)
          VALUES($source,$bucket,$keyboard,$typing,$pointer,$vertical,$horizontal,$active,$keyboard_active,$pointer_active,$peak_typing,$peak_clicks,$revision)
          ON CONFLICT(source_id,bucket_utc) DO UPDATE SET
            keyboard_presses=excluded.keyboard_presses,typing_key_presses=excluded.typing_key_presses,pointer_clicks=excluded.pointer_clicks,
            vertical_scroll=excluded.vertical_scroll,horizontal_scroll=excluded.horizontal_scroll,active_ms=excluded.active_ms,
            keyboard_active_ms=excluded.keyboard_active_ms,pointer_active_ms=excluded.pointer_active_ms,
            peak_typing_keys_60s=excluded.peak_typing_keys_60s,peak_clicks_5s=excluded.peak_clicks_5s,revision=excluded.revision
            WHERE excluded.revision > revision;
          """;
        command.Parameters.AddWithValue("$source", row.SourceId);
        command.Parameters.AddWithValue("$bucket", row.BucketUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$keyboard", Math.Max(0, row.KeyboardPresses));
        command.Parameters.AddWithValue("$typing", Math.Max(0, row.TypingKeyPresses));
        command.Parameters.AddWithValue("$pointer", Math.Max(0, row.PointerClicks));
        command.Parameters.AddWithValue("$vertical", Math.Max(0, row.VerticalScroll));
        command.Parameters.AddWithValue("$horizontal", Math.Max(0, row.HorizontalScroll));
        command.Parameters.AddWithValue("$active", Math.Max(0, row.ActiveMilliseconds));
        command.Parameters.AddWithValue("$keyboard_active", Math.Max(0, row.KeyboardActiveMilliseconds));
        command.Parameters.AddWithValue("$pointer_active", Math.Max(0, row.PointerActiveMilliseconds));
        command.Parameters.AddWithValue("$peak_typing", Math.Max(0, row.PeakTypingKeysPerMinute));
        command.Parameters.AddWithValue("$peak_clicks", Math.Max(0, row.PeakClicksPerFiveSeconds));
        command.Parameters.AddWithValue("$revision", row.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
      }
      if (includeWellness)
      {
        foreach (var achievement in bundle.Achievements)
        {
          var command = RequireConnection().CreateCommand();
          command.Transaction = (SqliteTransaction)transaction;
          command.CommandText = "INSERT OR IGNORE INTO wellness_achievements(id,goal_kind,local_date,target_snapshot,actual_value,achieved_utc) VALUES($id,$kind,$date,$target,$actual,$achieved);";
          command.Parameters.AddWithValue("$id", achievement.Id);
          command.Parameters.AddWithValue("$kind", achievement.GoalKind);
          command.Parameters.AddWithValue("$date", achievement.LocalDate.ToString("O"));
          command.Parameters.AddWithValue("$target", achievement.TargetSnapshot);
          command.Parameters.AddWithValue("$actual", achievement.ActualValue);
          command.Parameters.AddWithValue("$achieved", achievement.AchievedUtc.ToUniversalTime().ToString("O"));
          await command.ExecuteNonQueryAsync(cancellationToken);
        }
      }
      await transaction.CommitAsync(cancellationToken);
    }
    finally { _writeGate.Release(); }
  }

  public async ValueTask DisposeAsync()
  {
    if (_connection is not null)
    {
      await _connection.CloseAsync();
      await _connection.DisposeAsync();
    }
    _writeGate.Dispose();
  }

  private SqliteConnection RequireConnection() => _connection ?? throw new InvalidOperationException("The store has not been initialized.");

  private async Task<StatisticsSnapshot> QueryStatisticsCoreAsync(StatisticsQuery query, CancellationToken cancellationToken)
  {
    var start = query.StartUtc.ToUniversalTime().ToString("O");
    var end = query.EndUtc.ToUniversalTime().ToString("O");
    var trend = new List<StatisticsTrendPoint>();
    var summary = RequireConnection().CreateCommand();
    summary.CommandText = """
      SELECT bucket_utc,SUM(keyboard_presses),SUM(typing_key_presses),SUM(pointer_clicks),SUM(vertical_scroll),SUM(horizontal_scroll),
             SUM(active_ms),SUM(keyboard_active_ms),SUM(pointer_active_ms),MAX(peak_typing_keys_60s),MAX(peak_clicks_5s)
      FROM statistics_hourly_summaries WHERE bucket_utc >= $start AND bucket_utc < $end GROUP BY bucket_utc ORDER BY bucket_utc;
      """;
    summary.Parameters.AddWithValue("$start", start);
    summary.Parameters.AddWithValue("$end", end);
    long keyboard = 0, typing = 0, pointer = 0, vertical = 0, horizontal = 0, active = 0, keyboardActive = 0, pointerActive = 0;
    var peakTyping = 0;
    var peakClicks = 0;
    var busiestHour = 0;
    long busiestCount = -1;
    await using (var reader = await summary.ExecuteReaderAsync(cancellationToken))
    {
      while (await reader.ReadAsync(cancellationToken))
      {
        var bucket = DateTimeOffset.Parse(reader.GetString(0));
        var bucketKeyboard = reader.GetInt64(1);
        var bucketPointer = reader.GetInt64(3);
        keyboard += bucketKeyboard;
        typing += reader.GetInt64(2);
        pointer += bucketPointer;
        vertical += reader.GetInt64(4);
        horizontal += reader.GetInt64(5);
        active += reader.GetInt64(6);
        keyboardActive += reader.GetInt64(7);
        pointerActive += reader.GetInt64(8);
        peakTyping = Math.Max(peakTyping, reader.GetInt32(9));
        peakClicks = Math.Max(peakClicks, reader.GetInt32(10));
        var bucketCount = bucketKeyboard + bucketPointer;
        if (bucketCount > busiestCount)
        {
          busiestCount = bucketCount;
          busiestHour = bucket.ToLocalTime().Hour;
        }
        trend.Add(new(bucket, bucketKeyboard, bucketPointer, reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
      }
    }

    var breakdown = new List<StatisticsBreakdown>();
    var details = RequireConnection().CreateCommand();
    details.CommandText = """
      SELECT input_kind,device_family,physical_code,extended,input_group,SUM(count)
      FROM statistics_input_hourly WHERE bucket_utc >= $start AND bucket_utc < $end
      GROUP BY input_kind,device_family,physical_code,extended,input_group ORDER BY SUM(count) DESC;
      """;
    details.Parameters.AddWithValue("$start", start);
    details.Parameters.AddWithValue("$end", end);
    await using (var reader = await details.ExecuteReaderAsync(cancellationToken))
      while (await reader.ReadAsync(cancellationToken))
        breakdown.Add(new(Enum.Parse<InputKind>(reader.GetString(0)), Enum.Parse<DeviceFamily>(reader.GetString(1)), reader.GetInt32(2), reader.GetBoolean(3), Enum.Parse<InputGroup>(reader.GetString(4)), reader.GetInt64(5)));

    return new(query, keyboard, typing, pointer, vertical, horizontal, active, keyboardActive, pointerActive, peakTyping, peakClicks, busiestHour, trend, breakdown);
  }

  private async Task<IReadOnlyList<WellnessAchievement>> LoadAchievementsWithoutLockAsync(CancellationToken cancellationToken)
  {
    var command = RequireConnection().CreateCommand();
    command.CommandText = "SELECT id,goal_kind,local_date,target_snapshot,actual_value,achieved_utc FROM wellness_achievements ORDER BY local_date;";
    var results = new List<WellnessAchievement>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
      results.Add(new(reader.GetString(0), reader.GetString(1), DateOnly.Parse(reader.GetString(2)), reader.GetInt64(3), reader.GetInt64(4), DateTimeOffset.Parse(reader.GetString(5))));
    return results;
  }

  private static StatisticsQuery ComparisonRange(StatisticsQuery query)
  {
    if (query.Comparison == StatisticsComparison.PreviousYear)
      return new(query.StartUtc.AddYears(-1), query.EndUtc.AddYears(-1));
    var duration = query.EndUtc - query.StartUtc;
    return new(query.StartUtc - duration, query.StartUtc);
  }

  private static bool IsTypingGroup(InputGroup group) => group is InputGroup.Letters or InputGroup.Numbers or InputGroup.Punctuation or InputGroup.Space or InputGroup.Enter or InputGroup.Editing;

  private static (string Sql, bool HasStart, bool HasEnd) RangeClause(DateTimeOffset? start, DateTimeOffset? end) =>
    (start, end) switch
    {
      (not null, not null) => ("bucket_utc >= $start AND bucket_utc < $end", true, true),
      (not null, null) => ("bucket_utc >= $start", true, false),
      (null, not null) => ("bucket_utc < $end", false, true),
      _ => ("1=1", false, false)
    };

  private static void AddRangeParameters(SqliteCommand command, DateTimeOffset? start, DateTimeOffset? end)
  {
    if (start is not null) command.Parameters.AddWithValue("$start", start.Value.ToUniversalTime().ToString("O"));
    if (end is not null) command.Parameters.AddWithValue("$end", end.Value.ToUniversalTime().ToString("O"));
  }

  private async Task ExecuteWriteAsync(string sql, Action<SqliteCommand> configure, CancellationToken cancellationToken)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var command = RequireConnection().CreateCommand();
      command.CommandText = sql;
      configure(command);
      await command.ExecuteNonQueryAsync(cancellationToken);
    }
    finally
    {
      _writeGate.Release();
    }
  }
}
