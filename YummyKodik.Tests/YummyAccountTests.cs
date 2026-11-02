using System.Net;
using System.Reflection;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik;
using YummyKodik.Configuration;
using YummyKodik.Yummy;

internal static class YummyAccountTests
{
    public class SerializerProxy : DispatchProxy
    {
        public bool FailWrites { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.ReturnType == typeof(void) && FailWrites) throw new IOException("synthetic save failure");
            return method.ReturnType == typeof(void) ? null : new PluginConfiguration();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void AuthenticationTransitions() => TransitionsAsync().GetAwaiter().GetResult();

    private static async Task TransitionsAsync()
    {
        foreach (var scenario in new[] { "refresh", "recover", "password", "captcha", "network" })
        {
            var logins = 0;
            var saves = 0;
            var cfg = new PluginConfiguration { YummyAccessToken = "old", YummyLogin = "mail", YummyPassword = "secret" };
            using var http = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath == "/profile/token")
                    return scenario == "refresh" ? Reply("{\"response\":{\"token\":\"new\"}}")
                        : Reply("{}", scenario == "network" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
                logins++;
                return scenario == "password" ? Reply("{\"error_code\":7,\"error\":\"secret upstream body\"}", HttpStatusCode.BadRequest)
                    : scenario == "captcha" ? Reply("{}", (HttpStatusCode)420)
                    : Reply("{\"response\":{\"success\":true,\"token\":\"new\"}}");
            }));
            var client = new YummyClient(http, "app", "https://yummy.test");
            Exception? failure = null;
            try { await YummyAccountAuthentication.EnsureAsync(cfg, client, () => saves++, CancellationToken.None); }
            catch (Exception ex) { failure = ex; }
            Check(logins == (scenario is "refresh" or "network" ? 0 : 1), "Only confirmed auth denial may fall back to login.");
            if (scenario is "refresh" or "recover")
                Check(failure is null && cfg.YummyAccessToken == "new" && cfg.YummyAccountStatus == "connected" && saves == 1, "Successful recovery must persist token and status.");
            else
            {
                Check(failure is not null && cfg.YummyAccessToken == "old", "Failed requests must retain saved credentials.");
                Check(!failure!.ToString().Contains("secret upstream body"), "Remote response bodies must not leak through errors.");
                Check(scenario == "network" ? saves == 0 : cfg.YummyAccountStatus == (scenario == "password" ? "invalid_credentials" : "captcha_required"), "Failure classification must be actionable and not confuse outages with passwords.");
            }
        }
    }

    public static void CurrentConfigurationAndSaveFailure() => CurrentAsync().GetAwaiter().GetResult();

    private static async Task CurrentAsync()
    {
        var stale = new PluginConfiguration { YummyAccessToken = "old" };
        var current = new PluginConfiguration { YummyAccessToken = "current" };
        using var http = new HttpClient(new Handler(request =>
        {
            Check(request.Headers.Authorization?.Parameter == "current", "Auth must resolve live config after entering the gate.");
            return Reply("{\"response\":{\"token\":\"rotated\"}}");
        }));
        var client = new YummyClient(http, "app", "https://yummy.test");
        var result = await YummyAccountAuthentication.EnsureAsync(stale, client, () => { }, CancellationToken.None, () => current);
        Check(ReferenceEquals(result, current) && current.YummyAccessToken == "rotated" && stale.YummyAccessToken == "old", "Only current account state may change.");
        try
        {
            YummyAccountAuthentication.SetStatus(current, "connected", () => throw new IOException("disk full"), () => current.YummyAccessToken = "unsaved");
            throw new InvalidOperationException("Save failure must surface.");
        }
        catch (IOException) { }
        Check(current.YummyAccessToken == "rotated", "A failed write must restore the previous in-memory account.");
    }

    public static void StaleConfigurationCannotRestoreCredentials()
    {
        var paths = DispatchProxy.Create<IApplicationPaths, LateNativeMergeTests.HostDependencyProxy>();
        ((LateNativeMergeTests.HostDependencyProxy)(object)paths).Root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var serializer = DispatchProxy.Create<IXmlSerializer, SerializerProxy>();
        var plugin = new Plugin(paths, serializer, NullLogger<Plugin>.Instance);
        plugin.Configuration.YummyAccessToken = "fresh";
        plugin.Configuration.YummyPassword = "current";
        plugin.Configuration.YummyAccountStatus = "invalid_credentials";
        plugin.UpdateTranslationPreferences(cfg => cfg.SetSeriesPreferredTranslationId("yummy:42", "fresh-voice"));
        plugin.UpdateConfiguration(new PluginConfiguration { YummyAccessToken = "stale", YummyPassword = "old", YummyAccountStatus = "connected", PreferredQuality = 1080, YummyUserId = 42 });
        Check(plugin.Configuration.YummyAccessToken == "fresh" && plugin.Configuration.YummyPassword == "current" && plugin.Configuration.YummyAccountStatus == "invalid_credentials", "Normal settings save must preserve account-owned state.");
        Check(plugin.Configuration.PreferredQuality == 1080 && plugin.Configuration.YummyUserId == 42, "Other settings and public-list target remain editable.");
        Check(plugin.Configuration.GetSeriesPreferredTranslationId("yummy:42") == "fresh-voice", "Stale settings must not restore old voice preferences.");
        ((SerializerProxy)(object)serializer).FailWrites = true;
        try
        {
            plugin.UpdateTranslationPreferences(cfg => cfg.SetSeriesPreferredTranslationId("yummy:42", "unsaved-voice"));
            throw new InvalidOperationException("Save failure must surface.");
        }
        catch (IOException) { }
        Check(plugin.Configuration.GetSeriesPreferredTranslationId("yummy:42") == "fresh-voice", "Failed preference persistence must restore old snapshot.");
    }
}
