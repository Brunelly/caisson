using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Caisson.Drivers.MikroTik.IntegrationTests;

/// <summary>
/// An <see cref="ILoggerFactory"/> that writes every log line to the xunit test output, so the driver's
/// deterministic per-command logs (AC5/AC6) are captured as test artifacts for debugging failures.
/// </summary>
public sealed class XunitLoggerFactory : ILoggerFactory
{
    private readonly ITestOutputHelper _output;

    public XunitLoggerFactory(ITestOutputHelper output) => _output = output;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new XunitLogger(_output, categoryName);

    public void Dispose()
    {
    }

    private sealed class XunitLogger : ILogger
    {
        private readonly ITestOutputHelper _output;
        private readonly string _category;

        public XunitLogger(ITestOutputHelper output, string category)
        {
            _output = output;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try
            {
                _output.WriteLine($"[{logLevel}] {_category}: {formatter(state, exception)}");
            }
            catch (InvalidOperationException)
            {
                // The test has already completed; there is no active output sink. Ignore.
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
