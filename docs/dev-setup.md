# Gul Developer Setup

This is what a fresh checkout needs to run both halves of Gul locally. There's no database, no migrations, and no frontend build - two .NET projects and a `dotnet run`.

## Prerequisites

- **.NET 10 SDK** (both projects target `net10.0`)
- **An OIDC provider** for the login flow (any public/PKCE client - Pocket ID, Authentik, Keycloak, Auth0…). You only need this to exercise `gul login`.
- A local app to expose (anything on a port - a dev server, `python -m http.server 3000`, etc.).

That's it. No Docker is required for local development; the container image only matters when you [self-host](./self-host.md).

## The two projects

```
Gul.slnx
└── src/
    ├── Gul.Server/      ASP.NET Core (Microsoft.NET.Sdk.Web) - the tunnel hub + forwarding proxy
    └── Gul.Client/      Console app (Microsoft.NET.Sdk, OutputType Exe) - the `gul` CLI
```

| Project | SDK | Role |
| --- | --- | --- |
| **Gul.Server** | `Microsoft.NET.Sdk.Web` | Hosts the SignalR hub at `/tunnel`, keeps the in-memory subdomain registry, and forwards public requests down the owning connection. |
| **Gul.Client** | `Microsoft.NET.Sdk` (Exe) | The CLI: OIDC login, opens the hub connection, replays forwarded requests against `localhost`. |

The wire contract (`TunnelRequest` / `TunnelResponse`) and the hub method names (`Register`, `ForwardRequest`) are duplicated in both projects with a `// keep in sync with the other side` comment - SignalR serializes them as JSON, so the shapes must match exactly.

## 1. Configure the server

In development, config lives in **`src/Gul.Server/appsettings.Development.json`** (or dotnet user-secrets if you prefer to keep it out of the tree). Point `BaseDomain` at `localhost` so you can test subdomains without real DNS - browsers resolve `*.localhost` to loopback automatically.

```json
{
  "Gul": {
    "BaseDomain": "localhost"
  },
  "Oidc": {
    "Authority": "https://auth.example.com",
    "ClientId": "gul",
    "Scopes": "openid profile email",
    "RequireHttpsMetadata": "true"
  }
}
```

::: info
`appsettings.json` carries only ASP.NET framework defaults (logging, allowed hosts). Application config - `Gul:BaseDomain` and the `Oidc:*` keys - goes in `appsettings.Development.json` or user-secrets.
:::

## 2. Run the server

```powershell
dotnet run --project src/Gul.Server
```

It binds to the URL in `Properties/launchSettings.json` (e.g. `http://localhost:5080` - watch the startup log for the exact one). In Development it also mounts:

- **OpenAPI document** at `/openapi/v1.json` (anonymous)
- **Scalar API reference** at `/scalar/v1` (anonymous)
- `GET /health` and `GET /config` (both anonymous)

The forwarding middleware runs **first**, before auth, so public tunnel traffic never touches OIDC. It keys off the `Host` header: the apex (`localhost`) is the control plane, while `*.localhost` is a tunnel lookup.

## 3. Run the client

Start something to expose - say a static server on port 3000 - then, in another terminal, point the CLI at your local server and open a tunnel:

```powershell
# one-time: store the local server URL and log in
dotnet run --project src/Gul.Client -- setup      # enter http://localhost:5080
dotnet run --project src/Gul.Client -- login

# open the tunnel
dotnet run --project src/Gul.Client -- 3000
```

Everything after `--` is passed to the CLI as its args. The tunnel prints something like:

```
Tunnel live:  http://happy-otter.localhost:5080  ->  http://localhost:3000
```

Open that URL in a browser. Because `*.localhost` resolves to `127.0.0.1`, the request hits your local Gul server, gets forwarded down the SignalR connection to the CLI, and is replayed against `localhost:3000` - the whole round trip, no proxy or DNS needed.

::: tip
The CLI writes its config to `~/.gul/config.json` just like a release build. Delete that file to reset the stored server URL and tokens between experiments.
:::

## 4. Build & publish

Compile everything:

```powershell
dotnet build
```

Produce a self-contained single-file CLI the way the release pipeline does (pick your RID):

```powershell
dotnet publish src/Gul.Client -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

The client enables `InvariantGlobalization` and single-file publish but **not** AOT - SignalR and `System.Text.Json` rely on reflection at runtime, so a trimmed/AOT build would break serialization.

## Notes

- **No database, no migrations.** The server's only state is the `TunnelRegistry` - a `ConcurrentDictionary` mapping subdomain ↔ connection id. Restart the server and connected clients simply reconnect and re-register.
- **Version stamping.** The repo-root `Directory.Build.props` reads `<version>` from `application.properties` and stamps it into both assemblies at build time; the csproj files don't hardcode a version.
- **API exploration.** Use the Scalar UI at `/scalar/v1` or any spec-aware tool against `/openapi/v1.json`. Only the `/tunnel` hub requires a bearer token.
