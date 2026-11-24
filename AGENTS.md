# AGENTS.md

This file provides guidance to AI coding agents (Claude Code, Codex and others) when working with code in this repository.

## Project

An MCP server (.NET 10) that lets Claude read meters, hourly usage, estimated cost and CO2 from SEV's (Faroese electricity utility) customer REST API, and optionally a heat pump's state and logged history through myUplink. It runs locally for Claude Code or hosted on Azure App Service as a custom connector for the Claude apps.

- SEV's API reference PDF is SEV's document and isn't in the repo: keep a local copy at `docs/sev_rest_api_docs.pdf` (gitignored) and read it with `pdftotext -layout`.
- SEV's OpenAPI spec (https://github.com/dragan-norliga/sev-rest-clients/blob/master/DotNetExampleClient/api/openapi.yaml) is more precise about field types and nullability; its example values (dates with `Z`/offsets, `application/json-patch+json`) are Swagger-generated and don't match the real API.

## Rules and workflow

- **Public repository:** don't put real meter IDs, device or system IDs, addresses, usage figures or secrets in the repo, issues or PRs.
- **Azure guardrail:** never run `deploy/setup-azure.sh`, `deploy/setup-backup.sh`, or change Azure resources without the user's explicit go-ahead (creates billed resources).
- **Git & PR workflow:** every change, including docs, goes through an issue and a PR. Never commit directly to `main`.
  1. `gh issue create` first.
  2. Branch from an up-to-date `main` as `feature/<issue#>-<slug>` (e.g. `feature/135-reorganize-agents-md`).
  3. Run `dotnet build` and `dotnet test` before opening or updating a PR, and update `AGENTS.md` / `docs/` when architecture or behaviour changes.
  4. Open the PR with `Closes #<issue#>` in the body.
  5. Only merge when asked: wait for CI to pass, then `gh pr merge <pr#> --squash --delete-branch`.
  6. Never rename the head branch of an open PR (GitHub closes the PR instead of retargeting it).
- **Dependabot PRs:** need no issue; merge only when asked after CI passes (merging deploys to Azure). Major updates of `Microsoft.AspNetCore.*` stay ignored because their major version follows .NET (11.x needs net11); move them together with `TargetFramework`.

## Commands

```bash
dotnet build                                   # build the whole solution (SevMcp.slnx)
dotnet test                                    # run all tests (no network)
set -a; source .env; set +a; dotnet run --project Sev.SmokeTest   # run against the live API
dotnet user-secrets set "Sev:User" "..." --project Sev.McpServer  # one-time: credentials for the server
dotnet user-secrets set "Sev:Key" "..." --project Sev.McpServer
dotnet run --project Sev.McpServer --launch-profile http          # MCP server on http://localhost:5133/mcp, web pages at /usage
claude mcp add --transport http sev http://localhost:5133/mcp   # connect Claude Code to the local server
```

- `.env` (gitignored) holds `SEV_USER` and `SEV_KEY`, and optionally `MYUPLINK_CLIENT_ID` / `MYUPLINK_CLIENT_SECRET`. Nothing loads it automatically: export it into the shell first. `Sev.SmokeTest` throws at startup without the SEV pair; the setup script copies all four to the app settings.
- Shared project settings (`TargetFramework`, `Nullable`, `ImplicitUsings`) live in `Directory.Build.props`, not in each `.csproj`.

## Configuration

- `Sev` section (`SevOptions`): `User` and `Key` (required, the server refuses to start without them), and optional `CacheDirectory` and `MeterNicknames` (`Sev:MeterNicknames:<meter ID>`; `MeterNicknames` replaces SEV's nickname in `list_meters` and in meter matching for the usage tools; a key that isn't a number, two keys for one meter (`0123` and `123`) or an empty name stop the server at startup). User-secrets locally (loaded only in the Development environment, which the launch profile sets); `Sev__User` etc. as environment variables when hosted.
- `Auth` section (`AuthOptions`): the hosted login (`Authority`, `Resource`, `Audiences`, `Scopes`, `AllowedUsers`).
- `MyUplink` section (`MyUplinkOptions`): optional `ClientId`, `ClientSecret` and `LogHourly`.
- `docs/deploy-azure.md` has the full settings reference.

## Architecture

### Projects

