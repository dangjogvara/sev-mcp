# SEV MCP

An [MCP](https://modelcontextprotocol.io) server that lets Claude (or any other MCP client) read your electricity meters and hourly usage from [SEV](https://www.sev.fo), the Faroese electricity utility, through SEV's customer REST API. Ask things like *"How much electricity did we use last month?"* or *"Which day this week used the most?"* and Claude fetches the numbers for you.

Optionally, it also reads your heat pump through [myUplink](https://myuplink.com) (NIBE, CTC and other brands): *"How is the heat pump doing?"*

This is an unofficial, personal project, not made or endorsed by SEV. There's no shared service: everyone runs their own copy with their own SEV account, so your data never passes through anyone else's server.

## What it offers

| Tool | What it does |
|---|---|
| `list_meters` | The meters on your SEV account, with address and install/removal dates. |
| `get_usage` | Electricity use in kWh per hour, day or month, with a total per meter. |
| `get_cost` | SEV's estimated cost in DKK, per hour, day or month, and each meter's price per kWh. An estimate; it may differ from your bill. |
| `get_co2` | SEV's estimated CO2 emissions in kg, based on how green the grid was each hour. |
| `get_heatpump_status` | *Optional, with myUplink.* The heat pump's temperatures, status, settings and alarms right now. |
| `get_heatpump_history` | *Optional, with myUplink.* Past heat pump readings, such as outdoor temperature, from the server's own hourly log. |

Periods are given as dates or date-times in Faroese local time, per hour, day or month (up to 31 days by hour, 366 by day or 731 by month per question). Meters can be picked by ID, nickname or part of the address, or left out for all of them; if SEV's portal can't name your meters, you can give them nicknames in the server's settings (`Sev:MeterNicknames:<meter ID>`, see the [settings reference](docs/deploy-azure.md#settings-reference)). Missing data is never shown as zero: results say how many hours have no reading.

## Ways to run it

| | [Locally](#run-it-locally) | [Hosted on Azure](#host-your-own-on-azure) |
|---|---|---|
| Use it from | Claude Code on your computer | Claude on the web, desktop and phone |
| Runs | on your computer, while it's on | in your own Azure App Service, all the time |
| Login | none: only your computer can reach it | Microsoft Entra ID, only you (and people you allow) |
| Cost | free | about 13 USD a month |

Either way you need your own access to SEV's customer REST API: a user name and an API key, which you get from SEV.

## Run it locally

With the [.NET 10 SDK](https://dotnet.microsoft.com/download) installed, store your SEV credentials with user-secrets (they stay out of the repository), start the server, and connect [Claude Code](https://claude.com/claude-code) to it:

```bash
dotnet user-secrets set "Sev:User" "<your user name>" --project Sev.McpServer
dotnet user-secrets set "Sev:Key" "<your API key>" --project Sev.McpServer
dotnet run --project Sev.McpServer --launch-profile http
claude mcp add --transport http sev http://localhost:5133/mcp
```

For the heat pump tools, see [docs/heat-pump.md](docs/heat-pump.md).

## Host your own on Azure

To use it from the Claude apps on the web and your phone, host your own copy. A setup script does nearly all of it:

1. **Fork** this repository and turn on Actions in your fork.
2. **Clone your fork** and put `SEV_USER` and `SEV_KEY` in a `.env` file (gitignored), plus `MYUPLINK_CLIENT_ID` and `MYUPLINK_CLIENT_SECRET` for the heat pump.
3. **Run** `./deploy/setup-azure.sh` after `az login` and `gh auth login`. It creates the web app, a login that only lets you in, deployment from your fork, and daily backups, and prints the URL, client ID and client secret for Claude.
4. **Deploy** with `gh workflow run ci.yml --ref main`.
5. **Connect Claude:** Settings > Connectors > Add custom connector.

Your credentials and data stay in your own Azure subscription. A fork of a public repository is public, so never commit `.env`. [docs/deploy-azure.md](docs/deploy-azure.md) has the details: what you need, every setting, updates, backups, costs and troubleshooting.

## Good to know

- **SEV allows 10 calls per 5 minutes** and at most 31 days of hourly data per call. The server caches finished days and splits long periods; if a period doesn't fit in one question, the answer says which part is missing, and asking again a few minutes later fills it in. Over the limit, SEV answers with empty data instead of an error; the server says so rather than reporting no usage.
- **The server is locked down.** Locally it only accepts requests from your own computer (including against DNS rebinding from web pages). Hosted, it requires a Microsoft Entra login and lets in only the users you list, and it refuses to start online without one.
- **It only reads.** It can't change anything at SEV or on your heat pump.

## Development

```bash
dotnet build
dotnet test
```

The tests never call the real APIs. To try the client against the live API, put `SEV_USER` and `SEV_KEY` in `.env` and run `set -a; source .env; set +a; dotnet run --project Sev.SmokeTest`; keep it to a few runs at a time because of the rate limit.

| Project | Purpose |
|---|---|
| `Sev.Client` | SEV's API: client, login, rate limiting and cache. |
| `MyUplink.Client` | myUplink's API (heat pumps): login, current values and alarms. |
| `Sev.McpServer` | The MCP server (ASP.NET Core, Streamable HTTP) and its tools. |
| `*.Tests` | xUnit tests for each of the above. |
| `Sev.SmokeTest` | A console app that prints meters and one day of usage from the live API. |

[CLAUDE.md](CLAUDE.md) describes the design and what has been learned about the APIs' behaviour. SEV provides an API reference to its API customers, and an [OpenAPI description](https://github.com/dragan-norliga/sev-rest-clients/blob/master/DotNetExampleClient/api/openapi.yaml) is also available.

## License

[MIT](LICENSE)
