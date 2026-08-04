# ADR-009 — Screenplay breakdown via Gemini structured output on Vertex AI

**Status:** Accepted · 2026-08-04 · Implements EV-18

## Context

The hackathon Official Rules require Google Cloud AI to be used *at runtime*, and state
that naming a dependency in the README or listing it in `requirements.txt` is not
sufficient — it must be imported and called. Until now the breakdown agent called no model
at all: `_extract_with_gemini_or_fallback()` was keyword matching hardcoded to the demo
screenplay (`if "HOLMES" in text.upper()`). This was the first of two pass/fail gaps in
Stage One judging.

Two questions had to be answered: which Google SDK to use, and which backend to authenticate
against.

## Decision

### 1. `google-genai` with native structured output, not `google-adk`

Breakdown is a single-shot extraction: screenplay in, typed scenes out. There is no tool
use, no multi-turn state and no delegation, which is what ADK's `LlmAgent` + `Runner` +
session services exist to manage. Using `google-genai` directly with
`response_mime_type="application/json"` and a Pydantic `response_schema` gives us
model-enforced schema conformance with no orchestration ceremony.

`google-genai` is explicitly one of the four accepted packages in the rules, so this fully
satisfies the requirement.

ADK still arrives, but where it earns its keep: EV-24 wraps the agents as ADK `LlmAgent`s
with real MCP toolsets, and EV-25/EV-26 add A2A orchestration and Agent Engine deployment.
`BreakdownAgent.process_fountain_file()` is designed to be exposed as an ADK tool at that
point without rework.

### 2. Vertex AI as the default backend, API key as a fallback

`GeminiClient` prefers Vertex AI (Application Default Credentials + `GOOGLE_CLOUD_PROJECT`)
because that is what the Cloud Run and Agent Engine deployments will use, so local runs and
deployed runs exercise the same auth path. If `GEMINI_API_KEY`/`GOOGLE_API_KEY` is set
instead, the client falls back to the Gemini Developer API for contributors without a GCP
project.

Verified against project `stripboard-hack`, location `global`, model `gemini-2.5-flash`.

### 3. The model does not measure pages

`eighths` is a physical page-length measurement, not a semantic judgement, so it is computed
deterministically by `estimate_eighths()` from the scene text and merged into the model's
output afterwards. It is deliberately absent from the schema the model is asked to fill.
This is the project's guiding rule applied at the smallest scale: the model formulates,
deterministic code decides.

### 4. Failure is visible, never silent

- A payload that fails validation is retried up to 3 times with the specific validation
  errors fed back into the prompt.
- If extraction still fails, the agent degrades to a parser-only breakdown with **empty**
  cast and elements, labelled `source="fallback"`. It does not guess, because guessing is
  what the previous implementation did.
- Every result carries `source`, `model`, `backend`, `attempts` and `total_tokens`, so a
  caller — or a judge — can tell a real extraction from a replay at a glance.
- The demo cache is keyed by a hash of the screenplay content and is **off by default**. It
  is only written for a real Gemini result, so a fallback can never be replayed as though it
  were an extraction.

## Consequences

- The Stage One "Google Cloud AI at runtime" requirement is met and demonstrable:
  `python -m agents.breakdown --file demo/screenplay.fountain -v` shows the Vertex AI
  endpoint being called and the token count returned.
- Extraction now generalises. `demo/screenplay-harbour.fountain` shares no characters,
  locations or props with the demo script and is broken down correctly, which the hardcoded
  implementation could not do.
- Quality improved beyond the rule requirement: the model finds elements the hardcoded
  version never had (magnifying glass, tea service, market crowd, foghorn) and correctly
  excludes characters who are only mentioned in dialogue but never present — an error the
  keyword matcher made in scene 2.
- Integration tests make real API calls. They skip when no credentials are configured, and
  fail rather than skip when credentials exist but extraction is broken.
- Running the test suite now costs Vertex AI tokens (~2.5k per extraction call).

## Related

- Supersedes the extraction behaviour described in EV-01..EV-04.
- The remaining Stage One gap is the Grafana Cloud MCP client ([ADR-008](ADR-008-grafana-mcp-qualifying-use.md), EV-19).
