namespace Gul.Client;

// keep in sync with the other side (src/Gul.Server/Contracts.cs)

public sealed record TunnelRequest(string Method, string Path, Dictionary<string, string[]> Headers, byte[] Body);

public sealed record TunnelResponse(int Status, Dictionary<string, string[]> Headers, byte[] Body);

// WebSocket tunneling: the server asks the client to open a local socket, then both
// sides relay frames over the same SignalR connection until either end closes.
public sealed record SocketOpen(string SocketId, string Host, string Path, string[] SubProtocols);

public sealed record SocketOpenResult(bool Ok, string? SubProtocol, string? Error);

public sealed record SocketFrame(string SocketId, bool IsText, byte[] Data);
