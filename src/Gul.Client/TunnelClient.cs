using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
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
    // The name to request on the next (re)connect. Starts as the user's --name, then holds the
    // subdomain the server actually granted so a reconnect reclaims the same URL instead of a
    // fresh random one. (Race: if the dropped connection's cleanup hasn't freed the name yet,
    // the reclaim loses and a new name is drawn — no worse than before.)
    private string? _requestedName;

    private readonly HttpClient _http;
    private readonly RouteTable _routes;
    private readonly ConcurrentDictionary<string, ClientSocket> _sockets = new(StringComparer.Ordinal);
    // Close signals that arrive before OpenSocket finishes registering the socket, mapped to the
    // tick they arrived; consumed on registration and otherwise evicted once too old to be claimed.
    private readonly ConcurrentDictionary<string, long> _pendingClose = new(StringComparer.Ordinal);
    private Translator? _translator;
    private HubConnection? _connection;

    public TunnelClient(Config config, int port, string? requestedName)
    {
        _config = config;
        _port = port;
        _requestedName = requestedName;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
        _routes = RouteTable.ForPort(port);
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
        _connection = connection;

        connection.On<TunnelRequest, TunnelResponse>("ForwardRequest", ForwardToLocalAsync);
        connection.On<SocketOpen, SocketOpenResult>("OpenSocket", OpenSocketAsync);
        connection.On<SocketFrame>("SocketToLocal", frame => { EnqueueInbound(frame); return Task.CompletedTask; });
        connection.On<string, int, string?>("CloseSocket", CloseSocketFromServerAsync);

        // A dropped control connection orphans every live browser socket; the server has
        // already closed its side, so drop ours and let new ones open after reconnect.
        connection.Reconnecting += _ => { CloseAllSockets(); return Task.CompletedTask; };
        connection.Closed += _ => { CloseAllSockets(); return Task.CompletedTask; };

        connection.Reconnected += async _ =>
        {
            try
            {
                var url = await connection.InvokeAsync<string>("Register", _requestedName, CancellationToken.None);
                _requestedName = SubdomainOf(url) ?? _requestedName;
                _translator = new Translator(url, _port, _config.Translate, _config.TranslateHosts, _routes);
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
            _requestedName = SubdomainOf(publicUrl) ?? _requestedName;
            _translator = new Translator(publicUrl, _port, _config.Translate, _config.TranslateHosts, _routes);
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
            CloseAllSockets();
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

            // A translated route points at a secondary local service (an API, an OIDC provider).
            // Present it with the public origin it's reached through, so absolute URLs it emits —
            // an OIDC discovery `issuer` and the signed token `iss` — already match what the browser
            // uses and need no rewrite. The primary app instead keeps its own localhost Host, since
            // dev servers like Vite reject a foreign Host header.
            var routeForward = _translator is not null && _translator.IsTranslatedRoute(host) && !string.IsNullOrEmpty(host);

            foreach (var (name, values) in req.Headers)
            {
                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) continue;

                if (string.Equals(name, "Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;

                if (HopByHop.Contains(name)) continue;

                // We set forwarding headers ourselves (below) or strip them; never let the edge
                // proxy's copies through to corrupt the local service's view of its own origin.
                if (name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Forwarded", StringComparison.OrdinalIgnoreCase))
                    continue;

                var outValues = values;
                if (_translator is not null
                    && (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase)))
                    outValues = [.. values.Select(_translator.RewriteRequestUrl)];

                if (message.Headers.TryAddWithoutValidation(name, outValues)) continue;
                content ??= new ByteArrayContent(body);
                content.Headers.TryAddWithoutValidation(name, outValues);
            }

            if (routeForward)
            {
                message.Headers.Host = host;
                message.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
                message.Headers.TryAddWithoutValidation("X-Forwarded-Proto", _translator!.PublicScheme);
                message.Headers.TryAddWithoutValidation("X-Forwarded-Port", _translator!.PublicForwardedPort);
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

                if (_translator.RewriteAllHeaders)
                {
                    foreach (var key in headers.Keys.ToArray())
                        headers[key] = headers[key].Select(_translator.RewriteLocation).ToArray();
                }
                else
                {
                    if (headers.TryGetValue("Location", out var location) && location.Length > 0 && !string.IsNullOrEmpty(location[0]))
                        headers["Location"] = [_translator.RewriteLocation(location[0])];

                    if (headers.TryGetValue("Access-Control-Allow-Origin", out var acao) && acao.Length > 0 && !string.IsNullOrEmpty(acao[0]))
                        headers["Access-Control-Allow-Origin"] = [.. acao.Select(_translator.RewriteLocation)];
                }
            }

            // Accept-Encoding was stripped on the way in, so the local app answered in plaintext.
            // gzip text bodies here and let the browser decode natively (Content-Encoding: gzip):
            // the server forwards both untouched, so both internet hops carry the smaller payload.
            if (_translator is not null && _translator.IsTextResponse(headers)
                && responseBody.Length > 1024 && !headers.ContainsKey("Content-Encoding"))
            {
                responseBody = Gzip(responseBody);
                headers["Content-Encoding"] = ["gzip"];
                headers.Remove("Content-Length"); // the server restamps it from the compressed length
            }

            return new TunnelResponse((int)response.StatusCode, headers, responseBody);
        }
        catch (Exception ex)
        {
            return TextResponse(502, $"gul: could not reach {target} ({ex.Message})");
        }
    }

    // --- WebSocket tunneling -------------------------------------------------

    private async Task<SocketOpenResult> OpenSocketAsync(SocketOpen open)
    {
        var target = _translator is null ? $"http://localhost:{_port}" : _translator.ResolveTarget(open.Host);
        if (target is null)
            return new SocketOpenResult(false, null, $"gul: unknown route for {open.Host}");

        Uri wsUri;
        try { wsUri = BuildWsUri(target, open.Path); }
        catch (Exception ex) { return new SocketOpenResult(false, null, ex.Message); }

        var cws = new ClientWebSocket();
        foreach (var sub in open.SubProtocols)
            cws.Options.AddSubProtocol(sub);

        try
        {
            await cws.ConnectAsync(wsUri, CancellationToken.None);
        }
        catch (Exception ex)
        {
            cws.Dispose();
            return new SocketOpenResult(false, null, $"gul: could not reach {wsUri} ({ex.Message})");
        }

        var socket = new ClientSocket(cws);
        _sockets[open.SocketId] = socket;
        socket.InboundPump = Task.Run(() => PumpInboundAsync(socket));
        _ = Task.Run(() => PumpLocalToPublicAsync(open.SocketId, socket));

        // A close may have arrived while we were still connecting; honour it now (double-checked
        // against CloseSocketFromServerAsync so whichever runs second wins).
        if (_pendingClose.TryRemove(open.SocketId, out _))
            _ = TeardownAsync(open.SocketId, socket, WebSocketCloseStatus.NormalClosure, null, drain: true, notifyServer: false);

        return new SocketOpenResult(true, string.IsNullOrEmpty(cws.SubProtocol) ? null : cws.SubProtocol, null);
    }

    // Server -> local. A single ordered reader keeps frames in the order the browser sent them,
    // even if the SignalR client dispatches the handlers concurrently. The channel is bounded so
    // a flood from a visitor that outruns the local app tears down that one socket instead of
    // growing the CLI's memory without limit.
    private void EnqueueInbound(SocketFrame frame)
    {
        if (_sockets.TryGetValue(frame.SocketId, out var socket)
            && !socket.Inbound.Writer.TryWrite((frame.IsText, frame.Data)))
            _ = TeardownAsync(frame.SocketId, socket, WebSocketCloseStatus.InternalServerError, "local backpressure", drain: false, notifyServer: true);
    }

    private static async Task PumpInboundAsync(ClientSocket socket)
    {
        try
        {
            await foreach (var (isText, data) in socket.Inbound.Reader.ReadAllAsync(socket.Cts.Token))
            {
                if (socket.Ws.State != WebSocketState.Open) break;
                await socket.SendLock.WaitAsync(socket.Cts.Token);
                try
                {
                    if (socket.Ws.State != WebSocketState.Open) break;
                    await socket.Ws.SendAsync(
                        data,
                        isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                        endOfMessage: true,
                        socket.Cts.Token);
                }
                finally { socket.SendLock.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* the local socket went away; the outbound pump handles teardown */ }
    }

    private async Task PumpLocalToPublicAsync(string socketId, ClientSocket socket)
    {
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();
        var status = WebSocketCloseStatus.NormalClosure;
        string? reason = null;
        try
        {
            while (socket.Ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult recv;
                do
                {
                    recv = await socket.Ws.ReceiveAsync(buffer, socket.Cts.Token);
                    if (recv.MessageType == WebSocketMessageType.Close)
                    {
                        status = socket.Ws.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                        reason = socket.Ws.CloseStatusDescription;
                        await CloseLocalAsync(socket, status, reason);
                        return;
                    }
                    message.Write(buffer, 0, recv.Count);
                } while (!recv.EndOfMessage);

                if (_connection is not null)
                    await _connection.SendAsync(
                        "SocketToPublic",
                        new SocketFrame(socketId, recv.MessageType == WebSocketMessageType.Text, message.ToArray()),
                        socket.Cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            status = WebSocketCloseStatus.InternalServerError;
            reason = ex.Message;
        }
        finally
        {
            await TeardownAsync(socketId, socket, status, reason, drain: false, notifyServer: true);
        }
    }

    private async Task CloseSocketFromServerAsync(string socketId, int status, string? reason)
    {
        if (_sockets.TryGetValue(socketId, out var socket))
        {
            await TeardownAsync(socketId, socket, (WebSocketCloseStatus)status, reason, drain: true, notifyServer: false);
            return;
        }
        // OpenSocket is still in flight; leave a tombstone and re-check for the registration.
        _pendingClose[socketId] = Environment.TickCount64;
        PruneTombstones();
        if (_sockets.TryGetValue(socketId, out socket) && _pendingClose.TryRemove(socketId, out _))
            await TeardownAsync(socketId, socket, (WebSocketCloseStatus)status, reason, drain: true, notifyServer: false);
    }

    // Drop tombstones older than any OpenSocket could still be in flight (the server caps the open
    // at 30s), so garbage-collecting stale entries can never wipe one a live open is about to claim.
    private void PruneTombstones()
    {
        var cutoff = Environment.TickCount64 - 60_000;
        foreach (var (id, tick) in _pendingClose)
            if (tick < cutoff)
                _pendingClose.TryRemove(id, out _);
    }

    // The one teardown path for a single socket. `drain` flushes buffered browser->local frames to
    // the local app before a graceful close (browser/server-initiated); otherwise it aborts at once.
    private async Task TeardownAsync(string socketId, ClientSocket socket, WebSocketCloseStatus status, string? reason, bool drain, bool notifyServer)
    {
        if (!socket.MarkClosed()) return;
        _sockets.TryRemove(socketId, out _);
        socket.Inbound.Writer.TryComplete();

        if (drain)
        {
            var flushed = false;
            try { await socket.InboundPump.WaitAsync(TimeSpan.FromSeconds(5)); flushed = true; } catch { }
            // Send-only close (never CloseAsync): PumpLocalToPublicAsync may still have a
            // ReceiveAsync outstanding, and CloseAsync also receives the ack, which would collide.
            if (flushed)
                await CloseLocalAsync(socket, status, reason);
        }

        socket.Cts.Cancel();
        try { socket.Ws.Abort(); } catch { }

        if (notifyServer && _connection is not null)
        {
            try { await _connection.SendAsync("SocketClosed", socketId, (int)SafeStatus(status), reason); }
            catch { /* the tunnel connection may already be gone */ }
        }
    }

    private void CloseAllSockets()
    {
        foreach (var (id, socket) in _sockets)
        {
            _sockets.TryRemove(id, out _);
            if (!socket.MarkClosed()) continue;
            socket.Inbound.Writer.TryComplete();
            socket.Cts.Cancel();
            try { socket.Ws.Abort(); } catch { }
        }
        _pendingClose.Clear();
    }

    // First DNS label of the granted URL — the registered subdomain, which is a single label
    // (letters/digits/hyphen, no dots), so everything before the first '.' is it.
    private static string? SubdomainOf(string url)
    {
        try { return new Uri(url).Host.Split('.')[0]; } catch { return null; }
    }

    private static Uri BuildWsUri(string target, string path)
    {
        var builder = new UriBuilder(target)
        {
            Scheme = target.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        };
        return new Uri(builder.Uri, path);
    }

    private static WebSocketCloseStatus SafeStatus(WebSocketCloseStatus status)
    {
        var code = (int)status;
        return code is 1005 or 1006 || code is < 1000 or > 4999
            ? WebSocketCloseStatus.NormalClosure
            : status;
    }

    // A close reason is capped at 123 UTF-8 bytes by the protocol.
    private static string? Trim(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return reason;
        return Encoding.UTF8.GetByteCount(reason) <= 123 ? reason : reason[..Math.Min(reason.Length, 60)];
    }

    // Send-only close of the local socket, serialized with data sends via the socket's send lock.
    private static async Task CloseLocalAsync(ClientSocket socket, WebSocketCloseStatus status, string? reason)
    {
        if (!await socket.SendLock.WaitAsync(TimeSpan.FromSeconds(5))) return;
        try
        {
            if (socket.Ws.State == WebSocketState.Open)
                await socket.Ws.CloseOutputAsync(SafeStatus(status), Trim(reason), CancellationToken.None);
        }
        catch { /* already closing */ }
        finally { socket.SendLock.Release(); }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest))
            gz.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static TunnelResponse TextResponse(int status, string message)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = ["text/plain; charset=utf-8"],
        };
        return new TunnelResponse(status, headers, Encoding.UTF8.GetBytes(message));
    }

    private sealed class ClientSocket(ClientWebSocket ws)
    {
        private const int InboundCapacity = 256;
        private int _closed;

        public ClientWebSocket Ws { get; } = ws;
        public CancellationTokenSource Cts { get; } = new();
        public Task InboundPump { get; set; } = Task.CompletedTask;
        // A WebSocket permits only one send at a time; data frames and the close frame to the
        // local app come from different tasks, so every send to Ws goes through this lock.
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public Channel<(bool IsText, byte[] Data)> Inbound { get; } =
            Channel.CreateBounded<(bool, byte[])>(new BoundedChannelOptions(InboundCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        public bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;
    }
}
