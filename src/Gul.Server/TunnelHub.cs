using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Gul.Server;

[Authorize]
public sealed class TunnelHub(TunnelRegistry registry, WebSocketBridgeRegistry bridges, IConfiguration configuration) : Hub
{
    public string Register(string? requestedSubdomain)
    {
        var subdomain = registry.Add(requestedSubdomain, Context.ConnectionId);
        var baseDomain = configuration["Gul:BaseDomain"] ?? "localhost";
        var scheme = configuration["Gul:PublicScheme"] ?? "https";
        var port = configuration["Gul:PublicPort"];
        var host = string.IsNullOrEmpty(port) ? $"{subdomain}.{baseDomain}" : $"{subdomain}.{baseDomain}:{port}";
        return $"{scheme}://{host}";
    }

    // A frame the local app produced, on its way back out to the visitor's browser. This only
    // queues the frame and returns; a per-bridge writer task does the actual (possibly slow)
    // send, so one wedged browser can never stall other sockets sharing this connection.
    public Task SocketToPublic(SocketFrame frame)
    {
        if (bridges.TryGet(frame.SocketId, out var bridge) && bridge.ConnectionId == Context.ConnectionId
            && !bridge.TryEnqueue(frame.IsText, frame.Data))
            bridge.RequestClose(WebSocketCloseStatus.InternalServerError, "backpressure");
        return Task.CompletedTask;
    }

    // The local app closed its side; close the browser socket to match. Flag it so the middleware
    // doesn't echo a redundant CloseSocket back to a CLI that has already torn this socket down.
    public Task SocketClosed(string socketId, int status, string? reason)
    {
        if (bridges.TryGet(socketId, out var bridge) && bridge.ConnectionId == Context.ConnectionId)
        {
            bridge.MarkClientClosed();
            bridge.RequestClose((WebSocketCloseStatus)status, reason);
        }
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Release the subdomain first so a slow-closing browser can never delay it, then signal
        // every live browser socket to close (the writer tasks handle it without blocking here).
        registry.RemoveByConnection(Context.ConnectionId);
        foreach (var (socketId, bridge) in bridges.ByConnection(Context.ConnectionId))
        {
            bridges.Remove(socketId);
            bridge.RequestClose(WebSocketCloseStatus.EndpointUnavailable, "tunnel disconnected");
        }
        await base.OnDisconnectedAsync(exception);
    }
}
