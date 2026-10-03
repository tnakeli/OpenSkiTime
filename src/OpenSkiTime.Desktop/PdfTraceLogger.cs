using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenSkiTime.Reporting;

namespace OpenSkiTime.Desktop;

// Use the desktop's existing trace output; do not dump race records or native layout contents.
internal sealed class PdfTraceLogger : ILogger<ReportGenerationService>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Trace.WriteLine(formatter(state, null));
        if (exception is not null) { Trace.WriteLine(exception.GetType().FullName + "\n" + exception.StackTrace); }
    }
}
