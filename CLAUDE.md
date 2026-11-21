# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A .NET 10 client for SEV's (Faroese electricity utility) customer REST API, intended to become an MCP server exposing meter and usage data. The API reference is `docs/sev_rest_api_docs.pdf` (read it with `pdftotext -layout`). SEV's OpenAPI spec at https://github.com/dragan-norliga/sev-rest-clients/blob/master/DotNetExampleClient/api/openapi.yaml is more precise about field types and nullability; its example values (dates with `Z`/offsets, `application/json-patch+json`) are Swagger-generated and don't match the real API.

## Commands

```bash
dotnet build                                   # build the whole solution (SevMcp.slnx)
dotnet test                                    # run all tests (no network)
set -a; source .env; set +a; dotnet run --project Sev.SmokeTest   # run against the live API
dotnet user-secrets set "Sev:User" "..." --project Sev.McpServer  # one-time: credentials for the server
dotnet user-secrets set "Sev:Key" "..." --project Sev.McpServer
dotnet run --project Sev.McpServer --launch-profile http          # MCP server on http://localhost:5133/mcp
claude mcp add --transport http sev http://localhost:5133/mcp   # connect Claude Code to the local server
```

- `.env` (gitignored) holds `SEV_USER` and `SEV_KEY` for `Sev.SmokeTest`; nothing loads it automatically, so export it into the shell first. `Sev.SmokeTest` throws at startup if either is missing.
- `Sev.McpServer` reads credentials from the `Sev` configuration section (`SevOptions`): user-secrets locally (loaded only in the Development environment, which the launch profile sets), `Sev__User` / `Sev__Key` environment variables when hosted. It refuses to start if either is missing.
- Shared project settings (`TargetFramework`, `Nullable`, `ImplicitUsings`) live in `Directory.Build.props`, not in each `.csproj`.
- CI (`.github/workflows/ci.yml`) runs `dotnet restore`, `dotnet build` and `dotnet test`.

## Architecture

- **Sev.Client**: class library with all API logic. `SevClient` wraps a caller-supplied `HttpClient` whose `BaseAddress` must be `SevClient.DefaultBaseAddress` (the constructor rejects a missing base address or one without the trailing `/`). Every public method takes an optional `CancellationToken`; pass it through from MCP tool handlers. `SevClient` doesn't log in itself: its `HttpClient` must include `SevAuthHandler`, which adds the Bearer token from `SevTokenProvider` to each request. `SevTokenProvider` logs in on first use with its own `HttpClient` (not through `SevAuthHandler`), logs in again 10 minutes before the 4-hour expiry, and must be a singleton so all requests share one token. On a 401, `SevAuthHandler` calls `SevTokenProvider.Invalidate`, logs in again and retries the request once (as a copy, since a request can only be sent once). `SevRateLimitHandler` refuses a request with `SevRateLimitExceededException` (with `RetryAfter`) when the shared singleton `SevRateLimiter` has seen 10 sent calls in the last 5 minutes. Every `HttpClient` that calls SEV, including the token provider's login client, must have it as the innermost handler (right before the network), so only requests actually sent are counted.
- **Caching** (`Sev.Client/SevCache.cs`, `SevDataService.cs`): `SevDataService` has the same methods as `SevClient` but serves from `SevCache` where possible. The meter list is kept for 6 hours; usage is kept per meter per day, only for days before today (Faroese time, via `SevTime`) with at least 23 readings, so days SEV hasn't finished delivering aren't frozen. Missing data is fetched in one `SevClient` call (missing past days as whole days). Empty answers are never cached and are returned as empty (likely throttling). `SevCache` is a singleton because it holds the data; `SevDataService` is transient because it holds a `SevClient`, whose `HttpClient` must stay short-lived.
- **Sev.McpServer**: the MCP server, using the Streamable HTTP transport (`ModelContextProtocol.AspNetCore`) at `/mcp`. `Program.cs` registers `SevRateLimiter` and `SevTokenProvider` as singletons and `SevClient` as a typed `HttpClient` with `SevAuthHandler` then `SevRateLimitHandler`, and `SevCache` (singleton) with `SevDataService` (transient). Tools take `SevDataService`, never `SevClient` directly. Tools live in `Sev.McpServer/Tools/` and are registered with `WithTools<T>(toolJsonOptions)` (relaxed escaping keeps æ/ø/á readable). Only an `McpException`'s message reaches Claude (other exceptions become a generic error), so tools wrap SEV calls in `SevToolErrors.CallAsync` and throw `McpException` for invalid arguments. Keep tool logic in static methods so `Sev.McpServer.Tests` can test it without HTTP. It is HTTP rather than stdio so it can later be hosted and used as a custom connector from the Claude web and mobile apps.
- **Sev.Client.Tests**: xUnit tests for `SevClient`. `FakeHttpMessageHandler` returns canned responses and records requests, so tests never call the real API. `Fixtures/*.json` are sanitized copies of real API responses; keep their structure and field names exact when editing.
- **Sev.McpServer.Tests**: xUnit tests for the tools' pure logic (mapping, argument checks, error messages).
- **Sev.SmokeTest**: console app used as a manual smoke test: logs in, lists meters, prints hourly usage for all of the account's meters on a hardcoded date.

