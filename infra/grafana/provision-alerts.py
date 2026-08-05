"""
Provisions the shoot alert rules from versioned JSON (EV-29).

These alert on the production — union violations, schedule risk, idle cast, cost against
budget — rather than on the application. When one fires, the Conflict Sentinel reads it back
over MCP and opens the matching disruption, which is the loop the project is built around:

    the shoot emits signal -> Grafana alerts -> the agent reads Grafana over MCP
    -> Gemini formulates -> CP-SAT decides -> a human approves

Like the dashboard provisioner, this is infrastructure setup and talks to the Grafana HTTP
API directly. Runtime goes through MCP (ADR-010).

    export GRAFANA_URL=https://<stack>.grafana.net
    export GRAFANA_SERVICE_ACCOUNT_TOKEN=glsa_xxx
    python infra/grafana/provision-alerts.py            # create or update
    python infra/grafana/provision-alerts.py --delete   # remove them again
"""

import argparse
import json
import logging
import os
import sys
import time
import urllib.error
import urllib.request

logging.basicConfig(level=logging.INFO, format="%(levelname)-8s %(message)s")
logger = logging.getLogger("GrafanaAlertProvisioner")

RULES_PATH = os.path.join(os.path.dirname(__file__), "alert-rules.json")


class ProvisioningError(RuntimeError):
    pass


def _credentials():
    url = os.getenv("GRAFANA_URL", "").rstrip("/")
    token = os.getenv("GRAFANA_SERVICE_ACCOUNT_TOKEN") or os.getenv("GRAFANA_SENTINEL_TOKEN")

    if not url:
        raise ProvisioningError("GRAFANA_URL is not set (e.g. https://your-stack.grafana.net).")
    if not token:
        raise ProvisioningError(
            "GRAFANA_SERVICE_ACCOUNT_TOKEN is not set. It must be a service account token "
            "starting with 'glsa_'; Cloud Access Policy tokens ('glc_') are rejected with "
            "401 by the instance API (DT-009).")
    if not token.startswith("glsa_"):
        logger.warning("Token does not start with 'glsa_'; the instance API may reject it (DT-009).")

    return url, token


