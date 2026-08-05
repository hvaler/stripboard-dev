"""
HTTP front door for the Conflict Sentinel (EV-29).

The reasoning lives in Python because that is where the Gemini SDK and the Grafana MCP
client live; the UI lives in .NET. This is the seam between them, deliberately small: one
endpoint that answers a question about the shoot, and one that reports whether the pieces
behind it are actually connected.

Run it next to the MCP sidecar:

    export GRAFANA_MCP_ENDPOINT=http://localhost:8000/mcp
    export GOOGLE_CLOUD_PROJECT=<project>
    python -m agents.sentinel.service            # listens on :8081
"""

import json
import logging
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict

sys.path.insert(0, os.path.dirname(__file__))
# The Gemini client is shared with the breakdown agent. EV-24 unifies the agent packages;
# until then this is the one import that reaches across them.
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "breakdown"))

from gemini_client import GeminiClient, GeminiConfigError  # noqa: E402
from grafana_mcp_client import GrafanaMcpClient, GrafanaMcpError  # noqa: E402
from shoot_analyst import ShootAnalyst  # noqa: E402

logging.basicConfig(level=logging.INFO, format="%(levelname)-8s %(name)s: %(message)s")
logger = logging.getLogger("SentinelService")

MAX_QUESTION_BYTES = 4096


def _analyst():
    """A fresh MCP session per request: sessions are cheap and long-lived ones expire."""
    grafana = GrafanaMcpClient()
    grafana.connect()
    return ShootAnalyst(GeminiClient(), grafana), grafana


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _send(self, status: int, payload: Dict[str, Any]) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):  # quieter default logging
        logger.info("%s - %s", self.address_string(), fmt % args)

    def _read_body(self) -> bytes:
        """
        Read the request body with or without a Content-Length.

        .NET's JsonContent does not know its length in advance, so HttpClient sends the
        body chunked. Reading only Content-Length silently yielded zero bytes, and the
        service answered "question is required" to a request that clearly had one — while
        curl, which does send Content-Length, worked perfectly.
        """
        if (self.headers.get("Transfer-Encoding") or "").lower() == "chunked":
            chunks = bytearray()
            while True:
                line = self.rfile.readline().strip()
                if not line:
                    continue
                try:
                    size = int(line.split(b";")[0], 16)
                except ValueError as exc:
                    raise ValueError("malformed chunked body") from exc
                if size == 0:
                    self.rfile.readline()  # trailing CRLF
                    break
                chunks += self.rfile.read(size)
                self.rfile.read(2)  # CRLF after each chunk
                if len(chunks) > MAX_QUESTION_BYTES:
                    break
            return bytes(chunks)

        return self.rfile.read(int(self.headers.get("Content-Length") or 0))

    def do_GET(self):
        if self.path != "/api/health":
            self._send(404, {"error": "not found"})
            return

        try:
            with GrafanaMcpClient(timeout=10) as grafana:
                tools = grafana.list_tools()
            self._send(200, {
                "status": "ok",
                "grafana_mcp": grafana.server_info.get("name", "unknown"),
                "tools": len(tools),
                "gemini_configured": GeminiClient.is_configured(),
            })
        except GrafanaMcpError as exc:
            self._send(503, {"status": "degraded", "reason": str(exc)})

    def do_POST(self):
        if self.path != "/api/ask":
            self._send(404, {"error": "not found"})
            return

        try:
            raw = self._read_body()
        except ValueError as exc:
            self._send(400, {"error": str(exc)})
            return

        logger.info("POST /api/ask  content-length=%r transfer-encoding=%r bytes=%d raw=%r",
                    self.headers.get("Content-Length"), self.headers.get("Transfer-Encoding"),
                    len(raw), raw[:200])

        if len(raw) > MAX_QUESTION_BYTES:
            self._send(413, {"error": "question too long"})
            return

        try:
            question = (json.loads(raw or b"{}").get("question") or "").strip()
        except json.JSONDecodeError:
            self._send(400, {"error": "body must be JSON: {\"question\": \"...\"}"})
            return

        if not question:
            self._send(400, {"error": "question is required"})
            return

        grafana = None
        try:
            analyst, grafana = _analyst()
            answer = analyst.ask(question)
            self._send(200, {
                "question": question,
                "answer": answer.text,
                "rounds": answer.rounds,
                "total_tokens": answer.total_tokens,
                # The trace is part of the answer: it is what lets a producer see which
                # Grafana queries the figures came from instead of trusting a paragraph.
                "tool_calls": [
                    {"name": c.name, "arguments": c.arguments, "error": c.error}
                    for c in answer.tool_calls
                ],
            })
        except GeminiConfigError as exc:
            self._send(503, {"error": f"Gemini is not configured: {exc}"})
        except GrafanaMcpError as exc:
            self._send(503, {"error": f"Grafana MCP is unreachable: {exc}"})
        except Exception as exc:  # noqa: BLE001 - surface the reason, do not fake an answer
            logger.exception("Unhandled error answering a question")
            self._send(500, {"error": f"{type(exc).__name__}: {exc}"})
        finally:
            if grafana is not None:
                grafana.close()


def main() -> int:
    port = int(os.getenv("SENTINEL_PORT", "8081"))
    server = ThreadingHTTPServer(("0.0.0.0", port), Handler)
    logger.info("Conflict Sentinel listening on :%d  (MCP: %s)",
                port, os.getenv("GRAFANA_MCP_ENDPOINT", "http://localhost:8000/mcp"))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        logger.info("Shutting down")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
