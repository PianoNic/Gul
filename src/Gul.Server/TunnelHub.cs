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
        return $"https://{subdomain}.{baseDomain}";
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.RemoveByConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
