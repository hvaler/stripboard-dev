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
#   --max-instances=3     Raised from 1 now that state lives in Cloud SQL (EV-22). With
#                         session affinity keeping a circuit on its instance and the
#                         schedule shared in Postgres, a second instance no longer serves
#                         a different film.
#   --timeout=3600        The WebSocket is one long-lived request; the 300s default cuts it.
#
set -euo pipefail

PROJECT="${GCP_PROJECT:-stripboard-hack}"
REGION="${GCP_REGION:-europe-west1}"
SERVICE="${SERVICE_NAME:-stripboard-web}"

# Observability (EV-20/EV-29). The OpenTelemetry SDK reads these standard variables, so
# the Grafana Cloud credential is mounted straight from Secret Manager and never appears
# in the image, the repo or this script.
OTLP_ENDPOINT="${OTLP_ENDPOINT:-https://otlp-gateway-prod-eu-north-0.grafana.net/otlp}"
OTLP_SECRET="${OTLP_SECRET:-grafana-otlp-headers}"

# Persistence (EV-22). The connection string lives in Secret Manager and reaches the
# container as ConnectionStrings__Stripboard; the Cloud SQL connector mounts the instance
# on a Unix socket, so no database password travels over the network from here.
SQL_INSTANCE="${SQL_INSTANCE:-stripboard-db}"
SQL_CONNECTION_NAME="${SQL_CONNECTION_NAME:-$(gcloud sql instances describe "${SQL_INSTANCE}" \
  --project "${PROJECT}" --format='value(connectionName)' 2>/dev/null || true)}"
DB_SECRET="${DB_SECRET:-stripboard-db-connection}"

if [[ -n "${SQL_CONNECTION_NAME}" ]]; then
  echo "Cloud SQL: ${SQL_CONNECTION_NAME}"
  SQL_ARGS=(--add-cloudsql-instances "${SQL_CONNECTION_NAME}")
  SECRET_ARGS="OTEL_EXPORTER_OTLP_HEADERS=${OTLP_SECRET}:latest,ConnectionStrings__Stripboard=${DB_SECRET}:latest"
else
  echo "Cloud SQL: no instance found - the app will run on an in-memory database and say so."
  SQL_ARGS=()
  SECRET_ARGS="OTEL_EXPORTER_OTLP_HEADERS=${OTLP_SECRET}:latest"
fi

# "Ask your shoot" is answered by the Conflict Sentinel, deployed separately by
# infra/deploy-sentinel.sh. Discovered rather than hardcoded, and simply absent when the
# sentinel has not been deployed — the page then says so instead of failing obscurely.
SENTINEL_URL="${SENTINEL_URL:-$(gcloud run services describe "${SENTINEL_SERVICE:-stripboard-sentinel}" \
  --project "${PROJECT}" --region "${REGION}" --format='value(status.url)' 2>/dev/null || true)}"
if [[ -n "${SENTINEL_URL}" ]]; then
  echo "Conflict Sentinel: ${SENTINEL_URL}"
else
  echo "Conflict Sentinel: not deployed — 'Ask your shoot' will report itself unconfigured."
fi
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
  --max-instances 3 \
  --timeout 3600 \
  --cpu 1 \
  --memory 1Gi \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Production,OTEL_EXPORTER_OTLP_ENDPOINT=${OTLP_ENDPOINT},OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf,OTEL_METRIC_EXPORT_INTERVAL=15000,Sentinel__BaseUrl=${SENTINEL_URL}" \
  --set-secrets "${SECRET_ARGS}" \
  "${SQL_ARGS[@]}"

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
