using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Gul.Client;

public sealed partial class Translator
{
    private static readonly string[] TextTypes =
    [
        "text/html", "text/css", "application/json", "application/javascript",
        "text/javascript", "application/xml", "text/xml", "text/plain",
        "application/manifest+json", "image/svg+xml",
    ];

    [GeneratedRegex(@"https?://[A-Za-z0-9._-]+(?::\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    private static readonly HashSet<string> RedirectParams = new(StringComparer.OrdinalIgnoreCase) { "redirect_uri", "post_logout_redirect_uri" };

    private readonly string _mode;
    private readonly HashSet<string> _allow;
    private readonly string _primary;
    private readonly string _apex;
    private readonly string _mainsub;
    private readonly string _baseDomain;
    private readonly string _publicScheme;
    private readonly string? _publicPort;
    private readonly string _apexOrigin;
    private readonly ConcurrentDictionary<string, string> _idToTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _targetToId = new(StringComparer.OrdinalIgnoreCase);

    public Translator(string publicUrl, int primaryPort, string? mode, IEnumerable<string>? allowHosts)
    {
        var uri = new Uri(publicUrl);
        _publicScheme = uri.Scheme;
        _publicPort = uri.IsDefaultPort ? null : uri.Port.ToString();

        var parts = uri.Host.Split('.');
        var mainsub = parts[0];
        var baseDomain = string.Join('.', parts.Skip(1));
        _apex = mainsub + "." + baseDomain;
        _mainsub = mainsub;
        _baseDomain = baseDomain;

        _primary = "http://localhost:" + primaryPort;
        _mode = string.IsNullOrWhiteSpace(mode) ? "loopback" : mode.Trim().ToLowerInvariant();
        _allow = new HashSet<string>(allowHosts ?? [], StringComparer.OrdinalIgnoreCase);
        _apexOrigin = _publicScheme + "://" + _apex + (_publicPort is null ? "" : ":" + _publicPort);
    }

    public string RewriteRequestUrl(string url)
    {
        Uri uri;
        try { uri = new Uri(url); }
        catch { return url; }
        if (!IsOwnHost(uri.Host)) return url;
        var hostHeader = uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        var target = ResolveTarget(hostHeader);
        if (target is null) return url;
        var path = uri.PathAndQuery;
        return path == "/" ? target : target + path;
    }

    public string RewriteRedirectParams(string urlEncoded)
    {
        if (string.IsNullOrEmpty(urlEncoded)) return urlEncoded;
        var pairs = urlEncoded.Split('&');
        var changed = false;
        for (var i = 0; i < pairs.Length; i++)
        {
            var eq = pairs[i].IndexOf('=');
            if (eq < 0) continue;
            var key = pairs[i][..eq];
            if (!RedirectParams.Contains(Uri.UnescapeDataString(key))) continue;
            var decoded = Uri.UnescapeDataString(pairs[i][(eq + 1)..]);
            var rewritten = RewriteRequestUrl(decoded);
            if (rewritten != decoded)
            {
                if (decoded.EndsWith('/') && !rewritten.EndsWith('/')) rewritten += "/";
                pairs[i] = key + "=" + Uri.EscapeDataString(rewritten);
                changed = true;
            }
        }
        return changed ? string.Join("&", pairs) : urlEncoded;
    }

    private bool IsOwnHost(string host) => TryRouteId(host, out _);

    private bool TryRouteId(string host, out string? id)
    {
        id = null;
        var dot = host.IndexOf('.');
        if (dot < 0) return false;
        var label = host[..dot];
        if (!host[(dot + 1)..].Equals(_baseDomain, StringComparison.OrdinalIgnoreCase)) return false;
        if (label.Equals(_mainsub, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = _mainsub + "--";
        if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            id = label[prefix.Length..];
            return true;
        }
        return false;
    }

    public string? ResolveTarget(string? hostHeader)
    {
        if (string.IsNullOrEmpty(hostHeader)) return _primary;

        var host = hostHeader;
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];

        if (!TryRouteId(host, out var id)) return _primary;
        if (string.IsNullOrEmpty(id)) return _primary;
        return _idToTarget.TryGetValue(id, out var target) ? target : null;
    }

    public string PublicScheme => _publicScheme;

    public bool TranslationEnabled => _mode != "off";

    public bool IsTextResponse(IReadOnlyDictionary<string, string[]> headers)
    {
        if (!headers.TryGetValue("Content-Type", out var values) || values.Length == 0) return false;
        var contentType = values[0].ToLowerInvariant();
        return TextTypes.Any(contentType.Contains);
    }

    public byte[] RewriteBody(byte[] body)
    {
        if (body.Length == 0 || _mode == "off") return body;
        var text = Encoding.UTF8.GetString(body);
        var rewritten = UrlPattern().Replace(text, Rewrite);
        return ReferenceEquals(rewritten, text) ? body : Encoding.UTF8.GetBytes(rewritten);
    }

    public bool RewriteAllHeaders => _mode == "aggressive";

    public string RewriteLocation(string url) => _mode == "off" ? url : UrlPattern().Replace(url, Rewrite);

    private string Rewrite(Match match)
    {
        var matched = match.Value;
        Uri uri;
        try { uri = new Uri(matched); }
        catch { return matched; }

        var host = uri.Host;
        var port = uri.IsDefaultPort ? null : ":" + uri.Port;
        if (!ShouldTranslate(host, port)) return matched;

        var targetBase = uri.Scheme + "://" + CanonicalHost(host) + (port ?? "");
        if (string.Equals(targetBase, _primary, StringComparison.OrdinalIgnoreCase)) return _apexOrigin;
        var id = GetOrAllocateId(targetBase);
        return _publicScheme + "://" + _mainsub + "--" + id + "." + _baseDomain + (_publicPort is null ? "" : ":" + _publicPort);
    }

    // localhost, 127.0.0.1 and [::1] are the same machine, so collapse them onto one
    // canonical host. Equivalent origins then share a single route id, which keeps an
    // OIDC issuer stable no matter which spelling a discovery document happens to use.
    private static string CanonicalHost(string host) =>
        host is "127.0.0.1" or "[::1]" or "::1" ? "localhost" : host;

    private bool ShouldTranslate(string host, string? port)
    {
        if (IsOwnHost(host))
            return false;

        var loopback = host is "localhost" or "127.0.0.1" or "[::1]";
        return _mode switch
        {
            "off" => false,
            "loopback" => loopback,
            "allowlist" => loopback || _allow.Contains(host) || _allow.Contains(host + (port ?? "")),
            "aggressive" => true,
            _ => true,
        };
    }

    private string GetOrAllocateId(string targetBase)
    {
        if (_targetToId.TryGetValue(targetBase, out var existing)) return existing;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetBase))).ToLowerInvariant();
        var id = "r" + hash[..10];
        _idToTarget[id] = targetBase;
        _targetToId[targetBase] = id;
        return id;
    }
}
