using System.Text;
using System.Text.RegularExpressions;
using Gul.Client;

namespace Gul.Tests;

public class TranslatorTests
{
    private const string PublicUrl = "http://happy-otter.localhost:5080";
    private const int PrimaryPort = 3000;

    private static readonly Regex RouteHostPattern =
        new(@"happy-otter--r[0-9a-f]+\.localhost", RegexOptions.IgnoreCase);

    [Test]
    public void Mode_all_rewrites_loopback_authority_and_preserves_path()
    {
        var t = new Translator(PublicUrl, PrimaryPort, "all", null);
        var input = "before http://localhost:8000/api after";
        var output = Encoding.UTF8.GetString(t.RewriteBody(Encoding.UTF8.GetBytes(input)));

        var expected = new Regex(@"http://happy-otter--r[0-9a-f]+\.localhost:5080/api");
        if (!expected.IsMatch(output))
            throw new Exception($"Expected a rewritten route URL, got: '{output}'");
        if (output.Contains("localhost:8000"))
            throw new Exception($"Output still contains the original authority: '{output}'");
    }

    [Test]
    public void ResolveTarget_maps_generated_route_host_back_to_origin()
    {
        var t = new Translator(PublicUrl, PrimaryPort, "all", null);
        var input = "before http://localhost:8000/api after";
        var output = Encoding.UTF8.GetString(t.RewriteBody(Encoding.UTF8.GetBytes(input)));

        var routeHost = RouteHostPattern.Match(output).Value;
        if (string.IsNullOrEmpty(routeHost))
            throw new Exception($"Could not extract route host from: '{output}'");

        var withoutPort = t.ResolveTarget(routeHost);
        if (withoutPort != "http://localhost:8000")
            throw new Exception($"ResolveTarget('{routeHost}') returned '{withoutPort}'");

        var withPort = t.ResolveTarget(routeHost + ":5080");
        if (withPort != "http://localhost:8000")
            throw new Exception($"ResolveTarget('{routeHost}:5080') returned '{withPort}'");
    }

    [Test]
    public void Loopback_spellings_collapse_to_one_route_and_public_scheme_is_exposed()
    {
        var t = new Translator(PublicUrl, PrimaryPort, "all", null);

        if (t.PublicScheme != "http")
            throw new Exception($"PublicScheme should be 'http', got '{t.PublicScheme}'");
        if (!t.TranslationEnabled)
            throw new Exception("TranslationEnabled should be true for mode 'all'");
        if (new Translator(PublicUrl, PrimaryPort, "off", null).TranslationEnabled)
            throw new Exception("TranslationEnabled should be false for mode 'off'");

        var viaName = Encoding.UTF8.GetString(t.RewriteBody(Encoding.UTF8.GetBytes("x http://localhost:8000/a")));
        var viaIp = Encoding.UTF8.GetString(t.RewriteBody(Encoding.UTF8.GetBytes("x http://127.0.0.1:8000/a")));
        var nameHost = RouteHostPattern.Match(viaName).Value;
        var ipHost = RouteHostPattern.Match(viaIp).Value;
        if (string.IsNullOrEmpty(nameHost) || nameHost != ipHost)
            throw new Exception($"localhost and 127.0.0.1 must share one route host: '{nameHost}' vs '{ipHost}'");

        if (t.ResolveTarget(ipHost) != "http://localhost:8000")
            throw new Exception($"ResolveTarget('{ipHost}') returned '{t.ResolveTarget(ipHost)}'");
    }

    [Test]
    public void RouteTable_persists_and_reloads_across_instances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gul-routes-{Guid.NewGuid():N}.json");
        try
        {
            var first = RouteTable.Load(path);
            var id = first.GetOrAllocateId("http://localhost:8123");

            var reloaded = RouteTable.Load(path);
            if (!reloaded.TryGetTarget(id, out var target) || target != "http://localhost:8123")
                throw new Exception($"reloaded route did not resolve: id='{id}' target='{target}'");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Test]
    public void Translators_sharing_a_route_table_resolve_each_others_routes()
    {
        var routes = new RouteTable();
        var first = new Translator(PublicUrl, PrimaryPort, "all", null, routes);
        var output = Encoding.UTF8.GetString(first.RewriteBody(Encoding.UTF8.GetBytes("x http://localhost:8000/a")));
        var routeHost = RouteHostPattern.Match(output).Value;
        if (string.IsNullOrEmpty(routeHost))
            throw new Exception($"could not extract route host from: '{output}'");

        // A reconnect builds a brand-new Translator; sharing the table, it must still resolve the route
        // instead of forgetting it (which is what forced a hard refresh before).
        var afterReconnect = new Translator(PublicUrl, PrimaryPort, "all", null, routes);
        var resolved = afterReconnect.ResolveTarget(routeHost);
        if (resolved != "http://localhost:8000")
            throw new Exception($"reconnected translator did not resolve shared route: '{resolved}'");
    }

