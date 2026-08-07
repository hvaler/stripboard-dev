"""
The Model Context Protocol client this project uses for every MCP server it talks to.

This speaks the MCP Streamable HTTP transport directly over JSON-RPC 2.0 — POST for every
client message, `Mcp-Session-Id` carried across the session when the server issues one, and
responses accepted as either `application/json` or an SSE stream. It is a deliberate ~200
lines of protocol rather than a client library dependency: the hackathon rules restrict AI
tooling to Google Cloud packages, and the reference MCP SDK comes from a vendor those rules
name explicitly. Implementing the transport removes the question (ADR-010 §2).

It started life inside the Conflict Sentinel as a Grafana-only client. It is here because a
second consumer appeared — the orchestrator, talking to our *own* C# MCP servers — and two
copies of a transport is how the two drift apart. `GrafanaMcpClient` is now a four-line
subclass that supplies an endpoint and a name.

**Sessions are optional, and that is the one thing this had to learn.** Grafana's server is
stateful and returns `Mcp-Session-Id` from `initialize`. Our own servers run with
`Stateless = true`, so they return no such header — a stateless server survives Cloud Run
moving a request to another instance, which is why they are built that way. A client that
treats "no session id" as "not connected" works against Grafana and refuses to talk to our
own servers, so connection state is tracked separately from the session id.
"""

import json
import logging
import os
import time
import uuid  # noqa: F401  kept for callers that build their own request ids
from typing import Any, Dict, List, Optional

import requests

logger = logging.getLogger("McpClient")

PROTOCOL_VERSION = "2025-06-18"


class McpError(RuntimeError):
    """An MCP transport, protocol or tool-execution failure."""


