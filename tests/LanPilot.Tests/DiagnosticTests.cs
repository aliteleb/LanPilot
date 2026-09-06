using System.IO.Compression;
using System.Text.Json;
using LanPilot.Contracts;
using LanPilot.Service;
using LanPilot.Service.Diagnostics;
using LanPilot.Service.Engine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanPilot.Tests;

public sealed class DiagnosticTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LanPilot-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, PipeProtocol.JsonOptions);

    [Fact]
    public void EventsAndSamplesAreBoundedUnderConcurrentLoad()
    {
        DiagnosticRecorder recorder = new(_directory);
        Parallel.For(0, 5000, index => recorder.Record("LanPilot.Test", "Warning", $"Error {index}"));
        for (int index = 0; index < 500; index++) recorder.Sample(new { index });
        JsonElement snapshot = Json(recorder.Snapshot());
        Assert.Equal(DiagnosticRecorder.EventCapacity, snapshot.GetProperty("events").GetArrayLength());
        Assert.Equal(DiagnosticRecorder.SampleCapacity, snapshot.GetProperty("samples").GetArrayLength());
        Assert.True(snapshot.GetProperty("omittedOrCoalescedEvents").GetInt64() > 0);
    }

    [Fact]
    public void ErrorStormIsCoalescedAndSensitiveArgumentsAreNotRecorded()
    {
        DiagnosticRecorder recorder = new(_directory);
        using DiagnosticLoggerProvider provider = new(recorder);
        ILogger logger = provider.CreateLogger("LanPilot.Test");
        for (int index = 0; index < 10000; index++)
            logger.LogWarning(new IOException("secret exception https://private.example"),
                "Failed for {Path}", "C:/Users/secret/private.exe");
        JsonElement snapshot = Json(recorder.Snapshot());
        string output = snapshot.GetRawText();
        Assert.DoesNotContain("private.example", output);
        Assert.DoesNotContain("private.exe", output);
        Assert.Contains("System.IO.IOException", output);
        Assert.Equal(1, snapshot.GetProperty("events").GetArrayLength());
        Assert.Equal(9999, snapshot.GetProperty("omittedOrCoalescedEvents").GetInt64());
    }

    [Fact]
    public void OversizedSamplesAreReplacedWithExplicitOmission()
    {
        DiagnosticRecorder recorder = new(_directory);
        recorder.Sample(new { oversized = new string('x', 100000) });
        string output = Json(recorder.Snapshot()).GetRawText();
        Assert.Contains("Sample exceeded", output);
        Assert.True(output.Length < 2000);
    }

    [Fact]
    public async Task DiskHistoryRotatesAndSurvivesNewSession()
    {
        DiagnosticRecorder recorder = new(_directory);
        for (int batch = 0; batch < 10; batch++)
        {
            for (int index = 0; index < 32; index++) recorder.Sample(new { padding = new string('x', 28000), batch });
            await recorder.FlushAsync(CancellationToken.None);
        }
        string[] files = Directory.GetFiles(_directory);
        Assert.Equal(3, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, DiagnosticRecorder.FileLimitBytes));
        DiagnosticRecorder restarted = new(_directory);
        Assert.NotEqual(recorder.SessionId, restarted.SessionId);
        using MemoryStream bytes = new();
        using (ZipArchive archive = new(bytes, ZipArchiveMode.Create, true))
            await restarted.ExportAsync(archive, CancellationToken.None);
        bytes.Position = 0;
        using ZipArchive exported = new(bytes, ZipArchiveMode.Read);
        using StreamReader reader = new(exported.GetEntry("history/service-0.jsonl")!.Open());
        string history = await reader.ReadToEndAsync();
        Assert.Contains(recorder.SessionId, history);
        foreach (string line in history.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            using (JsonDocument.Parse(line)) { }
    }

    [Fact]
    public async Task UnwritableStorageStillAllowsInMemoryExport()
    {
        Directory.CreateDirectory(_directory);
        string obstacle = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(obstacle, "test");
        DiagnosticRecorder recorder = new(obstacle);
        recorder.Record("LanPilot.Test", "Warning", "Known event");
        await recorder.FlushAsync(CancellationToken.None);
        Assert.NotNull(Json(recorder.Snapshot()).GetProperty("storageError").GetString());
        using MemoryStream bytes = new();
        using (ZipArchive archive = new(bytes, ZipArchiveMode.Create, true))
            await recorder.ExportAsync(archive, CancellationToken.None);
        bytes.Position = 0;
        using ZipArchive exported = new(bytes, ZipArchiveMode.Read);
        Assert.NotNull(exported.GetEntry("flight-recorder.json"));
    }

    [Fact]
    public void FailedCollectorReturnsSafePartialResult()
    {
        JsonElement result = Json(DiagnosticWorker.CaptureSafely(() => throw new IOException("secret path")));
        Assert.True(result.GetProperty("unavailable").GetBoolean());
        Assert.Equal("IOException", result.GetProperty("error").GetString());
        Assert.DoesNotContain("secret path", result.GetRawText());
    }

    [Fact]
    public void PacketCountersRemainAccurateUnderConcurrency()
    {
        PacketDiagnostics counters = new();
        Parallel.For(0, 100000, _ =>
        {
            counters.Received(); counters.Sent(); counters.Blocked();
            counters.Limited(); counters.QueueFull(); counters.Error();
        });
        JsonElement snapshot = Json(counters.Snapshot());
        foreach (string name in new[] { "received", "sent", "blocked", "limited", "queueFull", "errors" })
            Assert.Equal(100000, snapshot.GetProperty(name).GetInt64());
        Assert.InRange(snapshot.GetProperty("lastSendAgeMs").GetInt64(), 0, 10000);
    }

    [Fact]
    public async Task CoordinatorExportsValidMultiEntryArchiveWithoutStartingTrafficControl()
    {
        DiagnosticRecorder recorder = new(_directory);
        await using TrafficEngine traffic = new(NullLogger<TrafficEngine>.Instance);
        await using ApplicationDownloadLimiter limiter = new(NullLogger<ApplicationDownloadLimiter>.Instance);
        using ApplicationTrafficMonitor monitor = new(NullLogger<ApplicationTrafficMonitor>.Instance);
        // Export must not need an initialized DB, scanner, or a live packet driver.
        LanPilotCoordinator coordinator = new(null!, null!, null!, traffic, new PolicyResolver(), null!,
            recorder, limiter, monitor, NullLogger<LanPilotCoordinator>.Instance);
        recorder.Sample(new { healthy = true });
        string destination = Path.Combine(_directory, "report.zip");
        OperationResult result = await coordinator.ExportDiagnosticsAsync(new ExportRequest(destination, false), CancellationToken.None);
        Assert.True(result.Success);
        using ZipArchive archive = ZipFile.OpenRead(destination);
        foreach (string name in new[] { "diagnostics.json", "flight-recorder.json", "windows-policies.json" })
        {
            using Stream stream = archive.GetEntry(name)!.Open();
            using JsonDocument document = await JsonDocument.ParseAsync(stream);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            if (name == "diagnostics.json") Assert.Equal(2, document.RootElement.GetProperty("formatVersion").GetInt32());
            if (name == "windows-policies.json")
            {
                // A permission error belongs inside the structured result, not
                // an unnoticed PowerShell launch/script failure.
                Assert.True(document.RootElement.TryGetProperty("data", out JsonElement data), document.RootElement.GetRawText());
                Assert.Equal(5, data.GetProperty("services").GetArrayLength());
            }
        }
        Assert.NotNull(archive.GetEntry("README.txt"));
        Assert.False(traffic.IsRunning);
    }

    public void Dispose()
    {
        // Only this test-owned, GUID-named temporary directory is removed.
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
