#!/usr/bin/env bash
# One-time setup of everything SEV MCP needs in Azure and GitHub. See docs/deploy-azure.md.
#
# Run from the repository root, logged in with `az login` and `gh auth login`:
#   ./deploy/setup-azure.sh
# Settings can be changed with environment variables, e.g. APP=my-sev-mcp ./deploy/setup-azure.sh
# Use the same ones every time you run it (RESOURCE_GROUP in particular), or it sets up a second copy.
#
# It's safe to run again: existing resources are reused, and settings are brought up to date.
# It creates a client secret for Claude only the first time (Claude's Entra app is new); see the
# guide for making a new one.
set -euo pipefail

LOCATION="${LOCATION:-northeurope}"
RESOURCE_GROUP="${RESOURCE_GROUP:-sev-mcp}"
PLAN="${PLAN:-sev-mcp-plan}"
SKU="${SKU:-B1}"
APP="${APP:-sev-mcp}"
ENTRA_APP_NAME="${ENTRA_APP_NAME:-SEV MCP}"
CLIENT_APP_NAME="${CLIENT_APP_NAME:-SEV MCP (Claude)}"
DEPLOY_IDENTITY="${DEPLOY_IDENTITY:-sev-mcp-deploy}"
# The GitHub repository that deploys: by default the one this checkout came from (your fork).
GITHUB_REPO="${GITHUB_REPO:-$(git remote get-url origin 2>/dev/null | sed -E 's#^(git@github\.com:|https://github\.com/)##; s#\.git$##')}"
CLAUDE_CALLBACK="https://claude.ai/api/mcp/auth_callback"
SCOPE_NAME="mcp.access"

step() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nError: %s\n' "$*" >&2; exit 1; }

# Role assignments can fail for a minute while a new identity spreads through Azure.
retry() {
  local attempt
  for attempt in 1 2 3 4 5 6; do
    if "$@"; then return 0; fi
    echo "   (not ready yet, retrying in 10 seconds)"
    sleep 10
  done
  return 1
}

for tool in az gh python3; do
  command -v "$tool" >/dev/null || fail "$tool isn't installed."
done
az account show -o none 2>/dev/null || fail "Not logged in to Azure: run 'az login'."
gh auth status >/dev/null 2>&1 || fail "Not logged in to GitHub: run 'gh auth login'."
[[ "$GITHUB_REPO" =~ ^[^/[:space:]]+/[^/[:space:]]+$ ]] \
  || fail "Couldn't tell which GitHub repository deploys (from 'git remote get-url origin'). Set GITHUB_REPO=<owner>/<repo>."
gh repo view "$GITHUB_REPO" --json name >/dev/null 2>&1 || fail "GitHub repository '$GITHUB_REPO' not found or not accessible."

# SEV credentials (and optionally myUplink's, for the heat pump tools): from the environment, or from .env
# like the smoke test.
if [[ -f .env ]] && [[ -z "${SEV_USER:-}" || -z "${SEV_KEY:-}" || -z "${MYUPLINK_CLIENT_ID:-}" ]]; then
  set -a; source .env; set +a
fi
[[ -n "${SEV_USER:-}" && -n "${SEV_KEY:-}" ]] || fail "Set SEV_USER and SEV_KEY, or put them in .env."
export SEV_USER SEV_KEY

TMP="$(mktemp -d)"
chmod 700 "$TMP"
trap 'rm -rf "$TMP"' EXIT

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
TENANT_ID="$(az account show --query tenantId -o tsv)"
USER_OID="$(az ad signed-in-user show --query id -o tsv)"
echo "Subscription: $(az account show --query name -o tsv)"
echo "You (allowed user): $(az ad signed-in-user show --query userPrincipalName -o tsv)"
echo "GitHub repository: $GITHUB_REPO"

# --- App Service ---------------------------------------------------------------------------

