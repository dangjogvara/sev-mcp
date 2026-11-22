#!/usr/bin/env bash
# Daily backup of the web app to a storage account. See "Backups" in docs/deploy-azure.md.
#
# App Service already makes hourly automatic backups on B1, but they're kept with the app: they can't be
# downloaded and go away with it. This adds a custom backup of the whole app, including /home (the SEV cache
# and the heat pump log, which can't be fetched again), to a storage account in its own resource group, so
# deleting the app or its resource group leaves the backups.
#
# setup-azure.sh runs it at the end; it can also be run on its own, with the same settings:
#   RESOURCE_GROUP=sev-mcp-swe LOCATION=swedencentral ./deploy/setup-backup.sh
# Safe to run again: it reuses the storage account and renews the SAS the backup uses. The SAS is valid for
# SAS_YEARS years; run the script again before then, or backups stop.
set -euo pipefail

LOCATION="${LOCATION:-northeurope}"
RESOURCE_GROUP="${RESOURCE_GROUP:-sev-mcp}"
APP="${APP:-sev-mcp}"
BACKUP_RESOURCE_GROUP="${BACKUP_RESOURCE_GROUP:-$RESOURCE_GROUP-backup}"
BACKUP_CONTAINER="${BACKUP_CONTAINER:-websitebackups}"
BACKUP_FREQUENCY="${BACKUP_FREQUENCY:-1d}"
BACKUP_RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-30}"
SAS_YEARS="${SAS_YEARS:-2}"

step() { printf '\n==> %s\n' "$*"; }
fail() { printf '\nError: %s\n' "$*" >&2; exit 1; }

command -v az >/dev/null || fail "az isn't installed."
az account show -o none 2>/dev/null || fail "Not logged in to Azure: run 'az login'."
az webapp show -g "$RESOURCE_GROUP" -n "$APP" -o none 2>/dev/null \
  || fail "Web app '$APP' not found in '$RESOURCE_GROUP'. Use the same RESOURCE_GROUP and APP as for setup-azure.sh."

step "Resource group '$BACKUP_RESOURCE_GROUP' for backups"
if ! az group show --name "$BACKUP_RESOURCE_GROUP" -o none 2>/dev/null; then
  az group create --name "$BACKUP_RESOURCE_GROUP" --location "$LOCATION" -o none
fi

step "Storage account"
# A new subscription may not have the storage provider registered yet ("SubscriptionNotFound").
if [[ "$(az provider show --namespace Microsoft.Storage --query registrationState -o tsv)" != "Registered" ]]; then
  echo "Registering the Microsoft.Storage provider (takes a minute or two)..."
  az provider register --namespace Microsoft.Storage --wait -o none
fi
STORAGE="$(az storage account list -g "$BACKUP_RESOURCE_GROUP" --query "[0].name" -o tsv)"
if [[ -z "$STORAGE" ]]; then
  # Storage account names are global: 3-24 lowercase letters and digits.
  STORAGE="sevmcpbackup$(python3 -c 'import secrets; print(secrets.token_hex(4))')"
  az storage account create -g "$BACKUP_RESOURCE_GROUP" -n "$STORAGE" -l "$LOCATION" \
    --sku Standard_LRS --kind StorageV2 --https-only true --min-tls-version TLS1_2 \
    --allow-blob-public-access false -o none
fi
echo "$STORAGE"

# App Service backups only accept a SAS URL (no managed identity), so the account key signs one. The key is
# passed through the environment rather than the command line, and neither it nor the SAS is printed.
AZURE_STORAGE_KEY="$(az storage account keys list -g "$BACKUP_RESOURCE_GROUP" -n "$STORAGE" --query "[0].value" -o tsv)"
export AZURE_STORAGE_KEY
az storage container create --name "$BACKUP_CONTAINER" --account-name "$STORAGE" --public-access off -o none

step "Daily backup of '$APP', kept $BACKUP_RETENTION_DAYS days"
EXPIRY="$(python3 -c "from datetime import datetime, timedelta, timezone; print((datetime.now(timezone.utc) + timedelta(days=365 * $SAS_YEARS)).strftime('%Y-%m-%dT%H:%MZ'))")"
SAS="$(az storage container generate-sas --name "$BACKUP_CONTAINER" --account-name "$STORAGE" \
  --permissions rwdl --expiry "$EXPIRY" --https-only -o tsv)"
CONTAINER_URL="https://$STORAGE.blob.core.windows.net/$BACKUP_CONTAINER?$SAS"
# --retain-one keeps the latest backup even if backups stop for longer than the retention.
az webapp config backup update -g "$RESOURCE_GROUP" -n "$APP" --container-url "$CONTAINER_URL" \
  --frequency "$BACKUP_FREQUENCY" --retention "$BACKUP_RETENTION_DAYS" --retain-one true -o none

# A new schedule starts with a backup straight away (observed 2026-11-22), so there's no separate first backup.
cat <<EOF

Backups: every $BACKUP_FREQUENCY to $STORAGE/$BACKUP_CONTAINER (resource group $BACKUP_RESOURCE_GROUP), kept $BACKUP_RETENTION_DAYS days.
The first one starts now and takes a minute or two. Check them with:
  az webapp config backup list -g $RESOURCE_GROUP -n $APP --query "[?storageAccountUrl!=null].{created:created,status:status,size:sizeInBytes}" -o table
The SAS the backups use expires $EXPIRY: run this script again before then.
EOF
