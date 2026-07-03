# Self-host Gul

Run the Gul server from the pre-built image:

- **Docker Hub**: `pianonic/gul:latest`
- **GitHub Container Registry**: `ghcr.io/pianonic/gul:latest`

You need a Linux/Windows host with **Docker + Compose v2**, and an **existing reverse proxy** that already terminates TLS for a wildcard domain. Gul does not do TLS or DNS itself. It reads the `Host` header, matches a subdomain, and forwards. The proxy and the certificates are yours.

## Before you start

Gul assumes three things already exist:

| You need | Why |
| --- | --- |
| A base domain, e.g. `gul.example.com` | Tunnels are handed out as `<name>.gul.example.com`. |
| Wildcard DNS: `*.gul.example.com` **and** `gul.example.com` -> your host | So every tunnel resolves to the machine running your proxy. |
| A reverse proxy with a **wildcard TLS cert** for `*.gul.example.com` | Gul speaks plain HTTP on `8080`, and the proxy terminates TLS and forwards both the wildcard and the apex to it. |
| An **OIDC provider** with a public (PKCE) client | Only the CLI control connection authenticates. See [OIDC setup](#oidc-provider-setup). |

::: tip
Because every tunnel is a *new* subdomain, per-host certificates don't scale. Use a **wildcard certificate** (Caddy's on-demand TLS, or a DNS-01 ACME challenge in nginx/Traefik).
:::

## Quickstart

Drop these two files in an empty folder and run `docker compose up -d`. The Gul server has no state to persist (the tunnel registry lives in memory), so there are no volumes.

**`compose.yml`**

```yaml
# Gul sits behind an EXISTING wildcard reverse proxy. That proxy terminates TLS for
# *.gul.example.com AND the apex gul.example.com and forwards both to this container on
# port 8080. Point your proxy at `gul:8080` (same docker network) or publish the port.
services:
  gul:
    image: ghcr.io/pianonic/gul:latest
    container_name: gul
    restart: unless-stopped
    environment:
      Gul__BaseDomain: ${GUL_BASE_DOMAIN}          # e.g. gul.example.com
      Oidc__Authority: ${GUL_OIDC_AUTHORITY}       # your OIDC issuer
      Oidc__ClientId: ${GUL_OIDC_CLIENT_ID}        # public (PKCE) client id
      Oidc__Scopes: "openid profile email"
      Oidc__RequireHttpsMetadata: "true"
    # If your reverse proxy shares this docker network it reaches the container as `gul:8080`
    # and you need no `ports:` mapping. Publish the port only if the proxy runs on the host
    # or another machine:
    # ports:
    #   - "8080:8080"
```

**`.env`**

```env
# The apex domain Gul hands out subdomains under. Must equal the wildcard your proxy terminates.
GUL_BASE_DOMAIN=gul.example.com

# Your OIDC provider. Only the CLI control connection authenticates against it - tunnel
# visitors are anonymous.
GUL_OIDC_AUTHORITY=https://auth.example.com
GUL_OIDC_CLIENT_ID=gul
```

That's the whole server. The next two sections wire up the proxy and the OIDC client.

## Reverse proxy

Forward **both** the wildcard and the apex to the container, preserving the original `Host` header (Gul reads it to pick the subdomain).

**Caddy.** The whole config is two lines. Caddy fetches a wildcard cert on demand:

```caddy
*.gul.example.com, gul.example.com {
    reverse_proxy gul:8080
}
```

**nginx.** One `server` block with a wildcard `server_name` and a wildcard certificate (issued out of band via DNS-01). Pass the host through:

```nginx
server {
    listen 443 ssl;
    server_name .gul.example.com;               # matches the apex and every subdomain
    ssl_certificate     /etc/ssl/gul/fullchain.pem;   # wildcard cert for *.gul.example.com
    ssl_certificate_key /etc/ssl/gul/privkey.pem;
    location / {
        proxy_pass http://gul:8080;
        proxy_set_header Host $host;            # Gul needs the original host
    }
}
```

**Traefik.** Route `` HostRegexp(`^.+\.gul\.example\.com$`) || Host(`gul.example.com`) `` to the `gul` service on port `8080`, with a wildcard certresolver (DNS challenge). Traefik forwards the `Host` header by default.

::: warning
Whatever proxy you use, it **must** pass the untouched `Host` header. If Gul sees the proxy's own hostname instead of `happy-otter.gul.example.com`, it can't find the tunnel and serves the apex control plane instead.
:::

## OIDC provider setup

The CLI logs in with **Authorization Code + PKCE** on a **loopback redirect** (`http://127.0.0.1:<port>/`). Register Gul on your IdP as a **public client** (no secret):

| Setting | Value |
| --- | --- |
| Client type | Public (PKCE, no client secret) |
| Allowed redirect URIs | `http://127.0.0.1:*/` |
| Scopes | `openid profile email` |

