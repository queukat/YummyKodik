using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik;
using YummyKodik.Cvh;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Logging;
using YummyKodik.Media;
using YummyKodik.Shikimori;
using YummyKodik.Tasks;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;
using YummyKodik.Web;
using YummyKodik.Yummy;
sealed class DelegatingTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public DelegatingTestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request));
    }
}

sealed class AsyncDelegatingTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public AsyncDelegatingTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _handler(request, cancellationToken);
    }
}

sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CaptureLogger(categoryName, Add);
    }

    public void Dispose()
    {
    }

    private void Add(LogEvent logEvent)
    {
        lock (_gate)
        {
            _events.Add(logEvent);
        }
    }
}

sealed class CaptureLogger : ILogger
{
    private readonly string _categoryName;
    private readonly Action<LogEvent> _add;

    public CaptureLogger(string categoryName, Action<LogEvent> add)
    {
        _categoryName = categoryName;
        _add = add;
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _add(new LogEvent(_categoryName, logLevel, formatter(state, exception)));
    }
}

sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    private NullScope()
    {
    }

    public void Dispose()
    {
    }
}

sealed record LogEvent(string Category, LogLevel Level, string Message);

sealed class RecordingProgress : IProgress<double>
{
    private readonly object _gate = new();
    private readonly List<double> _values = new();

    public IReadOnlyList<double> Values
    {
        get
        {
            lock (_gate)
            {
                return _values.ToArray();
            }
        }
    }

    public void Report(double value)
    {
        lock (_gate)
        {
            _values.Add(value);
        }
    }
}

sealed class RecordingPassThroughHandler : DelegatingHandler
{
    private readonly Action<string> _log;

    public RecordingPassThroughHandler(HttpMessageHandler innerHandler, Action<string> log)
        : base(innerHandler)
    {
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _log(
            "request " +
            request.Method +
            " " +
            request.RequestUri +
            " | Accepts-Controls=" +
            JoinHeader(request, "Accepts-Controls") +
            " | Authorizations=" +
            JoinHeader(request, "Authorizations") +
            " | Origin=" +
            JoinHeader(request, "Origin") +
            " | Referer=" +
            request.Headers.Referrer?.AbsoluteUri);

        var response = await base.SendAsync(request, cancellationToken);
        _log(
            "response " +
            (int)response.StatusCode +
            " " +
            request.RequestUri +
            " | Content-Type=" +
            response.Content.Headers.ContentType?.MediaType);
        return response;
    }

    private static string JoinHeader(HttpRequestMessage request, string name)
    {
        return request.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;
    }
}

static class TestData
{
    internal static readonly int[] EpisodeOne = { 1 };
    internal static readonly int[] EpisodesOneTwo = { 1, 2 };
    internal static readonly int[] UnorderedEpisodesWithDuplicate = { 3, 1, 2, 2 };

    internal static readonly string[] KodikSupplementTranslationNames =
    {
        "Dream Cast",
        "AnimeVost",
        "AniLibria"
    };
}
