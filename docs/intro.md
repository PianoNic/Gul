# What is Gul?

Gul is a minimal, self-hosted devtunnel - the short path between a port on your laptop and a public HTTPS URL. Run one command and whatever is listening on `localhost:3000` becomes reachable at `https://happy-otter.gul.example.com`, TLS and all.

- **One command.** `gul 3000` opens a tunnel and prints the public URL. Ctrl+C closes it.
- **Random or named subdomains.** You get a friendly name like `happy-otter` by default, or claim your own with `--name myapp`.
- **Secured control plane, anonymous visitors.** Opening a tunnel requires a browser OIDC login - only you can expose your machine. The people who visit your tunnel URL are anonymous, exactly like any other public site.
- **No database, no agent.** The server keeps its tunnel table in memory; the client is one small self-contained binary. A tunnel is just a live connection.

Gul is deliberately small: **two .NET projects**, no database, no message broker, no queue. It borrows a single trick - [SignalR client results](https://learn.microsoft.com/aspnet/core/signalr/hubs#client-results) - to turn a persistent client connection into a reverse proxy.

## How a request flows

Gul sits behind an **existing wildcard reverse proxy** that already terminates TLS for `*.gul.example.com` (and the apex `gul.example.com`) and forwards every matching host to the Gul server on one port. When a visitor hits your tunnel URL, the request makes a full round trip down to your laptop and back:

```
browser
  │  GET https://happy-otter.gul.example.com/api/items
  ▼
wildcard reverse proxy        terminates TLS for *.gul.example.com
  │  forwards to gul:8080, Host header preserved
  ▼
Gul.Server                    matches "happy-otter" -> its SignalR connection
  │  ForwardRequest(TunnelRequest)  ──  SignalR client result  ──►
  ▼
gul client                    running on your machine
  │  GET http://localhost:3000/api/items
  ▼
your local app
  │  200 OK
  ▲──  TunnelResponse  ──  back up the same path to the visitor  ──────────
```

Step by step:

1. The browser requests `https://happy-otter.gul.example.com/whatever`.
2. Your reverse proxy terminates TLS and forwards the request - `Host` header intact - to **Gul.Server** on port `8080`.
3. Gul.Server reads the `Host` header, strips the base domain to get the subdomain `happy-otter`, and looks up which SignalR connection owns it.
4. It forwards the request down that connection as a **SignalR client result**: the server invokes `ForwardRequest` on the client and awaits the return value.
5. The **gul client** on your machine receives the request, replays it against `http://localhost:3000`, and returns the response back over the same connection.
6. Gul.Server writes that response to the original visitor.

## Two planes

Gul has a clean split between the connection *you* open and the traffic *visitors* generate. Only the former is authenticated.

| Plane | Who | Authenticated? | Path |
| --- | --- | --- | --- |
| **Control connection** | The `gul` CLI (you) | **Yes** - OIDC bearer token | SignalR hub at `/tunnel` |
| **Public tunnel traffic** | Anonymous visitors | No | Any `*.gul.example.com` host |

The CLI opens the control connection once (after a browser login), registers a subdomain, and holds it open. Every visitor request rides *back down* that same connection. When the CLI exits - or the connection drops - the server removes the subdomain from its in-memory registry and the tunnel goes dark.

## What Gul is not

Gul is a KISS tool, not a platform. A few deliberate ceilings:

- **HTTP(S) only.** Plain WebSocket upgrades on the tunneled app are not proxied - the request/response bodies are buffered, not streamed.
- **In-memory registry.** Subdomain ownership lives in the server's memory. Restart the server and every client simply reconnects and re-registers.
- **No accounts or quotas.** Anyone your OIDC provider lets in can open a tunnel. Authorization is your IdP's job.

## Get started

- **[Self-hosting](./self-host)** - run the server image behind your reverse proxy with `docker compose`.
- **[CLI client](./cli)** - install the `gul` binary and open your first tunnel.
- **[Developer setup](./dev-setup)** - run both projects locally with `dotnet run`.
