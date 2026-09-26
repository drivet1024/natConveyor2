using System.Collections.Concurrent;

namespace Conveyor.Web.Services;

public sealed record RuntimeLogEntry(long Sequence, DateTimeOffset Timestamp, LogLevel Level, string Source, string Message, string? Exception);

public interface ILogStore
{
    event Action? Changed;
    IReadOnlyList<RuntimeLogEntry> GetRecent(int maximum = 250, LogLevel minimum = LogLevel.Trace, string? search = null);
    void Clear();
}

public sealed class InMemoryLogStore : ILoggerProvider, ILogStore
{
    private readonly ConcurrentQueue<RuntimeLogEntry> _entries = new();
    private long _sequence;
    private int _count;
    private const int Capacity = 2_000;
    public event Action? Changed;

    public ILogger CreateLogger(string categoryName) => new StoreLogger(this, categoryName);
    public void Dispose() { }

    internal void Add(LogLevel level, string source, string message, Exception? exception)
    {
        _entries.Enqueue(new RuntimeLogEntry(Interlocked.Increment(ref _sequence), DateTimeOffset.Now,
            level, source, message, exception?.ToString()));
        if (Interlocked.Increment(ref _count) > Capacity && _entries.TryDequeue(out _)) Interlocked.Decrement(ref _count);
        Changed?.Invoke();
    }

    public IReadOnlyList<RuntimeLogEntry> GetRecent(int maximum = 250, LogLevel minimum = LogLevel.Trace, string? search = null)
    {
        IEnumerable<RuntimeLogEntry> query = _entries.Where(entry => entry.Level >= minimum);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(entry => entry.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                         entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase));
        return query.TakeLast(Math.Clamp(maximum, 1, 1_000)).Reverse().ToArray();
    }

    public void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _count, 0);
        Changed?.Invoke();
    }

    private sealed class StoreLogger(InMemoryLogStore store, string source) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) store.Add(logLevel, source, formatter(state, exception), exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
