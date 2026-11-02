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
partial class Program
{
    static T InvokeStatic<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        AssertTrue(method is not null, methodName + " should exist.");
        return (T)method!.Invoke(null, args)!;
    }

    static async Task<T> InvokeInstanceTask<T>(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        AssertTrue(method is not null, methodName + " should exist.");
        var task = (Task)method!.Invoke(instance, args)!;
        await task.ConfigureAwait(false);
        return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    static T GetProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        AssertTrue(property is not null, propertyName + " should exist.");
        return (T)property!.GetValue(instance)!;
    }

    static IEnumerable<object?> GetEnumerableProperty(object instance, string propertyName)
    {
        var value = GetProperty<object?>(instance, propertyName);
        if (value is not System.Collections.IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            yield return item;
        }
    }

    static (string SeriesRoot, RefreshStateSeasonInput Input, Dictionary<int, HashSet<string>> ExpectedEpisodeFileBaseNames)
        CreateRefreshStateFixture(
            string tempRoot,
            int seasonNumber = 1,
            int expectedAvailableEpisodes = 1,
            bool createStrmPerVoiceTranslation = false,
            string? fingerprint = null)
    {
        var seriesRoot = Path.Combine(tempRoot, "Series");
        var seasonKey = RefreshStateManager.BuildSeasonKey(seasonNumber);
        var seasonDir = Path.Combine(seriesRoot, seasonKey);
        Directory.CreateDirectory(seasonDir);

        File.WriteAllText(Path.Combine(seriesRoot, "tvshow.nfo"), NfoBuilder.BuildSeriesNfo("Frieren", "Plot"));

        var expectedEpisodeFileBaseNames = new Dictionary<int, HashSet<string>>();
        var effectiveSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
        for (var episodeNumber = 1; episodeNumber <= expectedAvailableEpisodes; episodeNumber++)
        {
            var fileBaseName = $"S{effectiveSeasonNumber:00}E{episodeNumber:00}";
            File.WriteAllText(
                Path.Combine(seasonDir, fileBaseName + ".strm"),
                $"https://jellyfin.test/YummyKodik/stream?ep={episodeNumber}&allohaRequestToken=secret-request-{episodeNumber}" + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(seasonDir, fileBaseName + ".nfo"),
                NfoBuilder.BuildEpisodeNfo(episodeNumber, effectiveSeasonNumber, "Frieren", "Plot"));

            expectedEpisodeFileBaseNames[episodeNumber] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                fileBaseName
            };
        }

        var input = BuildRefreshStateSeasonInput(
            seasonNumber,
            expectedAvailableEpisodes,
            createStrmPerVoiceTranslation,
            fingerprint: fingerprint ?? RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(seasonNumber: seasonNumber)));

        return (seriesRoot, input, expectedEpisodeFileBaseNames);
    }

    static Task InvokeRefreshTaskStaticTask(string methodName, params object?[] args)
    {
        var method = typeof(RefreshYummyKodikLibraryTask).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        AssertTrue(method is not null, methodName + " should exist.");
        return (Task)method!.Invoke(null, args)!;
    }

    static Task<T> InvokeRefreshTaskStaticTaskResult<T>(string methodName, params object?[] args)
    {
        var method = typeof(RefreshYummyKodikLibraryTask).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        AssertTrue(method is not null, methodName + " should exist.");
        return (Task<T>)method!.Invoke(null, args)!;
    }

    static void InvokeRefreshFileWriterWriteTextAtomically(string path, string content)
    {
        var writerType = typeof(RefreshStateManager).Assembly.GetType("YummyKodik.Tasks.Refresh.RefreshFileWriter");
        AssertTrue(writerType is not null, "RefreshFileWriter should exist for atomic writer regression coverage.");

        var method = writerType!.GetMethod("WriteTextAtomicallyAsync", BindingFlags.Static | BindingFlags.Public);
        AssertTrue(method is not null, "WriteTextAtomicallyAsync should be reachable for atomic writer regression coverage.");

        var task = (Task)method!.Invoke(null, new object?[] { path, content, null, "strm", CancellationToken.None })!;
        task.GetAwaiter().GetResult();
    }

    static StaleReleaseCleanupResult RunStaleReleaseCleanup(string outputRoot, IEnumerable<string> currentKeys)
    {
        var normalizedKeys = new HashSet<string>(currentKeys, StringComparer.OrdinalIgnoreCase);
        return StaleReleaseCleanupService.CleanupAsync(
                outputRoot,
                normalizedKeys,
                NullLogger.Instance,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    static void WriteCleanupState(string seriesRoot, params (string SeasonKey, string CleanKey)[] seasons)
    {
        var payload = new
        {
            schemaVersion = RefreshStateManager.SchemaVersion,
            seasons = seasons.ToDictionary(
                season => season.SeasonKey,
                season => new { cleanKey = season.CleanKey },
                StringComparer.Ordinal)
        };

        File.WriteAllText(
            Path.Combine(seriesRoot, RefreshStateManager.StateFileName),
            JsonSerializer.Serialize(payload));
    }

    static string WriteManagedCleanupSeason(string seriesRoot, int seasonNumber, string cleanKey)
    {
        var seasonKey = RefreshStateManager.BuildSeasonKey(seasonNumber);
        var seasonDir = Path.Combine(seriesRoot, seasonKey);
        var fileBaseName = $"S{seasonNumber:00}E01";
        Directory.CreateDirectory(seasonDir);
        File.WriteAllText(Path.Combine(seriesRoot, "tvshow.nfo"), NfoBuilder.BuildSeriesNfo("Cleanup series", "Plot"));
        File.WriteAllText(Path.Combine(seasonDir, fileBaseName + ".strm"), "http://127.0.0.1:8096/YummyKodik/stream?provider=cvh&animeId=1&ep=1" + Environment.NewLine);
        File.WriteAllText(Path.Combine(seasonDir, fileBaseName + ".nfo"), NfoBuilder.BuildEpisodeNfo(1, seasonNumber, "Cleanup series", "Plot"));

        var expectedFiles = new Dictionary<int, HashSet<string>>
        {
            [1] = new(StringComparer.OrdinalIgnoreCase) { fileBaseName }
        };
        var input = BuildRefreshStateSeasonInput(
            seasonNumber: seasonNumber,
            expectedAvailableEpisodes: 1,
            fingerprint: RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(seasonNumber: seasonNumber)),
            cleanKey: cleanKey);

        var written = RefreshStateManager.WriteSeasonStateAsync(
                seriesRoot,
                input,
                expectedFiles,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(written, "Complete generated season artifacts should produce cleanup state.");
        return seasonDir;
    }

    static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }

    static void AssertTrue(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    static void AssertFalse(bool value, string message)
    {
        if (value)
        {
            throw new InvalidOperationException(message);
        }
    }

    static TException AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(message);
    }

    static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    static string InvokePrepareSeasonDirectory(string seriesRoot, string seasonDir, int seasonNumber)
    {
        var helperType = typeof(NfoBuilder).Assembly.GetType("YummyKodik.Tasks.SeasonDirectoryMaintenance");
        AssertTrue(helperType != null, "SeasonDirectoryMaintenance helper should exist for season regression coverage.");

        var method = helperType!.GetMethod(
            "PrepareSeasonDirectory",
            BindingFlags.NonPublic | BindingFlags.Static);

        AssertTrue(method != null, "PrepareSeasonDirectory should be reachable via reflection for regression coverage.");

        return (string)method!.Invoke(null, new object[] { NullLogger.Instance, seriesRoot, seasonDir, seasonNumber })!;
    }

    static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