class McpHttpClient:
    """
    An active MCP client over Streamable HTTP.

    Every method here performs real network I/O. There is no stub path and no canned
    response: if the server is unreachable or a tool fails, this raises.
    """

    #: Used in the `initialize` handshake so a server's logs name who is calling.
    client_name = "stripboard"

    #: Prefix for error messages, so a failure says which server refused.
    label = "MCP"

    def __init__(
        self,
        endpoint: Optional[str] = None,
        token: Optional[str] = None,
        timeout: int = 30,
    ):
        self.endpoint = endpoint or self.default_endpoint()
        # Only needed when the MCP endpoint itself is protected (e.g. a Cloud Run service
        # behind IAM). A sidecar that holds its own upstream credential needs nothing here.
        # A static token is fine for a laptop and useless for a long-running service: an
        # identity token lasts an hour, and an agent that minted one at startup would stop
        # being able to call anything after lunch. Where a metadata server exists, the token
        # is minted per request instead — see _bearer_token.
        self.token = token or self.default_token()
        self.timeout = timeout

        self._session = requests.Session()
        self._session_id: Optional[str] = None
        self._connected = False
        self._server_info: Dict[str, Any] = {}
        self._next_id = 0
        self._minted_token: Optional[str] = None
        self._minted_expiry = 0.0

    # --- what a subclass supplies ---------------------------------------------------

    @staticmethod
    def default_endpoint() -> str:
        raise NotImplementedError("Subclasses supply their own endpoint default.")

    @staticmethod
    def default_token() -> Optional[str]:
        return None

    # --- lifecycle ----------------------------------------------------------------

    def __enter__(self) -> "McpHttpClient":
        self.connect()
        return self

    def __exit__(self, *_exc) -> None:
        self.close()

    @property
    def is_connected(self) -> bool:
        # Deliberately not `self._session_id is not None`: a stateless server completes the
        # handshake and issues no session id, and it is still connected.
        return self._connected

    @property
    def session_id(self) -> Optional[str]:
        """The server's session id, or None when the server is stateless."""
        return self._session_id

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
                "clientInfo": {"name": self.client_name, "version": "1.0.0"},
            },
            expect_session=True,
        )
        self._server_info = result.get("serverInfo", {})
        self._connected = True

        # The spec requires the client to confirm initialization before issuing requests.
        self._notify("notifications/initialized")

        logger.info(
            "MCP session established with %s %s (protocol %s, session %s)",
            self._server_info.get("name", "unknown"),
            self._server_info.get("version", "?"),
            result.get("protocolVersion"),
            self._session_id or "stateless",
        )
        return result

    def close(self) -> None:
        """Terminate the MCP session. Servers may refuse, which is not an error."""
        if not self._connected:
            return
        if self._session_id:
            try:
                self._session.delete(
                    self.endpoint, headers=self._headers(), timeout=self.timeout
                )
            except requests.RequestException as exc:
                logger.debug("Session delete failed (harmless): %s", exc)
        self._session_id = None
        self._connected = False

    # --- tools --------------------------------------------------------------------

    def list_tools(self) -> List[Dict[str, Any]]:
        """Every tool this server exposes, following pagination to the end."""
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
        Invoke a tool and return its decoded content.

        Raises when the server reports the tool itself failed, so a broken query can never
        be mistaken for an empty result.
        """
        self._require_session()
        result = self._request("tools/call", {"name": name, "arguments": arguments or {}})

        if result.get("isError"):
            raise McpError(f"{self.label} tool {name!r} failed: {self._as_text(result)}")

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
        bearer = self._bearer_token()
        if bearer:
            headers["Authorization"] = f"Bearer {bearer}"
        return headers

    def _bearer_token(self) -> Optional[str]:
        """
        The credential for this call: an explicit token if one was given, otherwise one minted
        from the GCP metadata server for this endpoint's audience.

        Cloud Run and Agent Engine both expose a metadata server that issues identity tokens
        for a named audience, which is how a service reaches a private service **as itself**
        rather than with a secret somebody pasted into an environment variable. Off Google
        Cloud there is no metadata server, this returns None, and the client sends no
        Authorization header — which is correct for a local server that authenticates nobody.

        Tokens are cached until shortly before they expire. Minting one per request would be
        an extra HTTP round trip on every tools/call, and reusing one for ever would fail an
        hour in, at the point where nobody is still watching the logs.
        """
        if self.token:
            return self.token

        now = time.time()
        if self._minted_token and now < self._minted_expiry:
            return self._minted_token

        audience = self.endpoint.split("/mcp")[0] if "/mcp" in self.endpoint else self.endpoint
        try:
            response = self._session.get(
                "http://metadata.google.internal/computeMetadata/v1/instance/"
                f"service-accounts/default/identity?audience={audience}",
                headers={"Metadata-Flavor": "Google"},
                timeout=5)
        except requests.RequestException:
            # Not on Google Cloud. Not an error: a developer's machine has no metadata server
            # and the local MCP servers do not ask for a token.
            return None

        if response.status_code != 200 or not response.text.strip():
            logger.debug("Metadata server returned %s for an identity token", response.status_code)
            return None

        self._minted_token = response.text.strip()
        # Identity tokens last an hour; renew with five minutes to spare so a call in flight
        # never carries one that expires mid-request.
        self._minted_expiry = now + 55 * 60
        return self._minted_token

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
            raise McpError(
                f"MCP transport error calling {method!r} at {self.endpoint}: {exc}") from exc

        if response.status_code == 404 and self._session_id:
            raise McpError("MCP session expired; reconnect required.")
        if response.status_code >= 400:
            raise McpError(
                f"MCP {method!r} returned HTTP {response.status_code}: {response.text[:300]}"
            )

        if expect_session:
            # Absent on a stateless server. That is legal, and not an error.
            self._session_id = response.headers.get("Mcp-Session-Id")

        message = self._parse_body(response, expect_id=payload["id"])
        if "error" in message:
            err = message["error"]
            raise McpError(f"MCP {method!r} error {err.get('code')}: {err.get('message')}")
        return message.get("result", {})

    def _notify(self, method: str, params: Optional[Dict[str, Any]] = None) -> None:
        """Fire-and-forget JSON-RPC notification; the server answers 202 with no body."""
        payload = {"jsonrpc": "2.0", "method": method, "params": params or {}}
        try:
            response = self._session.post(
                self.endpoint, headers=self._headers(), json=payload, timeout=self.timeout
            )
        except requests.RequestException as exc:
            raise McpError(f"MCP transport error sending {method!r}: {exc}") from exc

        if response.status_code >= 400:
            raise McpError(
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
                raise McpError(f"Malformed MCP response body: {response.text[:300]}") from exc

        # requests falls back to ISO-8859-1 for a text/* body with no charset, which is what
        # Grafana sends. Reading UTF-8 bytes that way turns every em-dash and accented name
        # into mojibake, and the damage is silent: the JSON still parses, so it surfaces as
        # corrupted text in an answer rather than as an error. MCP is UTF-8.
        response.encoding = "utf-8"

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

        raise McpError(
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
        MCP tool results arrive as content blocks. Servers return JSON as text, so decode
        it when it parses and hand back the raw text when it does not.
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
        if not self._connected:
            raise McpError(
                "Not connected. Call connect() or use the client as a context manager.")


def sanitise_schema(schema: Any) -> Any:
    """
    Convert an MCP JSON Schema into something Gemini accepts as a function parameter schema.

    Gemini rejects vocabulary it does not model — `additionalProperties`, `$schema`,
    `default` — so those are dropped rather than passed through and rejected wholesale.
    The MCP servers on the other end are free to describe themselves fully; this is the
    adapter, and it belongs on our side of the boundary.
    """
    if not isinstance(schema, dict):
        return schema

    allowed = {"type", "description", "properties", "required", "items", "enum", "nullable"}
    cleaned: Dict[str, Any] = {}

    for key, value in schema.items():
        if key not in allowed:
            continue
        if key == "properties" and isinstance(value, dict):
            cleaned[key] = {k: sanitise_schema(v) for k, v in value.items()}
        elif key == "items":
            cleaned[key] = sanitise_schema(value)
        elif key == "type" and isinstance(value, list):
            # JSON Schema spells an optional value as a union: `"type": ["string", "null"]`,
            # which is what the .NET MCP SDK emits for a `Guid?` parameter. Gemini's schema
            # dialect has no union type and rejects the list outright, so the declaration
            # fails to build — for the whole tool, not just the field. Collapse it to the
            # concrete type plus the nullable flag, which is the same statement in the
            # vocabulary Gemini does model.
            concrete = [t for t in value if t != "null"]
            cleaned[key] = concrete[0] if concrete else "string"
            if len(concrete) < len(value):
                cleaned["nullable"] = True
        else:
            cleaned[key] = value

    if cleaned.get("type") == "object" and "properties" not in cleaned:
        # Gemini rejects an object schema with no properties.
        cleaned["properties"] = {}

    return cleaned
