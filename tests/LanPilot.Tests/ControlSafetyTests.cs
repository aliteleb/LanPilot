using LanPilot.Contracts;
using LanPilot.Service;
using LanPilot.Service.Diagnostics;
using LanPilot.Service.Engine;
using LanPilot.Service.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanPilot.Tests;

// These tests NEVER open capture handles or mutate Windows rules.
public sealed class ControlSafetyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LanPilot.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeWindows _windows = new();
    private readonly TrafficEngine _traffic = new(NullLogger<TrafficEngine>.Instance);
    private readonly ApplicationDownloadLimiter _limiter = new(NullLogger<ApplicationDownloadLimiter>.Instance);
    private readonly ApplicationTrafficMonitor _monitor = new(NullLogger<ApplicationTrafficMonitor>.Instance);
    private SqliteStore _store = null!;
    private LanPilotCoordinator _coordinator = null!;
    private static LocalApplicationPolicy Policy(bool blocked) => new(ApplicationTrafficController.CreateId(Environment.ProcessPath!),
        "Test process", Environment.ProcessPath!, 48000, blocked);

    public async Task InitializeAsync()
    {
        _store = new(_root);
        await _store.InitializeAsync(CancellationToken.None);
        _coordinator = new(_store, new ControlSessionJournal(_root), null!, _traffic, new PolicyResolver(), _windows,
            new DiagnosticRecorder(_root), _limiter, _monitor, NullLogger<LanPilotCoordinator>.Instance, new ControlSafetyJournal(_root));
        // Bypass discovery only in this fixture; all control lifecycle code remains real.
        typeof(LanPilotCoordinator).GetProperty(nameof(LanPilotCoordinator.IsInitialized))!.SetValue(_coordinator, true);
        // No selected adapter => application-only activation, using the fake OS boundary.
        Assert.True((await _coordinator.SetControlAsync(true, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task CleanupDoesNotClaimInternetConnectivityWasVerified()
    {
        var result = await _coordinator.SuspendAllAsync("UserPause", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains("connectivity has not been verified", result.Message);
        Assert.DoesNotContain("Internet access restored", result.Message);
    }

    [Fact]
    public async Task FaultSuspendsAllScopesButPreservesSavedPolicies()
    {
        Assert.True((await _coordinator.SaveApplicationPolicyAsync(Policy(true), CancellationToken.None)).Success);
        Assert.True((await _coordinator.SuspendAllAsync("Fault", CancellationToken.None)).Success);
        Assert.Empty(_windows.Active);
        Assert.Single(await _store.LoadApplicationPoliciesAsync(CancellationToken.None));
        var safety = await new ControlSafetyJournal(_root).LoadAsync(CancellationToken.None);
        Assert.True(safety!.RequiresManualResume);
        Assert.True(safety.RestorationComplete);
        Assert.False((await _coordinator.OpenUiAsync(CancellationToken.None)).Success);
    }

    [Fact]
    public async Task FailedCleanupIsNotReportedAsSuccess()
    {
        _windows.FailCleanup = true;
        Assert.False((await _coordinator.SuspendAllAsync("Fault", CancellationToken.None)).Success);
        Assert.False(_coordinator.GetSnapshot().ControlSafety!.RestorationComplete);
        _windows.FailCleanup = false;
        Assert.True((await _coordinator.SuspendAllAsync("UserPause", CancellationToken.None)).Success);
        Assert.Equal("Fault", _coordinator.GetSnapshot().ControlSafety!.Reason);
    }

    [Fact]
    public async Task PauseCancelsInFlightPolicyWithoutReapplyingIt()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _windows.BeforeApply = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        Task<OperationResult> save = _coordinator.SaveApplicationPolicyAsync(Policy(true), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<OperationResult> pause = _coordinator.SuspendAllAsync("UserPause", CancellationToken.None);
        Assert.False((await save.WaitAsync(TimeSpan.FromSeconds(3))).Success);
        Assert.True((await pause.WaitAsync(TimeSpan.FromSeconds(3))).Success);
        Assert.Empty(_windows.Active);
        Assert.True(_coordinator.GetSnapshot().ControlSafety!.RequiresManualResume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDatabaseSaveRestoresPolicyOrSuspendsOnRollbackFailure(bool rollbackFails)
    {
        LocalApplicationPolicy previous = Policy(false);
        Assert.True((await _coordinator.SaveApplicationPolicyAsync(previous, CancellationToken.None)).Success);
        await using (SqliteConnection connection = new($"Data Source={_store.DatabasePath}"))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_policy BEFORE INSERT ON entities WHEN NEW.kind = 'application-policy' BEGIN SELECT RAISE(FAIL, 'injected storage failure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        if (rollbackFails) _windows.BeforeApply = token => !_windows.Active[previous.Id].BlockInternet
            ? Task.CompletedTask : Task.FromException(new IOException("injected rollback failure"));
        Assert.False((await _coordinator.SaveApplicationPolicyAsync(Policy(true), CancellationToken.None)).Success);
        Assert.Equal(previous, Assert.Single(await _store.LoadApplicationPoliciesAsync(CancellationToken.None)));
        if (rollbackFails)
        {
            Assert.True(_coordinator.GetSnapshot().ControlSafety!.RequiresManualResume);
            Assert.Empty(_windows.Active);
        }
        else Assert.Equal(previous, _windows.Active[previous.Id]);
    }

    [Fact]
    public async Task OneHundredSimulatedControlCyclesLeaveNoActiveRules()
    {
        for (int i = 0; i < 100; i++)
        {
            Assert.True((await _coordinator.SetControlAsync(true, CancellationToken.None)).Success);
            Assert.True((await _coordinator.SaveApplicationPolicyAsync(Policy(true), CancellationToken.None)).Success);
            Assert.True((await _coordinator.SuspendAllAsync("UserPause", CancellationToken.None)).Success);
            Assert.Empty(_windows.Active);
        }
    }

    public async Task DisposeAsync()
    {
        await _traffic.DisposeAsync();
        await _limiter.DisposeAsync();
        _monitor.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeWindows : IApplicationPolicyController
    {
        public Dictionary<string, LocalApplicationPolicy> Active { get; } = [];
        public bool FailCleanup;
        public Func<CancellationToken, Task>? BeforeApply;
        public Task<IReadOnlyList<LocalApplicationSnapshot>> DiscoverAsync(IReadOnlyDictionary<string, LocalApplicationPolicy> policies, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<LocalApplicationSnapshot>>([]);
        public async Task ApplyAsync(LocalApplicationPolicy policy, CancellationToken token)
        {
            if (BeforeApply is not null) await BeforeApply(token);
            token.ThrowIfCancellationRequested();
            Active[policy.Id] = policy;
        }
        public Task RemoveAsync(LocalApplicationPolicy policy, CancellationToken token) { Active.Remove(policy.Id); return Task.CompletedTask; }
        public Task SuspendAllAsync(CancellationToken token)
        {
            if (FailCleanup) throw new IOException("injected OS cleanup failure");
            Active.Clear();
            return Task.CompletedTask;
        }
    }
}