step "Resource group '$RESOURCE_GROUP'"
# An existing group is reused wherever it is: a group's own region doesn't limit where its resources
# go, so running again with another LOCATION (after a quota error, say) works.
if az group show --name "$RESOURCE_GROUP" -o none 2>/dev/null; then
  echo "Exists (in $(az group show --name "$RESOURCE_GROUP" --query location -o tsv))."
else
  az group create --name "$RESOURCE_GROUP" --location "$LOCATION" -o none
fi

step "App Service plan '$PLAN' ($SKU, Linux)"
if ! az appservice plan show -g "$RESOURCE_GROUP" -n "$PLAN" -o none 2>/dev/null; then
  az appservice plan create -g "$RESOURCE_GROUP" -n "$PLAN" --sku "$SKU" --is-linux --location "$LOCATION" -o none \
    || fail "Couldn't create the plan in $LOCATION. If Azure says the $SKU quota is 0, request more at
       https://aka.ms/azure-quota (provider App Service), or run again in another region, e.g.
       LOCATION=swedencentral ./deploy/setup-azure.sh. See Troubleshooting in docs/deploy-azure.md."
fi
echo "In $(az appservice plan show -g "$RESOURCE_GROUP" -n "$PLAN" --query location -o tsv)."

step "Web app '$APP' (.NET 10)"
if ! az webapp show -g "$RESOURCE_GROUP" -n "$APP" -o none 2>/dev/null; then
  az webapp create -g "$RESOURCE_GROUP" -n "$APP" --plan "$PLAN" --runtime "DOTNETCORE:10.0" \
    --domain-name-scope TenantReuse --https-only true -o none
fi
# Always On keeps the one instance (and with it the cache and rate-limit count) running.
az webapp config set -g "$RESOURCE_GROUP" -n "$APP" --always-on true --min-tls-version 1.2 \
  --ftps-state Disabled --startup-file "dotnet Sev.McpServer.dll" -o none
# Health check pings /health every minute; an instance that stays unhealthy for an hour is replaced.
az webapp config set -g "$RESOURCE_GROUP" -n "$APP" --generic-configurations '{"healthCheckPath": "/health"}' -o none
HOST="$(az webapp show -g "$RESOURCE_GROUP" -n "$APP" --query defaultHostName -o tsv)"
RESOURCE="https://$HOST/mcp"
echo "MCP URL: $RESOURCE"

# --- Entra app registrations: the server's API and Claude's login ---------------------------
#
# Two registrations: "SEV MCP" is the API the tokens are for (the mcp.access scope, the MCP URL as
# an Application ID URI), and "SEV MCP (Claude)" is the client Claude signs in with. They must be
# separate: Claude sends the MCP URL as the "resource", and Entra refuses an app a token for itself
# unless it's named by its api://<guid> URI (AADSTS90009), which broke Claude's token refresh.

app_id_named() { az ad app list --filter "displayName eq '$1'" --query '[0].appId' -o tsv; }
ensure_sp() { az ad sp show --id "$1" -o none 2>/dev/null || az ad sp create --id "$1" -o none; }
patch_app() {
  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$1" \
    --headers Content-Type=application/json --body "@$2" -o none
}

step "Entra app registration '$ENTRA_APP_NAME' (the server's API)"
APP_ID="$(app_id_named "$ENTRA_APP_NAME")"
if [[ -z "$APP_ID" ]]; then
  APP_ID="$(az ad app create --display-name "$ENTRA_APP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
fi
APP_OBJECT_ID="$(az ad app show --id "$APP_ID" --query id -o tsv)"
ensure_sp "$APP_ID"

