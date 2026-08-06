#!/usr/bin/env bash
# ==============================================================================
# Stripboard Infrastructure as Code (IaC) — Agent IAM & Workload Identity
# ADR-004: Dedicated Service Accounts per Agent & Least Privilege Access
# ==============================================================================

set -euo pipefail

PROJECT_ID="stripboard-hack"
REGION="europe-west1"

echo "Configuring dedicated Service Accounts for Stripboard multi-agent network in ${PROJECT_ID}..."

AGENT_SERVICE_ACCOUNTS=(
    "sa-breakdown:Breakdown Agent Service Account"
    "sa-scheduler:CP-SAT Scheduler Agent Service Account"
    "sa-sentinel:Conflict Sentinel Watcher Service Account"
    "sa-replanner:Replanner Agent Service Account"
    "sa-callsheets:Call Sheets Generator Service Account"
    "sa-orchestrator:Orchestrator Agent Service Account (Vertex AI Agent Engine)"
    # One identity per MCP server (EV-23). These are servers rather than agents, and they
    # are separate from each other on purpose: the roles granted below differ, and a shared
    # account would give the weather server the database access the schedule server needs.
    "sa-mcp-schedule:MCP Schedule Server Service Account"
    "sa-mcp-people:MCP People Server Service Account"
    "sa-mcp-locations:MCP Locations Server Service Account"
    "sa-mcp-weather:MCP Weather Server Service Account"
)

for sa in "${AGENT_SERVICE_ACCOUNTS[@]}"; do
    SA_NAME="${sa%%:*}"
    SA_DISPLAY="${sa#*:}"
    
    if ! gcloud iam service-accounts describe "${SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com" &>/dev/null; then
        echo "Creating Service Account: ${SA_NAME}"
        gcloud iam service-accounts create "${SA_NAME}" \
            --display-name="${SA_DISPLAY}" \
            --project="${PROJECT_ID}"
    else
        echo "Service Account ${SA_NAME} already exists."
    fi
done

# Assign Secret Manager Access to Sentinel Token for sa-sentinel
gcloud secrets add-iam-policy-binding grafana-sentinel-token \
    --member="serviceAccount:sa-sentinel@${PROJECT_ID}.iam.gserviceaccount.com" \
    --role="roles/secretmanager.secretAccessor" \
    --project="${PROJECT_ID}" || true

# The orchestrator runs on Vertex AI Agent Engine under its own identity (EV-26/ADR-018),
# so it needs to use Vertex AI and to read the staging bucket its package is uploaded to.
# It gets nothing else: committing a schedule is refused in the application, not by IAM,
# because the rule is "only a human Producer" rather than "only this principal".
for role in roles/aiplatform.user roles/storage.objectViewer; do
    gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
        --member="serviceAccount:sa-orchestrator@${PROJECT_ID}.iam.gserviceaccount.com" \
        --role="${role}" \
        --condition=None >/dev/null
    echo "sa-orchestrator granted ${role}"
done

# The MCP servers (EV-23). Three of them read and write the shooting schedule in Cloud SQL,
# so they need cloudsql.client and the connection string; the fourth does not.
#
# sa-mcp-weather is granted NOTHING, and that is the point rather than an oversight. The
# weather server generates a deterministic synthetic forecast and touches no data, so it is
# physically unable to reach the schedule. "Cannot" is a stronger claim than "does not", and
# it is the one a `gcloud projects get-iam-policy` dump can settle.
for name in schedule people locations; do
    SA="sa-mcp-${name}@${PROJECT_ID}.iam.gserviceaccount.com"
    gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
        --member="serviceAccount:${SA}" \
        --role="roles/cloudsql.client" \
        --condition=None >/dev/null
    gcloud secrets add-iam-policy-binding stripboard-db-connection \
        --member="serviceAccount:${SA}" \
        --role="roles/secretmanager.secretAccessor" \
        --project="${PROJECT_ID}" >/dev/null || true
    echo "sa-mcp-${name} granted cloudsql.client and read on stripboard-db-connection"
done
echo "sa-mcp-weather granted nothing: it has no database to reach."

echo "Agent IAM & Workload Identity configuration complete."
