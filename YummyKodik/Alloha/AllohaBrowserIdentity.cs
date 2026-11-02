using System.Globalization;
using System.Text.Json;

namespace YummyKodik.Alloha;

/// <summary>Replace a rejected identity only after verifying actual video with its replacement.</summary>
internal sealed class AllohaBrowserIdentity
{
    internal const int BootstrapMajor = 153;
    internal const string VersionUrl = "https://versionhistory.googleapis.com/v1/chrome/platforms/win/channels/stable/versions/all/releases?filter=endtime=none&order_by=version%20desc&pageSize=1";
    private readonly HttpClient _http;
    private readonly string? _cachePath;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _major = BootstrapMajor;
    private DateTimeOffset _nextCheck;
    private DateTimeOffset _nextBootstrapCheck;

    internal AllohaBrowserIdentity(HttpClient http, string? cachePath = null, Func<DateTimeOffset>? utcNow = null)
    {
        _http = http;
        _cachePath = cachePath;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        try
        {
            if (cachePath != null && new FileInfo(cachePath) is { Exists: true, Length: <= 16 } &&
                int.TryParse(File.ReadAllText(cachePath), NumberStyles.None, CultureInfo.InvariantCulture, out var major) &&
                major >= _major && major <= 9999)
                _major = major;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal string Current => ForMajor(Volatile.Read(ref _major));
    internal static string ForMajor(int major) =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major.ToString(CultureInfo.InvariantCulture)}.0.0.0 Safari/537.36";

    internal async Task<string?> TryRecoverAsync(string rejected, Func<string, CancellationToken, Task> verify, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var previousChecks = (_nextCheck, _nextBootstrapCheck);
        try
        {
            if (Current != rejected) return Current; // Another playback already verified the replacement.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(30));
            // A previously verified newer identity can subsequently be rejected. Recover
            // with the known bootstrap before consulting version metadata again.
            if (_major > BootstrapMajor && _utcNow() >= _nextBootstrapCheck)
            {
                _nextBootstrapCheck = _utcNow().AddHours(1);
                try
                {
                    var bootstrap = ForMajor(BootstrapMajor);
                    await verify(bootstrap, attempt.Token).WaitAsync(attempt.Token).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    Volatile.Write(ref _major, BootstrapMajor);
                    _nextCheck = _utcNow().AddHours(1);
                    Save(BootstrapMajor);
                    return bootstrap;
                }
                catch (Exception ex) when (!attempt.IsCancellationRequested &&
                    ex is HttpRequestException or IOException or InvalidOperationException)
                {
                    // Bootstrap failed video proof too; try the current official release.
                }
            }
            if (_utcNow() < _nextCheck) return null;
            _nextCheck = _utcNow().AddHours(1);
            int major;
            using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token))
            {
                lookup.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await _http.GetAsync(VersionUrl, HttpCompletionOption.ResponseHeadersRead, lookup.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(64 * 1024, lookup.Token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(lookup.Token).ConfigureAwait(false));
                if (!json.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array ||
                    releases.GetArrayLength() == 0 || !releases[0].TryGetProperty("version", out var value) ||
                    value.ValueKind != JsonValueKind.String || !Version.TryParse(value.GetString(), out var version) ||
                    version.Major <= _major || version.Major > 9999) return null;
                major = version.Major;
            }
            var candidate = ForMajor(major);
            await verify(candidate, attempt.Token).WaitAsync(attempt.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Volatile.Write(ref _major, major);
            Save(major);
            return candidate;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // An abandoned playback has not disproved recovery for the next caller.
            (_nextCheck, _nextBootstrapCheck) = previousChecks;
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
        finally { _gate.Release(); }
    }

    private void Save(int major)
    {
        if (_cachePath == null) return;
        var temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(temporary, major.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
