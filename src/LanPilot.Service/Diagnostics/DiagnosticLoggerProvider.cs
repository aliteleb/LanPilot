namespace LanPilot.Service.Diagnostics;

public sealed class DiagnosticLoggerProvider(DiagnosticRecorder recorder) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(recorder, categoryName);
    public void Dispose() { }

    private sealed class DiagnosticLogger(DiagnosticRecorder recorder, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => category.StartsWith("LanPilot.", StringComparison.Ordinal) && level >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string? template = (state as IEnumerable<KeyValuePair<string, object?>>)?
                .FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string;
            recorder.Record(category, logLevel.ToString(), template ?? "Unstructured log (arguments omitted)", exception);
        }
    }
}
