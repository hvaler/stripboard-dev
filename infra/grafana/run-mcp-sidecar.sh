#!/usr/bin/env bash
#
# Runs the official Grafana MCP server (grafana/mcp-grafana) as a local sidecar over the
# MCP Streamable HTTP transport, which is what the Conflict Sentinel connects to.
#
# Why a sidecar and not the hosted Grafana Cloud MCP endpoint: the hosted endpoint
# authorises through interactive OAuth 2.1 in a browser, which an unattended agent
# cannot complete. See ADR-010.
#
# Usage:
#   export GRAFANA_URL=https://<your-stack>.grafana.net
#   export GRAFANA_SERVICE_ACCOUNT_TOKEN=glsa_xxx
#   ./infra/grafana/run-mcp-sidecar.sh
#
#   # then, in another shell:
#   export GRAFANA_MCP_ENDPOINT=http://localhost:8000/mcp
#   python -m unittest discover -s agents/sentinel -p "test_*.py"
#
set -euo pipefail

CONTAINER_NAME="${CONTAINER_NAME:-stripboard-mcp-grafana}"
PORT="${PORT:-8000}"
IMAGE="${IMAGE:-grafana/mcp-grafana:latest}"

if [[ -z "${GRAFANA_URL:-}" ]]; then
  echo "error: GRAFANA_URL is not set (e.g. https://your-stack.grafana.net)" >&2
  exit 1
fi

# Token resolution, in order of preference. The token is never echoed.
#   1. GRAFANA_SERVICE_ACCOUNT_TOKEN in the environment
#   2. .secrets/grafana-token  (gitignored, for local development)
#   3. GCP Secret Manager      (the deployment path, per ADR-008)
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TOKEN_FILE="${GRAFANA_TOKEN_FILE:-${REPO_ROOT}/.secrets/grafana-token}"
SECRET_NAME="${GRAFANA_SECRET_NAME:-grafana-sentinel-token}"

if [[ -z "${GRAFANA_SERVICE_ACCOUNT_TOKEN:-}" && -r "${TOKEN_FILE}" ]]; then
  GRAFANA_SERVICE_ACCOUNT_TOKEN="$(tr -d '[:space:]' < "${TOKEN_FILE}")"
  echo "Token loaded from ${TOKEN_FILE}"
fi

if [[ -z "${GRAFANA_SERVICE_ACCOUNT_TOKEN:-}" ]] && command -v gcloud >/dev/null 2>&1; then
  if GRAFANA_SERVICE_ACCOUNT_TOKEN="$(gcloud secrets versions access latest \
        --secret="${SECRET_NAME}" 2>/dev/null | tr -d '[:space:]')" \
     && [[ -n "${GRAFANA_SERVICE_ACCOUNT_TOKEN}" ]]; then
    echo "Token loaded from Secret Manager (${SECRET_NAME})"
  else
    GRAFANA_SERVICE_ACCOUNT_TOKEN=""
  fi
fi

if [[ -z "${GRAFANA_SERVICE_ACCOUNT_TOKEN:-}" ]]; then
  cat >&2 <<EOF
error: no Grafana service account token found. Looked in:
  1. \$GRAFANA_SERVICE_ACCOUNT_TOKEN
  2. ${TOKEN_FILE}
  3. Secret Manager secret '${SECRET_NAME}'
EOF
  cat >&2 <<'EOF'

It must be a Grafana *service account* token, which starts with `glsa_`.
A Cloud Access Policy token (`glc_`) is rejected with 401 by the instance API — this
was DT-009.

To mint one:
  Grafana → Administration → Users and access → Service accounts → Add service account
  Role: Editor (needs annotation write; read-only roles cannot publish disruptions)
  → Add service account token → copy the glsa_... value
EOF
  exit 1
fi

if [[ "${GRAFANA_SERVICE_ACCOUNT_TOKEN}" != glsa_* ]]; then
  echo "warning: token does not start with 'glsa_'. Instance APIs reject glc_ access" >&2
  echo "         policy tokens with 401 (DT-009). Continuing anyway." >&2
fi

docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true

docker run -d \
  --name "${CONTAINER_NAME}" \
  -p "${PORT}:8000" \
  -e GRAFANA_URL="${GRAFANA_URL}" \
  -e GRAFANA_SERVICE_ACCOUNT_TOKEN="${GRAFANA_SERVICE_ACCOUNT_TOKEN}" \
  "${IMAGE}" \
  -t streamable-http \
  -address 0.0.0.0:8000 \
  -allowed-hosts '*' \
  >/dev/null

echo "Waiting for the MCP server to accept an initialize handshake..."
for _ in $(seq 1 30); do
  if curl -sf -X POST "http://localhost:${PORT}/mcp" \
      -H 'Content-Type: application/json' \
      -H 'Accept: application/json, text/event-stream' \
      -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"healthcheck","version":"0"}}}' \
      >/dev/null 2>&1; then
    echo "Grafana MCP server ready at http://localhost:${PORT}/mcp"
    echo
    echo "  export GRAFANA_MCP_ENDPOINT=http://localhost:${PORT}/mcp"
    exit 0
  fi
  sleep 1
done

echo "error: the MCP server did not become ready. Logs:" >&2
docker logs "${CONTAINER_NAME}" >&2
exit 1
