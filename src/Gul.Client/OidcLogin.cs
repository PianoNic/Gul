using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Gul.Client;

public static class OidcLogin
{
    private static readonly HttpClient Http = new();

    public sealed record Tokens(string AccessToken, string? RefreshToken, DateTime ExpiresAtUtc);

    public static async Task<(string Authority, string ClientId, string Scopes)> GetServerConfigAsync(
        string serverUrl, CancellationToken ct = default)
    {
        var cfg = await Http.GetFromJsonAsync<ServerConfigResponse>(
            serverUrl.TrimEnd('/') + "/config", ct)
            ?? throw new InvalidOperationException("The server returned no /config document.");
        return (cfg.authority, cfg.clientId, cfg.scopes);
    }

    public static async Task<(string AuthorizationEndpoint, string TokenEndpoint)> GetDiscoveryAsync(
        string authority, CancellationToken ct = default)
    {
        var doc = await Http.GetFromJsonAsync<OidcDiscoveryDocument>(
            authority.TrimEnd('/') + "/.well-known/openid-configuration", ct)
            ?? throw new InvalidOperationException("The authority returned no discovery document.");
        return (doc.authorization_endpoint, doc.token_endpoint);
    }

    public static async Task<Tokens> LoginAsync(string serverUrl, CancellationToken ct = default)
    {
        var (authority, clientId, scopes) = await GetServerConfigAsync(serverUrl, ct);
        var (authorizeEndpoint, tokenEndpoint) = await GetDiscoveryAsync(authority, ct);

        var (listener, redirectUri) = StartLoopbackListener();

        try
        {
            var (verifier, challenge) = CreatePkce();
            var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

            var authorizeUrl = authorizeEndpoint + "?" + string.Join("&",
                "response_type=code",
                "client_id=" + Uri.EscapeDataString(clientId),
                "redirect_uri=" + Uri.EscapeDataString(redirectUri),
                "scope=" + Uri.EscapeDataString(scopes),
                "state=" + Uri.EscapeDataString(state),
                "code_challenge=" + Uri.EscapeDataString(challenge),
                "code_challenge_method=S256");

            Console.WriteLine("If your browser didn't open, visit:");
            Console.WriteLine("  " + authorizeUrl);
            OpenBrowser(authorizeUrl);

            var code = await WaitForCodeAsync(listener, state, redirectUri, ct);

            using var resp = await Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            }), ct);
            resp.EnsureSuccessStatusCode();

            var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new InvalidOperationException("The token endpoint returned an empty response.");
            return ToTokens(token);
        }
        finally
        {
            listener.Close();
        }
    }

    public static async Task<Tokens> RefreshAsync(string tokenEndpoint, string clientId, string refreshToken,
        CancellationToken ct = default)
    {
        using var resp = await Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }), ct);
        resp.EnsureSuccessStatusCode();

        var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(ct)
            ?? throw new InvalidOperationException("The token endpoint returned an empty response.");
        return ToTokens(token);
    }

    private static Tokens ToTokens(TokenResponse token)
    {
        var seconds = token.expires_in > 0 ? token.expires_in : 3600;
        return new Tokens(token.access_token, token.refresh_token, DateTime.UtcNow.AddSeconds(seconds));
    }

    private static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static (HttpListener Listener, string RedirectUri) StartLoopbackListener()
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var redirectUri = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            try
            {
                listener.Start();
                return (listener, redirectUri);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }

    private static async Task<string> WaitForCodeAsync(HttpListener listener, string state, string redirectUri,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using var _ = timeout.Token.Register(() => { try { listener.Abort(); } catch { } });

        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for the login redirect.");
            }

            var query = ctx.Request.QueryString;
            var isCallback = query["code"] != null || query["error"] != null;

            if (!isCallback || query["state"] != state)
            {
                WriteAndClose(ctx, 404, "Not found.");
                continue;
            }

            if (query["error"] != null)
            {
                var error = query["error_description"] ?? query["error"]!;
                WriteAndClose(ctx, 400, "Login failed: " + WebUtility.HtmlEncode(error));
                throw new InvalidOperationException("Login failed: " + error);
            }

            WriteAndClose(ctx, 200, "Login complete. You can close this tab and return to your terminal.");
            return query["code"]!;
        }
    }

    private static void WriteAndClose(HttpListenerContext ctx, int status, string message)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Gul</title></head>" +
            $"<body style=\"font-family:system-ui;background:#0B0F14;color:#E6EDF3;display:grid;place-items:center;height:100vh;margin:0\">" +
            $"<p style=\"font-size:1.1rem\">{message}</p></body></html>");
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
        ctx.Response.Close();
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", url);
            else
                Process.Start("xdg-open", url);
        }
        catch
        {
        }
    }

    private sealed record ServerConfigResponse(string authority, string clientId, string scopes, string baseDomain);

    private sealed record OidcDiscoveryDocument(string authorization_endpoint, string token_endpoint);

    private sealed record TokenResponse(string access_token, string? refresh_token, string? id_token,
        int expires_in, string? token_type);
}