def _call(url, token, path, method="GET", body=None):
    request = urllib.request.Request(
        f"{url}{path}",
        data=json.dumps(body).encode("utf-8") if body is not None else None,
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            # Provisioned rules are read-only in the UI unless this is set. Keeping them
            # editable matters during a shoot: a 1st AD should be able to silence a rule at
            # 5am without a redeploy.
            "X-Disable-Provenance": "true",
        },
        method=method)

    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            payload = response.read().decode("utf-8")
            return json.loads(payload) if payload.strip() else {}
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:400]
        if exc.code == 401:
            raise ProvisioningError(
                f"401 Unauthorized from {path}. The token is not a valid service account "
                f"token for this instance (DT-009). Response: {detail}") from exc
        raise ProvisioningError(f"HTTP {exc.code} from {method} {path}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise ProvisioningError(f"Could not reach {url}{path}: {exc.reason}") from exc


def _ensure_folder(url, token, folder):
    """
    Grafana refuses an alert rule with no folder, and folder creation is not idempotent.

    The existence check lists folders rather than fetching one by uid: folder permissions are
    scoped per folder, so `GET /api/folders/{uid}` on a folder that does not exist answers
    403 "you need folders:read" rather than 404. Reading that as a permission problem sends
    you looking for a token that was fine all along.
    """
    for existing in _call(url, token, "/api/folders"):
        if existing.get("uid") == folder["uid"]:
            logger.info("Folder '%s' already exists (uid=%s).", existing.get("title"), folder["uid"])
            return folder["uid"]

    created = _call(url, token, "/api/folders", method="POST",
                    body={"uid": folder["uid"], "title": folder["title"]})
    logger.info("Created folder '%s' (uid=%s).", created.get("title"), created.get("uid"))

    # Grafana Cloud resolves the folder-scoped grants a moment after the folder appears, so
    # the first write into a brand-new folder answers 403 "not authorized to access rule
    # group". Waiting is the fix; the alternative is telling the operator their token is
    # wrong when it is not.
    _wait_for_folder_access(url, token, created.get("uid", folder["uid"]))
    return created.get("uid", folder["uid"])


def _wait_for_folder_access(url, token, folder_uid, attempts=10, delay=3):
    for attempt in range(1, attempts + 1):
        try:
            _call(url, token, f"/api/folders/{folder_uid}")
            return
        except ProvisioningError:
            if attempt == attempts:
                logger.warning(
                    "Folder %s is still not readable after %ds. Continuing; if the next call "
                    "returns 403, re-run this script.", folder_uid, attempts * delay)
                return
            logger.info("Waiting for permissions on the new folder (%d/%d)...", attempt, attempts)
            time.sleep(delay)


def _rule_payload(rule, spec, folder_uid):
    """
    Three stages, which is how Grafana's unified alerting models a threshold: query the
    series, reduce it to one number, compare that number.
    """
    if not rule.get("stripboardTrigger"):
        raise ProvisioningError(
            f"Rule '{rule['title']}' has no stripboardTrigger label. The sentinel keys off "
            "that label to decide what kind of disruption to open, so a rule without one "
            "can fire but can never be acted on.")

    return {
        "title": rule["title"],
        "ruleGroup": spec["ruleGroup"],
        "folderUID": folder_uid,
        "orgID": 1,
        "condition": "C",
        "for": rule["for"],
        "noDataState": "OK",
        # A schedule that has not been re-solved recently is not an emergency; it is a gap
        # in the data. Alerting on absence here would page someone for a quiet weekend.
        "execErrState": "Error",
        "labels": {
            "stripboard": "true",
            "severity": rule["severity"],
            "stripboardTrigger": rule["stripboardTrigger"],
            "stripboardAction": rule.get("stripboardAction", "replan"),
        },
        "annotations": {
            "summary": rule["summary"],
            "runbook": rule["runbook"],
        },
        "data": [
            {
                "refId": "A",
                "relativeTimeRange": {"from": 600, "to": 0},
                "datasourceUid": spec["datasourceUid"],
                "model": {
                    "refId": "A", "editorMode": "code", "expr": rule["expr"],
                    "instant": True, "range": False, "intervalMs": 60000, "maxDataPoints": 43200,
                },
            },
            {
                "refId": "B",
                "datasourceUid": "__expr__",
                "model": {"refId": "B", "type": "reduce", "expression": "A", "reducer": "last"},
            },
            {
                "refId": "C",
                "datasourceUid": "__expr__",
                "model": {
                    "refId": "C", "type": "threshold", "expression": "B",
                    "conditions": [{"evaluator": {"type": rule["op"], "params": [rule["threshold"]]}}],
                },
            },
        ],
    }


def _existing_by_title(url, token):
    return {rule["title"]: rule for rule in _call(url, token, "/api/v1/provisioning/alert-rules")}


def provision(delete=False):
    url, token = _credentials()
    with open(RULES_PATH, "r", encoding="utf-8") as handle:
        spec = json.load(handle)

    existing = _existing_by_title(url, token)

    if delete:
        for rule in spec["rules"]:
            found = existing.get(rule["title"])
            if not found:
                logger.info("Not present, nothing to delete: %s", rule["title"])
                continue
            _call(url, token, f"/api/v1/provisioning/alert-rules/{found['uid']}", method="DELETE")
            logger.info("Deleted: %s", rule["title"])
        return

    folder_uid = _ensure_folder(url, token, spec["folder"])

    for rule in spec["rules"]:
        payload = _rule_payload(rule, spec, folder_uid)
        found = existing.get(rule["title"])

        if found:
            payload["uid"] = found["uid"]
            _call(url, token, f"/api/v1/provisioning/alert-rules/{found['uid']}",
                  method="PUT", body=payload)
            logger.info("Updated: %-42s %s %s %s",
                        rule["title"], rule["expr"], rule["op"], rule["threshold"])
        else:
            created = _call(url, token, "/api/v1/provisioning/alert-rules",
                            method="POST", body=payload)
            logger.info("Created: %-42s uid=%s", rule["title"], created.get("uid"))

    _call(url, token,
          f"/api/v1/provisioning/folder/{folder_uid}/rule-groups/{spec['ruleGroup']}",
          method="PUT",
          body={"title": spec["ruleGroup"], "folderUid": folder_uid, "interval": spec["interval"]})
    logger.info("Rule group '%s' evaluates every %ss.", spec["ruleGroup"], spec["interval"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--delete", action="store_true", help="Remove the rules instead of creating them.")
    args = parser.parse_args()

    try:
        provision(delete=args.delete)
    except ProvisioningError as exc:
        logger.error("%s", exc)
        sys.exit(1)
