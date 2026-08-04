"""
Model Context Protocol client for the official Grafana MCP server (`grafana/mcp-grafana`).

This speaks the MCP Streamable HTTP transport directly over JSON-RPC 2.0 — POST for every
client message, `Mcp-Session-Id` carried across the session, and responses accepted as
either `application/json` or an SSE stream. It is a deliberate ~200 lines of protocol
rather than a client library dependency: the hackathon rules restrict AI tooling to Google
Cloud, and implementing the transport keeps the dependency surface to `requests`.

Deployment (ADR-010): the Grafana MCP server runs as a sidecar we control, authenticated
to Grafana with a `glsa_` service account token. The hosted Grafana Cloud MCP endpoint uses
interactive OAuth 2.1, which an unattended agent cannot complete.
"""

import json
import logging
import os
import uuid
from typing import Any, Dict, List, Optional

import requests

logger = logging.getLogger("GrafanaMcpClient")

DEFAULT_ENDPOINT = "http://localhost:8000/mcp"
PROTOCOL_VERSION = "2025-06-18"
CLIENT_NAME = "stripboard-conflict-sentinel"


class GrafanaMcpError(RuntimeError):
    """An MCP transport, protocol or tool-execution failure."""


class GrafanaMcpClient:
    """
    Active MCP client for the Grafana stack (§6 / ADR-008 / ADR-010).

    Every method here performs real network I/O. There is no stub path and no canned
    response: if the server is unreachable or a tool fails, this raises.
    """

    def __init__(
        self,
        endpoint: Optional[str] = None,
        token: Optional[str] = None,
        timeout: int = 30,
    ):
        self.endpoint = endpoint or os.getenv("GRAFANA_MCP_ENDPOINT", DEFAULT_ENDPOINT)
        # Only needed when the MCP endpoint itself is protected (e.g. a Cloud Run service
        # behind IAM). The sidecar holds the Grafana service account token itself.
        self.token = token or os.getenv("GRAFANA_MCP_BEARER_TOKEN")
        self.timeout = timeout

        self._session = requests.Session()
        self._session_id: Optional[str] = None
        self._server_info: Dict[str, Any] = {}
        self._next_id = 0

    # --- lifecycle ----------------------------------------------------------------

    def __enter__(self) -> "GrafanaMcpClient":
        self.connect()
        return self

    def __exit__(self, *_exc) -> None:
        self.close()

    @property
    def is_connected(self) -> bool:
        return self._session_id is not None

    @property
    def server_info(self) -> Dict[str, Any]:
        return dict(self._server_info)

    def connect(self) -> Dict[str, Any]:
        """MCP initialize handshake. Returns the server's InitializeResult."""
        result = self._request(
            "initialize",
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": CLIENT_NAME, "version": "1.0.0"},
            },
            expect_session=True,
        )
        self._server_info = result.get("serverInfo", {})

        # The spec requires the client to confirm initialization before issuing requests.
        self._notify("notifications/initialized")

        logger.info(
            "MCP session established with %s %s (protocol %s, session %s)",
            self._server_info.get("name", "unknown"),
            self._server_info.get("version", "?"),
            result.get("protocolVersion"),
            self._session_id,
        )
        return result

    def close(self) -> None:
        """Terminate the MCP session. Servers may refuse, which is not an error."""
        if not self._session_id:
            return
        try:
            self._session.delete(
                self.endpoint, headers=self._headers(), timeout=self.timeout
            )
        except requests.RequestException as exc:
            logger.debug("Session delete failed (harmless): %s", exc)
        finally:
            self._session_id = None

    # --- tools --------------------------------------------------------------------

    def list_tools(self) -> List[Dict[str, Any]]:
        """Every tool the Grafana MCP server exposes."""
        self._require_session()
        tools: List[Dict[str, Any]] = []
        cursor: Optional[str] = None

        while True:
            params = {"cursor": cursor} if cursor else {}
            result = self._request("tools/list", params)
            tools.extend(result.get("tools", []))
            cursor = result.get("nextCursor")
            if not cursor:
                break

        return tools

    def call_tool(self, name: str, arguments: Optional[Dict[str, Any]] = None) -> Any:
        """
        Invoke a Grafana MCP tool and return its decoded content.

        Raises GrafanaMcpError when the server reports the tool itself failed, so a
        broken query can never be mistaken for an empty result.
        """
        self._require_session()
        result = self._request("tools/call", {"name": name, "arguments": arguments or {}})

        if result.get("isError"):
            raise GrafanaMcpError(f"Grafana MCP tool {name!r} failed: {self._as_text(result)}")

        structured = result.get("structuredContent")
        if structured is not None:
            return structured
        return self._decode_content(result.get("content", []))

    # --- transport ----------------------------------------------------------------

    def _headers(self) -> Dict[str, str]:
        headers = {
            "Content-Type": "application/json",
            # The spec requires the client to accept both response shapes.
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": PROTOCOL_VERSION,
        }
        if self._session_id:
            headers["Mcp-Session-Id"] = self._session_id
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        return headers

    def _request(
        self,
        method: str,
        params: Optional[Dict[str, Any]] = None,
        expect_session: bool = False,
    ) -> Dict[str, Any]:
        self._next_id += 1
        payload = {"jsonrpc": "2.0", "id": self._next_id, "method": method, "params": params or {}}

        try:
            response = self._session.post(
                self.endpoint, headers=self._headers(), json=payload, timeout=self.timeout
            )
        except requests.RequestException as exc:
            raise GrafanaMcpError(f"MCP transport error calling {method!r} at {self.endpoint}: {exc}") from exc

        if response.status_code == 404 and self._session_id:
            raise GrafanaMcpError("MCP session expired; reconnect required.")
        if response.status_code >= 400:
            raise GrafanaMcpError(
                f"MCP {method!r} returned HTTP {response.status_code}: {response.text[:300]}"
            )

        if expect_session:
            self._session_id = response.headers.get("Mcp-Session-Id")

        message = self._parse_body(response, expect_id=payload["id"])
        if "error" in message:
            err = message["error"]
            raise GrafanaMcpError(
                f"MCP {method!r} error {err.get('code')}: {err.get('message')}"
            )
        return message.get("result", {})

    def _notify(self, method: str, params: Optional[Dict[str, Any]] = None) -> None:
        """Fire-and-forget JSON-RPC notification; the server answers 202 with no body."""
        payload = {"jsonrpc": "2.0", "method": method, "params": params or {}}
        try:
            response = self._session.post(
                self.endpoint, headers=self._headers(), json=payload, timeout=self.timeout
            )
        except requests.RequestException as exc:
            raise GrafanaMcpError(f"MCP transport error sending {method!r}: {exc}") from exc

        if response.status_code >= 400:
            raise GrafanaMcpError(
                f"MCP notification {method!r} rejected with HTTP {response.status_code}"
            )

    @staticmethod
    def _parse_body(response: requests.Response, expect_id: int) -> Dict[str, Any]:
        """
        Accept a plain JSON body or an SSE stream, and return the JSON-RPC message that
        actually answers `expect_id`.

        The spec allows a server to send requests and notifications on the stream before
        the response, and Grafana Cloud does exactly that: the first tools/call of a
        session is preceded by a `notifications/tools/list_changed` frame. Matching on
        the id rather than taking the first frame is therefore required, not defensive.
        """
        content_type = response.headers.get("Content-Type", "")

        if "text/event-stream" not in content_type:
            try:
                return response.json()
            except ValueError as exc:
                raise GrafanaMcpError(f"Malformed MCP response body: {response.text[:300]}") from exc

        seen_ids = []
        for line in response.text.splitlines():
            if not line.startswith("data:"):
                continue
            data = line[len("data:"):].strip()
            if not data:
                continue
            try:
                message = json.loads(data)
            except ValueError:
                continue
            if message.get("id") == expect_id:
                return message
            if "id" in message:
                seen_ids.append(message["id"])

        raise GrafanaMcpError(
            f"SSE stream carried no response for request id {expect_id} "
            f"(saw ids: {seen_ids or 'none'})."
        )

    @staticmethod
    def _as_text(result: Dict[str, Any]) -> str:
        return " ".join(
            block.get("text", "") for block in result.get("content", []) if isinstance(block, dict)
        ).strip()

    @staticmethod
    def _decode_content(content: List[Dict[str, Any]]) -> Any:
        """
        MCP tool results arrive as content blocks. Grafana returns JSON as text, so
        decode it when it parses and hand back the raw text when it does not.
        """
        texts = [b.get("text", "") for b in content if isinstance(b, dict) and b.get("type") == "text"]
        if not texts:
            return content

        joined = "\n".join(texts)
        try:
            return json.loads(joined)
        except ValueError:
            return joined

    def _require_session(self) -> None:
        if not self._session_id:
            raise GrafanaMcpError("Not connected. Call connect() or use the client as a context manager.")
