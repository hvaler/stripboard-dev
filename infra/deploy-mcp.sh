#!/usr/bin/env bash
#
# Deploys the four Stripboard.Mcp.* servers to Cloud Run (EV-23, deployment half).
#
# All four are PRIVATE. They are not a public API: mcp-schedule can run the solver and can
# commit a schedule, and an open endpoint is a database anyone on the internet can write to.
# Callers reach them with an identity token, which is also what makes the governance rule
# real — CallerIdentityResolver only trusts a principal when K_SERVICE says it is on Cloud
# Run. Locally nothing is verified, so locally nobody can commit at all.
#
# Weather gets no database and no project role, deliberately. A server that cannot reach the
# schedule is a stronger statement than a server that is merely not asked to.
#
# Usage:
#   ./infra/deploy-mcp.sh                    # build, push, deploy all four, verify
#   ./infra/deploy-mcp.sh schedule weather   # only the named ones
#   SKIP_BUILD=1 ./infra/deploy-mcp.sh       # redeploy images already published
#
set -euo pipefail

PROJECT="${GCP_PROJECT:-stripboard-hack}"
REGION="${GCP_REGION:-europe-west1}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
AR="${REGION}-docker.pkg.dev/${PROJECT}/stripboard"

SQL_INSTANCE="${SQL_INSTANCE:-stripboard-db}"
DB_SECRET="${DB_SECRET:-stripboard-db-connection}"

# Which servers need the shooting schedule. Weather is absent from this list because it
# generates a deterministic synthetic forecast and touches no data (see ADR-021).
NEEDS_DATABASE=(schedule people locations)

