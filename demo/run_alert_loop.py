"""
The loop the project is built around, driven by Grafana rather than by a person (EV-29).

    the shoot emits shoot_* metrics over OTLP
      -> Grafana Cloud evaluates the rules in infra/grafana/alert-rules.json
        -> the Conflict Sentinel reads the firing ones back over MCP
          -> the replanner asks CP-SAT for options
            -> a human Producer approves one

Nothing here decides anything. The last step is deliberately missing: this script stops at
the options and shows that the agent's own attempt to commit is refused.

    export GRAFANA_MCP_ENDPOINT=http://localhost:8000/mcp
    export STRIPBOARD_URL=https://<the deployed service>
    python demo/run_alert_loop.py
"""

import asyncio
import datetime
import os
import sys

_HERE = os.path.dirname(__file__)
sys.path.insert(0, os.path.abspath(os.path.join(_HERE, "..", "agents", "sentinel")))
sys.path.insert(0, os.path.abspath(os.path.join(_HERE, "..", "agents", "orchestrator")))

from grafana_mcp_client import GrafanaMcpClient, GrafanaMcpError  # noqa: E402
from sentinel_agent import ConflictSentinelAgent  # noqa: E402
from orchestrator_agent import Orchestrator, commit_schedule, get_schedule  # noqa: E402


def _today():
    """The model has no clock. Left to itself it invents a date, and it invents a wrong one."""
    return datetime.date.today().isoformat()


def _connect():
    if not os.getenv("GRAFANA_MCP_ENDPOINT"):
        print("GRAFANA_MCP_ENDPOINT is not set. Start the sidecar with "
              "infra/grafana/run-mcp-sidecar.sh — without Grafana there is no loop to run.")
        return None
    try:
        client = GrafanaMcpClient(timeout=30)
        client.connect()
        return client
    except GrafanaMcpError as exc:
        print(f"Grafana MCP server configured but unreachable: {exc}")
        return None


async def main():
    sys.stdout.reconfigure(encoding="utf-8")
    grafana = _connect()
    if grafana is None:
        return 1

    print("=" * 72)
    print("ALERT-DRIVEN REPLAN — Grafana starts this, not a person")
    print("=" * 72)
    print(f"Grafana MCP: {grafana.server_info.get('name')} {grafana.server_info.get('version')}")

    print("\n[1/3] Conflict Sentinel asking Grafana which of the shoot's rules are firing...")
    alerts = ConflictSentinelAgent(grafana).firing_alerts()

    if not alerts:
        # Saying "no disruptions" here would be a claim about the shoot. All that is known
        # is that no rule crossed its threshold.
        print("   -> No rule is firing. The schedule is inside every threshold in "
              "infra/grafana/alert-rules.json, so there is nothing to replan.")
        board = get_schedule()
        if "error" not in board:
            print(f"      For reference: v{board['versionNumber']}, {board['days']} days, "
                  f"{board['unionViolations']} union violations, ${board['costUsd']:,}.")
        grafana.close()
        return 0

    for alert in alerts:
        flag = "" if alert["actionable"] else "   (no stripboardTrigger label — cannot be acted on)"
        print(f"   -> [{alert['severity']}] {alert['title']}{flag}")
        print(f"      {alert['summary']}")

    actionable = [a for a in alerts if a["actionable"]]
    if not actionable:
        print("\nNothing firing carries a trigger type, so there is nothing to hand the replanner.")
        grafana.close()
        return 0

    orchestrator = Orchestrator()
    alert = actionable[0]

    if alert["action"] == "consolidate":
        # Nothing is blocked, so there is no disruption to absorb. The question is what a
        # tighter constraint costs.
        ask = (f"Grafana is firing '{alert['title']}' ({alert['severity']}). "
               f"{alert['summary']} The runbook says: {alert['runbook']} "
               "Consolidate the schedule to at most 2 locations a day and tell me what "
               "obeying that costs.")
    else:
        ask = (f"Grafana is firing '{alert['title']}' ({alert['severity']}). "
               f"{alert['summary']} The runbook says: {alert['runbook']} "
               f"Treat it as a {alert['trigger_type']} disruption starting "
               f"{_today()} for 1 day. What are my options?")

    print(f"\n[2/3] Handing '{alert['title']}' to the agents  (action: {alert['action']})...")
    result = await orchestrator.ask_async(ask)

    print(f"   handled by: {', '.join(result.delegated_to) or 'the root agent alone'}")
    for call in result.tool_calls:
        print(f"   tool:       {call['agent']} -> {call['name']}({call['arguments']})")
    print(f"\n{result.text}\n")

    print("[3/3] The agent tries to commit its own recommendation...")
    board = get_schedule()
    refusal = commit_schedule(board.get("versionId", ""), "sa-stripboard-replanner")
    print(f"   -> committed={refusal.get('committed')}  {refusal.get('error', '')}")
    print("      That refusal comes from the scheduling service, not from a prompt.")

    grafana.close()
    print("\n" + "=" * 72)
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
