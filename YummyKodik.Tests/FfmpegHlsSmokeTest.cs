using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using YummyKodik.Kodik;

// Opt-in integration check: real FFmpeg, synthetic media, loopback HTTP, no provider credentials.
internal static class FfmpegHlsSmokeTest
{
    public static async Task RunAsync(string ffmpeg, string ffprobe)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("yummykodik-hls-").FullName;
        try
        {
            foreach (var executable in new[] { ffmpeg, ffprobe })
            {
                var version = await RunProcessAsync(executable, ["-version"], ct);
                Require(version.ExitCode == 0, version.Error);
                Console.WriteLine(version.Output.Split('\n')[0].Trim());
            }

            var generated = await RunProcessAsync(ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=25",
                 "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "2",
                 "-c:v", "libx264", "-preset", "ultrafast", "-g", "25", "-c:a", "aac",
                 "-f", "hls", "-hls_time", "1", "-hls_list_size", "0",
                 "-hls_segment_filename", Path.Combine(directory, "segment%d.ts"), Path.Combine(directory, "index.m3u8")], ct);
            Require(generated.ExitCode == 0, generated.Error);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            using var http = new HttpClient(new FixtureHandler(directory));
            var service = new KodikPlaybackService(http);
            var session = new KodikPlaybackSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                ManifestUrl = "https://fixture.invalid/index.m3u8",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
            };
            session.ProxyResources["manifest"] = session.ManifestUrl;

            app.MapGet("/YummyKodik/stream", context =>
            {
                context.Response.Redirect(KodikPlaybackService.BuildManifestProxyUrl(session, "/YummyKodik/kodik-proxy"));
                return Task.CompletedTask;
            });
            app.MapGet("/YummyKodik/kodik-proxy/{file}", async context =>
            {
                var resourceId = context.Request.Query["resource"].ToString();
                if (!session.ProxyResources.TryGetValue(resourceId, out var upstream))
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                await service.DownloadProxyResourceAsync(session, resourceId, upstream, "/YummyKodik/kodik-proxy",
                    context.RequestAborted, async (contentType, bytes, token) =>
                    {
                        if (!context.Response.HasStarted) context.Response.ContentType = contentType;
                        await context.Response.Body.WriteAsync(bytes, token);
                    });
            });
            // Control: the old MIME header must fail through the same extensionless redirect.
            app.MapGet("/legacy/stream", context =>
            {
                context.Response.Redirect("/legacy/index.m3u8");
                return Task.CompletedTask;
            });
            app.MapGet("/legacy/index.m3u8", async context =>
            {
                var resource = await service.DownloadProxyResourceAsync(session, "manifest", session.ManifestUrl,
                    "/YummyKodik/kodik-proxy", context.RequestAborted);
                context.Response.ContentType = "text/plain";
                await context.Response.Body.WriteAsync(resource.Content, context.RequestAborted);
            });

            await app.StartAsync(ct);
            try
            {
                var origin = app.Urls.Single();
                var probeArgs = new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "json" };
                var legacy = await RunProcessAsync(ffprobe, [.. probeArgs, origin + "/legacy/stream?format=hls"], ct);
                Require(legacy.ExitCode != 0 && legacy.Error.Contains("Invalid data", StringComparison.OrdinalIgnoreCase),
                    "The legacy text/plain control did not reproduce issue #1. Use FFmpeg/ffprobe 8 or newer.");
                Console.WriteLine("PASS legacy text/plain redirect reproduces issue #1");

                var url = origin + "/YummyKodik/stream?provider=kodik&format=hls";
                var probe = await RunProcessAsync(ffprobe, [.. probeArgs, url], ct);
                Require(probe.ExitCode == 0, probe.Error);
                using var json = JsonDocument.Parse(probe.Output);
                var tracks = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                Require(tracks.Any(track => track.GetProperty("codec_type").GetString() == "video") &&
                        tracks.Any(track => track.GetProperty("codec_type").GetString() == "audio"),
                    "The normalized proxy must expose both video and audio.");
                var decoded = await RunProcessAsync(ffmpeg,
                    ["-hide_banner", "-loglevel", "error", "-xerror", "-i", url, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"], ct);
                Require(decoded.ExitCode == 0, decoded.Error);
                Console.WriteLine("PASS normalized Kodik proxy: redirect, HLS probe, video/audio decode");
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string executable, string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Cannot start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureHandler(string directory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var filename = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(directory, filename), ct));
            content.Headers.ContentType = new MediaTypeHeaderValue(filename.EndsWith(".m3u8", StringComparison.Ordinal) ? "text/plain" : "video/mp2t");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