# v2 tokens and the mcp.access scope first: Entra only accepts the azurewebsites.net URL as an
# Application ID URI on apps that use v2 tokens. No redirect URI: nobody signs in with this app
# (setups from before the split had Claude's callback here).
SCOPE_ID="$(az ad app show --id "$APP_ID" --query "api.oauth2PermissionScopes[?value=='$SCOPE_NAME'].id | [0]" -o tsv)"
[[ -n "$SCOPE_ID" ]] || SCOPE_ID="$(python3 -c 'import uuid; print(uuid.uuid4())')"
SCOPE_ID="$SCOPE_ID" SCOPE_NAME="$SCOPE_NAME" python3 - "$TMP/api.json" <<'PY'
import json, os, sys
description = "Read your SEV electricity meters, usage, cost and CO2 through SEV MCP."
json.dump({
    "api": {
        "requestedAccessTokenVersion": 2,
        "oauth2PermissionScopes": [{
            "id": os.environ["SCOPE_ID"],
            "value": os.environ["SCOPE_NAME"],
            "type": "User",
            "isEnabled": True,
            "adminConsentDisplayName": "Use SEV MCP",
            "adminConsentDescription": description,
            "userConsentDisplayName": "Use SEV MCP",
            "userConsentDescription": description,
        }],
    },
    "web": {"redirectUris": []},
}, open(sys.argv[1], "w"))
PY
patch_app "$APP_OBJECT_ID" "$TMP/api.json"

# Claude sends the MCP URL as the token's "resource", so Entra must know it as one of the API's URIs.
printf '{"identifierUris": ["api://%s", "%s"]}' "$APP_ID" "$RESOURCE" > "$TMP/uris.json"
patch_app "$APP_OBJECT_ID" "$TMP/uris.json" \
  || fail "Entra refused '$RESOURCE' as an Application ID URI. See Troubleshooting in docs/deploy-azure.md."

step "Entra app registration '$CLIENT_APP_NAME' (Claude's login)"
CLIENT_ID="$(app_id_named "$CLIENT_APP_NAME")"
NEW_CLIENT=false
if [[ -z "$CLIENT_ID" ]]; then
  CLIENT_ID="$(az ad app create --display-name "$CLIENT_APP_NAME" --sign-in-audience AzureADMyOrg \
    --web-redirect-uris "$CLAUDE_CALLBACK" --query appId -o tsv)"
  NEW_CLIENT=true
fi
CLIENT_OBJECT_ID="$(az ad app show --id "$CLIENT_ID" --query id -o tsv)"
ensure_sp "$CLIENT_ID"
# Claude's callback, and the API's mcp.access scope as the permission it asks for.
APP_ID="$APP_ID" SCOPE_ID="$SCOPE_ID" CLAUDE_CALLBACK="$CLAUDE_CALLBACK" python3 - "$TMP/client.json" <<'PY'
import json, os, sys
json.dump({
    "web": {"redirectUris": [os.environ["CLAUDE_CALLBACK"]]},
    "requiredResourceAccess": [{
        "resourceAppId": os.environ["APP_ID"],
        "resourceAccess": [{"id": os.environ["SCOPE_ID"], "type": "Scope"}],
    }],
}, open(sys.argv[1], "w"))
PY
patch_app "$CLIENT_OBJECT_ID" "$TMP/client.json"

# The API trusts Claude's login for mcp.access, so signing in doesn't ask for consent to it.
CLIENT_ID="$CLIENT_ID" SCOPE_ID="$SCOPE_ID" python3 - "$TMP/preauth.json" <<'PY'
import json, os, sys
json.dump({"api": {"preAuthorizedApplications": [
    {"appId": os.environ["CLIENT_ID"], "delegatedPermissionIds": [os.environ["SCOPE_ID"]]},
]}}, open(sys.argv[1], "w"))
PY
patch_app "$APP_OBJECT_ID" "$TMP/preauth.json"

CLIENT_SECRET=""
if [[ "$NEW_CLIENT" == true ]]; then
  CLIENT_SECRET="$(az ad app credential reset --id "$CLIENT_ID" --display-name "Claude connector" \
    --years 2 --append --query password -o tsv)"
fi
# Setups from before the split gave Claude a secret on the API's registration; it's no longer used.
OLD_SECRETS="$(az ad app show --id "$APP_ID" --query 'length(passwordCredentials)' -o tsv)"

