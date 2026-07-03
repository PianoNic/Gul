using System.Text;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gul.Client;

public sealed class TunnelClient
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade",
        "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Content-Length",
    };

    private readonly Config _config;
    private readonly int _port;
    private readonly string? _requestedName;

    private readonly HttpClient _http;
    private Translator? _translator;

    public TunnelClient(Config config, int port, string? requestedName)
    {
        _config = config;
        _port = port;
        _requestedName = requestedName;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var hubUrl = _config.ServerUrl!.TrimEnd('/') + "/tunnel";

        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(_config.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        connection.On<TunnelRequest, TunnelResponse>("ForwardRequest", ForwardToLocalAsync);

        connection.Reconnected += async _ =>
        {
            try
            {
                var url = await connection.InvokeAsync<string>("Register", _requestedName, CancellationToken.None);
                _translator = new Translator(url, _port, _config.Translate, _config.TranslateHosts);
                Console.WriteLine($"{Ui.Green("Reconnected.")}  {Ui.Url(url)}  {Ui.Dim("->")}  {Ui.Dim($"http://localhost:{_port}")}");
            }
            catch (Exception ex)
            {
                Ui.Err($"Re-registration after reconnect failed: {ex.Message}");
            }
        };

        await connection.StartAsync(ct);
        try
        {
            var publicUrl = await connection.InvokeAsync<string>("Register", _requestedName, ct);
            _translator = new Translator(publicUrl, _port, _config.Translate, _config.TranslateHosts);
            Console.WriteLine();
            Console.WriteLine($"  {Ui.Badge}  {Ui.Green("Tunnel live")}");
            Console.WriteLine($"  {Ui.Url(publicUrl)}  {Ui.Dim("->")}  {Ui.Dim($"http://localhost:{_port}")}");
            Console.WriteLine();
            Console.WriteLine(Ui.Dim("  Press Ctrl+C to stop."));

            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private async Task<TunnelResponse> ForwardToLocalAsync(TunnelRequest req)
    {
        var host = req.Headers.TryGetValue("Host", out var hostValues) && hostValues.Length > 0 ? hostValues[0] : null;
        var target = _translator is null ? $"http://localhost:{_port}" : _translator.ResolveTarget(host);
        if (target is null)
            return TextResponse(502, $"gul: unknown route for {host}");

        try
        {
            var path = req.Path;
            if (_translator is not null)
            {
                var qIndex = path.IndexOf('?');
                if (qIndex >= 0)
                {
                    var pathPart = path[..qIndex];
                    var query = _translator.RewriteRedirectParams(path[(qIndex + 1)..]);
                    path = pathPart + "?" + query;
                }
            }

            using var message = new HttpRequestMessage(new HttpMethod(req.Method), new Uri(new Uri(target), path));

            var body = req.Body ?? [];
            if (_translator is not null && body.Length > 0)
            {
                var contentType = req.Headers
                    .FirstOrDefault(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    .Value?.FirstOrDefault();
                if (contentType is not null && contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    var formStr = Encoding.UTF8.GetString(body);
                    var rewrittenForm = _translator.RewriteRedirectParams(formStr);
                    if (rewrittenForm != formStr) body = Encoding.UTF8.GetBytes(rewrittenForm);
                }
            }

            HttpContent? content = body.Length > 0 ? new ByteArrayContent(body) : null;

            foreach (var (name, values) in req.Headers)
            {
                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) continue;

                if (HopByHop.Contains(name)) continue;

                var outValues = values;
                if (_translator is not null
                    && (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase)))
                    outValues = [.. values.Select(_translator.RewriteRequestUrl)];

                if (message.Headers.TryAddWithoutValidation(name, outValues)) continue;
                content ??= new ByteArrayContent(body);
                content.Headers.TryAddWithoutValidation(name, outValues);
            }

            message.Content = content;

            using var response = await _http.SendAsync(message);
            var responseBody = await response.Content.ReadAsByteArrayAsync();

            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers) headers[h.Key] = [.. h.Value];
            foreach (var h in response.Content.Headers) headers[h.Key] = [.. h.Value];

            if (_translator is not null)
            {
                if (_translator.IsTextResponse(headers))
                    responseBody = _translator.RewriteBody(responseBody);

                if (headers.TryGetValue("Location", out var location) && location.Length > 0 && !string.IsNullOrEmpty(location[0]))
                    headers["Location"] = [_translator.RewriteLocation(location[0])];

                if (headers.TryGetValue("Access-Control-Allow-Origin", out var acao) && acao.Length > 0 && !string.IsNullOrEmpty(acao[0]))
                    headers["Access-Control-Allow-Origin"] = [.. acao.Select(_translator.RewriteLocation)];
            }

            return new TunnelResponse((int)response.StatusCode, headers, responseBody);
        }
        catch (Exception ex)
        {
            return TextResponse(502, $"gul: could not reach {target} ({ex.Message})");
        }
    }

    private static TunnelResponse TextResponse(int status, string message)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = ["text/plain; charset=utf-8"],
        };
        return new TunnelResponse(status, headers, Encoding.UTF8.GetBytes(message));
    }
}
