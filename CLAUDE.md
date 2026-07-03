# Gul — working conventions

## Workflow (enforced)

Never work on main. Always:
1. `gh issue create` (with a label)
2. Branch `feature/<issue#>_PascalCase` or `fix/<issue#>_PascalCase`
3. `gh pr create` (with a label) — body is Summary + `Closes #<issue>` only
4. Squash-merge + delete branch

## Commits

- Short imperative subject line.
- No AI / Claude attribution. No `Co-Authored-By`, no `🤖 Generated with...`, nothing.

## PRs

- Title mirrors the commit / issue.
- Body: 1–2 sentence summary + `Closes #<issue>`. No test plans, no checklists, no headers.
- Labels: `feature`, `enhancement`, `bug`, `refactor`, `documentation`, `CI/CD`.

## CLI generators

Use them whenever one exists — `gh issue create`, `gh pr create`, `dotnet new`, etc.

## Project layout

Two projects, no more. KISS — no database, no EF Core/migrations, no frontend.
- `src/Gul.Server` — ASP.NET Core (`Microsoft.NET.Sdk.Web`): the SignalR tunnel hub + the
  terminal forwarding middleware that routes `<sub>.gul.example.com` to the owning connection.
- `src/Gul.Client` — single-file console CLI (`Microsoft.NET.Sdk`, `OutputType=Exe`).
- `Contracts.cs` (the `TunnelRequest`/`TunnelResponse` wire records) is duplicated in both
  projects and marked `// keep in sync with the other side` — change both together.

## Local dev

- Server: `dotnet run --project src/Gul.Server`
- Client: `dotnet run --project src/Gul.Client -- 3000`
- No migrations, no seed, no compose dependencies to spin up for the app itself.

## Versioning

`application.properties` (repo root) is the single source of truth. `Directory.Build.props`
stamps `<Version>` from it; CI overrides it with the release tag via build-arg / `-p:Version`.
Do not hardcode a version in a csproj.
