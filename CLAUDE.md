# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A .NET 10 client for SEV's (Faroese electricity utility) customer REST API, intended to become an MCP server exposing meter and usage data. The API reference is `docs/sev_rest_api_docs.pdf`.

## Commands

```bash
dotnet build                                   # build the whole solution (SevMcp.slnx)
set -a; source .env; set +a; dotnet run --project Sev.Mcp   # run against the live API
```

- `.env` (gitignored) holds `SEV_USER` and `SEV_KEY`; nothing loads it automatically, so export it into the shell first. `Sev.Mcp` throws at startup if either is missing.
- There are no test projects yet. CI (`.github/workflows/ci.yml`) only runs `dotnet restore` and `dotnet build`.

## Architecture

- **Sev.Client**: class library with all API logic. `SevClient` wraps a caller-supplied `HttpClient` whose `BaseAddress` must be `https://api.sev.fo/api/CustomerRESTApi/`. `LoginAsync` stores the JWT on that `HttpClient`'s `DefaultRequestHeaders`, so later calls depend on login having happened on the same instance.
- **Sev.Mcp**: console app that will become the MCP server. Currently a manual smoke test: logs in, lists meters, prints hourly usage for hardcoded meter IDs and dates.
- **Sev.Api**: unmodified ASP.NET weatherforecast template; references Sev.Client but does not use it yet.

### JSON conventions
- `SevClient` serializes with `JsonNamingPolicy.SnakeCaseLower`, so models are PascalCase positional records that map to the API's snake_case fields. Use `[property: JsonPropertyName(...)]` only when the C# name differs from the API name (e.g. `UsageReading.Value` ← `reading`).
- `meter_id` is an `int` in `get_available_meters` but a `string` in usage responses; the models reflect this intentionally.

## SEV API constraints

- **Rate limit: 10 calls per 5 minutes per customer**, login included. Reuse tokens and cache the meter list rather than calling per request.
- JWTs expire after 4 hours; `refresh_jwt_key_token` (POST, no body, current Bearer token) returns a new one.
- All endpoints are POST. Date ranges are `[from_date, to_date)` and always in local Faroese time (send `DateTime` with `Kind` unspecified, no `Z`).
- Not yet implemented in `SevClient`: `refresh_jwt_key_token`, `estimated_CO2` (same request/response shape as `hourly_kwh_usage`, values in kg CO2), and `estimated_cost` (same request; readings have `reading`, `unit`, `price_unit`, `tarif`, no `cumulative_value`; values in DKK).
- Responses are standard HTTP status codes; `SevClient` calls `EnsureSuccessStatusCode()`.

## Workflow

Every change, including docs, goes through an issue and a PR. Never commit directly to `main`.

1. `gh issue create` first.
2. Branch from an up-to-date `main` as `feature/<issue#>-<slug>` (e.g. `feature/9-docs-and-claude-md`).
3. Open the PR with `Closes #<issue#>` in the body.
4. Only merge when asked: wait for CI to pass, then `gh pr merge <pr#> --squash --delete-branch`.

Never rename the head branch of an open PR. GitHub closes the PR instead of retargeting it.
