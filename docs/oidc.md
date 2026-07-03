# OIDC providers

If your app uses an OIDC provider, add your gul URL as a callback URL there.

```bash
gul 3000 --name myapp
```

Register `https://myapp.gul.example.com/*` as an allowed redirect URI in the provider (Keycloak, Microsoft, Auth0, Okta, and the like). `--name` keeps the URL stable. Keep translation on the default `loopback` so the provider is reached directly.

## Self-hosted on localhost

A provider on `localhost` needs nothing. Gul reaches it as a routed service, rewrites `redirect_uri` for you, and — because translation is on — forwards `X-Forwarded-Host` and `X-Forwarded-Proto` carrying the public gul origin, exactly like a reverse proxy.

A provider that honors forwarded headers (mock-oauth2-server, Keycloak, Duende IdentityServer, and most modern IdPs) then mints both its discovery `issuer` and the signed token `iss` from the gul host. Discovery and token validation line up, and login just works.

## Providers that ignore forwarded headers

If a provider derives its issuer from a fixed configured value and ignores `X-Forwarded-*`, its discovery `issuer` and signed-token `iss` stay on the real local origin. Gul can rewrite the issuer text in the discovery document but never inside a signed token, so the client sees the mismatch and login fails. Point the provider's configured issuer (public base URL) at your gul URL, or reach the provider directly.

See also: [Auto-router translator](./translator.md) · [CLI client](./cli.md)
