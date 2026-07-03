# What is Gul?

Gul is a self-hosted devtunnel that puts your **whole local stack on one public URL**, not just one port. Run one command and whatever is listening on `localhost:3000` becomes reachable at `https://happy-otter.gul.example.com`, TLS and all. Then keep going, because gul carries your entire multi-service setup through that single tunnel.

- **One command.** `gul 3000` opens a tunnel and prints the public URL. Ctrl+C closes it.
- **Your whole stack on one URL.** The auto-router translator rewrites cross-service local URLs in your app's responses into gul routes on the fly, and forwards them back to the right local port, so a multi-service setup works through a single tunnel with zero code changes. See [Auto-router translator](./translator).
- **CORS and OIDC just work.** Cross-service browser calls succeed and self-hosted OIDC logins go straight through the tunnel, with no config on your side.
- **Random or named subdomains.** You get a friendly name like `happy-otter` by default, or claim your own with `--name myapp`.
- **Secured control plane, anonymous visitors.** Opening a tunnel requires a browser OIDC login, so only you can expose your machine. The people who visit your tunnel URL are anonymous, exactly like any other public site.
- **No database, no agent.** The server keeps its tunnel table in memory, and the client is one small self-contained binary. A tunnel is just a live connection.

Gul is deliberately small: **two .NET projects**, no database, no message broker, no queue. It borrows a single trick, [SignalR client results](https://learn.microsoft.com/aspnet/core/signalr/hubs#client-results), to turn a persistent client connection into a reverse proxy.

## Why Gul

Every other local tunnel (ngrok, cloudflared, localtunnel) exposes a **single port**. That is fine until your dev setup is more than one service, and then everything breaks the moment it goes through the tunnel. A frontend calls `http://localhost:8000` and hits nothing. The browser blocks the call for a cross-origin violation. Your login bounces off the OIDC provider because the redirect no longer matches. Gul fixes all three, automatically. These are the things no single-port tunnel can do.

### 1. Auto-router translator

This is the flagship, and no other local tunnel does it. One tunnel exposes your entire multi-service dev setup. Gul rewrites cross-service local URLs (a frontend on `:3000` calling `http://localhost:8000`) into gul routes on the fly, and routes them back to the right port. Your whole stack works through one URL with zero code changes.

### 2. CORS just works

Because your services now sit on different gul origins, browsers would normally block the calls between them. Gul does bidirectional origin translation. It rewrites `Origin` and `Referer` inbound to the local origin, and `Access-Control-Allow-Origin` outbound to the gul origin, so the cross-service calls just succeed.

### 3. OIDC just works

Apps behind a self-hosted OIDC provider (Keycloak, Authentik, Zitadel, Pocket ID, Dex, and any standard OAuth2 or OIDC server) log in straight through the tunnel with zero provider config. Gul rewrites `redirect_uri` and `post_logout_redirect_uri` inbound to the localhost callback the provider already allows, and the login callback lands back in the tunnel via the `Location` rewrite. Cloud providers (Auth0, Okta, Google, Entra, Cognito) just need the gul public URL whitelisted once, so use a stable `--name`.

### How it compares

| | Single-port tunnels (ngrok, cloudflared, localtunnel) | Gul |
| --- | --- | --- |
| **Exposes** | One port | Your whole multi-service stack on one URL |
| **Cross-service links** | Break, you rewrite them by hand | Rewritten automatically by the translator |
| **CORS** | Blocked, you reconfigure every service | Bidirectional origin translation, just works |
| **Self-hosted OIDC login** | Breaks on redirect mismatch | Rewritten inbound, zero provider config |
| **Hosting** | Someone else's servers see your traffic | Self-hosted, you own the domain and the data |

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
2. Your reverse proxy terminates TLS and forwards the request, `Host` header intact, to **Gul.Server** on port `8080`.
3. Gul.Server reads the `Host` header, strips the base domain to get the subdomain `happy-otter`, and looks up which SignalR connection owns it.
4. It forwards the request down that connection as a **SignalR client result**: the server invokes `ForwardRequest` on the client and awaits the return value.
5. The **gul client** on your machine receives the request, replays it against `http://localhost:3000`, and returns the response back over the same connection.
6. Gul.Server writes that response to the original visitor.

## Two planes

Gul has a clean split between the connection *you* open and the traffic *visitors* generate. Only the former is authenticated.

| Plane | Who | Authenticated? | Path |
| --- | --- | --- | --- |
| **Control connection** | The `gul` CLI (you) | **Yes** (OIDC bearer token) | SignalR hub at `/tunnel` |
| **Public tunnel traffic** | Anonymous visitors | No | Any `*.gul.example.com` host |

The CLI opens the control connection once (after a browser login), registers a subdomain, and holds it open. Every visitor request rides *back down* that same connection. When the CLI exits, or the connection drops, the server removes the subdomain from its in-memory registry and the tunnel goes dark.

## What Gul is not

Gul is a KISS tool, not a platform. A few deliberate ceilings:

- **HTTP(S) only.** Plain WebSocket upgrades on the tunneled app are not proxied. The request/response bodies are buffered, not streamed.
- **In-memory registry.** Subdomain ownership lives in the server's memory. Restart the server and every client simply reconnects and re-registers.
- **No accounts or quotas.** Anyone your OIDC provider lets in can open a tunnel. Authorization is your IdP's job.

## Get started

- **[Self-hosting](./self-host)**. Run the server image behind your reverse proxy with `docker compose`.
- **[CLI client](./cli)**. Install the `gul` binary and open your first tunnel.
- **[Auto-router translator](./translator)**. Run a whole multi-service stack through one tunnel.
- **[Developer setup](./dev-setup)**. Run both projects locally with `dotnet run`.
