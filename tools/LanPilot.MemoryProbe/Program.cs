using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Divert.Windows;

// Opt-in, elevated, local-only reproduction. Captures this probe's loopback
// UDP port, never the user's applications or LAN traffic. Does not set policy.
string output = args.FirstOrDefault() ?? "memory-probe.json";
bool forward = args.Contains("--forward", StringComparer.Ordinal);
using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
try
{
    using UdpClient sink = new(new IPEndPoint(IPAddress.Loopback, 0));
    using UdpClient sender = new(AddressFamily.InterNetwork);
    IPEndPoint endpoint = (IPEndPoint)sink.Client.LocalEndPoint!;
    sender.Connect(endpoint);
    using DivertService service = new(
        $"loopback and outbound and udp and udp.DstPort == {endpoint.Port}",
        DivertLayer.Network, priority: -200,
        flags: forward ? DivertFlags.None : DivertFlags.Sniff | DivertFlags.ReceiveOnly);
    Console.WriteLine($"Started {(forward ? "forward" : "sniff")} probe on loopback UDP port {endpoint.Port}.");
    int received = 0;
    byte[] receiveBuffer = new byte[ushort.MaxValue + 40];
    DivertAddress[] addresses = new DivertAddress[1];
    Task capture = Task.Run(async () =>
    {
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var result = await service.ReceiveAsync(receiveBuffer, addresses, timeout.Token);
                if (forward) await service.SendAsync(receiveBuffer.AsMemory(0, result.DataLength), addresses, timeout.Token);
                Interlocked.Increment(ref received);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
    });

    byte[] payload = new byte[64];
    async Task SendPackets(int count)
    {
        int expected = Volatile.Read(ref received) + count;
        for (int i = 0; i < count; i++)
        {
            await sender.SendAsync(payload, timeout.Token);
            await sink.ReceiveAsync(timeout.Token);
        }
        while (Volatile.Read(ref received) < expected)
        {
            if (capture.IsCompleted) await capture;
            await Task.Delay(1, timeout.Token);
        }
    }

    static long RetainedBytes()
    {
        // Only the isolated probe forces GC, to measure retained objects.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    await SendPackets(2_000);
    Console.WriteLine("Warmup complete.");
    long baseline = RetainedBytes();
    var checkpoints = new List<object>();
    for (int batch = 1; batch <= 10; batch++)
    {
        await SendPackets(10_000);
        long retained = RetainedBytes();
        using Process process = Process.GetCurrentProcess();
        checkpoints.Add(new { packets = batch * 10_000, retainedBytes = retained,
            retainedGrowthBytes = retained - baseline, workingSetBytes = process.WorkingSet64 });
        Console.WriteLine($"{batch * 10_000} packets: retained growth {retained - baseline} bytes.");
    }
    timeout.Cancel();
    Console.WriteLine("Stopping capture.");
    await capture;
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
    {
        success = true,
        version = typeof(DivertService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        mode = forward ? "forward" : "sniff",
        received,
        baseline,
        checkpoints
    }, new JsonSerializerOptions { WriteIndented = true }));
}
catch (Exception exception)
{
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = false, error = exception.ToString() }));
    Environment.ExitCode = 1;
}