    [Test]
    public void Mode_loopback_leaves_external_hosts_while_mode_all_rewrites_them()
    {
        var input = "img https://cdn.example.com/x end";

        var loopback = new Translator(PublicUrl, PrimaryPort, "loopback", null);
        var loopbackOut = Encoding.UTF8.GetString(loopback.RewriteBody(Encoding.UTF8.GetBytes(input)));
        if (loopbackOut != input)
            throw new Exception($"loopback mode should not rewrite external host, got: '{loopbackOut}'");

        var all = new Translator(PublicUrl, PrimaryPort, "all", null);
        var allOut = Encoding.UTF8.GetString(all.RewriteBody(Encoding.UTF8.GetBytes(input)));
        if (allOut == input)
            throw new Exception($"all mode should rewrite external host, got unchanged: '{allOut}'");
        if (allOut.Contains("cdn.example.com"))
            throw new Exception($"all mode should replace external authority, got: '{allOut}'");
    }

    [Test]
    public void ResolveTarget_apex_returns_primary()
    {
        var t = new Translator(PublicUrl, PrimaryPort, "all", null);
        var target = t.ResolveTarget("happy-otter.localhost:5080");
        if (target != "http://localhost:3000")
            throw new Exception($"ResolveTarget apex returned '{target}'");
    }

    [Test]
    public void Bidirectional_origin_translation()
    {
        var t = new Translator(PublicUrl, 4000, "all", null);
        var input = "a http://localhost:8000/api b http://localhost:4000/ c";
        var output = Encoding.UTF8.GetString(t.RewriteBody(Encoding.UTF8.GetBytes(input)));

        var routeHost = RouteHostPattern.Match(output).Value;
        if (string.IsNullOrEmpty(routeHost))
            throw new Exception($"Could not extract route host from: '{output}'");

        var location = t.RewriteLocation("http://localhost:4000");
        if (location != "http://happy-otter.localhost:5080")
            throw new Exception($"RewriteLocation primary returned '{location}'");

        var apexBack = t.RewriteRequestUrl("http://happy-otter.localhost:5080");
        if (apexBack != "http://localhost:4000")
            throw new Exception($"RewriteRequestUrl apex returned '{apexBack}'");

        var routeBack = t.RewriteRequestUrl("http://" + routeHost + ":5080");
        if (routeBack != "http://localhost:8000")
            throw new Exception($"RewriteRequestUrl route returned '{routeBack}'");

        var external = t.RewriteRequestUrl("https://cdn.example.com/x");
        if (external != "https://cdn.example.com/x")
            throw new Exception($"RewriteRequestUrl external returned '{external}'");

        var star = t.RewriteLocation("*");
        if (star != "*")
            throw new Exception($"RewriteLocation('*') returned '{star}'");
    }

    [Test]
    public void Aggressive_mode_translates_external_and_rewrites_all_headers()
    {
        var t = new Translator(PublicUrl, 4000, "aggressive", null);
        var input = "img https://cdn.example.com/x end";
        var output = Encoding.UTF8.GetString(t.RewriteBody(Encoding.UTF8.GetBytes(input)));
        var expected = new Regex(@"http://happy-otter--r[0-9a-f]+\.localhost:5080");
        if (!expected.IsMatch(output))
            throw new Exception($"aggressive mode should rewrite external host to a route URL, got: '{output}'");
        if (output.Contains("cdn.example.com"))
            throw new Exception($"aggressive mode should replace external authority, got: '{output}'");

        if (!t.RewriteAllHeaders)
            throw new Exception("aggressive mode should have RewriteAllHeaders == true");

        var all = new Translator(PublicUrl, 4000, "all", null);
        if (all.RewriteAllHeaders)
            throw new Exception("all mode should have RewriteAllHeaders == false");

        var loopback = new Translator(PublicUrl, 4000, "loopback", null);
        if (loopback.RewriteAllHeaders)
            throw new Exception("loopback mode should have RewriteAllHeaders == false");
    }

    [Test]
    public void RedirectParams_are_rewritten_inbound()
    {
        var t = new Translator(PublicUrl, 4000, "all", null);

        var apexRedirect = "client_id=web&redirect_uri=" + Uri.EscapeDataString("http://happy-otter.localhost:5080/") + "&scope=openid";
        var apexOut = t.RewriteRedirectParams(apexRedirect);
        var apexPairs = apexOut.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        if (Uri.UnescapeDataString(apexPairs["redirect_uri"]) != "http://localhost:4000/")
            throw new Exception($"redirect_uri not mapped back to primary: '{apexOut}'");
        if (apexPairs["client_id"] != "web")
            throw new Exception($"client_id was altered: '{apexOut}'");
        if (apexPairs["scope"] != "openid")
            throw new Exception($"scope was altered: '{apexOut}'");

        var external = "redirect_uri=" + Uri.EscapeDataString("https://real.example.com/cb");
        var externalOut = t.RewriteRedirectParams(external);
        if (externalOut != external)
            throw new Exception($"non-gul redirect_uri should be unchanged, got: '{externalOut}'");

        var stateParam = "state=" + Uri.EscapeDataString("http://happy-otter.localhost:5080/x");
        var stateOut = t.RewriteRedirectParams(stateParam);
        if (stateOut != stateParam)
            throw new Exception($"non-redirect param should be unchanged, got: '{stateOut}'");
    }
}
