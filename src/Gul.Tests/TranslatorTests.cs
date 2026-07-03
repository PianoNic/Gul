using System.Text;
using System.Text.RegularExpressions;
using Gul.Client;

namespace Gul.Tests;

public class TranslatorTests
{
    private const string PublicUrl = "http://happy-otter.localhost:5080";
    private const int PrimaryPort = 3000;

    private static readonly Regex RouteHostPattern =
        new(@"r[0-9a-f]+\.happy-otter\.localhost", RegexOptions.IgnoreCase);

    [Test]
    public void Mode_all_rewrites_loopback_authority_and_preserves_path()
    {
        var t = new Translator(PublicUrl, PrimaryPort, "all", null);
        var input = "before http://localhost:8000/api after";
        var output = Encoding.UTF8.GetString(t.RewriteBody(Encoding.UTF8.GetBytes(input)));

        var expected = new Regex(@"http://r[0-9a-f]+\.happy-otter\.localhost:5080/api");
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
}
