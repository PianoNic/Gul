<p align="center">
  <img src="assets/gul-icon.svg" width="160" alt="Gul logo" />
</p>
<p align="center">
  <strong>Gul</strong> <sub>(굴 — Korean for tunnel/burrow/cave)</sub><br/>
  Instant public HTTPS URLs for your localhost. A minimal, self-hosted devtunnel.
</p>
<p align="center">
  <a href="https://github.com/PianoNic/gul"><img src="https://img.shields.io/badge/Self--Host-Instructions-0B0F14.svg?labelColor=0B0F14&color=30363D" alt="Self-hosting" /></a>
  <img src="https://img.shields.io/badge/.NET-10-0B0F14.svg?labelColor=0B0F14&color=30363D" alt=".NET 10" />
  <img src="https://img.shields.io/badge/SignalR-client--results-0B0F14.svg?labelColor=0B0F14&color=30363D" alt="SignalR" />
</p>

---

> **Heads up:** Gul is in early development. Expect rough edges and breaking changes between versions.

## What is Gul?

Gul is a tiny, ngrok-style devtunnel you host yourself. Run one command — `gul 3000` — and a
public HTTPS URL like `https://happy-otter.gul.example.com` forwards straight to a server running
on your machine. No inbound firewall holes, no port forwarding: the CLI holds one outbound
connection open and the server pushes each public request down it.

- **One binary CLI.** A self-contained single-file executable per OS. Only dependency is SignalR.
- **No database, no frontend.** Two projects, KISS. The server keeps the tunnel registry in memory.
- **OIDC-protected control plane.** Opening a tunnel requires a browser login (Auth Code + PKCE).
  Public visitors to your tunnel stay anonymous.
- **Behind your own proxy.** Gul rides on an existing wildcard reverse proxy that already
  terminates TLS for `*.gul.example.com`.

## How it works

```
   public visitor                                                     you
        │                                                              │
        │  GET https://happy-otter.gul.example.com/whatever      gul 3000  (OIDC browser login)
        ▼                                                              │
 ┌──────────────────────┐                                             ▼
 │  wildcard reverse     │        forwards Host + request      ┌──────────────┐   SignalR    ┌────────────┐
 │  proxy (TLS for       │ ─────────────────────────────────► │  Gul.Server   │ ◄══════════► │ gul client │
 │  *.gul.example.com)    │                                     │  (in-memory   │  client      │ (your box) │
 └──────────────────────┘                                     │   registry)   │  result      └─────┬──────┘
        ▲                                                      └──────────────┘                     │
        │                     TunnelResponse ◄───────────────────────────────────────────────      ▼
        └──────────────────────────────────────────────────────────────────────  http://localhost:3000
```

1. The CLI authenticates via OIDC, opens a SignalR connection to `Gul.Server`, and is assigned a
   subdomain (random friendly name, or `--name yours`).
2. A visitor hits `https://<sub>.gul.example.com`. Your wildcard proxy forwards it to `Gul.Server`.
3. `Gul.Server` reads the `Host` header, finds the connection that owns `<sub>`, and invokes
   `ForwardRequest` on that client (a SignalR **client result**) — awaiting the response.
4. The client re-issues the request to `http://localhost:3000` and returns the response back over
   the connection. The server writes it to the original visitor.

## Use it (CLI)

Download the `gul` binary for your OS from the [latest release](https://github.com/PianoNic/gul/releases),
put it on your `PATH` (on Unix: `chmod +x gul-*`), then:

```sh
gul setup                  # store your server URL, e.g. https://gul.example.com
gul login                  # browser OIDC login (stores tokens)
gul 3000                   # open a tunnel to http://localhost:3000, prints the public URL
gul 3000 --name myapp      # request a custom subdomain (myapp.gul.example.com) if it is free
gul logout                 # clear stored tokens
```

Config lives at `~/.gul/config.json` (server URL + tokens).

## Self-host the server

Gul.Server runs as a single container and expects an **existing** wildcard reverse proxy to
terminate TLS and forward both `*.gul.example.com` (tunnels) and the apex `gul.example.com`
(control plane) to it on port `8080`.

```sh
docker compose up -d
```

Caddy example for the proxy you already run:

```
*.gul.example.com, gul.example.com {
    reverse_proxy gul:8080
}
```

### Server environment variables

| Variable                   | Default                | Description                                                        |
| -------------------------- | ---------------------- | ------------------------------------------------------------------ |
| `Gul__BaseDomain`          | —                      | The zone tunnels live under, e.g. `gul.example.com`.                |
| `Oidc__Authority`          | —                      | OIDC issuer URL. The CLI control connection validates tokens here. |
| `Oidc__ClientId`           | —                      | Public (PKCE, no secret) client id used by the CLI.                |
| `Oidc__Scopes`             | `openid profile email` | Space-separated scopes requested at login.                         |
| `Oidc__RequireHttpsMetadata` | `true`               | Set `false` only for local/dev issuers over HTTP.                  |

Register the OIDC client as a **public** client (PKCE, no secret) whose allowed redirect URIs
include `http://127.0.0.1/*` and `http://localhost/*` for the CLI's loopback login.

## Develop

Two projects, no database, no migrations:

```sh
dotnet run --project src/Gul.Server              # the tunnel server (listens on :8080)
dotnet run --project src/Gul.Client -- 3000      # the CLI, tunnelling localhost:3000
```

- `src/Gul.Server` — ASP.NET Core: the `/tunnel` SignalR hub + a terminal forwarding middleware.
- `src/Gul.Client` — self-contained console CLI.

Full documentation: the `docs/` folder (VitePress).

## License

TBD.

---

<p align="center">Made with care by <a href="https://github.com/PianoNic">PianoNic</a></p>
