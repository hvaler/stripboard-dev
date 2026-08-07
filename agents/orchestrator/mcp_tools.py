"""
ADK tools discovered from our own MCP servers (EV-23).

ADR-001 said "the Python/.NET boundary is the MCP boundary". Until now that was true in one
direction only: the Conflict Sentinel is a real client of Grafana's MCP server, but our own
C# servers — which speak the protocol properly, with `initialize`, `tools/list` and typed
schemas — had no consumer. The agents reached the same engine over REST, so the four servers
were a second interface nobody used. This module closes that.

**Nothing here is a hardcoded tool.** The tools are read from the server with `tools/list`,
their MCP input schemas become Gemini function declarations, and calling one is a `tools/call`
back over the wire. Adding a tool to `ScheduleTools.cs` gives the agent a new capability with
no Python change — which is the property that makes this an integration rather than a
translation layer.

**Why not ADK's own `MCPToolset`.** It imports the reference MCP SDK, which comes from a
vendor the hackathon rules name explicitly (ADR-010 §2). We already own a transport that
avoids the question, so the toolset is ~80 lines on top of it rather than a dependency.
"""

import logging
import os
import sys
from typing import Any, Dict, Iterable, List, Optional

from google.adk.tools.base_tool import BaseTool
from google.genai import types

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "common")))

from mcp_client import McpError, McpHttpClient, sanitise_schema  # noqa: E402

logger = logging.getLogger("StripboardMcp")

DEFAULT_SCHEDULE_ENDPOINT = "http://localhost:5067/mcp"

#: What each specialist is allowed to reach for. The split is the governance model, not a
#: convenience: the scheduler reads, and only governance may even attempt a commit. The
#: attempt is the point — the server refuses it, and a rule that is never exercised is a
#: rule nobody has tested.
SCHEDULER_TOOLS = ("get_schedule", "validate_rules")
GOVERNANCE_TOOLS = ("commit_schedule",)


class StripboardMcpClient(McpHttpClient):
    """Client for one of our own `Stripboard.Mcp.*` servers."""

    client_name = "stripboard-orchestrator"
    label = "Stripboard MCP"

    @staticmethod
    def default_endpoint() -> str:
        return os.getenv("STRIPBOARD_MCP_SCHEDULE_ENDPOINT", DEFAULT_SCHEDULE_ENDPOINT)

    @staticmethod
    def default_token() -> Optional[str]:
        # Set when the server sits behind Cloud Run IAM; empty locally, where the server
        # authenticates nobody and therefore lets nobody commit.
        return os.getenv("STRIPBOARD_MCP_BEARER_TOKEN")



# One connection per endpoint, opened the first time a tool is actually called.
#
# Deliberately module level rather than per tool: five tools against one server should share
# one MCP session, and the connection has to survive between calls or every `tools/call`
# would pay for a fresh handshake. Deliberately lazy rather than eager, because in the
# deployed case this module is unpickled long before anything calls it, in a container that
# has its own identity to authenticate with.
_CLIENTS: Dict[str, "StripboardMcpClient"] = {}


def _client_for(endpoint: str) -> "StripboardMcpClient":
    client = _CLIENTS.get(endpoint)
    if client is None or not client.is_connected:
        client = StripboardMcpClient(endpoint=endpoint)
        client.connect()
        _CLIENTS[endpoint] = client
    return client


