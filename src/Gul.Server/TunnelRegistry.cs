using System.Collections.Concurrent;
using RandomFriendlyNameGenerator;

namespace Gul.Server;

public sealed class TunnelRegistry
{
    private readonly ConcurrentDictionary<string, string> _bySubdomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _byConnection = new(StringComparer.Ordinal);

    public string Add(string? requested, string connectionId)
    {
        var subdomain = Normalize(requested);

        if (subdomain is null || !_bySubdomain.TryAdd(subdomain, connectionId))
        {
            do { subdomain = RandomSubdomain(); }
            while (!_bySubdomain.TryAdd(subdomain, connectionId));
        }

        _byConnection[connectionId] = subdomain;
        return subdomain;
    }

    public void RemoveByConnection(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var subdomain)) return;
        _bySubdomain.TryRemove(new KeyValuePair<string, string>(subdomain, connectionId));
    }

    public bool TryGet(string subdomain, out string connectionId) =>
        _bySubdomain.TryGetValue(subdomain, out connectionId!);

    public static bool IsValid(string subdomain) =>
        subdomain.Length is >= 1 and <= 63
        && subdomain.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string? Normalize(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var subdomain = requested.Trim().ToLowerInvariant();
        return IsValid(subdomain) ? subdomain : null;
    }

    private static string RandomSubdomain() =>
        Sanitize(NameGenerator.Identifiers.Get(
            IdentifierComponents.Adjective | IdentifierComponents.Animal, separator: "-").ToLowerInvariant());

    private static string Sanitize(string value) =>
        new(value.Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray());
}
