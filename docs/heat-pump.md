# Heat pump (myUplink)

Optionally, SEV MCP also reads your heat pump through [myUplink](https://myuplink.com), the cloud service that NIBE, CTC and several other brands report to. You can then ask Claude things like *"How is the heat pump doing?"* or *"Compare my electricity use with the outdoor temperature this week."*

Without the settings below, none of this is switched on and the server only has the SEV tools.

## Tools

| Tool | What it does |
|---|---|
| `get_heatpump_status` | The current state: outdoor, heating and hot water temperatures, what the heat pump is doing, compressor speed, the immersion heater, settings such as setpoints (marked `setting: true`), and active alarms. |
| `get_heatpump_history` | Past readings per hour, day or month, from the server's own hourly log (see below). It takes the same `from`, `to` and `granularity` as `get_usage`, so it lines up with your electricity use. Outdoor temperature by default; ask for other readings by name, or `all`. |

Both only read: the server asks myUplink for read access only (scope `READSYSTEM`) and can't change any settings.

Values come grouped by the manufacturer's categories, with their English names. Parameters without an English name and sensors that aren't connected are left out, and coded values such as status are given as their label ("On", "Normal").

## Setting it up

1. Register an application at [dev.myuplink.com](https://dev.myuplink.com), signed in with your myUplink account. Any name will do. If it asks for a callback URL, `https://localhost` is fine: the server logs in as the application and doesn't use it.
2. Give the server the application's client ID and secret:
   - **Locally**, with user-secrets:
     ```bash
     dotnet user-secrets set "MyUplink:ClientId" "<client id>" --project Sev.McpServer
     dotnet user-secrets set "MyUplink:ClientSecret" "<client secret>" --project Sev.McpServer
     ```
   - **Hosted**, put `MYUPLINK_CLIENT_ID` and `MYUPLINK_CLIENT_SECRET` in `.env` before running the setup script, which copies them to the app settings `MyUplink__ClientId` and `MyUplink__ClientSecret`.
3. Restart the server. If Claude was already connected, disconnect and connect the connector again so it sees the new tools.

Both settings are needed: with only one of them, the server stops at startup and says so.

## The hourly log

myUplink has endpoints for a parameter's history, but they refuse apps: they answered 403 both with the application's own login and with a user's login, even with an active myUplink history subscription.

So the server keeps its own history. Every 5 minutes it checks whether the current hour has been logged, and if not, reads the heat pump and saves its measurements. That's about 2 myUplink calls an hour. Settings, status codes and software versions aren't logged; `get_heatpump_status` shows them as they are now.

- **It starts when you switch it on.** There's no history from before; `get_heatpump_history` says when logging started.
- **It's kept with the cache.** With `Sev__CacheDirectory` set (hosted: `/home/data/sev-cache`), the log is saved there as one file per month (`HeatPumpLog-2026-11.json`) and survives restarts and deploys. Without it (normal locally), it's kept in memory and lost when the server stops.
- **Hours without a sample** (the server was down, or myUplink didn't answer) are reported as missing, never as zero.
- **It can't be fetched again** if it's lost, so the hosted setup backs it up daily (see [Backups](deploy-azure.md#backups)).
- **To switch it off,** set `MyUplink:LogHourly` (`MyUplink__LogHourly` hosted) to `false`.

## Limits and notes

- **myUplink allows 25 calls per minute.** A status question costs about 2 calls, so it's hard to reach.
- **Other models:** the tools pick readings by name and category rather than by the manufacturer's parameter numbers, so other myUplink heat pumps should work too. They've only been tested with a CTC heat pump.
- **No energy use:** the heat pump tested reports no kWh counters, so electricity use comes from SEV (`get_usage`).
