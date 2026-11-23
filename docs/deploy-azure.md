# Deploying to Azure App Service

This puts SEV MCP online so you can use it from the Claude web and mobile apps. It runs on Azure App Service (Linux, .NET 10, no Docker). Claude logs in with Microsoft Entra ID, and only you are allowed in.

How it fits together:

- **Azure App Service** runs the server at `https://<app>-<hash>.<region>.azurewebsites.net/mcp`.
- **Two Entra app registrations:** "SEV MCP" stands for the server (the API the tokens are for), and "SEV MCP (Claude)" is what Claude logs in with. The server checks the tokens (see `McpLogin`). They're separate because Entra won't renew a token an app asked for itself when it's named by its URL (AADSTS90009), and Claude names the server by its URL.
- **GitHub Actions** deploys `main` after the tests pass. It logs in to Azure with a short-lived GitHub token through a federated credential, so no password is stored anywhere.

## 1. Before you start

- Your own copy of this repository: fork it on GitHub, turn on Actions in the fork (its Actions tab), and clone the fork. The script deploys from the checkout's `origin`; set `GITHUB_REPO=<owner>/<repo>` to pick another repository.
- An Azure subscription. The App Service plan (B1) costs roughly 13 USD a month. Everything else used here is free.
- The [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az login`) and the [GitHub CLI](https://cli.github.com/) (`gh auth login`), logged in as yourself.
- `SEV_USER` and `SEV_KEY` in `.env`, as for the smoke test. Optionally `MYUPLINK_CLIENT_ID` and `MYUPLINK_CLIENT_SECRET` too, for the heat pump tools.
- A free-trial subscription works, but it stops when the trial ends: upgrade to pay-as-you-go to keep the server running.

## 2. Run the setup script

From the repository root:

```bash
./deploy/setup-azure.sh
```

Settings can be changed with environment variables. The most likely one is the app name, which must be free in the region:

```bash
APP=sev-mcp-yourname LOCATION=northeurope ./deploy/setup-azure.sh
```

If you change `RESOURCE_GROUP` or `LOCATION`, pass the same values every time you run the script, and use your group name wherever the commands below say `sev-mcp` after `-g` or `--name`. An existing resource group is reused wherever it is, so running again with another `LOCATION` (after a quota error) creates the plan and app in the new region.

What it does:

1. Creates the resource group `sev-mcp`, the Linux App Service plan `sev-mcp-plan` (B1), and the web app. The web app gets .NET 10, HTTPS only, Always On (so the app isn't put to sleep when idle, which would drop the SEV login and the rate-limit count), a [Health check](#health-check) on `/health`, TLS 1.2 and FTP disabled.
2. Creates the Entra app registration "SEV MCP", the server's API:
   - single tenant
   - v2 access tokens
   - an `mcp.access` scope
   - the MCP URL as an extra Application ID URI. Claude sends it as the token's `resource`, and Entra rejects it otherwise.
3. Creates the Entra app registration "SEV MCP (Claude)", the login Claude uses:
   - single tenant
   - Claude's callback URL `https://claude.ai/api/mcp/auth_callback`
   - permission to the API's `mcp.access` scope, pre-authorized by the API so signing in doesn't ask for it
   - a client secret, on the first run only
4. Sets the app settings:
   - your SEV credentials
   - `Auth__Authority`, `Auth__Resource`, `Auth__Audiences__0`, `Auth__Scopes__0`
   - `Auth__AllowedUsers__0`: your own Entra object ID, so only you get in
   - `AllowedHosts`
   - `Sev__CacheDirectory`: `/home/data/sev-cache`, where the cache is saved. `/home` survives restarts and deploys, so cached days don't have to be fetched from SEV again.
   - `MyUplink__ClientId` and `MyUplink__ClientSecret`, only if `MYUPLINK_CLIENT_ID` and `MYUPLINK_CLIENT_SECRET` are in `.env`: they turn on the optional heat pump tools and the hourly heat pump log, which is kept in the same directory as the cache.
5. Creates the deploy identity `sev-mcp-deploy`. It can only deploy to this web app, and only trusts GitHub jobs in this repository's `production` environment.
6. Creates that GitHub environment (limited to `main`), its secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the repository variable `AZURE_WEBAPP_NAME`. The deploy job runs only once this variable exists.
7. Sets up a daily backup of the app (`deploy/setup-backup.sh`), see [Backups](#backups).

At the end it prints the MCP URL, Claude's client ID and the client secret. **The secret is shown only once:** copy it into Claude (step 4), then clear your terminal.

The script is safe to run again, for example after changing a setting. It reuses what exists, and doesn't create a new secret.

## 3. Deploy

Either merge something to `main`, or start a deployment by hand:

```bash
gh workflow run ci.yml --ref main
```

The `deploy` job in the CI run publishes `Sev.McpServer` and uploads it. When it's done, this should return JSON naming your MCP URL:

```bash
curl https://<your-host>/.well-known/oauth-protected-resource/mcp
```

## 4. Connect Claude

In Claude (web or desktop app): **Settings > Connectors > Add custom connector**.

- **URL:** the MCP URL printed by the script, e.g. `https://sev-mcp-abc123.northeurope-01.azurewebsites.net/mcp`
- **OAuth client:** use your own, and enter the client ID and client secret from the script (those of "SEV MCP (Claude)")

When you connect, Claude opens Microsoft's sign-in page. Sign in with the account you ran the script with; if it asks, accept the permissions. The connector then works in the Claude mobile app too.

## Keeping it up to date

- **New versions:** on GitHub, press **Sync fork** on your fork (or merge from this repository). The push to `main` runs the tests and deploys.
- **New tools:** Claude keeps the list of tools from when the connector was connected. After an update that adds tools, open **Settings > Connectors**, disconnect the connector and connect it again, then start a new chat.
- **From one app registration to two (issue #123):** setups from before then have Claude log in with "SEV MCP" itself, and Entra refuses to renew those logins, so the connector asks you to sign in again every hour or two. Run the setup script again with the same `RESOURCE_GROUP`, `LOCATION` and `APP`: it creates "SEV MCP (Claude)" with a client secret and removes Claude's callback from "SEV MCP". In Claude, remove the connector and add it again with the new client ID and secret. Then delete the old client secret on "SEV MCP" (Entra portal > App registrations > SEV MCP > Certificates & secrets).
- **New settings:** if an update adds settings, run the setup script again with the same `RESOURCE_GROUP`, `LOCATION` and `APP`. It reuses everything and doesn't create a new client secret.
- **Package updates:** Dependabot opens PRs for new package versions in this repository, and syncing your fork brings them in. In a fork Dependabot may be off; turn it on under Settings > Code security if you want its PRs there too.

## Keep it to one instance

Run the server as a single instance: don't scale the App Service plan out to more than one, and don't turn on autoscale. Several things live in the server's memory and only work if there's exactly one copy:

- **SEV's rate limit** is counted in memory (`SevRateLimiter`). Two instances would each think they had 10 calls, and SEV would start answering with empty data.
- **The SEV and myUplink logins** are shared tokens in memory, so each instance would log in separately and spend calls doing it.
- **The cache and the heat pump log** are in memory and saved to files in `/home`, which all instances share. Two instances would overwrite each other's files, and both would log the heat pump every hour.
- **MCP sessions** (Streamable HTTP) live in memory, so a client's next request has to reach the same instance.

B1 runs one instance by default, so nothing needs doing; just don't change it. More instances would need that state moved out of the process first (for example the cache to a database and the rate limit to a shared store).

## Health check

The server answers `GET /health` (and `GET /`) with 200 without a login, and without calling SEV or myUplink. The setup script turns on App Service's Health check for `/health`: it pings it every minute and, with one instance, replaces the instance if it stays unhealthy for an hour. The status is under **Monitoring > Health check** on the web app. Turning Health check on or off restarts the app.

## Meter nicknames

SEV's API can't set a meter's nickname, so you can give your meters nicknames in the server's settings instead. Claude then shows them in `list_meters` and accepts them when you ask about a meter ("how much did the heat pump use?"). A nickname set here replaces SEV's for that meter. Use the meter IDs that `list_meters` shows:

```bash
az webapp config appsettings set -g sev-mcp -n <app> --settings Sev__MeterNicknames__12345=House Sev__MeterNicknames__67890="Heat pump"
```

Locally, use user-secrets: `dotnet user-secrets set "Sev:MeterNicknames:12345" "House" --project Sev.McpServer`. Changing app settings restarts the app. A key that isn't a meter ID, or an empty nickname, stops the server at startup with a message saying which.

## Your data and your secrets

- Your credentials are app settings in your Azure subscription, and your usage and heat pump data are cached in your App Service (`/home/data/sev-cache`) and backed up to your own storage account. The server only talks to SEV, myUplink and your Claude.
- A fork of a public repository is public: never commit `.env`, and don't paste keys or client secrets into issues or chats.

## More users in your household

Others can use your server too, with your data. Invite them as guests in your Entra tenant (Entra ID > Users > Invite external user), then add each one's object ID as the next `Auth__AllowedUsers__<n>` app setting (`Auth__AllowedUsers__1`, `__2`, ...). They add the same connector in their own Claude and sign in as themselves.

## Settings reference

The setup script sets these app settings (environment variables). You only need this table to change them by hand or to host the server somewhere else.

| Setting | Example | Purpose |
|---|---|---|
| `Sev__User`, `Sev__Key` | from SEV | Your SEV API user name and key. Required. |
| `Auth__Authority` | `https://login.microsoftonline.com/<tenant-id>/v2.0` | The token issuer (v2 tokens). Without the `Auth` settings the server only accepts local use, and outside Development it refuses to start. |
| `Auth__Resource` | `https://<app>.azurewebsites.net/mcp` | The public MCP URL, exactly as entered in Claude. |
| `Auth__Audiences__0` | `<api-id>` | Accepted token audience: the application (client) ID of the "SEV MCP" app registration, not Claude's. |
| `Auth__Scopes__0` | `api://<api-id>/mcp.access` | The scope Claude asks for; tokens without it get 403. |
| `Auth__AllowedUsers__0`, `__1`, ... | `<Entra object ID>` | Who may use the server; anyone else gets 403. |
| `AllowedHosts` | `<app>.azurewebsites.net` | The host name the server answers to. |
| `Sev__MeterNicknames__<meter ID>` | `House` | A nickname for the meter with that ID (as `list_meters` shows it), replacing SEV's. One setting per meter. Optional. |
| `Sev__CacheDirectory` | `/home/data/sev-cache` | Where the cache and the heat pump log are saved so they survive restarts. Optional. |
| `MyUplink__ClientId`, `MyUplink__ClientSecret` | from dev.myuplink.com | Turn on the heat pump tools and the hourly log ([heat-pump.md](heat-pump.md)). Optional. |
| `MyUplink__LogHourly` | `false` | Turns the hourly heat pump log off. Optional; on by default. |

## Troubleshooting

- **Watch the server's log** (startup errors, requests):
  ```bash
  az webapp log tail -g sev-mcp -n <app>
  ```
- **"Operation cannot be completed without additional quota" (B1 limit 0):** new subscriptions often have no App Service quota in some regions. Either request 1 B1 instance at https://aka.ms/azure-quota (provider App Service, your region) and run the script again, or run it again in another region, e.g. `LOCATION=swedencentral ./deploy/setup-azure.sh`.
- **The deploy job fails at "Azure login" with AADSTS700213 ("No matching federated identity record"):** the deploy identity trusts a different token subject than GitHub sends. The log shows the subject GitHub sent (`subject claim - repo:...`). Repositories with GitHub's immutable, ID-based subjects send `repo:<owner>@<id>/<repo>@<id>:environment:production`; the script reads the right form from GitHub and fixes an existing credential, so running it again (with the same settings) is enough. Then rerun the failed job.
- **The server doesn't start:** look for "Invalid Auth configuration" or "requires a login" in the log. One of the `Auth__` app settings is missing or wrong. Re-running the script sets them again.
- **AADSTS9010010 or `invalid_target` during sign-in:** Entra doesn't recognise the `resource` Claude sent. Check that the "SEV MCP" app registration's Application ID URIs include the MCP URL exactly (no trailing slash), and that `requestedAccessTokenVersion` is 2 in its manifest.
- **The script fails at "Application ID URI":** your tenant has a policy against non-verified URIs. Check that the token version really is 2 (the exemption depends on it), or ask your tenant admin about an exemption.
- **403 after signing in:** you signed in with a different account than the one in `Auth__AllowedUsers__0`, or the token lacks the `mcp.access` scope. Check that `Auth__Scopes__0` is `api://<api-id>/mcp.access` and that "SEV MCP" still exposes the `mcp.access` scope (re-running the script sets both).
- **The connector asks you to sign in again every hour or two:** Claude couldn't renew its login. Since issue #115 the server asks for a refresh token (`offline_access` in its login metadata); if you connected before that, disconnect and connect the connector once. If Entra's sign-in log (Entra ID > Monitoring > Sign-in logs > User sign-ins (non-interactive)) shows AADSTS90009, Claude is logging in with "SEV MCP" itself: move to two app registrations, see [Keeping it up to date](#keeping-it-up-to-date).
- **The connector keeps asking you to sign in:** the log shows why each token was rejected (audience, issuer, expiry). Also check that the client secret in Claude hasn't expired (it's valid for 2 years).
- **New client secret:** this adds a new secret alongside the old one; enter it in Claude, then delete the old one in the Entra portal.
  ```bash
  az ad app credential reset --id <claude-client-id> --display-name "Claude connector" --years 2 --append --query password -o tsv
  ```

## Backups

The heat pump log can't be fetched again if it's lost (myUplink gives apps no history), so the app is backed up in two ways:

- **Automatic backups** (built in on B1, nothing to set up): every hour, kept 30 days, including `/home`. They're stored with the app, can't be downloaded, and go away if the app or subscription does.
- **A daily custom backup** (`deploy/setup-backup.sh`, run by the setup script): the whole app, including `/home/data/sev-cache` (the SEV cache and the heat pump log), as a ZIP in the storage account in the resource group `<group>-backup` (container `websitebackups`), kept 30 days. Keeping it in its own resource group means deleting the app's group doesn't delete the backups. It costs a few cents a month.

Run the backup script on its own with the same settings as the setup script:

```bash
RESOURCE_GROUP=sev-mcp LOCATION=northeurope ./deploy/setup-backup.sh
```

- **Renew before the SAS expires:** custom backups write through a SAS URL (App Service doesn't support a managed identity here). The script makes one valid for 2 years and prints the date; run it again before then, or the backups stop.
- **Check the backups:** `az webapp config backup list -g <group> -n <app> -o table`, or **Backups** on the web app in the portal.
- **Restore:** in the portal, **Backups** → pick a backup → **Restore**, to the same app or a new one. App Service stops the app while restoring.
- **Get one file back:** download the backup's ZIP from the storage account and take it from `fs/data/sev-cache/`. Don't change files in the `websitebackups` container, or the backups can't be restored.

## Optional: only accept Anthropic's servers

Claude's custom connectors reach your server from Anthropic's cloud, not from your phone. For an extra layer, you can limit the web app to Anthropic's outbound IP ranges ([listed by Anthropic](https://platform.claude.com/docs/en/api/ip-addresses)) with App Service access restrictions. The login already blocks everyone else, and the ranges can change, so this is optional.

## Costs and cleanup

The B1 plan is the main cost; the backup storage adds a few cents a month. To delete everything (this stops the costs):

```bash
az group delete --name sev-mcp
az group delete --name sev-mcp-backup
az ad app delete --id <api-id>
az ad app delete --id <claude-client-id>
```

The backup group is separate on purpose: delete it only if you don't want the backups any more.

Also remove the connector in Claude, and the `production` environment and `AZURE_WEBAPP_NAME` variable on GitHub.
