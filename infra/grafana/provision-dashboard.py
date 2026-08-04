"""
Provisions the "Shoot Mission Control" dashboard from versioned JSON (§6 / ADR-008).

This is infrastructure setup, not agent runtime, so it talks to the Grafana HTTP API
directly. Everything the Conflict Sentinel does at runtime goes through the Grafana MCP
server instead (ADR-010).
"""

import json
import logging
import os
import sys
import urllib.error
import urllib.request

logging.basicConfig(level=logging.INFO, format="%(levelname)-8s %(message)s")
logger = logging.getLogger("GrafanaDashboardProvisioner")


class ProvisioningError(RuntimeError):
    pass


def provision_dashboard(grafana_url: str = None, token: str = None) -> dict:
    """
    Push the dashboard to Grafana. Raises ProvisioningError on any failure — this used
    to swallow exceptions and return success, which meant a broken provisioning run
    looked identical to a working one.
    """
    grafana_url = (grafana_url or os.getenv("GRAFANA_URL", "")).rstrip("/")
    token = token or os.getenv("GRAFANA_SERVICE_ACCOUNT_TOKEN") or os.getenv("GRAFANA_SENTINEL_TOKEN")

    if not grafana_url:
        raise ProvisioningError("GRAFANA_URL is not set (e.g. https://your-stack.grafana.net).")
    if not token:
        raise ProvisioningError(
            "GRAFANA_SERVICE_ACCOUNT_TOKEN is not set. It must be a service account token "
            "starting with 'glsa_'; Cloud Access Policy tokens ('glc_') are rejected with "
            "401 by the instance API (DT-009)."
        )
    if not token.startswith("glsa_"):
        logger.warning("Token does not start with 'glsa_'; the instance API may reject it (DT-009).")

    json_path = os.path.join(os.path.dirname(__file__), "dashboard-mission-control.json")
    with open(json_path, "r", encoding="utf-8") as f:
        dashboard_json = json.load(f)

    payload = json.dumps({
        "dashboard": dashboard_json,
        "overwrite": True,
        "message": "Provisioned via Stripboard IaC",
    }).encode("utf-8")

    url = f"{grafana_url}/api/dashboards/db"
    logger.info("Provisioning '%s' to %s", dashboard_json.get("title"), url)

    request = urllib.request.Request(
        url,
        data=payload,
        headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            body = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:300]
        if exc.code == 401:
            raise ProvisioningError(
                f"401 Unauthorized from {url}. The token is not a valid service account "
                f"token for this instance (DT-009). Response: {detail}"
            ) from exc
        raise ProvisioningError(f"HTTP {exc.code} from {url}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise ProvisioningError(f"Could not reach {url}: {exc.reason}") from exc

    logger.info("Dashboard provisioned: uid=%s version=%s url=%s",
                body.get("uid"), body.get("version"), body.get("url"))
    return body


if __name__ == "__main__":
    try:
        provision_dashboard()
    except ProvisioningError as exc:
        logger.error("%s", exc)
        sys.exit(1)
