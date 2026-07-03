using System.Net.WebSockets;
using Microsoft.AspNetCore.SignalR;

namespace Gul.Server;

public sealed class TunnelForwardingMiddleware
{
    private const int MaxSocketMessageBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade",
        "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Content-Length", "Server", "Date",
    };

    private readonly RequestDelegate _next;
    private readonly TunnelRegistry _registry;
    private readonly WebSocketBridgeRegistry _bridges;
    private readonly IHubContext<TunnelHub> _hub;
    private readonly ILogger<TunnelForwardingMiddleware> _logger;
    private readonly string _baseDomain;

    public TunnelForwardingMiddleware(
        RequestDelegate next,
        TunnelRegistry registry,
        WebSocketBridgeRegistry bridges,
        IHubContext<TunnelHub> hub,
        IConfiguration configuration,
        ILogger<TunnelForwardingMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _bridges = bridges;
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
            var sep = subdomain.LastIndexOf("--", StringComparison.Ordinal);
            if (sep <= 0 || !_registry.TryGet(subdomain[..sep], out connectionId))
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync($"No active tunnel for {subdomain}");
                return;
            }
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleWebSocketAsync(context, connectionId);
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
        if (!IsBodyless(response.Status) && response.Body.Length > 0)
            await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }

    private async Task HandleWebSocketAsync(HttpContext context, string connectionId)
    {
        var socketId = Guid.NewGuid().ToString("N");

        // Register the bridge before the CLI opens its local socket so a frame the local app
        // pushes on connect is buffered rather than dropped, and so a mid-handshake tunnel
        // disconnect (OnDisconnectedAsync) always finds the bridge to tear down.
        var bridge = new WebSocketBridge(connectionId);
        _bridges.Add(socketId, bridge);

        var closeStatus = WebSocketCloseStatus.NormalClosure;
        string? closeReason = null;
        try
        {
            var open = new SocketOpen(
                socketId,
                context.Request.Host.Value ?? string.Empty,
                context.Request.Path + context.Request.QueryString,
                [.. context.WebSockets.WebSocketRequestedProtocols]);

            SocketOpenResult result;
            using (var openCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
            {
                openCts.CancelAfter(TimeSpan.FromSeconds(30));
                result = await _hub.Clients.Client(connectionId)
                    .InvokeAsync<SocketOpenResult>("OpenSocket", open, openCts.Token);
            }

            if (!result.Ok)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                if (!string.IsNullOrEmpty(result.Error))
                    await context.Response.WriteAsync(result.Error);
                return;
            }

            // Not `using`: the browser socket outlives this scope until the bridge's writer task
            // has flushed and closed it (awaited via bridge.Completion below); the framework
            // disposes it when the request finishes.
            var ws = string.IsNullOrEmpty(result.SubProtocol)
                ? await context.WebSockets.AcceptWebSocketAsync()
                : await context.WebSockets.AcceptWebSocketAsync(result.SubProtocol);

            bridge.AttachBrowser(ws);
            (closeStatus, closeReason) = await PumpBrowserToClientAsync(socketId, connectionId, ws, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebSocket setup for {SocketId} failed.", socketId);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
        finally
        {
            _bridges.Remove(socketId);
            bridge.RequestClose(closeStatus, closeReason);
            // Let the writer flush + close the browser before the framework disposes the socket.
            try { await bridge.Completion.WaitAsync(TimeSpan.FromSeconds(6)); } catch { }
            // Tell the CLI to tear down its local socket — unless the close came from the CLI
            // itself (it already tore down), which would only leave a stale tombstone there.
            if (!bridge.CloseOriginatedFromClient)
                await NotifyClientClosedAsync(connectionId, socketId, (int)WebSocketStatus.Sanitize(closeStatus), closeReason);
        }
    }

    private async Task NotifyClientClosedAsync(string connectionId, string socketId, int status, string? reason)
    {
        try
        {
            await _hub.Clients.Client(connectionId).SendAsync("CloseSocket", socketId, status, reason, CancellationToken.None);
        }
        catch { /* the tunnel connection may already be gone */ }
    }

    private async Task<(WebSocketCloseStatus Status, string? Reason)> PumpBrowserToClientAsync(
        string socketId, string connectionId, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult recv;
                do
                {
                    recv = await ws.ReceiveAsync(buffer, ct);
                    if (recv.MessageType == WebSocketMessageType.Close)
                        return (recv.CloseStatus ?? WebSocketCloseStatus.NormalClosure, recv.CloseStatusDescription);
                    message.Write(buffer, 0, recv.Count);
                    if (message.Length > MaxSocketMessageBytes)
                        throw new InvalidOperationException("WebSocket message exceeded the size limit.");
                } while (!recv.EndOfMessage);

                await _hub.Clients.Client(connectionId).SendAsync(
                    "SocketToLocal",
                    new SocketFrame(socketId, recv.MessageType == WebSocketMessageType.Text, message.ToArray()),
                    ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebSocket browser pump ended for {SocketId}.", socketId);
        }
        return (WebSocketCloseStatus.NormalClosure, null);
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
        if (!IsBodyless(response.Status))
            context.Response.ContentLength = response.Body.Length;
    }

    private static bool IsBodyless(int status) =>
        status is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified
        || status is >= 100 and < 200;

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
