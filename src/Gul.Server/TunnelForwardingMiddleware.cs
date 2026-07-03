using Microsoft.AspNetCore.SignalR;

namespace Gul.Server;

public sealed class TunnelForwardingMiddleware
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade",
        "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Content-Length", "Server", "Date",
    };

    private readonly RequestDelegate _next;
    private readonly TunnelRegistry _registry;
    private readonly IHubContext<TunnelHub> _hub;
    private readonly ILogger<TunnelForwardingMiddleware> _logger;
    private readonly string _baseDomain;

    public TunnelForwardingMiddleware(
        RequestDelegate next,
        TunnelRegistry registry,
        IHubContext<TunnelHub> hub,
        IConfiguration configuration,
        ILogger<TunnelForwardingMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _hub = hub;
        _logger = logger;
        _baseDomain = configuration["Gul:BaseDomain"] ?? "localhost";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;

        if (!TryGetSubdomain(host, out var subdomain))
        {
            await _next(context);
            return;
        }

        if (!_registry.TryGet(subdomain, out var connectionId))
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync($"No active tunnel for {subdomain}");
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            await context.Response.WriteAsync("WebSocket tunneling is not supported.");
            return;
        }

        var request = await BuildRequestAsync(context);

        TunnelResponse response;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(100));
        try
        {
            response = await _hub.Clients.Client(connectionId)
                .InvokeAsync<TunnelResponse>("ForwardRequest", request, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tunnel forward for {Subdomain} failed.", subdomain);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                await context.Response.WriteAsync($"Tunnel for {subdomain} did not respond.");
            }
            return;
        }

        WriteResponse(context, response);
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }

    private static async Task<TunnelRequest> BuildRequestAsync(HttpContext context)
    {
        await using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in context.Request.Headers)
            headers[header.Key] = header.Value.Select(v => v ?? string.Empty).ToArray();

        var path = context.Request.Path + context.Request.QueryString;
        return new TunnelRequest(context.Request.Method, path, headers, buffer.ToArray());
    }

    private static void WriteResponse(HttpContext context, TunnelResponse response)
    {
        context.Response.StatusCode = response.Status;
        foreach (var (name, values) in response.Headers)
        {
            if (HopByHop.Contains(name)) continue;
            context.Response.Headers[name] = values;
        }
        context.Response.ContentLength = response.Body.Length;
    }

    private bool TryGetSubdomain(string host, out string subdomain)
    {
        subdomain = "";
        if (host.Equals(_baseDomain, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!host.EndsWith("." + _baseDomain, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = host[..^(_baseDomain.Length + 1)];
        var lastDot = remainder.LastIndexOf('.');
        subdomain = lastDot >= 0 ? remainder[(lastDot + 1)..] : remainder;
        if (subdomain.Length == 0 || subdomain.Equals("www", StringComparison.OrdinalIgnoreCase))
        {
            subdomain = "";
            return false;
        }
        return true;
    }
}
