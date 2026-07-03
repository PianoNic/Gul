using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Gul.Server;

[Authorize]
public sealed class TunnelHub(TunnelRegistry registry, IConfiguration configuration) : Hub
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

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.RemoveByConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
