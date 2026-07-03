namespace Gul.Client;

public sealed record TunnelRequest(string Method, string Path, Dictionary<string, string[]> Headers, byte[] Body);

public sealed record TunnelResponse(int Status, Dictionary<string, string[]> Headers, byte[] Body);
