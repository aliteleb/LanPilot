using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LanPilot.Contracts;

namespace LanPilot.Service.Diagnostics;

// The service's flight recorder is bounded both in RAM and on disk. All disk
// work happens on the diagnostics worker/export, never on a packet callback.
public sealed class DiagnosticRecorder
{
    public const int EventCapacity = 1000;
    public const int SampleCapacity = 360;
    public const int FileLimitBytes = 2 * 1024 * 1024;
    private const int PendingCapacity = 256;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly Queue<DiagnosticEvent> _events = new();
    private readonly Queue<JsonElement> _samples = new();
    private readonly Queue<string> _pending = new();
    private readonly string _directory;
    private long _omitted, _sequence;
    private string? _lastKey;
    private long _lastEventTick;
    private string? _storageError;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public DiagnosticRecorder() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LanPilot", "Diagnostics")) { }

    public DiagnosticRecorder(string directory) => _directory = directory;

    public void Record(string category, string level, string template, Exception? exception = null)
    {
        // Store templates, not formatted arguments (which can contain executable
        // paths, names, remote endpoints, or command output). No exception message.
        string? error = exception?.GetType().FullName;
        category = Clip(category, 160);
        template = Clip(template, 512);
        string key = $"{category}|{level}|{template}|{error}";
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (_lastKey == key && now - _lastEventTick < 5000)
            {
                _omitted++;
                return;
            }
            _lastKey = key;
            _lastEventTick = now;
            string? stack = exception is null ? null : string.Join("\n",
                (new StackTrace(exception, false).GetFrames() ?? []).Take(16)
                .Select(frame => $"{frame.GetMethod()?.DeclaringType?.FullName}.{frame.GetMethod()?.Name}"));
            DiagnosticEvent item = new(++_sequence, DateTimeOffset.UtcNow, category, level,
                template, error, exception?.HResult, Clip(stack ?? "", 2048));
            Enqueue(_events, item, EventCapacity);
            AddPending(JsonSerializer.Serialize(new { kind = "event", sessionId = SessionId, data = item }, PipeProtocol.JsonOptions));
        }
    }

    public void Sample(object state)
    {
        JsonElement item = JsonSerializer.SerializeToElement(new
        {
            kind = "sample", sessionId = SessionId, at = DateTimeOffset.UtcNow, state
        }, PipeProtocol.JsonOptions);
        if (Encoding.UTF8.GetByteCount(item.GetRawText()) > 32768)
            item = JsonSerializer.SerializeToElement(new { kind = "sample", sessionId = SessionId,
                at = DateTimeOffset.UtcNow, error = "Sample exceeded 32 KiB; details omitted" }, PipeProtocol.JsonOptions);
        lock (_gate)
        {
            Enqueue(_samples, item, SampleCapacity);
            AddPending(item.GetRawText());
        }
    }

    public object Snapshot()
    {
        lock (_gate) return new
        {
            SessionId, StartedAt, sampleIntervalSeconds = 5,
            events = _events.ToArray(), samples = _samples.ToArray(),
            omittedOrCoalescedEvents = _omitted, storageError = _storageError,
            eventCapacity = EventCapacity, sampleCapacity = SampleCapacity,
            diskFileLimitBytes = FileLimitBytes, diskFileCount = 3
        };
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _diskGate.WaitAsync(cancellationToken);
        try { await FlushCoreAsync(cancellationToken); }
        finally { _diskGate.Release(); }
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            // A burst of errors must not keep export/shutdown waiting forever.
            for (int count = 0; count < PendingCapacity; count++)
            {
                string line;
                lock (_gate)
                {
                    if (!_pending.TryPeek(out string? next)) break;
                    line = next;
                }
                string current = Path.Combine(_directory, "service-0.jsonl");
                if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) + 1 > FileLimitBytes)
                {
                    for (int index = 1; index >= 0; index--)
                    {
                        string source = Path.Combine(_directory, $"service-{index}.jsonl");
                        if (File.Exists(source)) File.Move(source, Path.Combine(_directory, $"service-{index + 1}.jsonl"), true);
                    }
                }
                await File.AppendAllTextAsync(current, line + "\n", cancellationToken);
                lock (_gate)
                {
                    // Producers may have evicted this line while the write ran.
                    if (_pending.TryPeek(out string? next) && ReferenceEquals(next, line)) _pending.Dequeue();
                    _storageError = null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _storageError = ex.GetType().Name;
        }
    }

    public async Task ExportAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        await _diskGate.WaitAsync(cancellationToken);
        try
        {
            await FlushCoreAsync(cancellationToken);
            await using (Stream output = archive.CreateEntry("flight-recorder.json", CompressionLevel.Optimal).Open())
                await JsonSerializer.SerializeAsync(output, Snapshot(), PipeProtocol.JsonOptions, cancellationToken);
            for (int index = 0; index < 3; index++)
            {
                string path = Path.Combine(_directory, $"service-{index}.jsonl");
                try
                {
                    if (!File.Exists(path)) continue;
                    await using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (source.Length > FileLimitBytes) continue;
                    await using Stream output = archive.CreateEntry($"history/service-{index}.jsonl", CompressionLevel.Optimal).Open();
                    await source.CopyToAsync(output, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lock (_gate) _storageError = ex.GetType().Name;
                }
            }
        }
        finally { _diskGate.Release(); }
    }

    private void AddPending(string line)
    {
        if (_pending.Count >= PendingCapacity) { _pending.Dequeue(); _omitted++; }
        _pending.Enqueue(line);
    }

    private static void Enqueue<T>(Queue<T> queue, T item, int capacity)
    {
        if (queue.Count == capacity) queue.Dequeue();
        queue.Enqueue(item);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
    private sealed record DiagnosticEvent(long Sequence, DateTimeOffset At, string Category,
        string Level, string Template, string? ExceptionType, int? HResult, string Stack);
}
