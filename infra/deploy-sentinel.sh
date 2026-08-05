#!/usr/bin/env bash
#
# Deploys the Conflict Sentinel and the Grafana MCP server as one multi-container Cloud Run
# service, and lets only the web app call it (EV-31).
#
# The service is deliberately NOT public. It answers questions by calling Gemini, so an
# open endpoint is a bill anyone on the internet can run up. The Blazor app reaches it with
# an identity token minted from its own service account.
#
# Usage:
#   ./infra/deploy-sentinel.sh                 # build, push, deploy, authorise, verify
#   SKIP_BUILD=1 ./infra/deploy-sentinel.sh    # redeploy the images already published
#
set -euo pipefail

PROJECT="${GCP_PROJECT:-stripboard-hack}"
REGION="${GCP_REGION:-europe-west1}"
SERVICE="${SENTINEL_SERVICE:-stripboard-sentinel}"
WEB_SERVICE="${WEB_SERVICE:-stripboard-web}"
GRAFANA_URL="${GRAFANA_URL:-https://pinkcorridor3522.grafana.net}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

AR="${REGION}-docker.pkg.dev/${PROJECT}/stripboard"
WEB_SA="sa-stripboard-web@${PROJECT}.iam.gserviceaccount.com"

if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
  echo "Building the sentinel image…"
  gcloud auth configure-docker "${REGION}-docker.pkg.dev" --quiet >/dev/null
  docker build -q -f "${REPO_ROOT}/agents/Dockerfile.sentinel" -t "${AR}/sentinel:latest" "${REPO_ROOT}" >/dev/null

  # The MCP server is Grafana's official image, mirrored into Artifact Registry because
  # Cloud Run pulls from there.
  echo "Mirroring grafana/mcp-grafana…"
  docker pull -q grafana/mcp-grafana:latest >/dev/null
  docker tag grafana/mcp-grafana:latest "${AR}/mcp-grafana:latest"

  docker push -q "${AR}/sentinel:latest" >/dev/null
  docker push -q "${AR}/mcp-grafana:latest" >/dev/null
fi

# Deploy by digest, not by :latest.
#
# `gcloud run services replace` diffs the spec, and a spec that still says :latest is
# byte-identical to the one already deployed — so Cloud Run creates no revision and keeps
# serving the old image while this script prints "deployed". That is the worst kind of
# deployment: a successful-looking one that changed nothing. Pinning the digest makes the
# spec differ exactly when the image differs.
digest_of() {
  gcloud artifacts docker images describe "${AR}/$1:latest" \
    --project "${PROJECT}" --format='value(image_summary.digest)'
}

SENTINEL_DIGEST="$(digest_of sentinel)"
MCP_DIGEST="$(digest_of mcp-grafana)"
echo "sentinel     ${SENTINEL_DIGEST}"
echo "mcp-grafana  ${MCP_DIGEST}"

echo "Deploying ${SERVICE}…"
RENDERED="$(mktemp)"
trap 'rm -f "${RENDERED}"' EXIT
sed -e "s|__PROJECT__|${PROJECT}|g" \
    -e "s|__REGION__|${REGION}|g" \
    -e "s|__GRAFANA_URL__|${GRAFANA_URL}|g" \
    -e "s|stripboard/sentinel:latest|stripboard/sentinel@${SENTINEL_DIGEST}|g" \
    -e "s|stripboard/mcp-grafana:latest|stripboard/mcp-grafana@${MCP_DIGEST}|g" \
    "${REPO_ROOT}/infra/cloudrun/sentinel-service.yaml" > "${RENDERED}"

BEFORE="$(gcloud run services describe "${SERVICE}" --project "${PROJECT}" --region "${REGION}" \
  --format='value(status.latestCreatedRevisionName)' 2>/dev/null || true)"

gcloud run services replace "${RENDERED}" --project "${PROJECT}" --region "${REGION}" --quiet

AFTER="$(gcloud run services describe "${SERVICE}" --project "${PROJECT}" --region "${REGION}" \
  --format='value(status.latestCreatedRevisionName)')"

if [[ -n "${BEFORE}" && "${BEFORE}" == "${AFTER}" ]]; then
  echo "note: no new revision (${AFTER}) — the images and spec are unchanged since the last deploy."
else
  echo "New revision: ${AFTER}"
fi

# Only the web app may invoke it. No allUsers binding anywhere.
echo "Authorising ${WEB_SA} to invoke ${SERVICE}…"
gcloud run services add-iam-policy-binding "${SERVICE}" \
  --project "${PROJECT}" --region "${REGION}" \
  --member "serviceAccount:${WEB_SA}" --role roles/run.invoker --quiet >/dev/null

URL="$(gcloud run services describe "${SERVICE}" --project "${PROJECT}" --region "${REGION}" \
        --format='value(status.url)')"
echo
echo "Sentinel deployed (private): ${URL}"

echo "Verifying it refuses anonymous callers…"
CODE="$(curl -s -o /dev/null -w '%{http_code}' "${URL}/api/health" || true)"
if [[ "${CODE}" == "403" || "${CODE}" == "401" ]]; then
  echo "  anonymous request -> HTTP ${CODE}, as intended"
else
  echo "  WARNING: anonymous request returned HTTP ${CODE}; the service should not be public" >&2
fi

echo "Verifying it answers an authenticated caller…"
TOKEN="$(gcloud auth print-identity-token 2>/dev/null || true)"
if [[ -n "${TOKEN}" ]]; then
  curl -s -H "Authorization: Bearer ${TOKEN}" "${URL}/api/health" || true
  echo
fi

echo
echo "Point the web app at it:"
echo "  gcloud run services update ${WEB_SERVICE} --region ${REGION} --project ${PROJECT} \\"
echo "    --update-env-vars Sentinel__BaseUrl=${URL}"
