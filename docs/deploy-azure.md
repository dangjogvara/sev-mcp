# Deploying to Azure App Service

This puts SEV MCP online so you can use it from the Claude web and mobile apps. It runs on Azure App Service (Linux, .NET 10, no Docker). Claude logs in with Microsoft Entra ID, and only you are allowed in.

How it fits together:

- **Azure App Service** runs the server at `https://<app>-<hash>.<region>.azurewebsites.net/mcp`.
- **An Entra app registration** ("SEV MCP") is what Claude logs in with. Its tokens are checked by the server (see `McpLogin`).
- **GitHub Actions** deploys `main` after the tests pass. It logs in to Azure with a short-lived GitHub token through a federated credential, so no password is stored anywhere.

## 1. Before you start

- An Azure subscription. The App Service plan (B1) costs roughly 13 USD a month. Everything else used here is free.
- The [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az login`) and the [GitHub CLI](https://cli.github.com/) (`gh auth login`), logged in as yourself.
- `SEV_USER` and `SEV_KEY` in `.env`, as for the smoke test.

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

1. Creates the resource group `sev-mcp`, the Linux App Service plan `sev-mcp-plan` (B1), and the web app. The web app gets .NET 10, HTTPS only, Always On (so the app isn't put to sleep when idle, which would drop the SEV login and the rate-limit count), TLS 1.2 and FTP disabled.
2. Creates the Entra app registration "SEV MCP", the login Claude uses:
   - single tenant
   - Claude's callback URL `https://claude.ai/api/mcp/auth_callback`
   - v2 access tokens
   - an `mcp.access` scope
   - the MCP URL as an extra Application ID URI. Claude sends it as the token's `resource`, and Entra rejects it otherwise.
3. Creates a client secret for Claude, on the first run only.
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

At the end it prints the MCP URL, the client ID and the client secret. **The secret is shown only once:** copy it into Claude (step 4), then clear your terminal.

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
- **OAuth client:** use your own, and enter the client ID and client secret from the script

When you connect, Claude opens Microsoft's sign-in page. Sign in with the account you ran the script with, and accept the "Use SEV MCP" permission. The connector then works in the Claude mobile app too.

## Troubleshooting

- **Watch the server's log** (startup errors, requests):
  ```bash
  az webapp log tail -g sev-mcp -n <app>
  ```
- **"Operation cannot be completed without additional quota" (B1 limit 0):** new subscriptions often have no App Service quota in some regions. Either request 1 B1 instance at https://aka.ms/azure-quota (provider App Service, your region) and run the script again, or run it again in another region, e.g. `LOCATION=swedencentral ./deploy/setup-azure.sh`.
- **The deploy job fails at "Azure login" with AADSTS700213 ("No matching federated identity record"):** the deploy identity trusts a different token subject than GitHub sends. The log shows the subject GitHub sent (`subject claim - repo:...`). Repositories with GitHub's immutable, ID-based subjects send `repo:<owner>@<id>/<repo>@<id>:environment:production`; the script reads the right form from GitHub and fixes an existing credential, so running it again (with the same settings) is enough. Then rerun the failed job.
- **The server doesn't start:** look for "Invalid Auth configuration" or "requires a login" in the log. One of the `Auth__` app settings is missing or wrong. Re-running the script sets them again.
- **AADSTS9010010 or `invalid_target` during sign-in:** Entra doesn't recognise the `resource` Claude sent. Check that the app registration's Application ID URIs include the MCP URL exactly (no trailing slash), and that `requestedAccessTokenVersion` is 2 in its manifest.
- **The script fails at "Application ID URI":** your tenant has a policy against non-verified URIs. Check that the token version really is 2 (the exemption depends on it), or ask your tenant admin about an exemption.
- **403 after signing in:** you signed in with a different account than the one in `Auth__AllowedUsers__0`, or the token lacks the `mcp.access` scope. Check that `Auth__Scopes__0` is `api://<client-id>/mcp.access` and that the app registration still exposes the `mcp.access` scope (re-running the script sets both).
- **The connector keeps asking you to sign in:** the log shows why each token was rejected (audience, issuer, expiry). Also check that the client secret in Claude hasn't expired (it's valid for 2 years).
- **New client secret:** this adds a new secret alongside the old one; enter it in Claude, then delete the old one in the Entra portal.
  ```bash
  az ad app credential reset --id <client-id> --display-name "Claude connector" --years 2 --append --query password -o tsv
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
az ad app delete --id <client-id>
```

The backup group is separate on purpose: delete it only if you don't want the backups any more.

Also remove the connector in Claude, and the `production` environment and `AZURE_WEBAPP_NAME` variable on GitHub.
