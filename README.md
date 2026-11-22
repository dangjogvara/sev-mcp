# SEV MCP

An [MCP](https://modelcontextprotocol.io) server that lets Claude (or any other MCP client) read your electricity meters and hourly usage from [SEV](https://www.sev.fo), the Faroese electricity utility, through SEV's customer REST API.

Ask things like *"How much electricity did we use last month?"* or *"Which day this week used the most?"* and Claude fetches the numbers for you.

Optionally, it also reads your heat pump's current state through [myUplink](https://myuplink.com) (NIBE, CTC and other brands), so you can ask *"How is the heat pump doing?"*.

This is an unofficial, personal project. It isn't made or endorsed by SEV.

There's no shared service to sign up for: everyone runs their own copy with their own SEV account, on their own computer or in their own Azure subscription, so your data never passes through anyone else's server. See [Ways to run it](#ways-to-run-it).

## What it offers

| Tool | What it does |
|---|---|
| `list_meters` | Lists the meters on your SEV account, with address and install/removal dates. |
| `get_usage` | Electricity use in kWh per hour, day or month for a period, with a total per meter. Meters can be picked by ID, nickname, meter name or part of the address; leave them out to get all active meters. |
| `get_cost` | SEV's estimated cost of the electricity used, in DKK, per hour, day or month, with a total per meter. Same arguments as `get_usage`. It's an estimate and may differ from your bill. |
| `get_co2` | SEV's estimated CO2 emissions for the electricity used, in kg, per hour, day or month, with a total per meter. Same arguments as `get_usage`. SEV bases each hour on how much of the grid's power came from renewable sources that hour. |

`get_usage`, `get_cost` and `get_co2` take `from` and `to` (a date like `2026-10-10`, or a date and time like `2026-10-10T06:00`, in Faroese local time; `to` is not included) and `granularity` (`hour`, `day` or `month`). A call can cover at most 31 days by hour, 366 days by day, or 731 days by month.

Missing data is never shown as zero. When SEV has no reading for some hours, the result says how many are missing, and the total is `null` if there are no readings at all.

Optional, only when myUplink is configured (see below):

| Tool | What it does |
|---|---|
| `get_heatpump_status` | The heat pump's current state: outdoor, heating and hot water temperatures, what it's doing, compressor speed, settings such as setpoints, and active alarms. Read-only. |
| `get_heatpump_history` | Past readings (outdoor temperature by default, or any other measurement) per hour, day or month, from the server's own hourly log. Takes the same `from`, `to` and `granularity` as `get_usage`, so it lines up with electricity use. |

## Ways to run it

| | [Locally](#run-it-locally) | [Hosted on Azure](#host-your-own-on-azure) |
|---|---|---|
| Use it from | Claude Code on your computer | Claude on the web, desktop and phone |
| Runs | on your computer, while it's on | in your own Azure App Service, all the time |
| Login | none: only your computer can reach it | Microsoft Entra ID, only you (and people you allow) |
| Heat pump history | logged while the server runs | logged every hour around the clock |
| Cost | free | about 13 USD a month |

Both need your own access to SEV's customer REST API: a user name and an API key, which you get from SEV.

## Run it locally

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and your SEV API user name and key.

Store your credentials with .NET user-secrets, so they stay out of the repository:

```bash
dotnet user-secrets set "Sev:User" "<your user name>" --project Sev.McpServer
dotnet user-secrets set "Sev:Key" "<your API key>" --project Sev.McpServer
```

Start the server:

```bash
dotnet run --project Sev.McpServer --launch-profile http
```

It listens on `http://localhost:5133/mcp`. To connect [Claude Code](https://claude.com/claude-code) to it:

```bash
claude mcp add --transport http sev http://localhost:5133/mcp
```

Outside development (where user-secrets aren't loaded), set the environment variables `Sev__User` and `Sev__Key` instead. The server refuses to start without them.

### Optional: heat pump (myUplink)

If your heat pump reports to myUplink, register an application at [dev.myuplink.com](https://dev.myuplink.com) with your myUplink account and add its client ID and secret:

```bash
dotnet user-secrets set "MyUplink:ClientId" "<client id>" --project Sev.McpServer
dotnet user-secrets set "MyUplink:ClientSecret" "<client secret>" --project Sev.McpServer
```

Hosted, use the environment variables `MyUplink__ClientId` and `MyUplink__ClientSecret`. Without them there's no `get_heatpump_status` tool and nothing else changes. The server only reads (scope `READSYSTEM`), and myUplink allows 25 calls per minute.

myUplink doesn't give its history to apps (its history endpoints answered 403 with either kind of login), so the server logs the heat pump's measurements itself once an hour, about 2 myUplink calls each time. `get_heatpump_history` reads that log, so it only goes back to when logging started. With `Sev__CacheDirectory` set, the log is kept there (`HeatPumpLog-<year>-<month>.json`) and survives restarts; otherwise it's lost when the server stops. Set `MyUplink__LogHourly` to `false` to turn logging off.

## Host your own on Azure

To use it from the Claude apps on the web and your phone, host your own copy on Azure App Service. A setup script does nearly all of it; [docs/deploy-azure.md](docs/deploy-azure.md) has the details and troubleshooting.

You need:

- your SEV API user name and key
- an Azure subscription. A new or free-trial subscription may have no quota for the B1 plan in some regions; the script says what to do (request quota, or pick another region with `LOCATION=`). A free trial stops when it ends, so upgrade to pay-as-you-go to keep the server running.
- a GitHub account, the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) and the [GitHub CLI](https://cli.github.com/)
- optionally, a myUplink application for the heat pump tools (see [above](#optional-heat-pump-myuplink))

Steps:

1. **Fork** this repository on GitHub, and turn on Actions in your fork (its Actions tab), so it can deploy.
2. **Clone your fork** and put your credentials in a `.env` file in it (gitignored): `SEV_USER` and `SEV_KEY`, and optionally `MYUPLINK_CLIENT_ID` and `MYUPLINK_CLIENT_SECRET`.
3. **Run the setup script** after `az login` and `gh auth login`:
   ```bash
   ./deploy/setup-azure.sh
   ```
   It creates the web app, a Microsoft Entra login that only lets you in, a deploy identity that only trusts GitHub Actions in your fork, and daily backups. At the end it prints the MCP URL, a client ID and a client secret (shown once).
4. **Deploy:**
   ```bash
   gh workflow run ci.yml --ref main
   ```
5. **Connect Claude:** Settings > Connectors > Add custom connector, with the MCP URL, and under the OAuth settings the client ID and secret. Sign in with the same Microsoft account.

Good to know:

- **Your data stays yours.** Your credentials are app settings in your Azure subscription, and your usage and heat pump data are cached in your App Service and backed up to your own storage account. The server only talks to SEV, myUplink and your Claude.
- **Keep secrets out of the repository.** A fork of a public repository is public: never commit `.env`, and don't paste keys or client secrets into issues or chats.
- **Costs:** about 13 USD a month for the B1 plan, plus a few cents for backups. A budget alert (Cost Management > Budgets) warns you if anything unexpected costs money.
- **Updates:** press **Sync fork** on GitHub; the new commits on `main` are tested and deployed automatically. If an update adds tools, disconnect and reconnect the connector in Claude so it sees them; if it adds settings, run the setup script again (it's safe to rerun).
- **Your household** can use your server too, with your data: invite them as guests in your Entra tenant and add their object IDs as `Auth__AllowedUsers__1`, `Auth__AllowedUsers__2`, and so on.

## Working within SEV's limits

SEV's API has a few limits that shape how this server works:

- **10 calls per 5 minutes per customer**, logging in included. The server logs in once and reuses the token for almost 4 hours, counts its own calls and refuses an 11th before sending it.
- **At most 31 days of hourly data per call.** Longer periods are fetched in 31-day pieces, newest first, with at most 6 pieces per question. If a period doesn't fit, the answer says which part is still missing, and asking again a few minutes later fills it in.
- **Too many calls get an empty answer instead of an error.** The server tells Claude that this probably means the limit was reached, rather than reporting no usage.

To save calls, the meter list is kept for 6 hours and completed past days are cached, so asking about them again costs nothing. Locally the cache is kept in memory and lost when the server stops. With `Sev__CacheDirectory` set (hosted), it is also saved there as JSON files and reloaded after a restart or deploy.

## Security

The server runs in one of two modes:

- **Local (no login).** When no `Auth` section is configured, it only accepts requests from the same computer. It rejects requests addressed to any host other than `localhost`, and requests from web pages that aren't on `localhost` (against DNS rebinding). This is the default for `dotnet run` in Development. Outside Development the server refuses to start in this mode, so it can't be put online unprotected by mistake.
- **Hosted (OAuth login).** When `Auth` is configured, `/mcp` requires an OAuth access token from Microsoft Entra ID, issued to one of the allowed users. Claude finds out where to log in through the [protected resource metadata](https://datatracker.ietf.org/doc/html/rfc9728) at `/.well-known/oauth-protected-resource/mcp`.

Settings for the hosted mode, as environment variables:

| Setting | Example | Purpose |
|---|---|---|
| `Auth__Authority` | `https://login.microsoftonline.com/<tenant-id>/v2.0` | The token issuer (v2 tokens). |
| `Auth__Resource` | `https://<app>.azurewebsites.net/mcp` | The public MCP URL, exactly as entered in Claude. |
| `Auth__Audiences__0` | `<client-id>` | Accepted token audience: the Entra app registration's client ID. |
| `Auth__Scopes__0` | `api://<client-id>/mcp.access` | The scope Claude asks for; tokens without it get 403. |
| `Auth__AllowedUsers__0` | `<your Entra object ID>` | Who may use the server; anyone else gets 403. |
| `AllowedHosts` | `<app>.azurewebsites.net` | The host name the server answers to. |
| `Sev__CacheDirectory` | `/home/data/sev-cache` | Where the cache is saved so it survives restarts. Optional. |
| `MyUplink__ClientId`, `MyUplink__ClientSecret` | `<from dev.myuplink.com>` | Turns on the heat pump tools and the hourly log. Optional. |

[docs/deploy-azure.md](docs/deploy-azure.md) explains how to host it on Azure App Service. A setup script creates the Entra app registration and these settings, and GitHub Actions deploys `main`.

## Project structure

| Project | Purpose |
|---|---|
| `Sev.Client` | Class library for SEV's API: the client, login and token handling, rate limiting, and the cache. |
| `MyUplink.Client` | Class library for myUplink's API (heat pumps): login and reading systems, current values and alarms. |
| `Sev.McpServer` | The MCP server (ASP.NET Core, Streamable HTTP) with the `list_meters`, `get_usage`, `get_cost` and `get_co2` tools, and `get_heatpump_status` / `get_heatpump_history` plus the hourly heat pump log when myUplink is configured. |
| `Sev.Client.Tests`, `MyUplink.Client.Tests`, `Sev.McpServer.Tests` | xUnit tests. They never call the real APIs. |
| `Sev.SmokeTest` | A small console app that logs in and prints meters and one day of usage from the live API. |

SEV provides an API reference document to its API customers. An [OpenAPI description](https://github.com/dragan-norliga/sev-rest-clients/blob/master/DotNetExampleClient/api/openapi.yaml) of the API is also available. [CLAUDE.md](CLAUDE.md) has more detail on the design and on what has been learned about the API's behaviour.

## Development

```bash
dotnet build
dotnet test
```

To try the client against the live API, put `SEV_USER` and `SEV_KEY` in a `.env` file (it's gitignored) and run the smoke test:

```bash
set -a; source .env; set +a; dotnet run --project Sev.SmokeTest
```

Every run uses some of your 10 calls per 5 minutes, so keep manual testing to a few calls at a time.

## License

[MIT](LICENSE)
