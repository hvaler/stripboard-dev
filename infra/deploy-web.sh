#!/usr/bin/env bash
#
# Deploys the Blazor web app to Cloud Run with the settings a stateful SignalR circuit
# actually needs (EV-30).
#
# The defaults are all wrong for Blazor Server, which is why the first deployment showed
# "An unhandled error has occurred / Rejoining the server…":
#
#   --no-cpu-throttling   Cloud Run throttles CPU between requests by default. A Blazor
#                         circuit is idle between user interactions, so its SignalR
#                         heartbeats never run and the connection is dropped. This is the
#                         single most important flag here.
#   --session-affinity    Without it a reconnect can land on a different instance, which
#                         has no idea about the caller's circuit.
#   --min-instances=1     Scale-to-zero destroys every live circuit and adds a cold start
#                         in front of the judge.
#   --max-instances=1     The database is still in-memory (EV-22). A second instance would
#                         serve a different schedule, so a disruption injected on one
#                         instance would be invisible on the other. Raise this only once
#                         Cloud SQL is wired.
#   --timeout=3600        The WebSocket is one long-lived request; the 300s default cuts it.
#
set -euo pipefail

PROJECT="${GCP_PROJECT:-stripboard-hack}"
REGION="${GCP_REGION:-europe-west1}"
SERVICE="${SERVICE_NAME:-stripboard-web}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ ! -f "${REPO_ROOT}/.dockerignore" ]]; then
  echo "error: .dockerignore is missing. The Dockerfile does 'COPY . .', so building" >&2
  echo "       without it would copy .secrets/ into the image." >&2
  exit 1
fi

echo "Deploying ${SERVICE} to ${REGION} (project ${PROJECT})…"

gcloud run deploy "${SERVICE}" \
  --source "${REPO_ROOT}" \
  --project "${PROJECT}" \
  --region "${REGION}" \
  --allow-unauthenticated \
  --port 8080 \
  --no-cpu-throttling \
  --session-affinity \
  --min-instances 1 \
  --max-instances 1 \
  --timeout 3600 \
  --cpu 1 \
  --memory 1Gi \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Production"

URL="$(gcloud run services describe "${SERVICE}" --project "${PROJECT}" --region "${REGION}" \
        --format='value(status.url)')"

echo
echo "Deployed: ${URL}"
echo "Checking readiness…"

for _ in $(seq 1 30); do
  if curl -sf "${URL}/api/health" >/dev/null 2>&1; then
    echo "Healthy:"
    curl -s "${URL}/api/health"
    echo
    exit 0
  fi
  sleep 2
done

echo "error: ${URL}/api/health did not become healthy." >&2
exit 1
