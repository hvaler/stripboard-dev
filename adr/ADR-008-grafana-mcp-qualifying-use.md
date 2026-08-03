# ADR-008 — Qualifying partner track use: Grafana Cloud MCP Server

**Status:** Accepted · 2026-08-03 (Supersedes [ADR-005](ADR-005-grafana-track.md))

## Context
The hackathon Official Rules mandate that projects competing in the Grafana partner track must actively use the Grafana stack at runtime:
> *"primarily through the Grafana Cloud MCP server (the official `grafana/mcp-grafana` server, or the hosted Grafana Cloud MCP endpoint), which exposes 60+ tools for querying metrics, logs, and traces, searching dashboards, and managing alerts and incidents."*

The rules explicitly state that OpenTelemetry-native monitoring (AI Observability) alone does not fulfill the track requirement: the MCP server connection is the mandatory pass/fail criteria evaluated during Stage One judging.

ADR-005 previously positioned OTLP telemetry export, Annotations API calls from code, and a provisioned dashboard as the primary partner integration. While valuable, this setup does not satisfy the qualifying requirement on its own.

## Decision
1. **Grafana Cloud MCP Server Integration:** The Conflict Sentinel (`agents/sentinel`) will connect to the hosted Grafana Cloud MCP endpoint (or `grafana/mcp-grafana`) as an active MCP client, establishing a 5th MCP endpoint alongside `mcp-schedule`, `mcp-people`, `mcp-locations`, and `mcp-weather`.
2. **Active Runtime Agent Querying:** In alignment with ADR-001 (*"The Python/.NET boundary is the MCP boundary"*), Conflict Sentinel invokes Grafana MCP tools dynamically at runtime to inspect system metrics, logs, traces, alerts, and incident states to validate schedule integrity and operational status.
3. **Observability as Complement:** OpenTelemetry OTLP export, Annotations API calls on anomalies, and the provisioned "Shoot Mission Control" dashboard pass to secondary/complementary roles for governance visualization.
4. **Authentication:** The hosted Grafana Cloud MCP endpoint will authenticate via service account / instance API token (`grafana-instance-token`), securely managed in GCP Secret Manager.

## Consequences
- Guarantees compliance with the pass/fail partner qualification requirement in Stage One of judging.
- Evolves Conflict Sentinel from a passive telemetry producer into an active Grafana Cloud MCP client.
- Retains existing OTLP telemetry, Annotations API, and dashboard features as complementary governance tools.
