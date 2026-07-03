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
    private readonly string _suffix;
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
        _suffix = "." + _apex;

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

    private bool IsOwnHost(string host) => host.Equals(_apex, StringComparison.OrdinalIgnoreCase) || host.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase);

    public string? ResolveTarget(string? hostHeader)
    {
        if (string.IsNullOrEmpty(hostHeader)) return _primary;

        var host = hostHeader;
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];

        if (host.Equals(_apex, StringComparison.OrdinalIgnoreCase)) return _primary;
        if (host.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase))
        {
            var id = host[..^_suffix.Length];
            if (id.Length == 0) return _primary;
            return _idToTarget.TryGetValue(id, out var target) ? target : null;
        }
        return _primary;
    }

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

        var targetBase = uri.Scheme + "://" + host + (port ?? "");
        if (string.Equals(targetBase, _primary, StringComparison.OrdinalIgnoreCase)) return _apexOrigin;
        var id = GetOrAllocateId(targetBase);
        return _publicScheme + "://" + id + _suffix + (_publicPort is null ? "" : ":" + _publicPort);
    }

    private bool ShouldTranslate(string host, string? port)
    {
        if (host.Equals(_apex, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var loopback = host is "localhost" or "127.0.0.1" or "[::1]";
        return _mode switch
        {
            "off" => false,
            "loopback" => loopback,
            "allowlist" => loopback || _allow.Contains(host) || _allow.Contains(host + (port ?? "")),
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
