using Microsoft.Extensions.Logging;

namespace YummyKodik.Logging;

internal class YummyKodikLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly string _categoryName;

    internal YummyKodikLogger(ILogger inner, string categoryName)
    {
        _inner = inner;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return YummyKodikLogFilter.ShouldLogCategory(_categoryName, logLevel) &&
               _inner.IsEnabled(logLevel);
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!YummyKodikLogFilter.ShouldLogCategory(_categoryName, logLevel))
        {
            return;
        }

        _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

internal sealed class YummyKodikLogger<T> : YummyKodikLogger, ILogger<T>
{
    internal YummyKodikLogger(ILogger<T> inner)
        : base(inner, ResolveCategoryName())
    {
    }

    private static string ResolveCategoryName()
    {
        return typeof(T).FullName ?? typeof(T).Name;
    }
}
