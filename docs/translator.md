# Auto-router translator

A normal tunnel exposes exactly one port. That is fine until the app on that port points at another service on your machine. A frontend on `:3000` ships HTML and JavaScript that call `http://localhost:8000` for its API, and the moment a remote visitor loads the page their browser tries to reach *their own* localhost. The API call dies, and with it your auth service, your websocket backend, and every other cross-service link.

Gul's auto-router translator fixes this without a single code change. It reads every response your tunneled app returns, finds references to other local services, and rewrites them into gul routes that forward back to the right local port. One tunnel carries your whole multi-service dev setup.

## Before and after

**Before.** Your frontend on `:3000` returns markup and scripts that hardcode `http://localhost:8000`:

```html
<script>
  fetch("http://localhost:8000/api/items")
</script>
```

Through an ordinary tunnel the visitor's browser resolves `localhost:8000` to their own machine, and the request fails.

**After.** Gul rewrites that reference before the response leaves your machine:

```html
<script>
  fetch("http://a1b2c3.happy-otter.localhost:5080/api/items")
</script>
```

`a1b2c3` is a gul route bound to your local `:8000`. The visitor's browser follows it back through the same tunnel, Gul forwards it to `localhost:8000`, and the API answers. Frontend, API, and auth all work through one public URL.

## How it works

```
visitor's browser
  │  loads https://happy-otter.gul.example.com  (your :3000 app)
  ▼
gul client                    response body references http://localhost:8000
  │  rewrites it to http://a1b2c3.happy-otter.gul.example.com
  ▼
visitor's browser
  │  GET https://a1b2c3.happy-otter.gul.example.com/api/items
  ▼
gul client                    a1b2c3 maps to local port 8000
  │  GET http://localhost:8000/api/items
  ▼
your API on :8000
```

Step by step:

1. You open your primary tunnel, e.g. `gul 3000`.
2. A visitor request comes down the tunnel and your local app answers.
3. Before the response is sent back, the client scans it for absolute `http(s)` URLs that point at local services.
4. For each distinct host and port it mints a short route id and rewrites the URL to `http://<routeId>.<yoursub>.<basedomain>` (for example `http://a1b2c3.happy-otter.localhost:5080`).
5. When the visitor's browser calls that route, Gul forwards it to the matching local port, exactly like the primary tunnel.

All of this happens client-side, on your machine. The server just forwards bytes.

### What gets rewritten

- **Text response bodies** such as HTML, CSS, JavaScript, and JSON. Binary bodies are left untouched.
- **The `Location` redirect header**, so a `3xx` that points at another local service keeps working through the tunnel.

Compressed responses (`gzip`, `brotli`, `deflate`) are transparently decompressed before rewriting, so a gzipped local service is handled the same as a plain one.

## CORS

Because your services now live on different gul origins (the apex for your primary app, `<routeId>.<sub>` for each translated service), a browser treats calls between them as cross-origin and would normally block them. Gul handles this for you with bidirectional origin translation.

- On the way in, it rewrites the `Origin` and `Referer` request headers from the gul origin back to the real local origin, so your service's CORS check sees the origin it was configured for.
- On the way out, it rewrites `Access-Control-Allow-Origin` from the local origin to the gul origin, so the browser is satisfied.

Cross-service `fetch` and `XHR` calls just succeed, with no CORS configuration changes.

## OIDC providers

Add your gul URL as a callback URL in the provider, and keep translation on the default `loopback`. A provider on `localhost` needs nothing: gul routes it and rewrites `redirect_uri` for you. Do not route the provider with `all` or `aggressive`, since its discovery `issuer` gets rewritten but the signed token does not, so login fails. Full detail on the [OIDC providers](./oidc.md) page.

## Configuration

Translation is on by default for your local services, the loopback hosts (`localhost`, `127.0.0.1`, `[::1]`). External hosts stay untouched, so services like Microsoft, Google, and CDNs are reached directly by the browser. Two settings tune it.

| Setting | Values | Default | What it does |
| --- | --- | --- | --- |
| `Translate` | `loopback`, `allowlist`, `all`, `aggressive`, `off` | `loopback` | `loopback` (the default) rewrites only loopback hosts (`localhost`, `127.0.0.1`, `[::1]`). `allowlist` also rewrites the hosts listed in `TranslateHosts`. `all` rewrites every absolute `http(s)` URL including external ones, which can break third-party services like Microsoft or Google, so use it deliberately. `aggressive` rewrites every host like `all` and additionally rewrites every response header rather than just `Location`, for the rare app that hides local URLs in unusual headers. `off` disables translation entirely. |
| `TranslateHosts` | `string[]` | `[]` | The hosts to rewrite when `Translate` is `allowlist`. Ignored in the other modes. |

For an OIDC provider, keep the default `loopback` and add your gul URL as a callback URL. Routing it with `all` or `aggressive` rewrites the discovery `issuer` but not the signed token, so login fails. See [OIDC providers](./oidc.md).

Both live in the client config file (`~/.gul/config.json`). See the [CLI config reference](./cli.md#configuration-file).

### From the command line

Set the mode for a single tunnel with `--translate`:

```bash
gul 3000                          # loopback (the default): only local references
gul 3000 --translate all          # also route external hosts
gul 3000 --translate aggressive   # all hosts plus every response header
gul 3000 --no-translate           # turn translation off for this run
```

`--no-translate` is shorthand for `--translate off`. A flag on the command line wins over the config file for that run.

## Production TLS caveat

Translated routes add a label in front of your subdomain, so `happy-otter.gul.example.com` becomes `a1b2c3.happy-otter.gul.example.com`. A single-label wildcard certificate for `*.gul.example.com` does **not** cover that deeper name, because a wildcard matches exactly one label. To serve translated routes over HTTPS in production you need a certificate that also covers `*.<sub>.gul.example.com` (or a wildcard issued per active subdomain).

On `*.localhost` this never comes up. Local development speaks plain HTTP, browsers resolve every `*.localhost` name to loopback, and nested labels like `a1b2c3.happy-otter.localhost` work out of the box.

See also: [CLI client](./cli.md) · [Self-host Gul](./self-host.md) · [What is Gul?](./intro.md)
