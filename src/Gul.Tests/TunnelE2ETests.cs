using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gul.Tests;

public sealed record TunnelRequest(string Method, string Path, Dictionary<string, string[]> Headers, byte[] Body);
public sealed record TunnelResponse(int Status, Dictionary<string, string[]> Headers, byte[] Body);
public sealed record SocketOpen(string SocketId, string Host, string Path, string[] SubProtocols);
public sealed record SocketOpenResult(bool Ok, string? SubProtocol, string? Error);
public sealed record SocketFrame(string SocketId, bool IsText, byte[] Data);

public class TunnelE2ETests
{
    [Test]
    public async Task Forwards_a_public_request_down_to_localhost_and_back()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        await using var target = await StartTargetAsync();
        var targetPort = new Uri(
            target.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

        await using var factory = new GulFactory();

        using var localHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{targetPort}") };
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "tunnel"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        connection.On<TunnelRequest, TunnelResponse>("ForwardRequest", async req =>
        {
            using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Path);
            if (req.Body.Length > 0)
                msg.Content = new ByteArrayContent(req.Body);
            using var resp = await localHttp.SendAsync(msg);
            var body = await resp.Content.ReadAsByteArrayAsync();
            return new TunnelResponse((int)resp.StatusCode, new Dictionary<string, string[]>(), body);
        });

        await connection.StartAsync(ct);
        var url = await connection.InvokeAsync<string>("Register", (string?)null, ct);
        if (string.IsNullOrEmpty(url) || !url.Contains(".gul.test"))
            throw new Exception($"Register returned an unexpected URL: '{url}'");
        var host = new Uri(url).Host;

        using var publicClient = factory.CreateClient();
        publicClient.DefaultRequestHeaders.Host = host;

        var getResp = await publicClient.GetAsync("/hello?x=1", ct);
        var getBody = await getResp.Content.ReadAsStringAsync(ct);
        if (getResp.StatusCode != HttpStatusCode.OK)
            throw new Exception($"GET returned {(int)getResp.StatusCode}, body '{getBody}'");
        if (getBody != "GET /hello?x=1")
            throw new Exception($"GET body mismatch: '{getBody}'");

        var postResp = await publicClient.PostAsync("/echo", new StringContent("ping"), ct);
        var postBody = await postResp.Content.ReadAsStringAsync(ct);
        if (postBody != "POST /echo ping")
            throw new Exception($"POST body mismatch: '{postBody}'");

        var nmResp = await publicClient.GetAsync("/nm", ct);
        if (nmResp.StatusCode != HttpStatusCode.NotModified)
            throw new Exception($"304 forward returned {(int)nmResp.StatusCode}");
        if ((await nmResp.Content.ReadAsByteArrayAsync(ct)).Length != 0)
            throw new Exception("304 response must have no body");

        var dot = host.IndexOf('.');
        var routeHost = host[..dot] + "--rtest." + host[(dot + 1)..];
        using var routeClient = factory.CreateClient();
        routeClient.DefaultRequestHeaders.Host = routeHost;
        var routeResp = await routeClient.GetAsync("/via-route", ct);
        var routeBody = await routeResp.Content.ReadAsStringAsync(ct);
        if (routeResp.StatusCode != HttpStatusCode.OK || routeBody != "GET /via-route")
            throw new Exception($"route host {routeHost} did not reach the tunnel: {(int)routeResp.StatusCode} '{routeBody}'");