# --- App settings ---------------------------------------------------------------------------

step "App settings (SEV credentials and login)"
# Written to a private temp file rather than the command line, so the SEV key doesn't show up
# in the process list.
AUTHORITY="https://login.microsoftonline.com/$TENANT_ID/v2.0" RESOURCE="$RESOURCE" APP_ID="$APP_ID" \
  USER_OID="$USER_OID" HOST="$HOST" SCOPE_NAME="$SCOPE_NAME" python3 - "$TMP/settings.json" <<'PY'
import json, os, sys
e = os.environ
settings = {
    "Sev__User": e["SEV_USER"],
    "Sev__Key": e["SEV_KEY"],
    "Auth__Authority": e["AUTHORITY"],
    "Auth__Resource": e["RESOURCE"],
    "Auth__Audiences__0": e["APP_ID"],
    "Auth__Scopes__0": f"api://{e['APP_ID']}/{e['SCOPE_NAME']}",
    "Auth__AllowedUsers__0": e["USER_OID"],
    "AllowedHosts": e["HOST"],
    # /home survives restarts and deploys; /home/site/wwwroot is replaced by every deploy.
    "Sev__CacheDirectory": "/home/data/sev-cache",
}
# Optional heat pump tools: only when myUplink credentials are in .env (or the environment).
if e.get("MYUPLINK_CLIENT_ID") and e.get("MYUPLINK_CLIENT_SECRET"):
    settings["MyUplink__ClientId"] = e["MYUPLINK_CLIENT_ID"]
    settings["MyUplink__ClientSecret"] = e["MYUPLINK_CLIENT_SECRET"]
json.dump([{"name": k, "value": v, "slotSetting": False} for k, v in settings.items()], open(sys.argv[1], "w"))
PY
chmod 600 "$TMP/settings.json"
az webapp config appsettings set -g "$RESOURCE_GROUP" -n "$APP" --settings "@$TMP/settings.json" -o none

# --- Deployment from GitHub Actions ---------------------------------------------------------

step "Deploy identity '$DEPLOY_IDENTITY' for GitHub Actions"
az identity create -g "$RESOURCE_GROUP" -n "$DEPLOY_IDENTITY" -o none
DEPLOY_CLIENT_ID="$(az identity show -g "$RESOURCE_GROUP" -n "$DEPLOY_IDENTITY" --query clientId -o tsv)"
DEPLOY_PRINCIPAL_ID="$(az identity show -g "$RESOURCE_GROUP" -n "$DEPLOY_IDENTITY" --query principalId -o tsv)"
# Trusts GitHub's OIDC tokens for jobs in this repo's "production" environment, and nothing else.
# The token's subject starts with repo:<owner>/<repo>, or with repo:<owner>@<id>/<repo>@<id> when the
# repository uses GitHub's immutable, ID-based subjects (newer repositories do). GitHub reports which.
if [[ -z "${OIDC_SUBJECT:-}" ]]; then
  OIDC_PREFIX="$(gh api "repos/$GITHUB_REPO/actions/oidc/customization/sub" \
    --jq "if .use_default == false then \"\" else (.sub_claim_prefix // \"repo:$GITHUB_REPO\") end")"
  [[ -n "$OIDC_PREFIX" ]] || fail "This repository has a custom OIDC subject template. Set OIDC_SUBJECT to the
       subject its production jobs get (the deploy log shows it) and run again."
  OIDC_SUBJECT="$OIDC_PREFIX:environment:production"
fi
echo "Trusted subject: $OIDC_SUBJECT"
CURRENT_SUBJECT="$(az identity federated-credential show -g "$RESOURCE_GROUP" --identity-name "$DEPLOY_IDENTITY" \
  -n github-production --query subject -o tsv 2>/dev/null || true)"
