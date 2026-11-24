# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

An MCP server (.NET 10) that lets Claude read meters, hourly usage, estimated cost and CO2 from SEV's (Faroese electricity utility) customer REST API, and optionally a heat pump's state and logged history through myUplink. It runs locally for Claude Code or hosted on Azure App Service as a custom connector for the Claude apps.

- The repo is public: don't put real meter IDs, device or system IDs, addresses, usage figures or secrets in the repo, issues or PRs.
- SEV's API reference PDF is SEV's document and isn't in the repo: keep a local copy at `docs/sev_rest_api_docs.pdf` (gitignored) and read it with `pdftotext -layout`.
- SEV's OpenAPI spec (https://github.com/dragan-norliga/sev-rest-clients/blob/master/DotNetExampleClient/api/openapi.yaml) is more precise about field types and nullability; its example values (dates with `Z`/offsets, `application/json-patch+json`) are Swagger-generated and don't match the real API.

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
- `Auth` section (`AuthOptions`): the hosted login, see [Login](#login).
- `MyUplink` section (`MyUplinkOptions`): optional `ClientId`, `ClientSecret` and `LogHourly`, see [Heat pump](#heat-pump-myuplink).
- `docs/deploy-azure.md` has the full settings reference.

## Architecture

### Projects

References only go one way: `Sev.McpServer` → `Sev.Web` → `Sev.Core` → `Sev.Client` / `MyUplink.Client`. The compiler enforces it, so the pages can't use tool code and the core knows nothing about MCP or ASP.NET.

- `Sev.Client`, `MyUplink.Client`: the two APIs.
- `Sev.Core`: what both front ends (the MCP tools and the web pages) need: `Periods`, `FaroeseTime`, `HourlySummary`, `CostPrices`, `MeterSummary`, `MeterNicknames`, and `HeatPump/` (`HeatPumpLog`, `HeatPumpReadings`). Bad input throws `InvalidInputException`, whose message says what to fix. Dates and times are formatted with `CultureInfo.InvariantCulture` (in a format `:` is the culture's time separator, `.` in Danish).
- `Sev.Web`: the Blazor pages (a Razor class library).
- `Sev.McpServer`: the host (`Program.cs`, login, options, DI, the heat pump logger) and the MCP tools. It's the only app: everything runs in one process, see below.

### Sev.Client

- `SevClient` wraps a caller-supplied `HttpClient` whose `BaseAddress` must be `SevClient.DefaultBaseAddress`; the constructor rejects a missing base address or one without the trailing `/`.
- Every public method takes an optional `CancellationToken`; pass it through from MCP tool handlers.
- `SevClient` doesn't log in itself: its `HttpClient` must include `SevAuthHandler`, which adds the Bearer token from `SevTokenProvider` to each request.
- `SevTokenProvider` (singleton, so all requests share one token) logs in on first use with its own `HttpClient` (not through `SevAuthHandler`), and again 10 minutes before the 4-hour expiry.
- On a 401, `SevAuthHandler` calls `SevTokenProvider.Invalidate`, logs in again and retries the request once, as a copy (a request can only be sent once).
- `SevRateLimitHandler` refuses a request with `SevRateLimitExceededException` (with `RetryAfter`) when the singleton `SevRateLimiter` has seen 10 sent calls in the last 5 minutes. Every `HttpClient` that calls SEV, the login client included, must have it as the innermost handler (right before the network), so only requests actually sent are counted.

### Caching

`SevDataService` has the same methods as `SevClient` but serves from `SevCache` where possible. `SevCache` is a singleton because it holds the data; `SevDataService` is transient because it holds a `SevClient`, whose `HttpClient` must stay short-lived.

- The meter list is kept for 6 hours. If a refresh after expiry hits a rate-limit refusal or an empty (throttled) answer, the stale cached list is still returned (with its timestamp unchanged so the next call retries), keeping cached hourly queries working.
- Usage, cost and CO2 are kept per meter per day (`UsageDays`, `CostDays`, `Co2Days`), all through one generic `GetHourlyAsync<TReading>` (steps: `MissingDays`, `FetchMissingAsync`, `Collect`), so these rules are written once.
- Only days before today (Faroese time, via `SevTime`) with all of that day's distinct local hours (`SevTime.HoursInDay`: 23 when summer time starts, 24 otherwise) are cached, so days SEV hasn't finished delivering (such as yesterday missing its 23:00 reading) aren't frozen. Days at least 7 days old (`SevCache.DaysUntilFinal`) are kept as SEV answered them, gaps included, so outages aren't refetched on every call.
- Nothing from the current hour on is fetched (a future range costs no call), and nothing outside a meter's install and removal dates, taken from the cached meter list (`get_usage` always loads it first; an unknown meter counts as installed).
- Missing days are fetched in pieces (`Piece`) of at most 31 days (`MaxDaysPerRequest`), newest first, past days as whole days, at most 6 per call (`MaxRequestsPerCall`). `PiecesNewestFirst` groups only the missing days, so cached stretches longer than a piece are skipped, and each piece asks only for the meters missing in it.
- `NotFetched` (on `UsageResult` etc., `HourlyResults.cs`) reports the older part that didn't fit; fetched days are cached, so asking again later completes it. A rate-limit refusal or empty answer keeps whatever was already cached or fetched (a rate-limit refusal with nothing cached or fetched still throws so the caller gets `RetryAfter`).
- Empty answers are never cached. When the first piece is empty, cached days are still returned, the rest is reported in `NotFetched`, and `EmptyAnswer` is set (likely throttling).
- Persistence (`SevCacheFiles`): with `Sev:CacheDirectory` set (hosted: `/home/data/sev-cache`; App Service keeps `/home` across restarts and deploys but replaces `/home/site/wwwroot`), `SevCache` loads the meter list and cached days from JSON files when it's created, and `SevDataService` saves a series' file whenever new days were cached (and the meter list when fetched), inside the cache lock, via a temp file and rename.
- The files are named after the reading type (`UsageReading.json`, ...) plus `Meters.json` and carry a format version. A missing, unreadable or other-version file is ignored and a failed save is only logged, so the files can never break a tool call. Without the setting (local use) the cache is memory only.
- JSON, not SQLite: `/home` is an Azure Files network share, where SQLite's locking isn't reliable.

### Sev.McpServer

- The MCP server uses the Streamable HTTP transport (`ModelContextProtocol.AspNetCore`) at `/mcp`. HTTP rather than stdio, so it can be hosted and used as a custom connector from the Claude apps.
- `Program.cs` is a short overview. `SevServices.AddSevClient` registers `SevRateLimiter` and `SevTokenProvider` as singletons, `SevClient` as a typed `HttpClient` with `SevAuthHandler` then `SevRateLimitHandler`, `SevCache` (singleton) and `SevDataService` (transient).
- Tools live in `Tools/` and are registered with `WithTools<T>(toolJsonOptions)` (relaxed escaping keeps æ/ø/á readable). Tools take `SevDataService`, never `SevClient` directly.
- Only an `McpException`'s message reaches Claude (other exceptions become a generic error), so tools wrap calls in `SevToolErrors.CallAsync` / `HeatPumpToolErrors.CallAsync` and throw `McpException` for invalid arguments. `ToolInput.ParsePeriod` reads from / to / granularity with `Periods` and passes `Sev.Core`'s `InvalidInputException` on as an `McpException`.
- Keep tool logic in static methods so `Sev.McpServer.Tests` can test it without HTTP, and logic a page could also use in `Sev.Core`.
- `GET /health` and `GET /` answer 200 without a login and without calling SEV or myUplink, for App Service's Health check and Always On (`Program.cs`).
- The server must run as **one instance**: the rate-limit count, tokens, MCP sessions and the cache live in memory, and the cache and heat pump log files in the shared `/home` would be written by every instance. `docs/deploy-azure.md` explains this.
- `Periods` (`Sev.Core`) holds what all period-based tools share: `Granularity`, the argument checks (`ParseRange`, `ParseGranularity`, `MaxRange`), `Key` (the period a time falls in) and `ExpectedHours` (the ended local hours in a range, skipping the hour summer time skips).

### Usage, cost and CO2 tools

- `get_usage` (kWh), `get_cost` (SEV's estimated cost, unit `DKR`) and `get_co2` (estimated emissions, unit `Kg CO2`) share one implementation, `UsageTools.GetHourlySeriesAsync`.
- `HourlySummary.Summarize` (`Sev.Core`) aggregates the hourly data into hours, days or months in C#, for any `IMeterReadings`, and returns `MeterSeriesResult`s.
- Results must never present missing data as zero: `total` is `null` without readings, and `expectedHours` / `missingHours` / `note` show gaps, per meter and per period. Expected hours are limited to each meter's installation (`ExpectedHoursFor`): on the install and removal days the meter starts at its first reading and ends after its last one.
- `GetHourlySeriesAsync` reads "now" (injected `TimeProvider`) before fetching, so a call that crosses the top of an hour never expects an hour `SevDataService` didn't fetch.
- `get_cost` also gives each meter `prices` (`CostPrices`, added through `GetHourlySeriesAsync`'s `addDetails`): stretches of the period with one `price_unit` and tariff. SEV's price is fixed per meter (the same every hour of the day), differs between meters, and changes rarely, so this is usually one stretch.
- Periods SEV hasn't been asked for yet are marked `notFetched`, with a note telling Claude to ask again later. Any new aggregation must keep all this.
- Real data shows why: one E350 meter delivers a day at a time, so today is always missing for it, while an E360 lags about an hour.

### Login

Login depends on the `Auth` section (`AuthOptions`, validated at startup). `McpLogin.AddLoginIfConfigured` reads it and `McpLogin.MapMcpEndpoint` maps `/mcp` for either mode.

- **Hosted** (`Auth:Authority` set):
  - JwtBearer checks Entra ID v2 tokens: signature, issuer, `Audiences`, expiry. `MapInboundClaims = false` keeps `oid` and `scp`.
  - The SDK's `McpAuth` scheme is the challenge scheme: it serves RFC 9728 metadata at `/.well-known/oauth-protected-resource/mcp` with `resource` = `Auth:Resource` (Claude requires it to equal the MCP URL exactly), and answers 401 with `WWW-Authenticate: Bearer resource_metadata=...`.
  - The metadata's `scopes_supported` also lists `offline_access` (`McpLogin.OfflineAccess`): Entra only issues a refresh token when it's requested, and without one Claude's access ran out with the access token (60–90 minutes) and the connector asked to reconnect. It's not checked in tokens.
  - The `AllowedUsers` policy on `MapMcp`: the token's `oid` must be in `Auth:AllowedUsers` and its `scp` must hold one of `Auth:Scopes`, compared by short name (`AuthOptions.ShortScopeName`: the config has `api://<client-id>/mcp.access`, the token `mcp.access`); else 403.
  - `X-Forwarded-Proto` is honoured so the challenge URL is https behind App Service. `AllowedHosts` must be set to the host name.
- **Local** (no `Auth`): `AllowedHosts` (`appsettings.json`) allows only `localhost`, `127.0.0.1` and `[::1]`, and `LocalOrigin.UseLocalOriginsOnly` returns 403 for any `Origin` header that isn't a loopback origin, against DNS rebinding; requests without `Origin` (Claude Code) pass. Outside Development the server refuses to start without `Auth`.
- Claude's connectors (per Anthropic's docs) use a pre-registered client ("Use your own OAuth client"), redirect to `https://claude.ai/api/mcp/auth_callback`, and send the MCP URL as the RFC 8707 `resource`. With Entra that needs v2 tokens (`requestedAccessTokenVersion: 2`) and the MCP URL as an Application ID URI.
- Claude's OAuth client must be its own app registration ("SEV MCP (Claude)"), separate from the API ("SEV MCP"): Entra refuses an app a token for itself unless the resource is its `api://<guid>` URI (AADSTS90009), so with one registration every refresh failed and the connector asked to reconnect every 1–2 hours (issue #123). The API pre-authorizes the client for `mcp.access`; `Auth:Audiences` stays the API's ID.
- Claude keeps a connector's tool list from when it was connected: after adding tools, the connector has to be disconnected and connected again.

### Heat pump (myUplink)

Optional: registered only when `MyUplinkServices.AddMyUplinkIfConfigured` finds both `MyUplink:ClientId` and `MyUplink:ClientSecret` (half a configuration stops the server), so users without a heat pump get exactly the SEV tools.

- **MyUplink.Client** reads myUplink's public API (https://api.myuplink.com/swagger/docs/public-v2/swagger.json; the Swagger page renders it in the browser only).
  - `MyUplinkTokenProvider` (singleton, own `HttpClient`) logs in with OAuth2 client credentials at `/oauth/token`, scope `READSYSTEM` only (never `WRITESYSTEM`), and reuses the token until 5 minutes before it expires (1 hour, no refresh token).
  - `MyUplinkClient` (typed `HttpClient`) reads systems, a device's current values (`GetPointsAsync`) and active alarms. On a 401 it logs in again and retries once; failures throw `MyUplinkApiException` with `RetryAfter` (from `ratelimit-reset`).
  - Every request sends `User-Agent: sev-mcp/1.0`: Cloudflare in front of myUplink refuses some default client signatures (error 1010).
  - 25 calls per minute (headers `ratelimit-limit` / `-remaining` / `-reset`). Parameter IDs arrive as strings though the spec says integers (`StringOrNumberConverter`).
  - The sign-in endpoints in its OpenID configuration (`/connect/authorize`, `/connect/deviceauthorization`) answer 404; the working ones are `/oauth/authorize` and `/oauth/token`.
- **Values:** `strVal` is the app's display text (the label for coded values), `-32768` means a sensor isn't connected, and some CTC parameters and categories have no English name ("Text not found: ..."). `Sev.Core/HeatPump/HeatPumpReadings` holds the rules: `ForStatus` (named and connected) and `ForLog` (also no settings, coded values or software versions).
- **`get_heatpump_status`** (`Tools/HeatPumpTools.cs`) groups current values by category, gives coded values as their label, marks settings, and fetches alarms only when the system has one.
- **History:** myUplink's history endpoints answered 403 with client credentials and with a user login (issue #96), so the server keeps its own.
  - `Sev.McpServer/HeatPump/HeatPumpLogger` (a `BackgroundService`, on unless `MyUplink:LogHourly` is false) checks every 5 minutes and, for each device from myUplink's last answer without a sample for the current local hour, logs its `ForLog` readings in `Sev.Core/HeatPump/HeatPumpLog` (singleton, one sample per local hour, the repeated summer-time hour once).
  - A device that isn't connected (`HeatPumpReadings.IsOffline`) is skipped with a warning: myUplink still answers with its last values, which would otherwise be logged as new readings. Its hours become missing, and with no device logged the next check tries again.
  - With `Sev:CacheDirectory` the log is saved as one JSON file per month there (`HeatPumpLog-2026-11.json`, temp file + rename, unreadable files ignored); without, memory only. It can't be fetched again if lost, hence the backups.
  - `get_heatpump_history` (`Tools/HeatPumpHistoryTools.cs`) takes the same `from` / `to` / `granularity` as `get_usage`, defaults to readings with "outdoor" in the name (or `all`, or names / parts of names), gives `value` per hour and `average` / `min` / `max` per day or month, and counts hours since logging started without a sample as `missingHours`, never as zero.
  - Tests that start the server with myUplink configured must set `MyUplink:LogHourly=false`, or the logger calls myUplink.

### Web pages (Blazor)

A small frontend, a learning project in progress.

- Lives in `Sev.Web` (`WebSetup.AddSevWeb` / `MapSevWeb`, `Components/` with `App`, `Routes`, `Layout/`, `Pages/`, and `wwwroot/`, served at `/_content/Sev.Web/`). `Sev.McpServer` hosts it in the same app as the tools on purpose: the rate-limit count, logins, cache and heat pump log are in-memory singletons, so a separate app would have its own count and could go over SEV's limit with the server.
- Static server-side rendering (`AddRazorComponents` without interactivity): plain HTML, no JavaScript or WebAssembly.
- **Local only:** `Program.cs` calls `MapSevWeb` only when `Auth` isn't set, since the pages have no browser login yet (`WebTests` checks both modes). Locally `AllowedHosts` and `LocalOrigin` already limit them to this machine.
- Pages load data through `SevDataService` (never `SevClient`) and the logic in `Sev.Core`. Calculations go in plain C# classes that can be tested without a browser; the `.razor` files only show the data.

### Tests and smoke test

- `Sev.Client.Tests`, `MyUplink.Client.Tests`: `FakeHttpMessageHandler` returns canned responses and records requests, so tests never call the real APIs. `Fixtures/*.json` are sanitized copies of real responses; keep their structure and field names exact when editing.
- `Sev.Core.Tests`: the shared logic (periods, summaries, prices, meters and nicknames, the heat pump log).
- `Sev.McpServer.Tests`: the tools (meter selection, error messages, the history tool, the logger), `WebTests`, plus `LoginTests` and the tool registration tests, which start the server with `WebApplicationFactory<Program>` (test-signed tokens for the login).
- `Sev.SmokeTest`: console app used as a manual smoke test: logs in, lists meters, prints hourly usage for all of the account's meters on a hardcoded date.

### JSON conventions

- `SevClient` serializes with `JsonNamingPolicy.SnakeCaseLower`, so models are PascalCase positional records that map to the API's snake_case fields. Use `[property: JsonPropertyName(...)]` only when the C# name differs from the API name (e.g. `UsageReading.Value` ← `reading`).
- `meter_id` is an `int` in `get_available_meters` but a `string` in usage responses; the models reflect this intentionally.
- The spec types `install_date`/`removal_date` as free-form nullable strings and `meter_nickname` as `object`. `Meter` reads them through the lenient converters in `Sev.Client/Json/`, so an unexpected value becomes `null` instead of failing the whole meter list.
- `MyUplinkClient` uses the web defaults (camelCase).

## SEV API constraints

- **Rate limit: 10 calls per 5 minutes per customer**, login included. Reuse tokens and cache the meter list rather than calling per request. `SevRateLimiter` enforces it locally; it only sees this process's calls, so a server restart resets its count and other clients on the same account (the smoke test, scripts) aren't counted.
- `login_and_get_jwt_token` returns the JWT as `text/plain` (no JSON quotes, no trailing newline).
- JWTs expire after 4 hours. `refresh_jwt_key_token` (POST, no body, current Bearer token) returns a new plain-text one, but only while the current token is still valid. It's deliberately not used: it costs a rate-limited call just like a login, and login works whether or not the token has expired.
- All endpoints are POST. Date ranges are `[from_date, to_date)` and always in local Faroese time (send `DateTime` with `Kind` unspecified, no `Z`). `SevClient` throws `ArgumentException` for any other `Kind` or when `from >= to`, so convert at the boundary, not inside the client. The tools do this with `FaroeseTime.TryParse`, which accepts only ISO 8601 dates and date-times (with or without `Z`/offset), never `06:00` or `11/10/2026`, and the cache finds "today" with `SevTime`; both use `SevTime.Zone` (`Atlantic/Faroe`), which needs the system time-zone database (present on normal hosts; slim Docker images need `tzdata`).
- `estimated_cost` (`GetEstimatedCostAsync`) takes the same request as `hourly_kwh_usage` and returns one reading per hour (observed 2026-11-21): `reading` is the hour's cost, `unit` is `"DKR"`, `price_unit` the price per kWh, `tarif` the installation's tariff number; no `cumulative_value`.
- `estimated_CO2` (`GetEstimatedCo2Async`) takes the same request and returns one reading per hour (observed 2026-11-22): `reading` in kg CO2, `unit` `"Kg CO2"`, `cumulative_value` always `null` (so `Co2Reading` leaves it out). SEV computes each hour as (1 − green share of production that hour) × kWh × 0.641 kg CO2/kWh.
- Hourly readings implement `IHourlyReading`, so a new hourly series can reuse the caching and summarizing.
- **`hourly_kwh_usage` returns at most 31 days per call and silently drops the rest** (observed 2026-11-21: a request for 2025-11-21 to 2026-11-21 returned 2025-11-21 to 2025-12-21 only). `SevDataService` splits longer ranges; a year therefore takes 12 calls, more than one rate-limit window.
- **Over the rate limit, SEV returns `200 OK` with an empty list**, not an error (observed 2026-11-21: `get_available_meters` returned `[]` after ~11 calls in a few minutes, and the meters again after waiting 5 minutes; the docs don't mention it). So an empty result usually means throttling: the tools report it via `SevToolErrors.ThrowIfEmpty` (meter list) and `ThrowIfEmptyAnswer` (usage, only when there are no cached readings to show either). Don't trigger it on purpose, and keep manual testing to a few calls at a time.
- Responses are standard HTTP status codes. On a non-success status `SevClient` throws `SevApiException` (derives from `HttpRequestException`) with `StatusCode`, `ResponseBody` and `RetryAfter`.

## CI and hosting

- CI (`.github/workflows/ci.yml`) runs `dotnet restore`, `dotnet build` and `dotnet test`. Its `deploy` job then publishes `Sev.McpServer` to Azure App Service on pushes to `main` and manual runs (`gh workflow run ci.yml --ref main`). It runs only when the repository variable `AZURE_WEBAPP_NAME` is set, and logs in with GitHub OIDC through the `production` environment's secrets (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`).
- `deploy/setup-azure.sh` (re-runnable, uses `az` and `gh`) sets everything up; the steps, connecting Claude and troubleshooting are in `docs/deploy-azure.md`. **Don't run it or change Azure resources without the user's go-ahead: it creates billed resources.**
  - It deploys from the checkout's `origin` repository by default (`GITHUB_REPO`, so it works from a fork).
  - It creates the App Service (Linux B1, .NET 10, Always On, startup `dotnet Sev.McpServer.dll`), two Entra app registrations (the API "SEV MCP": v2 tokens, `mcp.access` scope, the MCP URL as an Application ID URI; and Claude's client "SEV MCP (Claude)": Claude's callback, the client secret on first run), the app settings, and a deploy identity with a federated credential for the `production` environment.
  - The credential's subject is read from GitHub (`/actions/oidc/customization/sub`): this repo uses immutable, ID-based subjects (`repo:<owner>@<id>/<repo>@<id>:...`), not `repo:<owner>/<repo>:...`.
  - An existing resource group is reused wherever it is, and the plan is created in `LOCATION`, so a quota error can be fixed by running again in another region.
  - It sets App Service's Health check path to `/health`, and limits the `production` environment to `main`, failing loudly if GitHub refuses (it used to ignore errors there).
- `deploy/setup-backup.sh` (run at the end of the setup script, or alone with the same `RESOURCE_GROUP` / `LOCATION` / `APP`) creates a storage account in its own resource group `<group>-backup` (so deleting the app's group keeps the backups) and an App Service custom backup every day kept 30 days, including `/home` and so the heat pump log.
  - It registers `Microsoft.Storage` first (a new subscription answers `SubscriptionNotFound` otherwise).
  - Backups write through a container SAS valid 2 years; rerun the script before it expires. A new schedule starts with a backup straight away.
  - B1 also has hourly automatic backups built in, but they live with the app and can't be downloaded.

## Workflow

Every change, including docs, goes through an issue and a PR. Never commit directly to `main`.

1. `gh issue create` first.
2. Branch from an up-to-date `main` as `feature/<issue#>-<slug>` (e.g. `feature/9-docs-and-claude-md`).
3. Open the PR with `Closes #<issue#>` in the body.
4. Only merge when asked: wait for CI to pass, then `gh pr merge <pr#> --squash --delete-branch`.

Dependabot (`.github/dependabot.yml`) opens its own PRs every Monday for NuGet packages and GitHub Actions; they need no issue. Like any PR, merge one only when asked and after CI passes, and remember that merging deploys it. Major updates of `Microsoft.AspNetCore.*` are ignored because their major version follows .NET (11.x needs net11); move them together with `TargetFramework`.

Never rename the head branch of an open PR. GitHub closes the PR instead of retargeting it.