The CLI binds an ephemeral loopback port at login time, so the redirect port is not fixed. The `:*` wildcard matches whatever port it picks. The server never receives the code. It only validates the resulting access token on the hub.

- **Pocket ID.** Toggle **Public Client**, keep **PKCE** on, and set the callback URL to `http://127.0.0.1:*/`.
- **Authentik.** Use the *Provider*'s issuer (`/application/o/<slug>/`) as `Oidc__Authority`.
- **Auth0.** Authority is `https://<tenant>.auth0.com/` (trailing slash), and app type **Native**.
- **Keycloak.** Authority is `https://<host>/realms/<realm>`. Set the client to public and add the redirect URIs.

## Configuration reference

<details>
<summary><strong>Environment variables</strong></summary>

Set these on the `gul` service (the Quickstart pulls them from `.env`). `__` maps to nested config.

| Variable | What it does |
| --- | --- |
| `Gul__BaseDomain` | The apex domain tunnels live under, e.g. `gul.example.com`. Subdomains are handed out as `<name>.gul.example.com`. **Must match** the wildcard your reverse proxy terminates TLS for. |
| `Oidc__Authority` | OIDC issuer / discovery URL. Gul validates control-connection tokens against `<authority>/.well-known/openid-configuration`. Must match the token's `issuer` byte-for-byte. |
| `Oidc__ClientId` | The public (PKCE) client id the CLI logs in with. Also served to the CLI from `GET /config`. |
| `Oidc__Scopes` | Space-separated scopes requested at login. Default `openid profile email`. |
| `Oidc__RequireHttpsMetadata` | `true` (default). Set `false` only for a plain-HTTP IdP in development. |

The audience is **not** validated (`ValidateAudience=false`). Gul only needs to know the token was minted by your IdP for a real user, not that it names a specific API.

</details>

<details>
<summary><strong>What the server exposes</strong></summary>

All served on port `8080`, on the apex host (`gul.example.com`). Subdomains are always tunnel traffic:

| Path | Auth | Purpose |
| --- | --- | --- |
| `GET /health` | anonymous | Liveness check that returns `200 OK`. |
| `GET /config` | anonymous | `{ authority, clientId, scopes, baseDomain }`. The CLI reads this to bootstrap login. |
| `/tunnel` | **OIDC required** | The SignalR control hub the CLI connects to. |
| `/scalar/v1`, `/openapi/v1.json` | anonymous | API reference, **Development only**. |

</details>

---

## Operations

**Upgrade**

```bash
docker compose pull gul && docker compose up -d gul
```

There's no database and no migrations. The container comes up, clients reconnect, and each re-registers its subdomain. Pin a version by replacing `:latest` with a [published tag](https://github.com/PianoNic/Gul/pkgs/container/gul).

**Scale note.** Gul's registry is in-memory and per-process, so run **one** replica. A dropped tunnel just means the CLI reconnects, and there is no shared state to coordinate.

---

## Troubleshooting

<details>
<summary><strong>Common errors and fixes</strong></summary>

| Symptom | Fix |
| --- | --- |
| Apex/404 page instead of your app | The proxy isn't forwarding the wildcard host. Ensure `*.gul.example.com` **and** `gul.example.com` both proxy to `gul:8080` **and** pass the original `Host` header. |
| `No active tunnel for <sub>` (502) | No CLI is registered for that subdomain right now, so run `gul <port>` again. The tunnel closes the moment the CLI exits. |
| Visitor request hangs, then `504` | The forwarded request timed out (~100s). The local app is slow or the port the CLI targets isn't answering. |
| `redirect_uri` mismatch at login | The OIDC client must allow `http://127.0.0.1:*/` (wildcard the ephemeral port) and be a public (PKCE, no secret) client. |
| `401` on the hub / "can't open a tunnel" | `Oidc__Authority` must match the token's `issuer` byte-for-byte, and the discovery URL must be reachable **from inside the container**. |
| TLS error on a brand-new subdomain | The certificate must cover `*.gul.example.com`. Per-subdomain certs won't keep up, so use a wildcard cert (Caddy on-demand TLS or a DNS-01 challenge). |
| Login opens no browser on a headless host | The CLI prints the authorize URL as a fallback. The redirect returns to a loopback listener on the *same* machine, so complete login in a browser there (or tunnel the loopback port to your workstation). |
| Translated route (`<id>.<sub>.gul.example.com`) fails TLS | Translated routes add a label in front of the subdomain, so a `*.gul.example.com` cert doesn't cover them. Serve a cert that also covers `*.<sub>.gul.example.com`. See the [translator TLS caveat](./translator.md#production-tls-caveat). |

</details>

---

See also: [Auto-router translator](./translator.md) · [CLI client](./cli.md) · [Developer setup](./dev-setup.md)
