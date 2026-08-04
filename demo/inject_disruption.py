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
    3. Replanner returns its two hardcoded proposals.

    Scene/cast data is still mock, the solver is not invoked and nothing is persisted:
    see EV-21 and EV-24.
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
    print("NOTE: scene data is mock; the solver and persistence are not wired yet.")

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

    print("\n[1/3] Conflict Sentinel inspecting schedule integrity...")
    disruptions = sentinel.inspect_schedule_disruptions(mock_scenes, mock_availabilities, mock_permits, mock_weather)
    print(f"   -> Detected {len(disruptions)} disruption(s)!")
    for d in disruptions:
        published = (f"Grafana annotation #{d['annotation_id']}" if d["published"]
                     else "NOT published")
        print(f"      • [{d['trigger_type']}] {d['description']}")
        print(f"        -> {published}")

    print("\n[2/3] Replanner Agent formulating alternative options...")
    proposals = replanner.generate_replan_proposals(disruptions[0], mock_scenes)
    print(f"   -> Generated {len(proposals)} proposal options for Producer review:")
    for prop in proposals:
        print(f"\n   📋 {prop['title']}")
        print(f"      • Extra Shoot Days: {prop['cost_deltas']['extra_shoot_days']}")
        print(f"      • Cost Delta: +${prop['cost_deltas']['estimated_cost_delta_usd']:.2f}")
        print(f"      • Justification: {prop['justification']}")

    print("\n[3/3] Draft version registration (STUB — mcp-schedule is not contacted)...")
    for prop in proposals:
        res = replanner.register_draft_proposal(prop)
        print(f"   -> Version {prop['proposal_id'][:8]} would be registered as: {res['version_status']}")

    published = sum(1 for d in disruptions if d["published"])
    print("\n" + "=" * 60)
    if published:
        print(f"PIPELINE COMPLETED — {published} disruption(s) published to Grafana over MCP.")
        print("Replan proposals are still stubbed (EV-24).")
    else:
        print("PIPELINE COMPLETED — nothing was published to Grafana.")
    print("=" * 60)

    if grafana:
        grafana.close()
    return True

if __name__ == "__main__":
    run_disruption_pipeline()
