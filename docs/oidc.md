# OIDC providers

Gul handles OIDC and OAuth 2.0 login in one of two ways, depending on where your provider runs. Self-hosted providers are fully automatic. Cloud providers need a one-time redirect URI registration. Either way your app runs through the tunnel and the login lands back inside it.

## Self-hosted providers (automatic)

If you host the provider yourself on a loopback address, gul handles the whole login with no provider config. This covers **Keycloak, Authentik, Zitadel, Pocket ID, and Dex**, and any standard OAuth2 or OIDC server on `localhost`.

How it works:

1. Your app references the provider at `http://localhost:PORT`. Gul rewrites that into a gul route, so the login flow travels through the tunnel.
2. On requests to the provider, gul rewrites `redirect_uri` and `post_logout_redirect_uri` from the gul URL back to the `localhost` callback the provider already allows, so the provider accepts the request.
3. After you log in, the provider redirects to that `localhost` callback, and gul rewrites the `Location` back to the gul origin, so the browser lands back inside the tunnel with the authorization code.

`state`, `nonce`, and PKCE are opaque to gul and pass through untouched. There is nothing to configure. Just run `gul <port>` and log in.

## Cloud providers (register the gul URL)

Hosted providers like **Microsoft Entra, Auth0, Okta, Google, and AWS Cognito** are different. Their login page is public, so the browser reaches it directly rather than through the tunnel. Gul deliberately leaves them alone, which is what the default `loopback` translation mode does. Routing a third-party identity provider through the tunnel breaks its domain-bound cookies, its anti-forgery tokens, and its Content-Security-Policy, and it is a security anti-pattern.

Two steps make cloud login work.

1. Use a stable subdomain so your callback URL never changes:

   ```bash
   gul 3000 --name myapp
   ```

2. Register `https://myapp.gul.example.com/` (adjust the path to match your app's callback) as an allowed redirect URI in the provider's app settings.

Your tunneled app then bounces to the provider directly, you log in, and the provider redirects back to your gul URL with the code. This works for you and for anyone you share the URL with.

### Microsoft Entra example

In the Azure portal, open **App registrations**, pick your app, go to **Authentication**, and add your gul callback under **Redirect URIs**, for example `https://myapp.gul.example.com/`.

One caveat: Entra only accepts `https` redirect URIs, or exactly `http://localhost`. A local `http://myapp.localhost:5080` callback will not register. Test Microsoft login against your real `https` gul deployment, or point the app at an `http://localhost:<port>` callback for local development.

## Self-hosted vs cloud

| | Self-hosted (Keycloak, Authentik, Zitadel) | Cloud (Microsoft, Auth0, Okta, Google) |
| --- | --- | --- |
| Where it runs | `localhost` on your machine | A public URL |
| Reaches the browser | Through the tunnel | Directly |
| Gul setup | None, fully automatic | Keep `loopback`, register the gul URL once |
| Redirect URI | Rewritten automatically | Register `https://<name>.gul.example.com/` in the provider |

## Do not translate a cloud provider

If you switch translation to `all`, gul rewrites a cloud provider's URLs and routes its login through the tunnel, which produces exactly the CORS failures you would expect from proxying a locked-down identity provider. Keep the default `loopback`, or an `allowlist` that does not include the provider, when you work with cloud identity providers.

See also: [Auto-router translator](./translator.md) · [CLI client](./cli.md) · [Self-host Gul](./self-host.md)
