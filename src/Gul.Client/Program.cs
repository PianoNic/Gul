using Gul.Client;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintUsage();
    return 0;
}

return args[0] switch
{
    "remote" => Remote(args),
    "login" => await LoginAsync(),
    "logout" => Logout(),
    _ => await TunnelAsync(args),
};

static int Remote(string[] args)
{
    var config = Config.Load();
    string? input = args.Length > 1 ? args[1] : null;

    if (input is null)
    {
        if (!string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            Console.WriteLine(config.ServerUrl);
            return 0;
        }
        Console.Write("Server URL (e.g. https://gul.example.com): ");
        input = Console.ReadLine();
    }

    if (!TryNormalizeServerUrl(input, out var url, out var error))
    {
        Console.Error.WriteLine(error);
        return 1;
    }

    config.ServerUrl = url;
    config.Save();
    Console.WriteLine($"Server set to {Ui.Url(url)}");
    Console.WriteLine("Next: run 'gul login' to sign in.");
    return 0;
}

static bool TryNormalizeServerUrl(string? input, out string normalized, out string error)
{
    normalized = "";
    error = "";
    input = input?.Trim();
    if (string.IsNullOrWhiteSpace(input))
    {
        error = "No URL entered.";
        return false;
    }
    if (!input.Contains("://"))
        input = "https://" + input;

    if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        error = $"'{input}' is not a valid http(s) URL. Example: https://gul.example.com";
        return false;
    }
    if (uri.Host.Length == 0 || (!uri.Host.Contains('.') && uri.Host != "localhost"))
    {
        error = $"'{input}' has no valid host. Example: https://gul.example.com";
        return false;
    }

    normalized = uri.GetLeftPart(UriPartial.Authority);
    return true;
}

static async Task<int> LoginAsync()
{
    var config = Config.Load();
    if (string.IsNullOrWhiteSpace(config.ServerUrl) || !TryNormalizeServerUrl(config.ServerUrl, out _, out _))
    {
        Ui.Err("No valid server configured. Run 'gul remote <url>' first (e.g. gul remote https://gul.example.com).");
        return 1;
    }

    try
    {
        Console.WriteLine("Opening your browser to sign in...");
        var tokens = await OidcLogin.LoginAsync(config.ServerUrl);
        StoreTokens(config, tokens);
        Console.WriteLine(Ui.Green("Login successful."));
        return 0;
    }
    catch (Exception ex)
    {
        Ui.Err($"Login failed: {ex.Message}");
        return 1;
    }
}

static int Logout()
{
    var config = Config.Load();
    config.AccessToken = null;
    config.RefreshToken = null;
    config.ExpiresAtUtc = null;
    config.Save();
    Console.WriteLine("Logged out.");
    return 0;
}

static async Task<int> TunnelAsync(string[] args)
{
    if (!int.TryParse(args[0], out var port) || port is < 1 or > 65535)
    {
        Console.Error.WriteLine($"Unknown command or invalid port: '{args[0]}'.");
        PrintUsage();
        return 1;
    }

    string? name = null;
    string? translate = null;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--name" or "-n":
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--name requires a value.");
                    return 1;
                }
                name = args[++i];
                break;
            case "--translate" or "-t":
                if (i + 1 >= args.Length)
                {
                    Ui.Err("--translate requires a value (all|aggressive|loopback|allowlist|off).");
                    return 1;
                }
                translate = args[++i].ToLowerInvariant();
                if (translate is not ("all" or "aggressive" or "loopback" or "allowlist" or "off"))
                {
                    Ui.Err("--translate must be one of all|aggressive|loopback|allowlist|off.");
                    return 1;
                }
                break;
            case "--no-translate":
                translate = "off";
                break;
            default:
                Console.Error.WriteLine($"Unknown option: {args[i]}");
                return 1;
        }
    }

    var config = Config.Load();
    if (string.IsNullOrWhiteSpace(config.ServerUrl) || !TryNormalizeServerUrl(config.ServerUrl, out _, out _))
    {
        Ui.Err("No valid server configured. Run 'gul remote <url>' first (e.g. gul remote https://gul.example.com).");
        return 1;
    }

    if (!await EnsureTokenAsync(config))
        return 1;

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    if (translate is not null)
        config.Translate = translate;

    var client = new TunnelClient(config, port, name);
    try
    {
        await client.RunAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        Ui.Err($"Tunnel error: {ex.Message}");
        return 1;
    }

    Console.WriteLine("Tunnel closed.");
    return 0;
}

static async Task<bool> EnsureTokenAsync(Config config)
{
    if (!string.IsNullOrWhiteSpace(config.AccessToken)
        && config.ExpiresAtUtc is { } expires
        && expires > DateTime.UtcNow.AddSeconds(30))
    {
        return true;
    }

    if (!string.IsNullOrWhiteSpace(config.RefreshToken))
    {
        try
        {
            var (authority, clientId, _) = await OidcLogin.GetServerConfigAsync(config.ServerUrl!);
            var (_, tokenEndpoint) = await OidcLogin.GetDiscoveryAsync(authority);
            var tokens = await OidcLogin.RefreshAsync(tokenEndpoint, clientId, config.RefreshToken!);
            StoreTokens(config, tokens);
            return true;
        }
        catch
        {
            Console.WriteLine("Session expired; re-authenticating in your browser...");
        }
    }

    try
    {
        var tokens = await OidcLogin.LoginAsync(config.ServerUrl!);
        StoreTokens(config, tokens);
        return true;
    }
    catch (Exception ex)
    {
        Ui.Err($"Login failed: {ex.Message}");
        return false;
    }
}

static void StoreTokens(Config config, OidcLogin.Tokens tokens)
{
    config.AccessToken = tokens.AccessToken;
    config.RefreshToken = tokens.RefreshToken ?? config.RefreshToken;
    config.ExpiresAtUtc = tokens.ExpiresAtUtc;
    config.Save();
}

static void PrintUsage()
{
    Ui.Banner();
    Console.WriteLine(
        """
        Usage:
          gul remote [<url>]         Set (or show) the Gul server URL
          gul login                  Sign in via your browser (OIDC)
          gul logout                 Clear stored credentials
          gul <port> [--name <sub>]  Open a tunnel to http://localhost:<port>
          gul --help                 Show this help

        Options:
          --name <sub>                          Request a specific subdomain
          --translate <all|aggressive|loopback|allowlist|off>  Rewrite local URLs in responses (default all)
          --no-translate                        Disable URL rewriting (same as --translate off)

        Examples:
          gul remote https://gul.example.com
          gul 3000
          gul 8080 --name myapp
        """);
}
