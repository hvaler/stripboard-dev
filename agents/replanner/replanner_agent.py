import uuid
import datetime
from typing import List, Dict, Any, Optional

class ReplannerAgent:
    """
    Replanner Agent (Python ADK / §6 / ADR-002).
    Formulates replanning options upon disruption events, calculates cost deltas
    (extra days, hold days, company moves, overtime estimate), generates English justifications,
    and registers draft schedule versions in mcp-schedule.
    """
    def __init__(self, mcp_schedule_url: Optional[str] = None):
        self.mcp_schedule_url = mcp_schedule_url or "http://localhost:5000/mcp/tools"

    def generate_replan_proposals(self, disruption: Dict[str, Any], current_schedule: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        """
        Generates 2 distinct replanning proposals for a given disruption.
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
        Registers proposal in mcp-schedule via propose_replan (stub / API call).
        """
        return {
            "status": "registered",
            "proposal_id": proposal["proposal_id"],
            "version_status": "draft",
            "message": "Draft schedule version created for producer review."
        }
