---
layout: home

hero:
  name: Gul
  text: Your whole local stack on one public URL.
  tagline: The self-hosted tunnel that puts your entire multi-service dev setup on a single HTTPS URL. Cross-service links, CORS, and OIDC login all just work, with zero code changes.
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
    details: The auto-router translator rewrites cross-service local URLs (a frontend on `:3000` calling `http://localhost:8000`) into gul routes on the fly, and routes them back to the right port. Your entire multi-service setup runs through one URL. No other local tunnel does this.
  - title: CORS just works
    details: Your services now sit on different gul origins, so browsers would normally block the calls between them. Gul does bidirectional origin translation on Origin, Referer, and Access-Control-Allow-Origin, so cross-service requests just succeed.
  - title: OIDC login just works
    details: Apps behind a self-hosted OIDC provider (Keycloak, Authentik, Zitadel, Pocket ID, Dex) log in straight through the tunnel with zero provider config. Gul rewrites `redirect_uri` inbound and the login callback lands right back in the tunnel.
  - title: Self-hosted, you own it all
    details: You run the server, you own the domain, and you keep the data. No third party sees your traffic and there is no database to manage.
  - title: Secured control plane
    details: Only you can open tunnels, guarded by a browser OIDC login with Authorization Code and PKCE. Visitors to your tunnel URL stay anonymous, exactly like any other public site.
  - title: One small binary
    details: A single self-contained CLI, built for 6 targets across Windows, Linux, and macOS on x64 and arm64, installable with one line. Colored output, a 굴 badge, and friendly subdomains included.
---
