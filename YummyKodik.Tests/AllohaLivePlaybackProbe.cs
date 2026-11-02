using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Alloha;
using YummyKodik.Util;
using YummyKodik.Yummy;

internal static class AllohaLivePlaybackProbe
{
    // Opt-in integration check: use a local STRM so credentials never enter command output.
    internal static async Task RunAsync(string path, bool simulateOldIdentity = false, int quality = 720, int? browserMajor = null, bool sequential = false, bool repeatIdentity = false, bool liveChannel = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(600));
        var ct = deadline.Token;
        try
        {
            if (!YummyKodikStreamUri.TryParseRequest(await File.ReadAllTextAsync(path, ct), out var request))
                throw new InvalidOperationException("Invalid managed STRM.");
            var source = new YummyAllohaSource
            {
                MovieToken = request.AllohaMovieToken,
                RequestToken = request.AllohaRequestToken,
                TranslationId = request.AllohaTranslationId,
                SeasonNumber = request.AllohaSeasonNumber,
                EpisodeNumber = request.Episode ?? 1,
                Hidden = request.AllohaHidden,
                RefererUrl = request.AllohaRefererUrl
            };
            using var metadataHttp = new HttpClient();
            var identity = new AllohaBrowserIdentity(metadataHttp);
            if (browserMajor.HasValue)
                typeof(AllohaBrowserIdentity).GetField("_major", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(identity, browserMajor.Value);
            if (simulateOldIdentity)
            {
                typeof(AllohaBrowserIdentity).GetField("_major", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(identity, 146);
                Console.WriteLine("Diagnostic override: start with obsolete Chrome146 and exercise automatic recovery.");
            }
            var initialIdentity = identity.Current;
            await using var channel = new AllohaLiveTokenChannel();
            using var playbackHttp = (HttpClient)typeof(AllohaPlaybackService).GetMethod("CreateDefaultHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            var service = liveChannel
                ? new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, playbackHttp, channel.ResolveAsync, identity)
                : new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, identity);
            var started = System.Diagnostics.Stopwatch.StartNew();
            if (repeatIdentity)
            {
                var core = typeof(AllohaPlaybackService).GetMethod("ResolveViaHttpCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await ((Task)core.Invoke(service, new object?[] { source, quality, request.VoiceName, ct, identity.Current })!).WaitAsync(ct);
                        Console.WriteLine($"Same-identity attempt {attempt}: OK at {started.Elapsed.TotalSeconds:F1}s");
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine($"Same-identity attempt {attempt}: {error.GetType().Name} at {started.Elapsed.TotalSeconds:F1}s");
                    }
                }
                return;
            }
            var session = await service.CreateSessionAsync(source, quality, request.VoiceName, ct);
            if (liveChannel)
            {
                channel.TokenUpdated = token => session.StreamToken = token;
                if (channel.Token == null)
                    await channel.ResolveAsync(new AllohaStreamTokenRequest { UserAgent = session.UserAgent,
                        WebSocketBaseUrl = session.WebSocketBaseUrl, WebSocketSessionId = session.WebSocketSessionId,
                        AudioTrackId = session.AudioTrackId, SelectedQuality = session.SelectedQuality }, ct);
                if (channel.Token != null) session.StreamToken = channel.Token;
            }
            Console.WriteLine($"Alloha session ready in {started.Elapsed.TotalSeconds:F1}s; identity={identity.Current.Split("Chrome/")[1].Split('.')[0]}.");
            const string proxy = "http://localhost:8096/YummyKodik/alloha-proxy";
            await service.EnsurePlaybackReadyAsync(session, proxy, ct);

            var resources = session.ProxyResourceMediaPlaylists.Values
                .OrderByDescending(window => window.Resources.Length).First().Resources;
            var indices = new HashSet<int> { 0, resources.Length - 1 };
            foreach (var position in new[] { 300d, 660d })
            {
                var index = 0;
                var seconds = 0d;
                while (index < resources.Length - 1 && seconds + resources[index].DurationSeconds <= position)
                    seconds += resources[index++].DurationSeconds;
                indices.Add(index);
                if (index + 1 < resources.Length) indices.Add(index + 1);
            }

            // Exercise the normal proxy/recovery implementation, without background prefetch.
            var download = typeof(AllohaPlaybackService).GetMethod("DownloadProxyResourceCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            if (sequential || liveChannel)
            {
                for (var index = 0; index < Math.Min(80, resources.Length); index++)
                {
                    var id = resources[index].ResourceId;
                    try
                    {
                        var fragment = await service.DownloadProxyResourceAsync(session, id, session.ProxyResources[id], proxy, ct);
                        Console.WriteLine($"Sequential {index + 1}: bytes={fragment.Content.Length} elapsed={started.Elapsed.TotalSeconds:F1}s generation={session.ProxyResourceRefreshGeneration}");
                        if (liveChannel) await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                    catch
                    {
                        Console.WriteLine($"Sequential failed at fragment {index + 1}; elapsed={started.Elapsed.TotalSeconds:F1}s generation={session.ProxyResourceRefreshGeneration}");
                        throw;
                    }
                }
                Console.WriteLine($"PASS sequential Alloha downloads: mediaSeconds={resources.Take(Math.Min(80, resources.Length)).Sum(resource => resource.DurationSeconds):F1}; elapsedSeconds={started.Elapsed.TotalSeconds:F1}.");
                return;
            }
            foreach (var index in indices.Order())
            {
                var id = resources[index].ResourceId;
                var task = (Task<AllohaProxyResource>)download.Invoke(service,
                    new object[] { session, id, session.ProxyResources[id], proxy, false, ct })!;
                var fragment = await task.WaitAsync(ct);
                if (fragment.Content.Length == 0 || fragment.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Empty or HTML media response.");
                Console.WriteLine($"Alloha fragment {index + 1}/{resources.Length}: {fragment.Content.Length} bytes OK");
            }
            // Revisit uncached fragments on the same aging session, not a fresh bootstrap.
            var pause = TimeSpan.FromSeconds(180) - started.Elapsed;
            if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
            foreach (var index in new[] { Math.Min(40, resources.Length - 1), Math.Min(120, resources.Length - 1) }.Distinct())
            {
                var id = resources[index].ResourceId;
                var task = (Task<AllohaProxyResource>)download.Invoke(service,
                    new object[] { session, id, session.ProxyResources[id], proxy, false, ct })!;
                var fragment = await task.WaitAsync(ct);
                if (fragment.Content.Length == 0 || fragment.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Aged session returned no playable media.");
                Console.WriteLine($"Alloha aged session at {started.Elapsed.TotalSeconds:F1}s: fragment {index + 1}, {fragment.Content.Length} bytes OK; generation={session.ProxyResourceRefreshGeneration}.");
            }
            if (simulateOldIdentity && identity.Current == initialIdentity)
                throw new InvalidOperationException("Automatic browser identity recovery was not exercised.");
            Console.WriteLine($"PASS Alloha: playlist, dependencies, start, five/eleven-minute positions and final fragment; requestedQuality={quality}; renewal metadata={(!string.IsNullOrEmpty(session.WebSocketSessionId))}.");
        }
        catch (Exception error)
        {
            // Provider exceptions may contain signed URLs. Keep this diagnostic safe to share.
            Console.WriteLine($"FAIL Alloha live playback: {error.GetType().Name}");
            Environment.ExitCode = 1;
        }
    }

}
