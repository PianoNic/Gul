using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Gul.Server;

/// <summary>
/// One browser WebSocket and the buffered stream of frames on their way out to it.
///
/// The bridge is registered <em>before</em> the CLI is asked to open its local socket, so a
/// frame the local app pushes on connect (a Socket.IO/SockJS open packet, a welcome banner)
/// is buffered instead of dropped while the browser handshake is still completing. A single
/// writer task drains the channel to the browser, so <see cref="TryEnqueue"/> and close both
/// return immediately — a slow or wedged browser can never block a hub invocation or the
/// tunnel's disconnect handling, and its own socket is dropped once the buffer fills.
/// </summary>
public sealed class WebSocketBridge
{
    private const int OutboundCapacity = 256;

    private readonly Channel<(bool IsText, byte[] Data)> _outbound =
        Channel.CreateBounded<(bool, byte[])>(new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private WebSocketCloseStatus _closeStatus = WebSocketCloseStatus.NormalClosure;
    private string? _closeReason;
    private int _closeRequested;
    private int _clientClosed;
    private Task _writer = Task.CompletedTask;

    public string ConnectionId { get; }

    public WebSocketBridge(string connectionId) => ConnectionId = connectionId;

    /// <summary>Completes when the writer has flushed and closed the browser socket. Await before disposing it.</summary>
    public Task Completion => _writer;

    /// <summary>True once the CLI reported its own close, so the server needn't echo a CloseSocket back to it.</summary>
    public bool CloseOriginatedFromClient => Volatile.Read(ref _clientClosed) == 1;

    public void MarkClientClosed() => Interlocked.Exchange(ref _clientClosed, 1);

    /// <summary>Queue a client-&gt;browser frame. Returns false if the socket is closing or the buffer is full.</summary>
    public bool TryEnqueue(bool isText, byte[] data) =>
        Volatile.Read(ref _closeRequested) == 0 && _outbound.Writer.TryWrite((isText, data));

    /// <summary>Attach the accepted browser socket and start draining buffered + live frames to it.</summary>
    public void AttachBrowser(WebSocket browser) => _writer = RunWriterAsync(browser);

    /// <summary>Ask the writer to flush what's buffered and then close the browser. Non-blocking and idempotent.</summary>
    public void RequestClose(WebSocketCloseStatus status, string? reason)
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0) return;
        _closeStatus = status;
        _closeReason = reason;
        _outbound.Writer.TryComplete();
    }

    private async Task RunWriterAsync(WebSocket browser)
    {
        try
        {
            await foreach (var (isText, data) in _outbound.Reader.ReadAllAsync())
            {
                if (browser.State != WebSocketState.Open) break;
                using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await browser.SendAsync(
                    data,
                    isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                    endOfMessage: true,
                    sendCts.Token);
            }
        }
        catch { /* the browser went away or a send stalled past the timeout */ }
        finally
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (browser.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await browser.CloseOutputAsync(WebSocketStatus.Sanitize(_closeStatus), WebSocketStatus.Trim(_closeReason), closeCts.Token);
            }
            catch { /* unresponsive browser; fall through to Abort */ }
            try { browser.Abort(); } catch { }
        }
    }
}

public sealed class WebSocketBridgeRegistry
{
    private readonly ConcurrentDictionary<string, WebSocketBridge> _bridges = new(StringComparer.Ordinal);

    public void Add(string socketId, WebSocketBridge bridge) => _bridges[socketId] = bridge;

    public bool TryGet(string socketId, out WebSocketBridge bridge) => _bridges.TryGetValue(socketId, out bridge!);

    public void Remove(string socketId) => _bridges.TryRemove(socketId, out _);

    public IReadOnlyList<KeyValuePair<string, WebSocketBridge>> ByConnection(string connectionId) =>
        _bridges.Where(kv => kv.Value.ConnectionId == connectionId).ToArray();
}

public static class WebSocketStatus
{
    // 1005 (NoStatusReceived) and 1006 (AbnormalClosure) are receive-only sentinels and
    // must never be sent on the wire; anything outside 1000-4999 is invalid too.
    public static WebSocketCloseStatus Sanitize(WebSocketCloseStatus status)
    {
        var code = (int)status;
        return code is 1005 or 1006 || code is < 1000 or > 4999
            ? WebSocketCloseStatus.NormalClosure
            : status;
    }

    // A close reason is capped at 123 UTF-8 bytes by the protocol.
    public static string? Trim(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return reason;
        return System.Text.Encoding.UTF8.GetByteCount(reason) <= 123 ? reason : reason[..Math.Min(reason.Length, 60)];
    }
}
