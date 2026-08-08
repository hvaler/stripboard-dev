"""
Provisions a dashboard from versioned JSON (§6 / ADR-008).

    python infra/grafana/provision-dashboard.py                    # every dashboard-*.json
    python infra/grafana/provision-dashboard.py dashboard-agent-observability.json

This is infrastructure setup, not agent runtime, so it talks to the Grafana HTTP API
directly. Everything the Conflict Sentinel does at runtime goes through the Grafana MCP
server instead (ADR-010).

**There are two dashboards and they are deliberately not one.** "Shoot Mission Control"
observes the production; "The Agents Themselves" observes the software that reschedules it
(EV-47). Provisioning them from one loop rather than two scripts is what keeps the second
from quietly rotting — a dashboard nobody redeploys is a dashboard that stops matching the
metrics it draws.
"""

import glob
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


def dashboard_files(names=None):
    """
    The dashboard JSON to push: what was named, or every `dashboard-*.json` beside this file.

    Globbing rather than a hardcoded list so adding a dashboard is adding a file. A list here
    would have to be remembered, and the failure mode of forgetting is a dashboard that exists
    in the repo and not in Grafana.
    """
    here = os.path.dirname(os.path.abspath(__file__))
    if names:
        paths = [n if os.path.isabs(n) else os.path.join(here, os.path.basename(n)) for n in names]
        missing = [p for p in paths if not os.path.isfile(p)]
        if missing:
            raise ProvisioningError(f"No such dashboard JSON: {', '.join(missing)}")
        return paths

    paths = sorted(glob.glob(os.path.join(here, "dashboard-*.json")))
    if not paths:
        raise ProvisioningError(f"No dashboard-*.json found in {here}.")
    return paths


def provision_dashboard(grafana_url: str = None, token: str = None, names=None) -> list:
    """
    Push the dashboards to Grafana. Raises ProvisioningError on any failure — this used
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

    results = []
    for json_path in dashboard_files(names):
        results.append(_push(grafana_url, token, json_path))
    return results


def _push(grafana_url: str, token: str, json_path: str) -> dict:
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
        provision_dashboard(names=sys.argv[1:] or None)
    except ProvisioningError as exc:
        logger.error("%s", exc)
        sys.exit(1)
