# SEV MCP

An [MCP](https://modelcontextprotocol.io) server that lets Claude (or any other MCP client) read your electricity meters and hourly usage from [SEV](https://www.sev.fo), the Faroese electricity utility, through SEV's customer REST API.

Ask things like *"How much electricity did we use last month?"* or *"Which day this week used the most?"* and Claude fetches the numbers for you.

This is an unofficial, personal project. It isn't made or endorsed by SEV.

## What it offers

| Tool | What it does |
|---|---|
| `list_meters` | Lists the meters on your SEV account, with address and install/removal dates. |
| `get_usage` | Electricity use in kWh per hour, day or month for a period, with a total per meter. Meters can be picked by ID, nickname, meter name or part of the address; leave them out to get all active meters. |
| `get_cost` | SEV's estimated cost of the electricity used, in DKK, per hour, day or month, with a total per meter. Same arguments as `get_usage`. It's an estimate and may differ from your bill. |

`get_usage` and `get_cost` take `from` and `to` (a date like `2026-10-10`, or a date and time like `2026-10-10T06:00`, in Faroese local time; `to` is not included) and `granularity` (`hour`, `day` or `month`). A call can cover at most 31 days by hour, 366 days by day, or 731 days by month.

Missing data is never shown as zero. When SEV has no reading for some hours, the result says how many are missing, and the total is `null` if there are no readings at all.

## Getting started

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download)
- access to SEV's customer REST API: a user name and an API key

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

## Working within SEV's limits

SEV's API has a few limits that shape how this server works:

- **10 calls per 5 minutes per customer**, logging in included. The server logs in once and reuses the token for almost 4 hours, counts its own calls and refuses an 11th before sending it.
- **At most 31 days of hourly data per call.** Longer periods are fetched in 31-day pieces, newest first, with at most 6 pieces per question. If a period doesn't fit, the answer says which part is still missing, and asking again a few minutes later fills it in.
- **Too many calls get an empty answer instead of an error.** The server tells Claude that this probably means the limit was reached, rather than reporting no usage.

To save calls, the meter list is kept for 6 hours and completed past days are kept in memory, so asking about them again costs nothing. The cache is lost when the server restarts.

## Security

The server has **no authentication**, so it only accepts requests from the same computer: it rejects requests addressed to any host other than `localhost`, and requests from web pages that aren't on `localhost` (against DNS rebinding). Don't expose it to a network or the internet in its current form. Hosting it for the Claude web and mobile apps will need OAuth first.

## Project structure

| Project | Purpose |
|---|---|
| `Sev.Client` | Class library for SEV's API: the client, login and token handling, rate limiting, and the cache. |
| `Sev.McpServer` | The MCP server (ASP.NET Core, Streamable HTTP) with the `list_meters`, `get_usage` and `get_cost` tools. |
| `Sev.Client.Tests`, `Sev.McpServer.Tests` | xUnit tests. They never call the real API. |
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