class McpBackedTool(BaseTool):
    """
    One tool on a remote MCP server, presented to ADK.

    The declaration is the server's own input schema; this file does not know what
    `get_schedule` takes and must not, or the two descriptions would drift.
    """

    def __init__(self, endpoint: str, spec: Dict[str, Any]):
        super().__init__(
            name=spec["name"],
            description=(spec.get("description") or spec["name"])[:1000],
        )
        # An endpoint and a schema, not a connection. This object gets pickled and shipped to
        # Agent Engine, and a live socket cannot travel — nor should it: the credential that
        # opens the connection has to be minted *there*, as the service account the agent runs
        # as, not carried from whoever ran the deploy.
        self._endpoint = endpoint
        self._schema = spec.get("inputSchema") or {"type": "object", "properties": {}}

    def _get_declaration(self) -> Optional[types.FunctionDeclaration]:
        return types.FunctionDeclaration(
            name=self.name,
            description=self.description,
            parameters=sanitise_schema(self._schema),
        )

    async def run_async(self, *, args: Dict[str, Any], tool_context: Any) -> Any:
        try:
            result = _client_for(self._endpoint).call_tool(self.name, args or {})
        except McpError as exc:
            # Hand the failure to the model as data. A refused commit is the single most
            # important thing this system does, and it arrives here: `commit_schedule`
            # raises with the service's own refusal text. Swallowing it, or raising past
            # the agent, would turn "you are not allowed" into "something went wrong".
            logger.info("MCP tool %s refused or failed: %s", self.name, exc)
            return {"error": str(exc)}

        logger.info("MCP tool %s -> ok", self.name)
        return result if isinstance(result, dict) else {"result": result}


class StripboardMcpToolset:
    """
    Connects to one of our MCP servers, discovers its tools, and hands out ADK tools.

    Owns the connection, so it is a context manager. A discovery failure raises rather than
    returning an empty toolset: an agent with no tools still answers, plausibly and from
    nothing, which is the failure this project keeps finding and refusing.
    """

    def __init__(self, client: Optional[McpHttpClient] = None, endpoint: Optional[str] = None):
        self.client = client or StripboardMcpClient(endpoint=endpoint)
        self._specs: List[Dict[str, Any]] = []

    def __enter__(self) -> "StripboardMcpToolset":
        self.connect()
        return self

    def __exit__(self, *_exc) -> None:
        self.close()

    def connect(self) -> "StripboardMcpToolset":
        if not self.client.is_connected:
            self.client.connect()
        self._specs = self.client.list_tools()
        if not self._specs:
            raise McpError(
                f"{self.client.endpoint} completed the MCP handshake but listed no tools. "
                "That is a server with nothing to offer, not a client problem.")
        logger.info("Discovered %d tools from %s: %s", len(self._specs), self.client.endpoint,
                    ", ".join(sorted(s["name"] for s in self._specs)))
        return self

    def close(self) -> None:
        self.client.close()

    @property
    def tool_names(self) -> List[str]:
        return [s["name"] for s in self._specs]

    def tools(self, allow: Optional[Iterable[str]] = None) -> List[McpBackedTool]:
        """
        The discovered tools, optionally narrowed to an allowlist.

        A name in `allow` that the server does not offer raises. Silently handing back a
        smaller toolset would let a rename in C# quietly remove an agent's ability to do
        its job, and the first sign would be a confident answer that skipped a step.
        """
        if allow is None:
            return [McpBackedTool(self.client.endpoint, spec) for spec in self._specs]

        wanted = list(allow)
        available = {spec["name"]: spec for spec in self._specs}
        missing = [name for name in wanted if name not in available]
        if missing:
            raise McpError(
                f"{self.client.endpoint} does not offer {', '.join(missing)}. "
                f"It offers: {', '.join(sorted(available))}.")

        return [McpBackedTool(self.client.endpoint, available[name]) for name in wanted]


def connect_schedule_toolset(endpoint: Optional[str] = None) -> StripboardMcpToolset:
    """
    Connect to `mcp-schedule` and discover its tools.

    The one place that reads the environment. `build_orchestrator` takes the toolset it is
    given and nothing else, so what a test builds is what a demo builds — a build whose
    shape depends on an env var is a build whose tests describe a different program.

        STRIPBOARD_MCP_SCHEDULE_ENDPOINT   default http://localhost:5067/mcp
        STRIPBOARD_MCP_BEARER_TOKEN        only when the server is behind Cloud Run IAM
    """
    return StripboardMcpToolset(endpoint=endpoint).connect()