References only go one way: `Sev.McpServer` → `Sev.Web` → `Sev.Core` → `Sev.Client` / `MyUplink.Client`. The compiler enforces it, so the pages can't use tool code and the core knows nothing about MCP or ASP.NET.

- `Sev.Client`, `MyUplink.Client`: the two APIs.
- `Sev.Core`: what both front ends (the MCP tools and the web pages) need: `Periods`, `FaroeseTime`, `HourlySummary`, `CostPrices`, `MeterSummary`, `MeterNicknames`, and `HeatPump/` (`HeatPumpLog`, `HeatPumpReadings`). Bad input throws `InvalidInputException`, whose message says what to fix. Dates and times are formatted with `CultureInfo.InvariantCulture` (in a format `:` is the culture's time separator, `.` in Danish).
- `Sev.Web`: the Blazor pages (a Razor class library).
- `Sev.McpServer`: the host (`Program.cs`, login, options, DI, the heat pump logger) and the MCP tools. It's the only app: everything runs in one process.

### Web pages (Blazor)

A small frontend, a learning project in progress.

- Lives in `Sev.Web` (`WebSetup.AddSevWeb` / `MapSevWeb`, `Components/` with `App`, `Routes`, `Layout/`, `Pages/`, and `wwwroot/`, served at `/_content/Sev.Web/`). `Sev.McpServer` hosts it in the same app as the tools on purpose: the rate-limit count, logins, cache and heat pump log are in-memory singletons, so a separate app would have its own count and could go over SEV's limit with the server.
- **Static SSR:** `AddRazorComponents` without interactivity (plain HTML, GET forms, `[StreamRendering]`). Avoid adding interactive modes (Server/WASM) or JS/CSS frameworks unless asked.
- **Learning project:** keep changes small and incremental, and explain new Blazor concepts as we go when working on `Sev.Web`.
- **Local only:** `Program.cs` calls `MapSevWeb` only when `Auth` isn't set, since the pages have no browser login yet (`WebTests` checks both modes). Locally `AllowedHosts` and `LocalOrigin` already limit them to this machine.
- **Data and missing readings:** pages load data through `SevDataService` (never `SevClient`) and the logic in `Sev.Core`. Calculations go in plain C# classes that can be tested without a browser; `.razor` files only show the data. Follow the same rule as the tools: never present missing hours or readings as zero.

### Core invariants

Read `docs/architecture.md` before changing client, cache, tool, auth or heat pump code. Key invariants:

- **Single instance:** the server must run as one instance (in-memory rate limit, tokens, MCP sessions, cache, and `/home` persistence files).
- **SEV API & caching:** 10 calls per 5 minutes (login included), max 31 days per call, empty `200 OK` list means throttling, dates are `[from, to)` local Faroese time (`DateTimeKind.Unspecified`, `Atlantic/Faroe`). Tools and pages always call `SevDataService`, never `SevClient`. Missing data is never reported as zero.
- **MCP server & login:** Streamable HTTP at `/mcp`; keep tool logic in static methods; wrap errors in `McpException` (`SevToolErrors` / `HeatPumpToolErrors`); `GET /health` and `GET /` must return 200 anonymously without calling SEV or myUplink; hosted mode uses Entra ID v2 with two app registrations; local mode enforces loopback hosts and origins.
- **Heat pump (myUplink):** optional (`MyUplink:ClientId` + `ClientSecret`, read-only `READSYSTEM`); server logs hourly history itself; tests starting the server with myUplink configured must set `MyUplink:LogHourly=false`.
- **Tests:** never call real APIs (`FakeHttpMessageHandler`, `WebApplicationFactory<Program>`); keep `Fixtures/*.json` structure exact.
- **CI & hosting:** CI runs restore/build/test and deploys `Sev.McpServer` on pushes to `main` when `AZURE_WEBAPP_NAME` is set.

### Detailed references

- [docs/architecture.md](docs/architecture.md) — client handlers, caching/persistence, usage/cost/CO2 rules, Entra OAuth login, myUplink logger/history, tests, JSON conventions, SEV API constraints, and CI/backup internals.
- [docs/deploy-azure.md](docs/deploy-azure.md) — Azure App Service setup, settings reference, troubleshooting, and backups.
- [docs/heat-pump.md](docs/heat-pump.md) — myUplink setup and hourly log behaviour.