        Console.WriteLine($"E2E OK: {url} -> localhost:{targetPort}, GET, POST, 304, and {routeHost} route forwarded.");
    }

    [Test]
    public async Task Tunnels_a_websocket_end_to_end()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        await using var factory = new GulFactory();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "tunnel"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        // Stand-in for the CLI: greet the moment the socket opens (before the server has even
        // accepted the browser side, to prove early frames aren't dropped), then bounce every
        // inbound frame back out, and record the exact close code/reason the browser sent.
        SocketOpen? opened = null;
        connection.On<SocketOpen, SocketOpenResult>("OpenSocket", async open =>
        {
            opened = open;
            await connection.SendAsync("SocketToPublic", new SocketFrame(open.SocketId, true, Encoding.UTF8.GetBytes("greet")), ct);
            return new SocketOpenResult(true, open.SubProtocols.FirstOrDefault(), null);
        });
        connection.On<SocketFrame>("SocketToLocal", frame => connection.SendAsync("SocketToPublic", frame, ct));
        var closedByServer = new TaskCompletionSource<(int Status, string? Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, int, string?>("CloseSocket", (_, status, reason) =>
        {
            closedByServer.TrySetResult((status, reason));
            return Task.CompletedTask;
        });

        await connection.StartAsync(ct);
        var url = await connection.InvokeAsync<string>("Register", (string?)null, ct);
        var host = new Uri(url).Host;

        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.SubProtocols.Add("vite-hmr");
        var ws = await wsClient.ConnectAsync(new Uri($"ws://{host}/?token=abc"), ct);

        if (ws.SubProtocol != "vite-hmr")
            throw new Exception($"subprotocol not negotiated back to the browser: '{ws.SubProtocol}'");
        if (opened?.Path != "/?token=abc")
            throw new Exception($"client did not receive the ws path+query: '{opened?.Path}'");

        var buffer = new byte[1024];

        // The greeting emitted before the bridge existed must still arrive, as the first frame.
        var greetRecv = await ws.ReceiveAsync(buffer, ct);
        var greeting = Encoding.UTF8.GetString(buffer, 0, greetRecv.Count);
        if (greetRecv.MessageType != WebSocketMessageType.Text || greeting != "greet")
            throw new Exception($"server-greets-first frame was dropped: {greetRecv.MessageType} '{greeting}'");

        await ws.SendAsync(Encoding.UTF8.GetBytes("hello ws"), WebSocketMessageType.Text, true, ct);
        var textRecv = await ws.ReceiveAsync(buffer, ct);
        var echoed = Encoding.UTF8.GetString(buffer, 0, textRecv.Count);
        if (textRecv.MessageType != WebSocketMessageType.Text || echoed != "hello ws")
            throw new Exception($"text frame did not round-trip: {textRecv.MessageType} '{echoed}'");

        await ws.SendAsync(new byte[] { 1, 2, 3, 4, 5 }, WebSocketMessageType.Binary, true, ct);
        var binRecv = await ws.ReceiveAsync(buffer, ct);
        if (binRecv.MessageType != WebSocketMessageType.Binary || binRecv.Count != 5 || buffer[0] != 1 || buffer[4] != 5)
            throw new Exception($"binary frame did not round-trip: {binRecv.MessageType} count={binRecv.Count}");

        // A custom application close code/reason must reach the local app unchanged.
        await ws.CloseAsync((WebSocketCloseStatus)4001, "session-expired", ct);
        var close = await closedByServer.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (close.Status != 4001 || close.Reason != "session-expired")
            throw new Exception($"close code/reason not preserved: {close.Status} '{close.Reason}'");

        Console.WriteLine($"WS E2E OK: {url} negotiated 'vite-hmr', greet+text+binary delivered, close relayed ({close.Status}/'{close.Reason}').");
    }

    private static async Task<WebApplication> StartTargetAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/nm", () => Results.StatusCode(304));
        app.MapGet("/{**path}", (HttpContext ctx) => $"GET {ctx.Request.Path}{ctx.Request.QueryString}");
        app.MapPost("/{**path}", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return $"POST {ctx.Request.Path} {body}";
        });
        await app.StartAsync();
        return app;
    }
}

file sealed class GulFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Gul:BaseDomain", "gul.test");
        builder.UseSetting("Oidc:Authority", "https://issuer.test");
        builder.UseSetting("Oidc:ClientId", "gul");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(o =>
            {
                o.DefaultScheme = "Test";
                o.DefaultAuthenticateScheme = "Test";
                o.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }
}

file sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