if [[ "$CURRENT_SUBJECT" != "$OIDC_SUBJECT" ]]; then
  # Creates the credential, or replaces an out-of-date subject on an existing one.
  verb=create
  [[ -z "$CURRENT_SUBJECT" ]] || verb=update
  az identity federated-credential "$verb" -g "$RESOURCE_GROUP" --identity-name "$DEPLOY_IDENTITY" \
    -n github-production --issuer https://token.actions.githubusercontent.com \
    --subject "$OIDC_SUBJECT" --audiences api://AzureADTokenExchange -o none
fi
WEBAPP_ID="$(az webapp show -g "$RESOURCE_GROUP" -n "$APP" --query id -o tsv)"
retry az role assignment create --assignee-object-id "$DEPLOY_PRINCIPAL_ID" \
  --assignee-principal-type ServicePrincipal --role "Website Contributor" --scope "$WEBAPP_ID" -o none \
  || fail "Couldn't give the deploy identity access to the web app."

step "GitHub environment 'production' (main only), secrets and variable"
echo '{"deployment_branch_policy": {"protected_branches": false, "custom_branch_policies": true}}' \
  | gh api -X PUT "repos/$GITHUB_REPO/environments/production" --input - >/dev/null
# Only main may deploy. Created once; any error other than "already there" stops the script, since
# without the policy any branch could deploy.
POLICIES="$(gh api "repos/$GITHUB_REPO/environments/production/deployment-branch-policies" \
  --jq '[.branch_policies[] | select(.type == "branch") | .name] | join(" ")')" \
  || fail "Couldn't read the deployment branch policies of the 'production' environment."
if [[ " $POLICIES " != *" main "* ]]; then
  gh api "repos/$GITHUB_REPO/environments/production/deployment-branch-policies" -f name=main -f type=branch >/dev/null \
    || fail "Couldn't limit the 'production' environment to the main branch."
fi
echo "Deploys only from: main"
gh secret set AZURE_CLIENT_ID --env production --repo "$GITHUB_REPO" --body "$DEPLOY_CLIENT_ID"
gh secret set AZURE_TENANT_ID --env production --repo "$GITHUB_REPO" --body "$TENANT_ID"
gh secret set AZURE_SUBSCRIPTION_ID --env production --repo "$GITHUB_REPO" --body "$SUBSCRIPTION_ID"
gh variable set AZURE_WEBAPP_NAME --repo "$GITHUB_REPO" --body "$APP"

# --- Backups -------------------------------------------------------------------------------

# Daily backup of the app, including the heat pump log, to its own storage account. See setup-backup.sh.
LOCATION="$LOCATION" RESOURCE_GROUP="$RESOURCE_GROUP" APP="$APP" "$(dirname "$0")/setup-backup.sh"

# --- What to do next ------------------------------------------------------------------------

cat <<EOF

Done. Next steps (details in docs/deploy-azure.md):

1. Deploy: merge to main, or run:  gh workflow run ci.yml --ref main
2. In Claude: Settings > Connectors > Add custom connector
     URL:            $RESOURCE
     OAuth client:   use your own
     Client ID:      $CLIENT_ID
EOF
if [[ -n "$CLIENT_SECRET" ]]; then
  cat <<EOF
     Client secret:  $CLIENT_SECRET

   The client secret is shown only now and expires in 2 years. Copy it into Claude, then clear
   your terminal. Don't save it anywhere else.
EOF
else
  echo "     Client secret:  the one from the first run (to make a new one, see the guide)"
fi
if [[ "${OLD_SECRETS:-0}" != 0 ]]; then
  cat <<EOF

   '$ENTRA_APP_NAME' still has a client secret from before Claude got its own app registration.
   Once Claude works with the client ID above, delete it (Entra portal > App registrations >
   $ENTRA_APP_NAME > Certificates & secrets).
EOF
fi
cat <<EOF

To delete everything later:
  az group delete --name $RESOURCE_GROUP
  az ad app delete --id $APP_ID
  az ad app delete --id $CLIENT_ID
EOF
