# OIDC providers

If your app uses an OIDC provider, add your gul URL as a callback URL there.

```bash
gul 3000 --name myapp
```

Register `https://myapp.gul.example.com/*` as an allowed redirect URI in the provider (Keycloak, Microsoft, Auth0, Okta, and the like). `--name` keeps the URL stable. Keep translation on the default `loopback` so the provider is reached directly.

## Self-hosted on localhost

A provider on `localhost` needs nothing. Gul routes it, rewrites `redirect_uri` for you, and presents it with the **public gul origin** it is reached through — the `Host` header plus `X-Forwarded-Proto`/`X-Forwarded-Port` — on every translated route. (Your primary app keeps its own `localhost` Host instead, because dev servers like Vite reject a foreign one.)

Because the provider sees the public origin, it mints both its discovery `issuer` and the signed token `iss` as the gul URL. The discovery document then needs no rewrite, and the signed token — which gul can never rewrite — already carries the value the browser expects, so login completes. This works for `Host`-based providers (mock-oauth2-server) and `X-Forwarded`-aware ones (Keycloak, Duende IdentityServer) alike.

## Resource servers (APIs) that validate the token

Your API validates the same token, so it must expect the **same public issuer** the browser does. Point its accepted issuer at your gul URL — a stable `--name` keeps it fixed — and, if the API also fetches provider metadata, let it read JWKS from the provider's local address while validating `iss` against the gul URL. Many stacks expose both (for example an `Authority` for the issuer alongside an internal metadata address). An API pinned to the bare `localhost` issuer rejects the token with `401`.

See also: [Auto-router translator](./translator.md) · [CLI client](./cli.md)
