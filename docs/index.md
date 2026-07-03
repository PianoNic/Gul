---
layout: home

hero:
  name: Gul
  text: Your localhost, live on the internet.
  tagline: Instant public HTTPS URLs for anything running on your machine. One command, one tunnel, zero config.
  image:
    src: /logo.svg
    alt: Gul
  actions:
    - theme: brand
      text: Self-host Gul
      link: /self-host
    - theme: alt
      text: CLI client
      link: /cli
    - theme: alt
      text: GitHub
      link: https://github.com/PianoNic/Gul

features:
  - title: One command
    details: Run `gul 3000` and your local port is live at https://happy-otter.gul.example.com - TLS included.
  - title: Random or named subdomains
    details: Get a friendly name like happy-otter by default, or claim your own with `--name myapp`.
  - title: Secured control plane
    details: Only you can open tunnels - a browser OIDC login guards the control connection. Visitors stay anonymous.
  - title: One small binary
    details: A single self-contained .NET CLI over a SignalR connection. No agent, no daemon, no database.
---
