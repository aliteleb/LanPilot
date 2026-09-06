using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Divert.Windows.AsyncOperation;

internal interface IDivertValueTaskExecutor
{
    bool Execute(SafeHandle safeHandle, ref readonly PendingOperation pendingOperation);
}

internal static class DivertValueTaskExecutorDelay
{
    private const string DIVERT_WINDOWS_TESTS = "DIVERT_WINDOWS_TESTS";

    [Conditional(DIVERT_WINDOWS_TESTS)]
    public static void DelayExecutionInTests(this IDivertValueTaskExecutor executor)
    {
        if (Environment.GetEnvironmentVariable(DIVERT_WINDOWS_TESTS) is not string name)
        {
            return;
        }

        using var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        stream.Connect(); // Notify delay
        stream.ReadByte(); // continue
    }
}
