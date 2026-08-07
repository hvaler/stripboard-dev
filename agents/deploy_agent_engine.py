"""
Deploy the orchestrator to Vertex AI Agent Engine (EV-26).

The agent tree in `agents/orchestrator` is the same one the demo runs locally. This script
packages it as an `AdkApp` and creates a managed Agent Engine instance for it, running under
`sa-orchestrator` rather than a default account — the identity is the point, because
`POST /api/schedule/commit` refuses anything that is not a human Producer (ADR-018).

    # what it would create, and what is missing to create it
    python agents/deploy_agent_engine.py

    # actually create it
    python agents/deploy_agent_engine.py --deploy

Deploying is deliberately opt-in: an Agent Engine instance is a billed, long-lived resource,
and running a file should not quietly start one.

Prerequisites (checked below, and printed if unmet):
    pip install "google-cloud-aiplatform[agent_engines,adk]"
    gcloud storage buckets create gs://stripboard-hack-agent-staging --location=europe-west1
    bash infra/iam/setup-agent-iam.sh
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "orchestrator")))

PROJECT = os.getenv("GOOGLE_CLOUD_PROJECT", "stripboard-hack")
LOCATION = os.getenv("GOOGLE_CLOUD_LOCATION", "europe-west1")
STAGING_BUCKET = os.getenv("STRIPBOARD_STAGING_BUCKET", f"gs://{PROJECT}-agent-staging")
SERVICE_ACCOUNT = os.getenv(
    "STRIPBOARD_ORCHESTRATOR_SA", f"sa-orchestrator@{PROJECT}.iam.gserviceaccount.com")
DISPLAY_NAME = "stripboard-line-producer"

# The deployed agent reaches the scheduling service over HTTPS, so it needs the URL. Its
# tools are useless without it, which is why this is a hard requirement rather than a
# default that would silently point at localhost inside a managed container.
STRIPBOARD_URL = os.getenv("STRIPBOARD_URL", "")

# The private MCP server the deployed agent will call. sa-orchestrator already holds
# run.invoker on it (infra/deploy-mcp.sh), so it can reach it as itself.
MCP_ENDPOINT = os.getenv("STRIPBOARD_MCP_SCHEDULE_ENDPOINT", "")

REQUIREMENTS = [
    "google-cloud-aiplatform[agent_engines,adk]",
    "requests>=2.31.0",
    # Agent Engine pickles the agent to ship it and validates the schemas on the far side, so
    # both have to be present in the deployed environment even though nothing here imports
    # them by name. The SDK reports them as "missing requirements" rather than failing, which
    # is easy to read past in a wall of deploy output.
    "cloudpickle",
    "pydantic",
]


def _preflight():
    """Everything that must be true before a deploy, reported together rather than one crash at a time."""
    problems = []

    try:
        import vertexai  # noqa: F401
    except ImportError:
        problems.append(
            'google-cloud-aiplatform is not installed: '
            'pip install "google-cloud-aiplatform[agent_engines,adk]"')

    if not STRIPBOARD_URL:
        problems.append(
            "STRIPBOARD_URL is not set. The deployed agent cannot reach the scheduling "
            "service without it, and every one of its tools would fail at runtime.")
    elif STRIPBOARD_URL.startswith("http://localhost"):
        problems.append(
            f"STRIPBOARD_URL is {STRIPBOARD_URL}, which is localhost inside the managed "
            "container. Point it at the Cloud Run URL.")

    # Everything below was learned by deploying five times. Each of these produced a failure
    # that surfaced far from its cause: three of them as "Ready to deploy" followed by a
    # container that could not serve traffic, which says nothing about why. A preflight that
    # only reads environment variables gives a confidence it has not earned.

    # The staging bucket. Absent, the create fails after the upload has already started.
    try:
        from google.cloud import storage

        if not storage.Client(project=PROJECT).lookup_bucket(STAGING_BUCKET.removeprefix("gs://")):
            problems.append(
                f"{STAGING_BUCKET} does not exist: "
                f"gcloud storage buckets create {STAGING_BUCKET} --location={LOCATION}")
    except ImportError:
        pass
    except Exception as exc:  # noqa: BLE001 - a lookup failure is worth reporting, not raising
        problems.append(f"Could not check {STAGING_BUCKET}: {exc}")

    # APIs. Agent Engine needs Cloud Resource Manager, and its absence arrives as an opaque
    # PERMISSION_DENIED from a service nobody mentioned.
    try:
        import subprocess

        enabled = subprocess.run(
            ["gcloud", "services", "list", "--enabled", "--project", PROJECT,
             "--format=value(config.name)"],
            capture_output=True, text=True, timeout=60, shell=False).stdout
        for api in ("aiplatform.googleapis.com", "cloudresourcemanager.googleapis.com",
                    "storage.googleapis.com"):
            if api not in enabled:
                problems.append(f"{api} is not enabled: gcloud services enable {api} --project {PROJECT}")
    except Exception:  # noqa: BLE001 - no gcloud is not a reason to refuse to deploy
        pass

    # The packages that actually have to travel. cloudpickle rebuilds the agent by importing
    # its module by name, so every module the agent's own modules import must ship AND be on
    # the path. Missing one produced a container that started and died on ModuleNotFoundError.
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    for package in ("agents/orchestrator", "agents/replanner", "agents/common"):
        if not os.path.isdir(os.path.join(root, package)):
            problems.append(f"{package} is missing, and it ships in extra_packages.")

    return problems


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--deploy", action="store_true",
                        help="Create the Agent Engine instance. Without this, nothing is created.")
    parser.add_argument("--update", metavar="RESOURCE_NAME",
                        help="Update an existing instance instead of creating another one.")
    args = parser.parse_args()

    print(f"Project        {PROJECT}")
    print(f"Location       {LOCATION}")
    print(f"Staging bucket {STAGING_BUCKET}")
    print(f"Identity       {SERVICE_ACCOUNT}")
    print(f"Stripboard URL {STRIPBOARD_URL or '(not set)'}")
    print(f"Display name   {DISPLAY_NAME}")

    problems = _preflight()
    if problems:
        print("\nNot ready to deploy:")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    if not (args.deploy or args.update):
        print("\nReady to deploy. Re-run with --deploy to create the Agent Engine instance.")
        print("It is a billed resource, so this script will not create one by accident.")
        return 0

    import vertexai
    from vertexai import agent_engines
    from orchestrator_agent import build_orchestrator

    client = vertexai.Client(project=PROJECT, location=LOCATION)
    # The tools are discovered here, against the live MCP server, and travel as schemas plus
    # an endpoint. The connection is NOT made here: McpBackedTool opens it on first use inside
    # the container, minting its own identity token as sa-orchestrator. A socket cannot be
    # pickled, and a credential belonging to whoever ran the deploy has no business being one.
    toolset = None
    if MCP_ENDPOINT:
        from mcp_tools import StripboardMcpToolset

        toolset = StripboardMcpToolset(endpoint=MCP_ENDPOINT).connect()
        print(f"Discovered {len(toolset.tool_names)} MCP tools at {MCP_ENDPOINT}: "
              f"{', '.join(sorted(toolset.tool_names))}")
        toolset.close()
    else:
        print("STRIPBOARD_MCP_SCHEDULE_ENDPOINT is not set: the deployed agent will reach the "
              "engine over REST instead of MCP.")

    app = agent_engines.AdkApp(agent=build_orchestrator(toolset=toolset), enable_tracing=True)

    config = {
        "display_name": DISPLAY_NAME,
        "description": "Routes production requests to the scheduler, replanner or governance agent.",
        "staging_bucket": STAGING_BUCKET,
        "requirements": REQUIREMENTS,
        # The orchestrator imports propose_replan from the replanner package, and mcp_tools
        # from orchestrator reaches into agents/common for the MCP transport (EV-23). All
        # three ship or the container starts, fails on an import and cannot serve traffic —
        # which is what happened the first time, and is the same omission the sentinel's
        # Dockerfile had. A package a module imports is not optional cargo.
        "extra_packages": ["agents/orchestrator", "agents/replanner", "agents/common"],
        "service_account": SERVICE_ACCOUNT,
        # GOOGLE_CLOUD_PROJECT and GOOGLE_CLOUD_LOCATION are deliberately absent. Agent Engine
        # reserves them and rejects the whole deploy with FAILED_PRECONDITION if they appear —
        # it injects both itself, from the project and region the instance is created in, which
        # is more correct than anything this script could pass: an agent that believed it was
        # in a different project than the one hosting it would fail later and further away.
        "env_vars": {
            # cloudpickle serialises the agent **by reference**: the container has to be able
            # to `import orchestrator_agent` by that exact top-level name to rebuild it. Locally
            # that works because this script puts agents/orchestrator on sys.path; in the
            # container extra_packages only *copies* the directory, and nothing adds it to the
            # path. Without this the deploy succeeds, the container starts, and dies with
            # ModuleNotFoundError — a failure that surfaces as "cannot serve traffic" and says
            # nothing about the cause.
            "PYTHONPATH": "agents/orchestrator:agents/replanner:agents/common",
            "STRIPBOARD_URL": STRIPBOARD_URL,
            "STRIPBOARD_MCP_SCHEDULE_ENDPOINT": MCP_ENDPOINT,
            "GOOGLE_GENAI_USE_VERTEXAI": "TRUE",
            "GOOGLE_CLOUD_AGENT_ENGINE_ENABLE_TELEMETRY": "true",
        },
    }

    if args.update:
        remote = client.agent_engines.update(name=args.update, agent=app, config=config)
        print(f"\nUpdated {remote.api_resource.name}")
    else:
        remote = client.agent_engines.create(agent=app, config=config)
        print(f"\nCreated {remote.api_resource.name}")
        print("Keep that resource name: --update targets it, and it is how you delete it.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
