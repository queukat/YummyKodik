using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("YummyKodik/Account")]
public sealed class YummyKodikAccountController(IHttpClientFactory clients) : ControllerBase
{
    public sealed class SignInRequest
    {
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public bool UseToken { get; set; }
    }

    [HttpPost("SignIn")]
    public async Task<IActionResult> SignIn([FromBody] SignInRequest request, CancellationToken ct)
    {
        if (request.UseToken ? string.IsNullOrWhiteSpace(request.Token) : string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrWhiteSpace(request.Password))
            return Ok(new { Success = false, Code = "credentials_required" });

        await Plugin.ConfigurationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plugin = Plugin.Instance;
            var cfg = plugin.Configuration;
            using var http = clients.CreateClient(HttpClientNames.Yummy);
            var client = new YummyClient(http, cfg.YummyClientId, cfg.YummyApiBaseUrl);
            try
            {
                if (request.UseToken) client.SetAccessToken(request.Token);
                else await client.LoginAsync(request.Login, request.Password, null, ct).ConfigureAwait(false);
                var userId = await client.GetProfileIdAsync(ct).ConfigureAwait(false);
                // Do not commit results against settings replaced while the remote request was pending.
                if (!ReferenceEquals(cfg, plugin.Configuration)) return Ok(new { Success = false, Code = "settings_changed" });
                YummyAccountAuthentication.SetStatus(cfg, "connected", plugin.SaveConfiguration, () =>
                {
                    cfg.YummyAccessToken = client.GetAccessToken();
                    cfg.YummyUserId = userId;
                    cfg.YummyLogin = request.UseToken ? string.Empty : request.Login.Trim();
                    cfg.YummyPassword = request.UseToken ? string.Empty : request.Password;
                    cfg.YummyRecaptchaResponse = string.Empty;
                });
                return Ok(new { Success = true, Code = "connected", UserId = userId });
            }
            catch (YummyAccountException ex) { return Ok(new { Success = false, ex.Code }); }
            catch (IOException) { return Ok(new { Success = false, Code = "persistence_error" }); }
            catch (HttpRequestException) { return Ok(new { Success = false, Code = "network_error" }); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Ok(new { Success = false, Code = "network_error" }); }
            catch (System.Text.Json.JsonException) { return Ok(new { Success = false, Code = "unexpected_response" }); }
        }
        finally { Plugin.ConfigurationGate.Release(); }
    }
}
