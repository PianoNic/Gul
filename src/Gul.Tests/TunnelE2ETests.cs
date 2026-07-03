using System.Net;
using System.Security.Claims;
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

        Console.WriteLine($"E2E OK: {url} -> localhost:{targetPort}, GET and POST forwarded and returned.");
    }

    private static async Task<WebApplication> StartTargetAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
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
