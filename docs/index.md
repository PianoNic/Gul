---
layout: home

hero:
  name: Gul
  text: Your whole local stack on one public URL.
  tagline: A self-hosted tunnel that puts your entire multi-service dev setup on one HTTPS URL.
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
  - title: One tunnel for your whole stack
    details: The auto-router translator rewrites cross-service local URLs into gul routes on the fly, so your whole multi-service setup runs through one URL. No other local tunnel does this.
  - title: CORS just works
    details: Bidirectional origin translation on Origin, Referer, and Access-Control-Allow-Origin, so cross-service browser calls are not blocked.
  - title: OIDC
    details: Add your gul URL as a callback in your provider and log in through the tunnel. A provider on localhost needs nothing.
  - title: Self-hosted
    details: You run the server, own the domain, and keep the data. No third party, no database.
  - title: Secured control plane
    details: Only you can open tunnels, guarded by a browser OIDC login (Authorization Code + PKCE). Visitors stay anonymous.
  - title: One small binary
    details: A self-contained CLI for 6 targets (Windows, Linux, macOS on x64 and arm64), one-line install.
---
