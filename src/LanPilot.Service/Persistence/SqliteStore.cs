using System.Text.Json;
using LanPilot.Contracts;
using Microsoft.Data.Sqlite;

namespace LanPilot.Service.Persistence;

public sealed class SqliteStore
{
    private const int BackupVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;

    public SqliteStore() : this(null)
    {
    }

    public SqliteStore(string? dataRoot)
    {
        string root = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LanPilot");
        Directory.CreateDirectory(root);
        DatabasePath = Path.Combine(root, "lanpilot.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            string sql = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS entities (
                    kind TEXT NOT NULL,
                    id TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (kind, id)
                );
                CREATE TABLE IF NOT EXISTS traffic_samples (
                    device_id TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    download_bytes INTEGER NOT NULL,
                    upload_bytes INTEGER NOT NULL,
                    PRIMARY KEY (device_id, timestamp_utc)
                );
                CREATE INDEX IF NOT EXISTS ix_traffic_samples_timestamp
                    ON traffic_samples(timestamp_utc);
                """;
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AppSettings?> LoadSettingsAsync(CancellationToken cancellationToken) =>
        LoadOneAsync<AppSettings>("settings", "app", cancellationToken);

    public Task<IReadOnlyList<NetworkProfile>> LoadNetworksAsync(CancellationToken cancellationToken) =>
        LoadAllAsync<NetworkProfile>("network", cancellationToken);

    public Task<IReadOnlyList<DeviceSnapshot>> LoadDevicesAsync(CancellationToken cancellationToken) =>
        LoadAllAsync<DeviceSnapshot>("device", cancellationToken);

    public Task<IReadOnlyList<GroupPolicy>> LoadGroupsAsync(CancellationToken cancellationToken) =>
        LoadAllAsync<GroupPolicy>("group", cancellationToken);

    public Task<IReadOnlyList<ScheduleRule>> LoadSchedulesAsync(CancellationToken cancellationToken) =>
        LoadAllAsync<ScheduleRule>("schedule", cancellationToken);

    public Task<IReadOnlyList<RulePreset>> LoadPresetsAsync(CancellationToken cancellationToken) =>
        LoadAllAsync<RulePreset>("preset", cancellationToken);

    public Task<IReadOnlyList<LocalApplicationPolicy>> LoadApplicationPoliciesAsync(CancellationToken cancellationToken) =>
        LoadAllAsync<LocalApplicationPolicy>("application-policy", cancellationToken);

    public Task SaveSettingsAsync(AppSettings value, CancellationToken cancellationToken) =>
        SaveEntityAsync("settings", "app", value, cancellationToken);

    public Task SaveNetworkAsync(NetworkProfile value, CancellationToken cancellationToken) =>
        SaveEntityAsync("network", value.Id, value, cancellationToken);

    public Task SaveDeviceAsync(DeviceSnapshot value, CancellationToken cancellationToken) =>
        SaveEntityAsync("device", value.Id, value, cancellationToken);

    public Task SaveGroupAsync(GroupPolicy value, CancellationToken cancellationToken) =>
        SaveEntityAsync("group", value.Id, value, cancellationToken);

    public Task SaveScheduleAsync(ScheduleRule value, CancellationToken cancellationToken) =>
        SaveEntityAsync("schedule", value.Id, value, cancellationToken);

    public Task SavePresetAsync(RulePreset value, CancellationToken cancellationToken) =>
        SaveEntityAsync("preset", value.Id, value, cancellationToken);

    public Task SaveApplicationPolicyAsync(LocalApplicationPolicy value, CancellationToken cancellationToken) =>
        SaveEntityAsync("application-policy", value.Id, value, cancellationToken);

    public Task DeleteGroupAsync(string id, CancellationToken cancellationToken) =>
        DeleteEntityAsync("group", id, cancellationToken);

    public Task DeleteScheduleAsync(string id, CancellationToken cancellationToken) =>
        DeleteEntityAsync("schedule", id, cancellationToken);

    public Task DeletePresetAsync(string id, CancellationToken cancellationToken) =>
        DeleteEntityAsync("preset", id, cancellationToken);

    public Task DeleteApplicationPolicyAsync(string id, CancellationToken cancellationToken) =>
        DeleteEntityAsync("application-policy", id, cancellationToken);

    public async Task DeleteTrafficHistoryAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM traffic_samples WHERE device_id = $device;";
            command.Parameters.AddWithValue("$device", deviceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveTrafficSamplesAsync(IEnumerable<TrafficSample> samples, CancellationToken cancellationToken)
    {
        TrafficSample[] values = samples.ToArray();
        if (values.Length == 0) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (TrafficSample sample in values)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO traffic_samples(device_id, timestamp_utc, download_bytes, upload_bytes)
                    VALUES ($device, $timestamp, $down, $up)
                    ON CONFLICT(device_id, timestamp_utc) DO UPDATE SET
                        download_bytes = download_bytes + excluded.download_bytes,
                        upload_bytes = upload_bytes + excluded.upload_bytes;
                    """;
                command.Parameters.AddWithValue("$device", sample.DeviceId);
                command.Parameters.AddWithValue("$timestamp", sample.Timestamp.UtcDateTime.ToString("O"));
                command.Parameters.AddWithValue("$down", sample.DownloadBytes);
                command.Parameters.AddWithValue("$up", sample.UploadBytes);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PruneHistoryAsync(int retentionDays, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 365));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM traffic_samples WHERE timestamp_utc < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExportAsync(string destinationPath, bool includeHistory, CancellationToken cancellationToken)
    {
        LanPilotBackup backup = new(
            BackupVersion,
            DateTimeOffset.UtcNow,
            await LoadSettingsAsync(cancellationToken),
            await LoadNetworksAsync(cancellationToken),
            await LoadDevicesAsync(cancellationToken),
            await LoadGroupsAsync(cancellationToken),
            await LoadSchedulesAsync(cancellationToken),
            await LoadPresetsAsync(cancellationToken),
            includeHistory ? await LoadTrafficRowsAsync(cancellationToken) : []);

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = destinationPath + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(backup, PipeProtocol.JsonOptions),
            cancellationToken);
        File.Move(temporary, destinationPath, true);
    }

    public async Task ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        FileInfo source = new(sourcePath);
        if (!source.Exists) throw new FileNotFoundException("The backup file was not found.", sourcePath);
        if (source.Length > 16 * 1024 * 1024) throw new InvalidDataException("The backup file exceeds the 16 MB safety limit.");

        string json = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        LanPilotBackup backup = JsonSerializer.Deserialize<LanPilotBackup>(json, PipeProtocol.JsonOptions)
            ?? throw new InvalidDataException("The backup file is empty.");
        if (backup.Version != BackupVersion)
        {
            throw new InvalidDataException($"Backup version {backup.Version} is not supported.");
        }

        ValidateBackup(backup);

        if (backup.Settings is not null) await SaveSettingsAsync(backup.Settings, cancellationToken);
        foreach (NetworkProfile value in backup.Networks) await SaveNetworkAsync(value, cancellationToken);
        foreach (DeviceSnapshot value in backup.Devices) await SaveDeviceAsync(value, cancellationToken);
        foreach (GroupPolicy value in backup.Groups) await SaveGroupAsync(value, cancellationToken);
        foreach (ScheduleRule value in backup.Schedules) await SaveScheduleAsync(value, cancellationToken);
        foreach (RulePreset value in backup.Presets ?? []) await SavePresetAsync(value, cancellationToken);
    }

    private static void ValidateBackup(LanPilotBackup backup)
    {
        if (backup.Settings is { HistoryRetentionDays: < 1 or > 365 })
            throw new InvalidDataException("History retention must be between 1 and 365 days.");
        EnsureUnique(backup.Networks.Select(item => item.Id), "network");
        EnsureUnique(backup.Devices.Select(item => item.Id), "device");
        EnsureUnique(backup.Groups.Select(item => item.Id), "group");
        EnsureUnique(backup.Schedules.Select(item => item.Id), "schedule");
        EnsureUnique((backup.Presets ?? []).Select(item => item.Id), "preset");

        foreach (DeviceSnapshot device in backup.Devices)
        {
            if (device.Id != device.Policy.DeviceId)
                throw new InvalidDataException($"Device policy identity mismatch for '{device.Id}'.");
            ValidateRate(device.Policy.DownloadLimitBitsPerSecond, "device download");
            ValidateRate(device.Policy.UploadLimitBitsPerSecond, "device upload");
            if ((device.IsGateway || device.IsLocalComputer) &&
                (device.Policy.BlockInternet ||
                 device.Policy.DownloadLimitBitsPerSecond is not null ||
                 device.Policy.UploadLimitBitsPerSecond is not null))
                throw new InvalidDataException("A backup cannot apply rules to the router or local computer.");
        }
        foreach (GroupPolicy group in backup.Groups)
        {
            ValidateRate(group.DownloadLimitBitsPerSecond, "group download");
            ValidateRate(group.UploadLimitBitsPerSecond, "group upload");
        }
        foreach (ScheduleRule schedule in backup.Schedules)
        {
            if ((schedule.DeviceId is null) == (schedule.GroupId is null) || schedule.Days.Length == 0)
                throw new InvalidDataException($"Schedule '{schedule.Name}' has an invalid target or day selection.");
            ValidateRate(schedule.DownloadLimitBitsPerSecond, "schedule download");
            ValidateRate(schedule.UploadLimitBitsPerSecond, "schedule upload");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string kind)
    {
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        if (values.Any(value => string.IsNullOrWhiteSpace(value) || !unique.Add(value)))
            throw new InvalidDataException($"The backup contains an invalid or duplicate {kind} identifier.");
    }

    private static void ValidateRate(long? rate, string label)
    {
        if (rate is <= 0) throw new InvalidDataException($"The {label} limit must be positive or Unlimited.");
    }

    private async Task SaveEntityAsync<T>(string kind, string id, T value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO entities(kind, id, payload, updated_utc)
                VALUES ($kind, $id, $payload, $updated)
                ON CONFLICT(kind, id) DO UPDATE SET
                    payload = excluded.payload,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, PipeProtocol.JsonOptions));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DeleteEntityAsync(string kind, string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM entities WHERE kind = $kind AND id = $id;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T?> LoadOneAsync<T>(string kind, string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM entities WHERE kind = $kind AND id = $id;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string json ? JsonSerializer.Deserialize<T>(json, PipeProtocol.JsonOptions) : default;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<T>> LoadAllAsync<T>(string kind, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM entities WHERE kind = $kind ORDER BY updated_utc;";
            command.Parameters.AddWithValue("$kind", kind);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            List<T> values = [];
            while (await reader.ReadAsync(cancellationToken))
            {
                T? value = JsonSerializer.Deserialize<T>(reader.GetString(0), PipeProtocol.JsonOptions);
                if (value is not null) values.Add(value);
            }

            return values;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<BackupTrafficRow>> LoadTrafficRowsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT device_id, timestamp_utc, download_bytes, upload_bytes
                FROM traffic_samples ORDER BY timestamp_utc;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            List<BackupTrafficRow> values = [];
            while (await reader.ReadAsync(cancellationToken))
            {
                values.Add(new BackupTrafficRow(
                    reader.GetString(0),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetInt64(2),
                    reader.GetInt64(3)));
            }

            return values;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed record LanPilotBackup(
        int Version,
        DateTimeOffset CreatedAt,
        AppSettings? Settings,
        IReadOnlyList<NetworkProfile> Networks,
        IReadOnlyList<DeviceSnapshot> Devices,
        IReadOnlyList<GroupPolicy> Groups,
        IReadOnlyList<ScheduleRule> Schedules,
        IReadOnlyList<RulePreset>? Presets,
        IReadOnlyList<BackupTrafficRow> Traffic);

    private sealed record BackupTrafficRow(
        string DeviceId,
        DateTimeOffset Timestamp,
        long DownloadBytes,
        long UploadBytes);
}
