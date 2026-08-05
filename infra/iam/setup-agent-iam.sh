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

echo "Agent IAM & Workload Identity configuration complete."
