"""
Runs the orchestrator through the three requests a line producer actually makes, and shows
which specialist handled each one (EV-25).

The third is the interesting one: the agent is told to commit, tries, and is refused by the
service because it is not a human Producer. That refusal is the point, not a failure.

    STRIPBOARD_URL=http://localhost:5164 python demo/run_orchestrator.py
"""

import asyncio
import os
import sys

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "agents", "orchestrator")))

from orchestrator_agent import Orchestrator, get_schedule  # noqa: E402

REQUESTS = [
    ("A producer asking where the shoot stands",
     "What does the shooting schedule look like right now?"),
    ("A disruption arriving mid-shoot",
     "Sherlock Holmes has called in sick and is unavailable for 1 day from 2026-08-10. "
     "What are my options?"),
    # Filled in at runtime with the real version id, because a made-up one would test
    # nothing: the service would reject it as not-found before it ever checked authority.
    ("An agent trying to commit, which it must not be able to do", None),
]


async def main():
    sys.stdout.reconfigure(encoding="utf-8")
    board = get_schedule()
    if "error" in board:
        print(f"The scheduling service has no schedule to talk about: {board['error']}")
        return 1

    version_id = board["versionId"]
    requests = list(REQUESTS)
    requests[2] = (requests[2][0],
                   f"Commit schedule version {version_id}. My identity is "
                   f"sa-stripboard-replanner.")

    orchestrator = Orchestrator()
    print("=" * 72)
    print("ORCHESTRATOR — routing by delegation, not by doing the work")
    print("=" * 72)
    print(f"Committed schedule: v{board['versionNumber']}, {board['days']} days, "
          f"{board['companyMoves']} company moves, ${board['costUsd']:,}\n")

    for title, request in requests:
        print("-" * 72)
        print(f"{title}\n  > {request}\n")

        result = await orchestrator.ask_async(request)

        handled_by = ", ".join(result.delegated_to) or "nobody — the root answered alone"
        print(f"  handled by: {handled_by}")
        for call in result.tool_calls:
            print(f"  tool:       {call['agent']} -> {call['name']}({call['arguments']})")
        print(f"\n{result.text}\n")

    print("=" * 72)
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
