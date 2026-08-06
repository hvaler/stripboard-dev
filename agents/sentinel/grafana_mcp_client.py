"""
Model Context Protocol client for the official Grafana MCP server (`grafana/mcp-grafana`).

The transport lives in `agents/common/mcp_client.py` and is shared with the orchestrator,
which speaks the same protocol to our own C# servers. What is Grafana-specific is only what
you see below: where the server is, what to call ourselves, and which token opens it.

Deployment (ADR-010): the Grafana MCP server runs as a sidecar we control, authenticated
to Grafana with a `glsa_` service account token. The hosted Grafana Cloud MCP endpoint uses
interactive OAuth 2.1, which an unattended agent cannot complete.
"""

import os
import sys
from typing import Optional

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "common")))

from mcp_client import McpError, McpHttpClient  # noqa: E402

DEFAULT_ENDPOINT = "http://localhost:8000/mcp"
CLIENT_NAME = "stripboard-conflict-sentinel"

# One error type across every MCP server we talk to. Callers catch `GrafanaMcpError` in a
# dozen places and there is nothing they would do differently for a Grafana failure than
# for any other, so this is an alias rather than a subclass — a subclass would mean the
# base class's own raises escaped every one of those handlers.
GrafanaMcpError = McpError


class GrafanaMcpClient(McpHttpClient):
    """
    Active MCP client for the Grafana stack (§6 / ADR-008 / ADR-010).

    Every method performs real network I/O. There is no stub path and no canned response:
    if the server is unreachable or a tool fails, this raises.
    """

    client_name = CLIENT_NAME
    label = "Grafana MCP"

    @staticmethod
    def default_endpoint() -> str:
        return os.getenv("GRAFANA_MCP_ENDPOINT", DEFAULT_ENDPOINT)

    @staticmethod
    def default_token() -> Optional[str]:
        # Only needed when the MCP endpoint itself is protected (e.g. a Cloud Run service
        # behind IAM). The sidecar holds the Grafana service account token itself.
        return os.getenv("GRAFANA_MCP_BEARER_TOKEN")
