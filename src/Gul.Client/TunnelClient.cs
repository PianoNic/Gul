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

    private readonly HttpClient _local;

    public TunnelClient(Config config, int port, string? requestedName)
    {
        _config = config;
        _port = port;
        _requestedName = requestedName;
        _local = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://localhost:{port}"),
        };
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
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(req.Method), req.Path);

            var body = req.Body ?? [];
            HttpContent? content = body.Length > 0 ? new ByteArrayContent(body) : null;

            foreach (var (name, values) in req.Headers)
            {
                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) continue;

                if (HopByHop.Contains(name)) continue;

                if (message.Headers.TryAddWithoutValidation(name, values)) continue;
                content ??= new ByteArrayContent(body);
                content.Headers.TryAddWithoutValidation(name, values);
            }

            message.Content = content;

            using var response = await _local.SendAsync(message);
            var responseBody = await response.Content.ReadAsByteArrayAsync();

            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers) headers[h.Key] = [.. h.Value];
            foreach (var h in response.Content.Headers) headers[h.Key] = [.. h.Value];

            return new TunnelResponse((int)response.StatusCode, headers, responseBody);
        }
        catch (Exception ex)
        {
            var body = Encoding.UTF8.GetBytes($"gul: could not reach http://localhost:{_port} ({ex.Message})");
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = ["text/plain; charset=utf-8"],
            };
            return new TunnelResponse(502, headers, body);
        }
    }
}
