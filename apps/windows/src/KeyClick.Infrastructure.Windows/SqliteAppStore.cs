using System.Text.Json;
using System.Text.Json.Serialization;
using KeyClick.Core;
using Microsoft.Data.Sqlite;

namespace KeyClick.Infrastructure.Windows;

public sealed class SqliteAppStore(AppPaths paths) : IAppStore
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
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
      INSERT OR IGNORE INTO schema_migrations(version, applied_utc) VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
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
    return result is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(result, _json) ?? new AppSettings();
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
