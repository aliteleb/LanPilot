using LanPilot.Contracts;
using LanPilot.Service.Persistence;
using Microsoft.Data.Sqlite;

namespace LanPilot.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void AppSettings_EnableGuestAssignmentByDefault()
    {
        AppSettings settings = new(null, false, 30, true, false);

        Assert.True(settings.AutoAssignNewDevicesToGuests);
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AndSettingsRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "LanPilot.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            SqliteStore store = new(root);
            await store.InitializeAsync(CancellationToken.None);
            await store.InitializeAsync(CancellationToken.None);
            AppSettings expected = new("adapter", false, 45, true, false, false, true);

            await store.SaveSettingsAsync(expected, CancellationToken.None);

            Assert.True(File.Exists(store.DatabasePath));
            Assert.Equal(expected, await store.LoadSettingsAsync(CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeleteTrafficHistory_RemovesOnlyRequestedDevice()
    {
        string root = Path.Combine(Path.GetTempPath(), "LanPilot.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            SqliteStore store = new(root);
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset minute = DateTimeOffset.Now;
            await store.SaveTrafficSamplesAsync(
            [
                new TrafficSample("device-a", minute, 0, 0, 100, 50),
                new TrafficSample("device-b", minute, 0, 0, 200, 75)
            ], CancellationToken.None);

            await store.DeleteTrafficHistoryAsync("device-a", CancellationToken.None);

            await using SqliteConnection connection = new($"Data Source={store.DatabasePath}");
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT device_id FROM traffic_samples ORDER BY device_id;";
            Assert.Equal("device-b", await command.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeleteRuleEntities_RemovesOnlyRequestedRecords()
    {
        string root = Path.Combine(Path.GetTempPath(), "LanPilot.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            SqliteStore store = new(root);
            await store.InitializeAsync(CancellationToken.None);
            GroupPolicy group = new("group-a", "Family", null, null, DevicePriority.Normal, false);
            ScheduleRule schedule = new(
                "schedule-a", "Evening", null, group.Id, [DayOfWeek.Monday],
                new TimeOnly(20, 0), new TimeOnly(22, 0), false, null, null, true);
            RulePreset preset = new("preset-a", "Home", [], [group], [schedule], DateTimeOffset.Now);
            await store.SaveGroupAsync(group, CancellationToken.None);
            await store.SaveScheduleAsync(schedule, CancellationToken.None);
            await store.SavePresetAsync(preset, CancellationToken.None);

            await store.DeleteGroupAsync(group.Id, CancellationToken.None);
            await store.DeleteScheduleAsync(schedule.Id, CancellationToken.None);
            await store.DeletePresetAsync(preset.Id, CancellationToken.None);

            Assert.Empty(await store.LoadGroupsAsync(CancellationToken.None));
            Assert.Empty(await store.LoadSchedulesAsync(CancellationToken.None));
            Assert.Empty(await store.LoadPresetsAsync(CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Import_ValidatesEntireBackupBeforeApplyingIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "LanPilot.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            SqliteStore store = new(root);
            await store.InitializeAsync(CancellationToken.None);
            string backup = Path.Combine(root, "invalid.json");
            await File.WriteAllTextAsync(backup, """
                {"version":1,"createdAt":"2026-09-04T00:00:00Z","settings":{"selectedAdapterId":null,"autoControl":false,"historyRetentionDays":0,"notifyNewDevices":true,"displayRatesAsBytes":false},"networks":[],"devices":[],"groups":[],"schedules":[],"presets":[],"traffic":[]}
                """);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ImportAsync(backup, CancellationToken.None));
            Assert.Null(await store.LoadSettingsAsync(CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
