"""
How far does CP-SAT go before it stops proving anything? (EV-38)

The demo screenplay is 14 scenes. A feature is 90 to 130, and a judge is right to ask whether
the solver still answers at that size — or whether it quietly returns the first thing it found
and calls it a schedule. This measures it, at four sizes cut from one screenplay so that size
is the only variable.

    dotnet run --project src/Stripboard.Web        # in another terminal
    python demo/make_longform_screenplay.py
    python demo/run_scale_benchmark.py

**Reads `isOptimal`, not just the wall clock.** CP-SAT is capped at 10 seconds
(`CpSatScheduleSolver`), so past a certain size it returns the best schedule it found rather
than the best that exists. Both are usable; only one is provable, and a benchmark that
reported "solved in 9.8s" without saying which would be describing a different system.

**Refuses to run against a deployed instance.** Every import replaces the screenplay and
commits a new schedule, so pointing this at the demo would silently destroy whatever a
producer had approved.
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

DEFAULT_URL = "http://localhost:5164"
SIZES = [14, 28, 56, 112]

LOCAL_HOSTS = ("localhost", "127.0.0.1", "[::1]")


def target() -> str:
    url = (os.getenv("STRIPBOARD_URL") or DEFAULT_URL).rstrip("/")
    if not any(host in url for host in LOCAL_HOSTS):
        sys.exit(
            f"Refusing to benchmark against {url}.\n"
            "Each import replaces the screenplay and commits a new schedule, so running this\n"
            "against a deployed instance would destroy the committed board — including one a\n"
            "producer approved. Point STRIPBOARD_URL at localhost, or run the web app locally."
        )
    return url


def post(url: str, payload: dict) -> tuple[dict, float]:
    body = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        f"{url}/api/breakdown/import", data=body,
        headers={"Content-Type": "application/json"}, method="POST")

    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            result = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")[:300]
        raise SystemExit(f"{url} answered HTTP {exc.code}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise SystemExit(f"{url} is unreachable: {exc.reason}. Is the web app running?") from exc
    return result, time.perf_counter() - started


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    url = target()

    source = Path(__file__).with_name("screenplay-longform.breakdown.json")
    if not source.exists():
        raise SystemExit(f"{source.name} is missing. Run: python demo/make_longform_screenplay.py")

    full = json.loads(source.read_text(encoding="utf-8"))
    scenes = full["scenes"]

    print(f"Scale benchmark against {url}")
    print("Cut from demo/screenplay-longform.fountain, so size is the only variable.\n")
    header = f"{'scenes':>7} {'locations':>10} {'cast':>5} {'8ths':>5} {'days':>5} {'moves':>6} {'cost':>10} {'proved':>7} {'elapsed':>9}"
    print(header)
    print("-" * len(header))

    rows = []
    for size in SIZES:
        subset = scenes[:size]
        payload = {"source": full["source"], "scenes": subset}
        result, elapsed = post(url, payload)

        cast = {name for s in subset for name in s["cast"]}
        eighths = sum(s["eighths"] for s in subset)
        proved = "optimal" if result.get("isOptimal") else "feasible"

        row = {
            "scenes": size,
            "locations": result.get("locations"),
            "cast": len(cast),
            "eighths": eighths,
            "days": result.get("totalDays"),
            "companyMoves": result.get("companyMoves"),
            "costUsd": result.get("estimatedCostUsd"),
            "proved": proved,
            "elapsedSeconds": round(elapsed, 2),
        }
        rows.append(row)
        print(f"{size:>7} {row['locations']:>10} {len(cast):>5} {eighths:>5} "
              f"{row['days']:>5} {row['companyMoves']:>6} {row['costUsd']:>10,.0f} "
              f"{proved:>7} {elapsed:>8.2f}s")

    print()
    if all(r["proved"] == "optimal" for r in rows):
        print("Every size was proved optimal inside the 10-second cap.")
    else:
        first = next(r for r in rows if r["proved"] != "optimal")
        print(f"Optimality stops being provable at {first['scenes']} scenes: past that the "
              f"solver returns the best schedule it found inside the cap, not the best that "
              f"exists. The schedule is still feasible and still obeys every hard constraint "
              f"— turnaround, Day Out of Days, permit windows — because those are constraints "
              f"of the model rather than goals of the search.")

    print("\nTiming is end to end: import, solve and persist. The solve is the bulk of it but\n"
          "not all of it, so read these as an upper bound on the solver.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
