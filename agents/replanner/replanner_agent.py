import uuid
import datetime
from typing import List, Dict, Any, Optional

class ReplannerAgent:
    """
    Replanner: produces alternative schedule proposals for a disruption (§6 / ADR-002).

    NOT IMPLEMENTED YET: everything of substance. The two proposals below are hardcoded,
    the cost deltas are literals rather than computed figures, and register_draft_proposal()
    never contacts mcp-schedule. EV-24 replaces this with an ADK agent that drives the
    CP-SAT solver and grounds every number it reports in actual solver output.
    """
    def __init__(self, mcp_schedule_url: Optional[str] = None):
        self.mcp_schedule_url = mcp_schedule_url or "http://localhost:5000/mcp/tools"

    def generate_replan_proposals(self, disruption: Dict[str, Any], current_schedule: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        """
        STUB. Returns two hardcoded proposals with literal cost figures ($1,500 / $8,500).
        The only transformation applied to the schedule is swapping the first two scenes;
        no solver is invoked and no constraint is evaluated.
        """
        trigger_type = disruption.get("trigger_type", "Unknown")
        description = disruption.get("description", "")
        
        proposals = []

        # Option 1: Cover Day / Interior Swap (No Extra Days)
        option_1_id = str(uuid.uuid4())
        option_1_scenes = list(current_schedule)
        if len(option_1_scenes) > 1:
            # Swap first two scenes as a cover strategy
            option_1_scenes[0], option_1_scenes[1] = option_1_scenes[1], option_1_scenes[0]

        proposal_1 = {
            "proposal_id": option_1_id,
            "title": "Option A: Interior Cover Day & Location Grouping",
            "schedule_scenes": option_1_scenes,
            "cost_deltas": {
                "extra_shoot_days": 0,
                "extra_hold_days": 1,
                "company_moves_delta": 0,
                "estimated_cost_delta_usd": 1500.00
            },
            "justification": (
                f"Resolved disruption ({trigger_type}) by swapping scheduled outdoor scenes with indoor cover scenes. "
                "Maintains target completion date with zero extra shooting days. Minor hold fee of $1,500 applies."
            ),
            "created_at": datetime.datetime.now(datetime.timezone.utc).isoformat()
        }
        proposals.append(proposal_1)

        # Option 2: Schedule Extension / Rest Day Insert
        option_2_id = str(uuid.uuid4())
        option_2_scenes = list(current_schedule)

        proposal_2 = {
            "proposal_id": option_2_id,
            "title": "Option B: Insert Standby Rest Day & Extended Permit Window",
            "schedule_scenes": option_2_scenes,
            "cost_deltas": {
                "extra_shoot_days": 1,
                "extra_hold_days": 2,
                "company_moves_delta": 1,
                "estimated_cost_delta_usd": 8500.00
            },
            "justification": (
                f"Resolved disruption ({trigger_type}) by inserting a 1-day standby period for cast recovery/permit renewal. "
                "Minimizes daily crunch time but adds 1 shooting day and $8,500 in crew/equipment overtime fees."
            ),
            "created_at": datetime.datetime.now(datetime.timezone.utc).isoformat()
        }
        proposals.append(proposal_2)

        return proposals

    def register_draft_proposal(self, proposal: Dict[str, Any]) -> Dict[str, Any]:
        """
        STUB. Returns a canned success dict; self.mcp_schedule_url is never called.
        """
        return {
            "status": "registered",
            "proposal_id": proposal["proposal_id"],
            "version_status": "draft",
            "message": "Draft schedule version created for producer review."
        }