ALL=(schedule people locations weather)
TARGETS=("$@")
if [[ ${#TARGETS[@]} -eq 0 ]]; then
  TARGETS=("${ALL[@]}")
fi

project_of() {  # schedule -> Stripboard.Mcp.Schedule
  local name="$1"
  echo "Stripboard.Mcp.$(tr '[:lower:]' '[:upper:]' <<< "${name:0:1}")${name:1}"
}

needs_database() {
  local name="$1"
  for n in "${NEEDS_DATABASE[@]}"; do [[ "$n" == "$name" ]] && return 0; done
  return 1
}

if [[ ! -f "${REPO_ROOT}/.dockerignore" ]]; then
  echo "error: .dockerignore is missing. Dockerfile.mcp does 'COPY . .', so building" >&2
  echo "       without it would copy .secrets/ into the image." >&2
  exit 1
fi

SQL_CONNECTION_NAME="$(gcloud sql instances describe "${SQL_INSTANCE}" \
  --project "${PROJECT}" --format='value(connectionName)' 2>/dev/null || true)"
if [[ -z "${SQL_CONNECTION_NAME}" ]]; then
  echo "error: Cloud SQL instance '${SQL_INSTANCE}' not found." >&2
  echo "       These servers would start on an in-memory database, each with its own copy" >&2
  echo "       of the shoot. Four servers disagreeing about one schedule is worse than none." >&2
  exit 1
fi
echo "Cloud SQL: ${SQL_CONNECTION_NAME}"

# --- images ---------------------------------------------------------------------------
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
  gcloud auth configure-docker "${REGION}-docker.pkg.dev" --quiet >/dev/null
  for name in "${TARGETS[@]}"; do
    proj="$(project_of "${name}")"
    echo "Building ${proj}…"
    docker build -q \
      -f "${REPO_ROOT}/Dockerfile.mcp" \
      --build-arg "PROJECT=${proj}" \
      -t "${AR}/mcp-${name}:latest" "${REPO_ROOT}" >/dev/null
    docker push -q "${AR}/mcp-${name}:latest" >/dev/null
  done
fi

# --- deploy ---------------------------------------------------------------------------
for name in "${TARGETS[@]}"; do
  service="stripboard-mcp-${name}"
  sa="sa-mcp-${name}@${PROJECT}.iam.gserviceaccount.com"

  # By digest, never by :latest. A spec that still says :latest is byte-identical to the one
  # already deployed, so Cloud Run creates no revision and keeps serving the old image while
  # this script prints "deployed" — a successful-looking deployment that changed nothing.
  digest="$(gcloud artifacts docker images describe "${AR}/mcp-${name}:latest" \
    --project "${PROJECT}" --format='value(image_summary.digest)')"
  echo
  echo "Deploying ${service}  (${digest})"

  extra=()
  if needs_database "${name}"; then
    extra+=(--add-cloudsql-instances "${SQL_CONNECTION_NAME}"
            --set-secrets "ConnectionStrings__Stripboard=${DB_SECRET}:latest")
  fi

  # --service-account is not optional here. Cloud Run defaults a NEW service to the default
  # compute account, which carries Editor on most projects — so omitting it would hand these
  # four servers more authority than the web app has.
  gcloud run deploy "${service}" \
    --image "${AR}/mcp-${name}@${digest}" \
    --project "${PROJECT}" \
    --region "${REGION}" \
    --service-account "${sa}" \
    --no-allow-unauthenticated \
    --port 8080 \
    --min-instances 0 \
    --max-instances 2 \
    --cpu 1 \
    --memory 512Mi \
    --set-env-vars "ASPNETCORE_ENVIRONMENT=Production" \
    "${extra[@]}" \
    --quiet

  url="$(gcloud run services describe "${service}" --project "${PROJECT}" \
          --region "${REGION}" --format='value(status.url)')"
  revision="$(gcloud run services describe "${service}" --project "${PROJECT}" \
          --region "${REGION}" --format='value(status.latestReadyRevisionName)')"
  echo "  ${revision}  ${url}"

  # The orchestrator is the intended caller once it runs in GCP (EV-26). Until then the
  # person running this script is, so both are authorised — and nobody else.
  for member in "serviceAccount:sa-orchestrator@${PROJECT}.iam.gserviceaccount.com" \
                "user:$(gcloud config get-value account 2>/dev/null)"; do
    gcloud run services add-iam-policy-binding "${service}" \
      --project "${PROJECT}" --region "${REGION}" \
      --member "${member}" --role roles/run.invoker --quiet >/dev/null
  done

  # --- verify, rather than assume ------------------------------------------------------
  anon="$(curl -s -o /dev/null -w '%{http_code}' --max-time 30 -X POST "${url}/mcp" \
          -H 'Content-Type: application/json' \
          -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' || echo 000)"
  if [[ "${anon}" != "403" && "${anon}" != "401" ]]; then
    echo "  FAILED: an anonymous caller got HTTP ${anon}; this service must be private." >&2
    exit 1
  fi
  echo "  anonymous -> HTTP ${anon}, as intended"

  token="$(gcloud auth print-identity-token)"
  tools="$(curl -s --max-time 60 -X POST "${url}/mcp" \
    -H "Authorization: Bearer ${token}" \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18",
         "capabilities":{},"clientInfo":{"name":"deploy-mcp","version":"1"}}}' \
    | grep -o '"name":"[^"]*"' | head -1 || true)"
  if [[ -z "${tools}" ]]; then
    echo "  FAILED: authenticated initialize returned no serverInfo." >&2
    exit 1
  fi
  echo "  authenticated initialize -> ${tools}"
done

echo
echo "Done. Point an MCP client at one of them with an identity token:"
echo "  export STRIPBOARD_MCP_SCHEDULE_ENDPOINT=\$(gcloud run services describe stripboard-mcp-schedule \\"
echo "    --project ${PROJECT} --region ${REGION} --format='value(status.url)')/mcp"
echo "  export STRIPBOARD_MCP_BEARER_TOKEN=\$(gcloud auth print-identity-token)"
echo "  python demo/run_orchestrator.py"
