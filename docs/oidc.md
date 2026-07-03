# OIDC providers

If your app uses an OIDC provider, add your gul URL as a callback URL there.

```bash
gul 3000 --name myapp
```

Register `https://myapp.gul.example.com/*` as an allowed redirect URI in the provider (Keycloak, Microsoft, Auth0, Okta, and the like). `--name` keeps the URL stable. Keep translation on the default `loopback` so the provider is reached directly.

## Self-hosted on localhost

A provider on `localhost` needs nothing. Gul routes it and rewrites `redirect_uri` for you.

## Do not route the provider

`all` and `aggressive` rewrite the provider's discovery `issuer` to a gul route, but the signed token keeps the real one. The client sees the mismatch and login fails. Leave the provider direct.

See also: [Auto-router translator](./translator.md) · [CLI client](./cli.md)
