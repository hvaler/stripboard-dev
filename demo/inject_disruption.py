import asyncio
import os
import sys

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "agents", "sentinel")))
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "agents", "replanner")))

from grafana_mcp_client import GrafanaMcpClient, GrafanaMcpError
from sentinel_agent import ConflictSentinelAgent
from replanner_agent import ReplannerAgent


def _connect_grafana():
    """
    Connect to the Grafana MCP server when one is configured. Returns None otherwise,
    in which case the pipeline still runs but says clearly that nothing was published.
    """
    if not os.getenv("GRAFANA_MCP_ENDPOINT"):
        return None
    try:
        client = GrafanaMcpClient(timeout=10)
        client.connect()
        return client
    except GrafanaMcpError as exc:
        print(f"   !! Grafana MCP server configured but unreachable: {exc}")
        return None


def run_disruption_pipeline():
    """
    Executes the demo disruption pipeline (§8 of brief):
    1. Injects actor unavailability (Sherlock Holmes on 2026-08-10).
    2. Conflict Sentinel detects the anomaly (deterministic rules) and publishes it to
       Grafana as an annotation over MCP, when GRAFANA_MCP_ENDPOINT is set.
    3. The replanner agent (Google ADK) asks the scheduling service for alternatives
       and explains the options the CP-SAT solver returned.

    Scene/cast data here is mock; the replan figures are not — they come from the CP-SAT
    solver behind STRIPBOARD_URL, which must be running.
    """
    sys.stdout.reconfigure(encoding='utf-8')
    print("=" * 60)
    print("🎬 STRIPBOARD DEMO PIPELINE: INJECTING DISRUPTION EVENT")
    print("=" * 60)

    grafana = _connect_grafana()
    if grafana:
        print(f"Grafana MCP: connected to {grafana.server_info.get('name')} "
              f"{grafana.server_info.get('version')} — annotations WILL be published.")
    else:
        print("Grafana MCP: not configured — disruptions will NOT be published.")
        print("             set GRAFANA_MCP_ENDPOINT to publish for real.")
    print("NOTE: the scene fixtures below are mock; the replan figures come from CP-SAT.")

    sentinel = ConflictSentinelAgent(grafana)
    replanner = ReplannerAgent()

    mock_scenes = [
        {"number": 1, "set_location": "221B BAKER STREET", "int_ext": "INT", "cast": ["Sherlock Holmes"], "date": "2026-08-10"},
        {"number": 2, "set_location": "TOWER BRIDGE WHARF", "int_ext": "EXT", "cast": ["Sherlock Holmes", "Prof. James Moriarty"], "date": "2026-08-11"}
    ]

    mock_availabilities = {
        "Sherlock Holmes": ["2026-08-10"]  # Unavail on Aug 10
    }

    mock_permits = {
        "221B BAKER STREET": {"start": "2026-08-01", "end": "2026-08-30"},
        "TOWER BRIDGE WHARF": {"start": "2026-08-01", "end": "2026-08-30"}
    }

    mock_weather = {
        "TOWER BRIDGE WHARF": {"condition": "Rain", "precipitation_probability": 90}
    }

    print("\n[1/2] Conflict Sentinel inspecting schedule integrity...")
    disruptions = sentinel.inspect_schedule_disruptions(mock_scenes, mock_availabilities, mock_permits, mock_weather)
    print(f"   -> Detected {len(disruptions)} disruption(s)!")
    for d in disruptions:
        published = (f"Grafana annotation #{d['annotation_id']}" if d["published"]
                     else "NOT published")
        print(f"      • [{d['trigger_type']}] {d['description']}")
        print(f"        -> {published}")

    print("\n[2/2] Replanner agent (ADK) asking the solver for alternatives...")
    result = asyncio.run(replanner.replan_async(
        f"{disruptions[0]['description']} What are my options?"))

    if result.tool_calls:
        print(f"   -> {result.tool_calls[0]['name']}({result.tool_calls[0]['arguments']})")
        print(f"   -> CP-SAT returned {len(result.options)} option(s):")
        for option in result.options:
            print(f"\n   📋 {option.get('title')}")
            if not option.get("isFeasible"):
                # No schedule exists for this strategy, so it has no metrics to show.
                print(f"      • INFEASIBLE — {option.get('justification')}")
                continue
            delta = option.get("delta") or {}
            print(f"      • Days:  {option.get('days')} ({delta.get('extraShootDays', 0):+d})")
            print(f"      • Cost:  ${option.get('costUsd')}  (delta ${delta.get('costDeltaUsd', 0):+})")
            print(f"      • Moves: {option.get('companyMoves')}  ·  "
                  f"union violations: {option.get('unionViolations')}")
    else:
        print("   -> No solver run, so no plan.")

    print("\n" + "-" * 60)
    print(result.text)

    published = sum(1 for d in disruptions if d["published"])
    print("\n" + "=" * 60)
    print(f"PIPELINE COMPLETED — {published} disruption(s) published to Grafana over MCP; "
          f"{len(result.options)} replan option(s) computed by CP-SAT.")
    print("=" * 60)

    if grafana:
        grafana.close()
    return True

if __name__ == "__main__":
    run_disruption_pipeline()
