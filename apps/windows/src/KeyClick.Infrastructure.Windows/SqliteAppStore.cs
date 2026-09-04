using System.Text.Json;
using System.Text.Json.Serialization;
using KeyClick.Core;
using Microsoft.Data.Sqlite;

namespace KeyClick.Infrastructure.Windows;

public sealed class SqliteAppStore(AppPaths paths) : IAppStore, IStatisticsStore, ITypingChallengeStore
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
      CREATE TABLE IF NOT EXISTS statistics_application_hourly (
        source_id TEXT NOT NULL,
        bucket_utc TEXT NOT NULL,
        application_id TEXT NOT NULL,
        display_name TEXT NOT NULL,
        keyboard_presses INTEGER NOT NULL DEFAULT 0,
        pointer_clicks INTEGER NOT NULL DEFAULT 0,
        vertical_scroll INTEGER NOT NULL DEFAULT 0,
        horizontal_scroll INTEGER NOT NULL DEFAULT 0,
        revision INTEGER NOT NULL,
        PRIMARY KEY (source_id, bucket_utc, application_id),
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
      CREATE TABLE IF NOT EXISTS typing_challenge_results (
        id TEXT PRIMARY KEY,
        source_id TEXT NOT NULL,
        completed_utc TEXT NOT NULL,
        source TEXT NOT NULL,
        prompt_id TEXT NULL,
        prompt_title TEXT NOT NULL,
        language TEXT NOT NULL,
        difficulty TEXT NOT NULL,
        run_mode TEXT NOT NULL,
        mistake_mode TEXT NOT NULL,
        duration_limit_seconds INTEGER NULL,
        active_ms INTEGER NOT NULL,
        character_attempts INTEGER NOT NULL,
        correct_characters INTEGER NOT NULL,
        error_attempts INTEGER NOT NULL,
        corrections INTEGER NOT NULL,
        retained_characters INTEGER NOT NULL,
        words INTEGER NOT NULL,
        gross_wpm REAL NOT NULL,
        net_wpm REAL NOT NULL,
        accuracy_percent REAL NOT NULL,
        consistency_percent REAL NOT NULL,
        reference_completed INTEGER NOT NULL,
        valid_for_streak INTEGER NOT NULL,
        goal_wpm_snapshot REAL NOT NULL,
        goal_accuracy_snapshot REAL NOT NULL,
        revision INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS typing_challenge_samples (
        result_id TEXT NOT NULL,
        interval_index INTEGER NOT NULL,
        character_attempts INTEGER NOT NULL,
        correct_characters INTEGER NOT NULL,
        errors INTEGER NOT NULL,
        net_wpm REAL NOT NULL,
        PRIMARY KEY (result_id, interval_index),
        FOREIGN KEY (result_id) REFERENCES typing_challenge_results(id) ON DELETE CASCADE
      );
      CREATE TABLE IF NOT EXISTS typing_challenge_prompts (
        id TEXT PRIMARY KEY,
        title TEXT NOT NULL,
        prompt_text TEXT NOT NULL,
        language TEXT NOT NULL,
        difficulty TEXT NOT NULL,
        favorite INTEGER NOT NULL,
        created_utc TEXT NOT NULL,
        updated_utc TEXT NOT NULL,
        revision INTEGER NOT NULL
      );
      CREATE TABLE IF NOT EXISTS typing_challenge_achievements (
        id TEXT PRIMARY KEY,
        kind TEXT NOT NULL,
        local_date TEXT NOT NULL,
        result_id TEXT NOT NULL,
        goal_wpm_snapshot REAL NOT NULL,
        goal_accuracy_snapshot REAL NOT NULL,
        achieved_utc TEXT NOT NULL,
        FOREIGN KEY (result_id) REFERENCES typing_challenge_results(id) ON DELETE CASCADE
      );
      CREATE INDEX IF NOT EXISTS ix_statistics_input_range ON statistics_input_hourly(bucket_utc, input_kind);
      CREATE INDEX IF NOT EXISTS ix_statistics_summary_range ON statistics_hourly_summaries(bucket_utc);
      CREATE INDEX IF NOT EXISTS ix_statistics_application_range ON statistics_application_hourly(bucket_utc, application_id);
      CREATE INDEX IF NOT EXISTS ix_typing_challenge_result_range ON typing_challenge_results(completed_utc, source, run_mode);
      CREATE INDEX IF NOT EXISTS ix_typing_challenge_achievement_date ON typing_challenge_achievements(local_date, kind);
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(3, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(4, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(5, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      """;
    await migration.ExecuteNonQueryAsync(cancellationToken);

    var privacyMigration = _connection.CreateCommand();
    privacyMigration.CommandText = """
      UPDATE typing_challenge_results
      SET prompt_id=NULL
      WHERE source='Custom' AND prompt_id IS NOT NULL
        AND NOT EXISTS(SELECT 1 FROM schema_migrations WHERE version=6)
        AND NOT EXISTS(SELECT 1 FROM typing_challenge_prompts WHERE id=typing_challenge_results.prompt_id);
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(6, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      """;
    await privacyMigration.ExecuteNonQueryAsync(cancellationToken);

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
    settings.NormalizeFunStats();
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
      select.CommandText = "SELECT source_id FROM statistics_sources WHERE platform='windows' ORDER BY created_utc LIMIT 1;";
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

  public async Task MergeApplicationStatisticsAsync(IReadOnlyCollection<ApplicationStatisticsAggregateDelta> deltas, CancellationToken cancellationToken = default)
  {
    if (deltas.Count == 0) return;
    var sourceId = await GetStatisticsSourceIdAsync(cancellationToken);
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      await using var transaction = await RequireConnection().BeginTransactionAsync(cancellationToken);
      foreach (var delta in deltas)
      {
        var command = RequireConnection().CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
          INSERT INTO statistics_application_hourly(source_id,bucket_utc,application_id,display_name,keyboard_presses,pointer_clicks,vertical_scroll,horizontal_scroll,revision)
          VALUES($source,$bucket,$application,$name,$keyboard,$pointer,$vertical,$horizontal,$revision)
          ON CONFLICT(source_id,bucket_utc,application_id) DO UPDATE SET
            display_name=excluded.display_name,
            keyboard_presses=keyboard_presses+excluded.keyboard_presses,
            pointer_clicks=pointer_clicks+excluded.pointer_clicks,
            vertical_scroll=vertical_scroll+excluded.vertical_scroll,
            horizontal_scroll=horizontal_scroll+excluded.horizontal_scroll,
            revision=MAX(revision,excluded.revision);
          """;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$bucket", delta.Key.BucketUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$application", delta.Key.ApplicationId);
        command.Parameters.AddWithValue("$name", delta.Key.DisplayName);
        command.Parameters.AddWithValue("$keyboard", delta.KeyboardPresses);
        command.Parameters.AddWithValue("$pointer", delta.PointerClicks);
        command.Parameters.AddWithValue("$vertical", delta.VerticalScroll);
        command.Parameters.AddWithValue("$horizontal", delta.HorizontalScroll);
        command.Parameters.AddWithValue("$revision", delta.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

  public async Task<IReadOnlyList<ApplicationStatisticsRow>> QueryApplicationStatisticsAsync(StatisticsQuery query, CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var command = RequireConnection().CreateCommand();
      command.CommandText = """
        SELECT application_id,MAX(display_name),SUM(keyboard_presses),SUM(pointer_clicks),SUM(vertical_scroll),SUM(horizontal_scroll)
        FROM statistics_application_hourly
        WHERE bucket_utc >= $start AND bucket_utc < $end
        GROUP BY application_id
        ORDER BY SUM(keyboard_presses+pointer_clicks+vertical_scroll+horizontal_scroll) DESC, MAX(display_name);
        """;
      command.Parameters.AddWithValue("$start", FloorToHour(query.StartUtc).ToUniversalTime().ToString("O"));
      command.Parameters.AddWithValue("$end", query.EndUtc.ToUniversalTime().ToString("O"));
      var rows = new List<ApplicationStatisticsRow>();
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken))
        rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
      return rows;
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
      var applicationReset = new List<string>();
      if (request.Categories.Contains(StatisticsCategory.Keyboard)) applicationReset.Add("keyboard_presses=0");
      if (request.Categories.Contains(StatisticsCategory.Pointer)) applicationReset.Add("pointer_clicks=0");
      if (request.Categories.Contains(StatisticsCategory.Scrolling)) applicationReset.AddRange(["vertical_scroll=0", "horizontal_scroll=0"]);
      if (applicationReset.Count > 0)
      {
        var updateApplications = RequireConnection().CreateCommand();
        updateApplications.Transaction = (SqliteTransaction)transaction;
        updateApplications.CommandText = $"UPDATE statistics_application_hourly SET {string.Join(',', applicationReset)} WHERE {range.Sql};";
        AddRangeParameters(updateApplications, request.StartUtc, request.EndUtc);
        await updateApplications.ExecuteNonQueryAsync(cancellationToken);

        var removeEmptyApplications = RequireConnection().CreateCommand();
        removeEmptyApplications.Transaction = (SqliteTransaction)transaction;
        removeEmptyApplications.CommandText = "DELETE FROM statistics_application_hourly WHERE keyboard_presses=0 AND pointer_clicks=0 AND vertical_scroll=0 AND horizontal_scroll=0;";
        await removeEmptyApplications.ExecuteNonQueryAsync(cancellationToken);
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

  public async Task SaveTypingChallengeResultAsync(TypingChallengeResult result, CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      await using var transaction = await RequireConnection().BeginTransactionAsync(cancellationToken);
      await SaveTypingChallengeResultWithoutLockAsync(result, (SqliteTransaction)transaction, cancellationToken);
      await transaction.CommitAsync(cancellationToken);
    }
    finally { _writeGate.Release(); }
  }

  public async Task<IReadOnlyList<TypingChallengeResult>> QueryTypingChallengeResultsAsync(TypingChallengeQuery query, CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var command = RequireConnection().CreateCommand();
      command.CommandText = $"SELECT {TypingChallengeResultColumns} FROM typing_challenge_results WHERE completed_utc >= $start AND completed_utc < $end"
        + (query.Source is null ? string.Empty : " AND source=$source")
        + (query.RunMode is null ? string.Empty : " AND run_mode=$mode")
        + " ORDER BY completed_utc DESC;";
      command.Parameters.AddWithValue("$start", query.StartUtc.ToUniversalTime().ToString("O"));
      command.Parameters.AddWithValue("$end", query.EndUtc.ToUniversalTime().ToString("O"));
      if (query.Source is not null) command.Parameters.AddWithValue("$source", query.Source.Value.ToString());
      if (query.RunMode is not null) command.Parameters.AddWithValue("$mode", query.RunMode.Value.ToString());
      var rows = new List<TypingChallengeResult>();
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadTypingChallengeResult(reader, []));
      await reader.DisposeAsync();
      for (var index = 0; index < rows.Count; index++)
        rows[index] = rows[index] with { Samples = await LoadTypingChallengeSamplesWithoutLockAsync(rows[index].Id, cancellationToken) };
      return rows;
    }
    finally { _writeGate.Release(); }
  }

  public async Task DeleteTypingChallengeResultsAsync(TypingChallengeDeleteRequest request, CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      await using var transaction = await RequireConnection().BeginTransactionAsync(cancellationToken);
      if (request.DeleteResults && request.ResultIds.Count > 0)
      {
        foreach (var id in request.ResultIds)
        {
          var selected = RequireConnection().CreateCommand();
          selected.Transaction = (SqliteTransaction)transaction;
          selected.CommandText = "DELETE FROM typing_challenge_results WHERE id=$id;";
          selected.Parameters.AddWithValue("$id", id);
          await selected.ExecuteNonQueryAsync(cancellationToken);
        }
      }
      else if (request.DeleteResults && (request.StartUtc is not null || request.EndUtc is not null))
      {
        var range = RequireConnection().CreateCommand();
        range.Transaction = (SqliteTransaction)transaction;
        range.CommandText = "DELETE FROM typing_challenge_results WHERE completed_utc >= COALESCE($start, completed_utc) AND completed_utc < COALESCE($end, '9999-12-31T23:59:59Z');";
        range.Parameters.AddWithValue("$start", (object?)request.StartUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        range.Parameters.AddWithValue("$end", (object?)request.EndUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        await range.ExecuteNonQueryAsync(cancellationToken);
      }
      if (request.DeleteAchievements)
      {
        var achievement = RequireConnection().CreateCommand();
        achievement.Transaction = (SqliteTransaction)transaction;
        achievement.CommandText = request.StartUtc is null && request.EndUtc is null
          ? "DELETE FROM typing_challenge_achievements;"
          : "DELETE FROM typing_challenge_achievements WHERE achieved_utc >= COALESCE($start, achieved_utc) AND achieved_utc < COALESCE($end, '9999-12-31T23:59:59Z');";
        achievement.Parameters.AddWithValue("$start", (object?)request.StartUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        achievement.Parameters.AddWithValue("$end", (object?)request.EndUtc?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        await achievement.ExecuteNonQueryAsync(cancellationToken);
      }
      await transaction.CommitAsync(cancellationToken);
    }
    finally { _writeGate.Release(); }
  }

  public async Task<IReadOnlyList<SavedTypingPrompt>> LoadSavedTypingPromptsAsync(CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var command = RequireConnection().CreateCommand();
      command.CommandText = "SELECT id,title,prompt_text,language,difficulty,favorite,created_utc,updated_utc,revision FROM typing_challenge_prompts ORDER BY favorite DESC,updated_utc DESC;";
      var prompts = new List<SavedTypingPrompt>();
      await using var reader = await command.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) prompts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Enum.Parse<TypingChallengeDifficulty>(reader.GetString(4)), reader.GetBoolean(5), DateTimeOffset.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7)), reader.GetInt64(8)));
      return prompts;
    }
    finally { _writeGate.Release(); }
  }

  public Task SaveTypingPromptAsync(SavedTypingPrompt prompt, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(prompt.Title) || prompt.Title.Length > 120 || string.IsNullOrWhiteSpace(prompt.Text) || prompt.Text.Length > 50_000)
      throw new InvalidDataException("The saved typing prompt is empty or exceeds its safety limit.");
    return ExecuteWriteAsync("""
      INSERT INTO typing_challenge_prompts(id,title,prompt_text,language,difficulty,favorite,created_utc,updated_utc,revision)
      VALUES($id,$title,$text,$language,$difficulty,$favorite,$created,$updated,$revision)
      ON CONFLICT(id) DO UPDATE SET title=excluded.title,prompt_text=excluded.prompt_text,language=excluded.language,
        difficulty=excluded.difficulty,favorite=excluded.favorite,updated_utc=excluded.updated_utc,revision=excluded.revision
        WHERE excluded.revision >= revision;
      """, command =>
    {
      command.Parameters.AddWithValue("$id", prompt.Id);
      command.Parameters.AddWithValue("$title", prompt.Title.Trim());
      command.Parameters.AddWithValue("$text", prompt.Text);
      command.Parameters.AddWithValue("$language", prompt.Language);
      command.Parameters.AddWithValue("$difficulty", prompt.Difficulty.ToString());
      command.Parameters.AddWithValue("$favorite", prompt.IsFavorite);
      command.Parameters.AddWithValue("$created", prompt.CreatedUtc.ToUniversalTime().ToString("O"));
      command.Parameters.AddWithValue("$updated", prompt.UpdatedUtc.ToUniversalTime().ToString("O"));
      command.Parameters.AddWithValue("$revision", prompt.Revision);
    }, cancellationToken);
  }

  public Task DeleteTypingPromptAsync(string promptId, CancellationToken cancellationToken = default) => ExecuteWriteAsync(
    "DELETE FROM typing_challenge_prompts WHERE id=$id;", command => command.Parameters.AddWithValue("$id", promptId), cancellationToken);

  public async Task<IReadOnlyList<TypingChallengeAchievement>> LoadTypingChallengeAchievementsAsync(CancellationToken cancellationToken = default)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try { return await LoadTypingChallengeAchievementsWithoutLockAsync(cancellationToken); }
    finally { _writeGate.Release(); }
  }

  public Task SaveTypingChallengeAchievementAsync(TypingChallengeAchievement achievement, CancellationToken cancellationToken = default) => ExecuteWriteAsync("""
    INSERT OR IGNORE INTO typing_challenge_achievements(id,kind,local_date,result_id,goal_wpm_snapshot,goal_accuracy_snapshot,achieved_utc)
    VALUES($id,$kind,$date,$result,$wpm,$accuracy,$achieved);
    """, command =>
  {
    command.Parameters.AddWithValue("$id", achievement.Id);
    command.Parameters.AddWithValue("$kind", achievement.Kind);
    command.Parameters.AddWithValue("$date", achievement.LocalDate.ToString("O"));
    command.Parameters.AddWithValue("$result", achievement.ResultId);
    command.Parameters.AddWithValue("$wpm", achievement.GoalWordsPerMinuteSnapshot);
    command.Parameters.AddWithValue("$accuracy", achievement.GoalAccuracySnapshot);
    command.Parameters.AddWithValue("$achieved", achievement.AchievedUtc.ToUniversalTime().ToString("O"));
  }, cancellationToken);

  public async Task<TypingChallengeTransferBundle> ExportTypingChallengesAsync(bool includeHistory, bool includePrompts, CancellationToken cancellationToken = default)
  {
    var results = includeHistory
      ? await QueryTypingChallengeResultsAsync(new(DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue), cancellationToken)
      : [];
    var prompts = includePrompts ? await LoadSavedTypingPromptsAsync(cancellationToken) : [];
    var achievements = includeHistory ? await LoadTypingChallengeAchievementsAsync(cancellationToken) : [];
    return new(results, prompts, achievements);
  }

  public async Task ImportTypingChallengesAsync(TypingChallengeTransferBundle bundle, bool includeHistory, bool includePrompts, CancellationToken cancellationToken = default)
  {
    if (bundle.Results.Count > 250_000 || bundle.Prompts.Count > 1_000 || bundle.Achievements.Count > 500_000)
      throw new InvalidDataException("The profile contains too much typing challenge data.");
    if (includeHistory)
    {
      foreach (var result in bundle.Results) await SaveTypingChallengeResultAsync(result, cancellationToken);
      foreach (var achievement in bundle.Achievements) await SaveTypingChallengeAchievementAsync(achievement, cancellationToken);
    }
    if (includePrompts)
      foreach (var prompt in bundle.Prompts) await SaveTypingPromptAsync(prompt, cancellationToken);
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

  private const string TypingChallengeResultColumns = "id,source_id,completed_utc,source,prompt_id,prompt_title,language,difficulty,run_mode,mistake_mode,duration_limit_seconds,active_ms,character_attempts,correct_characters,error_attempts,corrections,retained_characters,words,gross_wpm,net_wpm,accuracy_percent,consistency_percent,reference_completed,valid_for_streak,goal_wpm_snapshot,goal_accuracy_snapshot,revision";

  private async Task SaveTypingChallengeResultWithoutLockAsync(TypingChallengeResult result, SqliteTransaction transaction, CancellationToken cancellationToken)
  {
    var command = RequireConnection().CreateCommand();
    command.Transaction = transaction;
    command.CommandText = $"""
      INSERT INTO typing_challenge_results({TypingChallengeResultColumns})
      VALUES($id,$source_id,$completed,$source,$prompt,$title,$language,$difficulty,$mode,$mistake,$limit,$active,$attempts,$correct,$errors,$corrections,$retained,$words,$gross,$net,$accuracy,$consistency,$completed_prompt,$valid,$goal_wpm,$goal_accuracy,$revision)
      ON CONFLICT(id) DO UPDATE SET source_id=excluded.source_id,completed_utc=excluded.completed_utc,source=excluded.source,
        prompt_id=excluded.prompt_id,prompt_title=excluded.prompt_title,language=excluded.language,difficulty=excluded.difficulty,
        run_mode=excluded.run_mode,mistake_mode=excluded.mistake_mode,duration_limit_seconds=excluded.duration_limit_seconds,
        active_ms=excluded.active_ms,character_attempts=excluded.character_attempts,correct_characters=excluded.correct_characters,
        error_attempts=excluded.error_attempts,corrections=excluded.corrections,retained_characters=excluded.retained_characters,
        words=excluded.words,gross_wpm=excluded.gross_wpm,net_wpm=excluded.net_wpm,accuracy_percent=excluded.accuracy_percent,
        consistency_percent=excluded.consistency_percent,reference_completed=excluded.reference_completed,valid_for_streak=excluded.valid_for_streak,
        goal_wpm_snapshot=excluded.goal_wpm_snapshot,goal_accuracy_snapshot=excluded.goal_accuracy_snapshot,revision=excluded.revision
        WHERE excluded.revision > revision;
      """;
    command.Parameters.AddWithValue("$id", result.Id);
    command.Parameters.AddWithValue("$source_id", result.SourceId);
    command.Parameters.AddWithValue("$completed", result.CompletedUtc.ToUniversalTime().ToString("O"));
    command.Parameters.AddWithValue("$source", result.Source.ToString());
    command.Parameters.AddWithValue("$prompt", (object?)result.PromptId ?? DBNull.Value);
    command.Parameters.AddWithValue("$title", result.PromptTitle);
    command.Parameters.AddWithValue("$language", result.Language);
    command.Parameters.AddWithValue("$difficulty", result.Difficulty.ToString());
    command.Parameters.AddWithValue("$mode", result.RunMode.ToString());
    command.Parameters.AddWithValue("$mistake", result.MistakeMode.ToString());
    command.Parameters.AddWithValue("$limit", (object?)result.DurationLimitSeconds ?? DBNull.Value);
    command.Parameters.AddWithValue("$active", Math.Max(0, result.ActiveMilliseconds));
    command.Parameters.AddWithValue("$attempts", Math.Max(0, result.CharacterAttempts));
    command.Parameters.AddWithValue("$correct", Math.Max(0, result.CorrectCharacters));
    command.Parameters.AddWithValue("$errors", Math.Max(0, result.ErrorAttempts));
    command.Parameters.AddWithValue("$corrections", Math.Max(0, result.Corrections));
    command.Parameters.AddWithValue("$retained", Math.Max(0, result.RetainedCharacters));
    command.Parameters.AddWithValue("$words", Math.Max(0, result.Words));
    command.Parameters.AddWithValue("$gross", Math.Max(0, result.GrossWordsPerMinute));
    command.Parameters.AddWithValue("$net", Math.Max(0, result.NetWordsPerMinute));
    command.Parameters.AddWithValue("$accuracy", Math.Clamp(result.AccuracyPercent, 0, 100));
    command.Parameters.AddWithValue("$consistency", Math.Clamp(result.ConsistencyPercent, 0, 100));
    command.Parameters.AddWithValue("$completed_prompt", result.ReferenceTextCompleted);
    command.Parameters.AddWithValue("$valid", result.ValidForStreak);
    command.Parameters.AddWithValue("$goal_wpm", Math.Max(0, result.GoalWordsPerMinuteSnapshot));
    command.Parameters.AddWithValue("$goal_accuracy", Math.Clamp(result.GoalAccuracySnapshot, 0, 100));
    command.Parameters.AddWithValue("$revision", Math.Max(1, result.Revision));
    var changed = await command.ExecuteNonQueryAsync(cancellationToken);
    if (changed == 0) return;
    var clear = RequireConnection().CreateCommand();
    clear.Transaction = transaction;
    clear.CommandText = "DELETE FROM typing_challenge_samples WHERE result_id=$id;";
    clear.Parameters.AddWithValue("$id", result.Id);
    await clear.ExecuteNonQueryAsync(cancellationToken);
    foreach (var sample in result.Samples.Take(720))
    {
      var insert = RequireConnection().CreateCommand();
      insert.Transaction = transaction;
      insert.CommandText = "INSERT INTO typing_challenge_samples(result_id,interval_index,character_attempts,correct_characters,errors,net_wpm) VALUES($result,$interval,$attempts,$correct,$errors,$wpm);";
      insert.Parameters.AddWithValue("$result", result.Id);
      insert.Parameters.AddWithValue("$interval", sample.IntervalIndex);
      insert.Parameters.AddWithValue("$attempts", Math.Max(0, sample.CharacterAttempts));
      insert.Parameters.AddWithValue("$correct", Math.Max(0, sample.CorrectCharacters));
      insert.Parameters.AddWithValue("$errors", Math.Max(0, sample.Errors));
      insert.Parameters.AddWithValue("$wpm", Math.Max(0, sample.NetWordsPerMinute));
      await insert.ExecuteNonQueryAsync(cancellationToken);
    }
  }

  private static TypingChallengeResult ReadTypingChallengeResult(SqliteDataReader reader, IReadOnlyList<TypingChallengeSample> samples) => new(
    reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)), Enum.Parse<TypingChallengeSource>(reader.GetString(3)),
    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), Enum.Parse<TypingChallengeDifficulty>(reader.GetString(7)),
    Enum.Parse<TypingChallengeRunMode>(reader.GetString(8)), Enum.Parse<TypingChallengeMistakeMode>(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetInt32(10),
    reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15), reader.GetInt64(16), reader.GetInt64(17),
    reader.GetDouble(18), reader.GetDouble(19), reader.GetDouble(20), reader.GetDouble(21), reader.GetBoolean(22), reader.GetBoolean(23), reader.GetDouble(24), reader.GetDouble(25), reader.GetInt64(26), samples);

  private async Task<IReadOnlyList<TypingChallengeSample>> LoadTypingChallengeSamplesWithoutLockAsync(string resultId, CancellationToken cancellationToken)
  {
    var command = RequireConnection().CreateCommand();
    command.CommandText = "SELECT interval_index,character_attempts,correct_characters,errors,net_wpm FROM typing_challenge_samples WHERE result_id=$id ORDER BY interval_index;";
    command.Parameters.AddWithValue("$id", resultId);
    var samples = new List<TypingChallengeSample>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) samples.Add(new(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetDouble(4)));
    return samples;
  }

  private async Task<IReadOnlyList<TypingChallengeAchievement>> LoadTypingChallengeAchievementsWithoutLockAsync(CancellationToken cancellationToken)
  {
    var command = RequireConnection().CreateCommand();
    command.CommandText = "SELECT id,kind,local_date,result_id,goal_wpm_snapshot,goal_accuracy_snapshot,achieved_utc FROM typing_challenge_achievements ORDER BY local_date;";
    var achievements = new List<TypingChallengeAchievement>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken)) achievements.Add(new(reader.GetString(0), reader.GetString(1), DateOnly.Parse(reader.GetString(2)), reader.GetString(3), reader.GetDouble(4), reader.GetDouble(5), DateTimeOffset.Parse(reader.GetString(6))));
    return achievements;
  }

  private async Task<StatisticsSnapshot> QueryStatisticsCoreAsync(StatisticsQuery query, CancellationToken cancellationToken)
  {
    var start = FloorToHour(query.StartUtc).ToUniversalTime().ToString("O");
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
        var bucketTyping = reader.GetInt64(2);
        var bucketPointer = reader.GetInt64(3);
        var bucketVertical = reader.GetInt64(4);
        var bucketHorizontal = reader.GetInt64(5);
        var bucketActive = reader.GetInt64(6);
        var bucketKeyboardActive = reader.GetInt64(7);
        var bucketPointerActive = reader.GetInt64(8);
        var bucketPeakTyping = reader.GetInt32(9);
        var bucketPeakClicks = reader.GetInt32(10);
        keyboard += bucketKeyboard;
        typing += bucketTyping;
        pointer += bucketPointer;
        vertical += bucketVertical;
        horizontal += bucketHorizontal;
        active += bucketActive;
        keyboardActive += bucketKeyboardActive;
        pointerActive += bucketPointerActive;
        peakTyping = Math.Max(peakTyping, bucketPeakTyping);
        peakClicks = Math.Max(peakClicks, bucketPeakClicks);
        var bucketCount = bucketKeyboard + bucketPointer;
        if (bucketCount > busiestCount)
        {
          busiestCount = bucketCount;
          busiestHour = bucket.ToLocalTime().Hour;
        }
        trend.Add(new(bucket, bucketKeyboard, bucketTyping, bucketPointer, bucketVertical, bucketHorizontal, bucketActive,
          bucketKeyboardActive, bucketPointerActive, bucketPeakTyping, bucketPeakClicks));
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

  private static DateTimeOffset FloorToHour(DateTimeOffset value)
  {
    var utc = value.ToUniversalTime();
    return new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
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