### JSON conventions
- `SevClient` serializes with `JsonNamingPolicy.SnakeCaseLower`, so models are PascalCase positional records that map to the API's snake_case fields. Use `[property: JsonPropertyName(...)]` only when the C# name differs from the API name (e.g. `UsageReading.Value` ← `reading`).
- `meter_id` is an `int` in `get_available_meters` but a `string` in usage responses; the models reflect this intentionally.
- The spec types `install_date`/`removal_date` as free-form nullable strings and `meter_nickname` as `object`. `Meter` reads them through the lenient converters in `Sev.Client/Json/`, so an unexpected value becomes `null` instead of failing the whole meter list.

## SEV API constraints

- **Rate limit: 10 calls per 5 minutes per customer**, login included. Reuse tokens and cache the meter list rather than calling per request. `SevRateLimiter` enforces it locally; it only sees this process's calls, so a server restart resets its count and other clients on the same account (the smoke test, scripts) aren't counted.
- `login_and_get_jwt_token` returns the JWT as `text/plain` (no JSON quotes, no trailing newline).
- JWTs expire after 4 hours; `refresh_jwt_key_token` (POST, no body, current Bearer token) returns a new plain-text one, but only while the current token is still valid. It is deliberately not used: it costs a rate-limited call just like a login, and login works whether or not the token has expired.
- All endpoints are POST. Date ranges are `[from_date, to_date)` and always in local Faroese time (send `DateTime` with `Kind` unspecified, no `Z`). `SevClient` throws `ArgumentException` for any other `Kind` or when `from >= to`, so convert at the boundary, not inside the client. The tools do this with `FaroeseTime.TryParse`, and the cache finds "today" with `SevTime`; both use `SevTime.Zone` (`Atlantic/Faroe`), which needs the system time-zone database: it's present on normal hosts, but slim Docker images need `tzdata` installed.
- Not yet implemented in `SevClient`: `estimated_CO2` (same request/response shape as `hourly_kwh_usage`, values in kg CO2), and `estimated_cost` (same request; readings have `reading`, `unit`, `price_unit`, `tarif`, no `cumulative_value`; values in DKK).
- **Over the rate limit, SEV returns `200 OK` with an empty list**, not an error (observed 2026-11-21: `get_available_meters` returned `[]` after ~11 calls in a few minutes, and the meters again after waiting 5 minutes; the docs don't mention it). So an empty result usually means throttling: the tools report it via `SevToolErrors.ThrowIfEmpty`. Don't trigger it on purpose, and keep manual testing to a few calls at a time.
- Responses are standard HTTP status codes. On a non-success status `SevClient` throws `SevApiException` (derives from `HttpRequestException`) with `StatusCode`, `ResponseBody` and `RetryAfter`.

## Workflow

Every change, including docs, goes through an issue and a PR. Never commit directly to `main`.

1. `gh issue create` first.
2. Branch from an up-to-date `main` as `feature/<issue#>-<slug>` (e.g. `feature/9-docs-and-claude-md`).
3. Open the PR with `Closes #<issue#>` in the body.
4. Only merge when asked: wait for CI to pass, then `gh pr merge <pr#> --squash --delete-branch`.

Never rename the head branch of an open PR. GitHub closes the PR instead of retargeting it.
