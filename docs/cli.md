# The `gul` CLI

`gul` is a single self-contained binary, the client half of Gul. It logs in once via your browser, opens a tunnel over one SignalR connection, and forwards public requests to a port on your machine until you press Ctrl+C. No runtime to install, no config beyond a server URL.

## Install

**One-liner.** Downloads the right binary for your OS and architecture and puts `gul` on your `PATH`:

```sh
curl -fsSL https://raw.githubusercontent.com/PianoNic/Gul/main/install.sh | sh
```

```powershell
irm https://raw.githubusercontent.com/PianoNic/Gul/main/install.ps1 | iex
```

**Portable.** Prefer no install? Grab the standalone single-file binary for your platform from the [latest release](https://github.com/PianoNic/Gul/releases/latest), drop it anywhere on your `PATH`, and run it:

| Platform | Asset |
| --- | --- |
| Windows x64 | `gul-win-x64.exe` |
| Windows ARM64 | `gul-win-arm64.exe` |
| Linux x64 | `gul-linux-x64` |
| Linux ARM64 | `gul-linux-arm64` |
| macOS Intel | `gul-osx-x64` |
| macOS Apple Silicon | `gul-osx-arm64` |

On macOS / Linux, mark it executable and put it on your `PATH`:

```bash
chmod +x gul-linux-x64
mv gul-linux-x64 ~/.local/bin/gul
```

Verify it runs:

```bash
gul --help
```

## First run

Two one-time steps: point the CLI at your server, then log in.

```bash
gul remote https://gul.example.com   # store the server URL
gul login                            # opens your browser for the OIDC login
```

- **`gul remote <url>`** sets the Gul server URL and stores it (run `gul remote` with no argument to print the current one). This is the apex URL your operator gave you (`https://gul.example.com`), *not* a tunnel subdomain. Invalid URLs are rejected.
- **`gul login`** fetches the server's OIDC settings from `GET /config`, runs **Authorization Code + PKCE** against your identity provider, and saves the resulting tokens. A browser tab opens. Approve, and it redirects to a local loopback listener that shows a "You can close this tab" page.

::: tip
You rarely call `gul login` by hand. Opening a tunnel checks your token first and logs you in automatically if it's missing or expired (refreshing silently when it can).
:::

## Open a tunnel

```bash
gul 3000
```

```
Tunnel live:  https://happy-otter.gul.example.com  ->  http://localhost:3000
```

Share that URL. Every request to it is forwarded to `http://localhost:3000` on your machine and the response streams back. Press **Ctrl+C** to close the tunnel. The subdomain is released the moment you disconnect.

### Custom subdomain

Ask for a specific name with `--name`:

```bash
gul 3000 --name myapp        # -> https://myapp.gul.example.com  (if free)
```

If the name is already taken or invalid (names must be lowercase `a-z`, `0-9`, `-`, 1-63 chars), the server falls back to a random friendly name and prints whatever you actually got. Without `--name` you always get a random one like `happy-otter`.

## Commands

| Command | What it does |
| --- | --- |
| `gul remote [<url>]` | Set the server URL, or print it when run with no argument. |
| `gul login` | Run the browser OIDC login and save the tokens. |
| `gul logout` | Clear the saved tokens (keeps the server URL). |
| `gul <port> [--name <sub>] [--translate <mode>]` | Ensure a valid token, open a tunnel to `localhost:<port>`, and forward until Ctrl+C. |
| `gul <port> --translate <all\|loopback\|allowlist\|off>` | Set URL translation for this run. `--no-translate` is shorthand for `--translate off`. See [Auto-router translator](./translator.md). |
| `gul` / `gul --help` | Print usage. |

## Configuration file

Everything the CLI remembers lives in a single JSON file:

```
~/.gul/config.json
```

(On Windows that's `%USERPROFILE%\.gul\config.json`.)

```json
{
  "ServerUrl": "https://gul.example.com",
  "AccessToken": "eyJhbGciOi...",
  "RefreshToken": "def502...",
  "ExpiresAtUtc": "2026-07-03T18:42:00Z",
  "Translate": "loopback",
  "TranslateHosts": []
}
```

- **`ServerUrl`** is set by `gul remote`.
- The token fields are written by `gul login` and refreshed automatically before a tunnel opens. `gul logout` clears them.
- **`Translate`** controls URL translation. It takes `loopback` (the default), `allowlist`, `all`, or `off`, and the `--translate` flag overrides it for a single run. See [Auto-router translator](./translator.md).
- **`TranslateHosts`** is the list of hosts to rewrite when `Translate` is `allowlist`, and it is ignored in the other modes.
- Delete the file to start completely fresh, or re-run `gul remote <url>` to repoint at a different server.

::: warning
The file holds live access and refresh tokens in plain text. It's created under your home directory with your user's permissions, so treat it like any other credential file and don't commit it.
:::

## How forwarding behaves

A few things worth knowing when a tunnel is live:

- **Cross-service URLs are rewritten.** By default Gul translates absolute `http(s)` URLs in the response body and the `Location` header into gul routes, so a multi-service local setup works through one tunnel. Tune it with `--translate` or turn it off with `--no-translate`. See [Auto-router translator](./translator.md).
- **Redirects aren't followed.** The CLI forwards your app's `3xx` responses as-is, so the visitor's browser sees them, and relative redirects keep working through the public URL.
- **The local app must be running.** If nothing answers on the target port, the visitor gets a clean `502` with a short text body instead of a hang.
- **HTTP(S) only.** Plain WebSocket upgrades on the tunneled app aren't proxied, and bodies are buffered in memory rather than streamed. Gul is built for developing and demoing web apps, not high-throughput file transfer.

See also: [Auto-router translator](./translator.md) · [Self-host Gul](./self-host.md) · [What is Gul?](./intro.md)
