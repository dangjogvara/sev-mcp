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

What it does:

1. Creates the resource group `sev-mcp`, the Linux App Service plan `sev-mcp-plan` (B1), and the web app. The web app gets .NET 10, HTTPS only, Always On (so the cache and rate-limit count survive), TLS 1.2 and FTP disabled.
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
5. Creates the deploy identity `sev-mcp-deploy`. It can only deploy to this web app, and only trusts GitHub jobs in this repository's `production` environment.
6. Creates that GitHub environment (limited to `main`), its secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the repository variable `AZURE_WEBAPP_NAME`. The deploy job runs only once this variable exists.

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
- **The server doesn't start:** look for "Invalid Auth configuration" or "requires a login" in the log. One of the `Auth__` app settings is missing or wrong. Re-running the script sets them again.
- **AADSTS9010010 or `invalid_target` during sign-in:** Entra doesn't recognise the `resource` Claude sent. Check that the app registration's Application ID URIs include the MCP URL exactly (no trailing slash), and that `requestedAccessTokenVersion` is 2 in its manifest.
- **The script fails at "Application ID URI":** your tenant has a policy against non-verified URIs. Check that the token version really is 2 (the exemption depends on it), or ask your tenant admin about an exemption.
- **403 after signing in:** you signed in with a different account than the one in `Auth__AllowedUsers__0`.
- **The connector keeps asking you to sign in:** the log shows why each token was rejected (audience, issuer, expiry). Also check that the client secret in Claude hasn't expired (it's valid for 2 years).
- **New client secret:** this adds a new secret alongside the old one; enter it in Claude, then delete the old one in the Entra portal.
  ```bash
  az ad app credential reset --id <client-id> --display-name "Claude connector" --years 2 --append --query password -o tsv
  ```

## Optional: only accept Anthropic's servers

Claude's custom connectors reach your server from Anthropic's cloud, not from your phone. For an extra layer, you can limit the web app to Anthropic's outbound IP ranges ([listed by Anthropic](https://platform.claude.com/docs/en/api/ip-addresses)) with App Service access restrictions. The login already blocks everyone else, and the ranges can change, so this is optional.

## Costs and cleanup

The B1 plan is the only paid part. To delete everything (this stops the costs):

```bash
az group delete --name sev-mcp
az ad app delete --id <client-id>
```

Also remove the connector in Claude, and the `production` environment and `AZURE_WEBAPP_NAME` variable on GitHub.
