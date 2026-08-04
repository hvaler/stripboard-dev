import os
import sys

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "agents", "sentinel")))
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "agents", "replanner")))

from sentinel_agent import ConflictSentinelAgent
from replanner_agent import ReplannerAgent

def run_disruption_pipeline():
    """
    Executes the end-to-end demo disruption pipeline (§8 of brief):
    1. Injects actor unavailability (Sherlock Holmes on 2026-08-10).
    2. Conflict Sentinel detects anomaly and emits Grafana annotation.
    3. Replanner Agent generates 2 alternative draft schedules with cost deltas.
    """
    sys.stdout.reconfigure(encoding='utf-8')
    print("=" * 60)
    print("🎬 STRIPBOARD DEMO PIPELINE: INJECTING DISRUPTION EVENT")
    print("=" * 60)

    sentinel = ConflictSentinelAgent()
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
        print(f"      • [{d['trigger_type']}] {d['description']}")

    print("\n[2/3] Replanner Agent formulating alternative options...")
    proposals = replanner.generate_replan_proposals(disruptions[0], mock_scenes)
    print(f"   -> Generated {len(proposals)} proposal options for Producer review:")
    for prop in proposals:
        print(f"\n   📋 {prop['title']}")
        print(f"      • Extra Shoot Days: {prop['cost_deltas']['extra_shoot_days']}")
        print(f"      • Cost Delta: +${prop['cost_deltas']['estimated_cost_delta_usd']:.2f}")
        print(f"      • Justification: {prop['justification']}")

    print("\n[3/3] Registering draft versions in mcp-schedule...")
    for prop in proposals:
        res = replanner.register_draft_proposal(prop)
        print(f"   -> Version {prop['proposal_id'][:8]} registered status: {res['version_status']}")

    print("\n" + "=" * 60)
    print("✅ DEMO DISRUPTION PIPELINE EXECUTED SUCCESSFULLY")
    print("   Open Stripboard Web -> Proposals to review and approve as Producer.")
    print("=" * 60)
    return True

if __name__ == "__main__":
    run_disruption_pipeline()
