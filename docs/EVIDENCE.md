# Evidence

The two hackathon technology requirements are pass/fail and a README reference is explicitly
insufficient for the partner track. This file records what was actually observed at runtime,
with the commands to reproduce it.

Recorded **2026-08-05** against project `stripboard-hack` (europe-west1) and Grafana Cloud
stack `pinkcorridor3522.grafana.net`.

Every command below is one anybody can run against their own project and stack. Nothing here
is a screenshot of something that only worked once.

---

## 1. Google Cloud AI — Gemini 2.5 Flash on Vertex AI

### The breakdown reads a screenplay the model has never seen

`demo/screenplay-nightfall.fountain` is an original 14-scene screenplay written for this
project. It is not Sherlock Holmes, and it is not in the demo cache.

```bash
export GOOGLE_CLOUD_PROJECT=stripboard-hack
python -m agents.breakdown --file demo/screenplay-nightfall.fountain --json
```

```
source=gemini   model=gemini-2.5-flash   attempts=1   scenes=14

 # location                          set                    I/E  D/N    8ths  cast
 1 SALFORD SORTING OFFICE                                   INT  NIGHT     5  Maeve Okonkwo
 2 SALFORD QUAYS                                            EXT  NIGHT     2  Maeve Okonkwo, Figure
 3 MAEVE'S FLAT                                             INT  NIGHT     2  Maeve Okonkwo
 4 GREATER MANCHESTER POLICE         CENTRAL                INT  DAY       6  DI Tomás Reyes, Maeve Okonkwo
 5 GREATER MANCHESTER POLICE         EVIDENCE STORE         INT  DAY       3  DI Tomás Reyes, Clerk
 6 ORDSALL PARK                                             EXT  DAY       5  DI Tomás Reyes, Dr. Priya Nair
 7 SALFORD SORTING OFFICE                                   INT  DAY       4  Maeve Okonkwo, DI Tomás Reyes
 8 SALFORD SORTING OFFICE            MANAGER'S OFFICE       INT  DAY       4  Derek Halliwell, DI Tomás Reyes
 9 SALFORD QUAYS                                            EXT  DAY       2  DI Tomás Reyes
10 MAEVE'S FLAT                                             INT  NIGHT     2  Maeve Okonkwo
11 MANCHESTER SHIP CANAL                                    EXT  NIGHT     2  DI Tomás Reyes, Maeve Okonkwo
12 MANCHESTER SHIP CANAL             CANAL MAINTENANCE HUT  INT  NIGHT     3  Maeve Okonkwo
13 GREATER MANCHESTER POLICE         INTERVIEW ROOM         INT  DAY       4  Derek Halliwell, DI Tomás Reyes
14 SALFORD SORTING OFFICE                                   EXT  DAY       2  Maeve Okonkwo
```

What this shows beyond "the API answered":

- **`source=gemini`, not `fallback`.** The deterministic parser is a labelled last resort and
  the output says which one produced it (ADR-009).
- **Location separated from set.** Scenes 8 and 12 name a set inside a location the unit
  already travelled to. That distinction is what makes the company-move count real rather
  than a count of scene headings (ADR-013).
- **`attempts=1`.** The validation-retry loop did not need to correct the model.
- **Eighths are computed, not guessed.** The model extracts; the page length is measured in
  Python. The guiding principle at its smallest scale.

Other formats, same agent — the PDF goes through Gemini multimodal:

```bash
python -m agents.breakdown --file demo/screenplay-metropole.fdx
python -m agents.breakdown --file demo/screenplay-metropole.pdf
```

### That breakdown drives the deployed product

```bash
python -m agents.breakdown --file demo/screenplay-nightfall.fountain --json \
  | curl -s -X POST https://stripboard-web-wc7oib7k6q-ew.a.run.app/api/breakdown/import \
         -H 'Content-Type: application/json' --data-binary @-
```

```json
{"scenes":14,"castCreated":6,"source":"gemini","versionNumber":1,
 "totalDays":2,"companyMoves":6,"estimatedCostUsd":26800.0}
```

The board on screen changes because the screenplay changed. Nothing in the demo path is
written into a `.razor` file.

### Every agent is Google Cloud AI

```bash
grep -rn "anthropic\|openai\|langchain\|cohere\|mistral" --include="*.py" --include="*.txt" \
     --include="*.csproj" --include="*.props" .
```

No matches. The dependency set across `agents/*/requirements.txt` is `google-adk`,
`google-genai`, `pydantic`, `jsonschema`, `defusedxml` and `requests`.

---

## 2. Grafana partner track — the MCP server, used at runtime

### Handshake and tool discovery

```bash
export GRAFANA_MCP_ENDPOINT=http://localhost:8000/mcp   # infra/grafana/run-mcp-sidecar.sh
python -m unittest discover -s agents/sentinel -p "test_*.py"
```

```
server: {'name': 'mcp-grafana', 'version': '(devel)'}
tools/list -> 73 tools
sample: add_activity_to_incident, alerting_manage_routing, alerting_manage_rules,
        analyze_loki_labels, check_datasources_health, create_annotation, create_datasource,
        create_folder, create_incident, create_snapshot, delete_snapshot,
        find_error_pattern_logs, …
```

The transport is implemented in `agents/sentinel/grafana_mcp_client.py`, not imported: MCP
Streamable HTTP over JSON-RPC 2.0, `initialize` with session negotiation,
`notifications/initialized`, paginated `tools/list`, `tools/call`, and both JSON and SSE
response bodies. One dependency: `requests` (ADR-010).

### Four real `tools/call` results

```
list_datasources({}) ->
  {"datasources": [
     {"id": 6, "uid": "grafanacloud-infinity", "type": "yesoreyeram-infinity-datasource"},
     {"id": 7, "uid": "grafanacloud-k6",       "type": "k6-datasource"},
     {"id": 1, "uid": "grafanacloud-alert-state-history", "type": "loki"},
     {"id": 2, "uid": "grafanacloud-cardinality-management", …}, …]}

get_annotations({"limit": 3}) ->
  {"Payload": [
     {"id": 26, "login": "sa-1-hackaton",
      "tags": ["stripboard","conflict-sentinel","weather-alert","high"],
      "text": "WEATHERALERT: Weather alert (Rain, 90% rain) for EXT Scene #2 at
               TOWER BRIDGE WHARF on 2026-08-11.",
      "time": 1786406400000}, …]}

list_prometheus_metric_names({"regex": "shoot.*"}) ->
  ["shoot_cast_utilization", "shoot_company_moves", "shoot_cost_estimate_usd",
   "shoot_days_total", "shoot_eighths_total", "shoot_locations_per_day_max",
   "shoot_risk_index", "shoot_scenes_total", "shoot_union_violations"]

alerting_manage_rules({"operation":"list","label_selectors":["{stripboard=\"true\"}"]}) ->
  [{"uid":"afu8w3mck1n9cc","title":"Union violation in the committed schedule",
    "state":"normal","folder_uid":"stripboard","rule_group":"shoot-health",
    "labels":{"severity":"critical","stripboard":"true",
              "stripboardTrigger":"Manual","stripboardAction":"replan"}}, …]
```

The annotation above was written **through the MCP server** by the Conflict Sentinel and read
back attributed to the sentinel's Grafana service account — a round trip, not a log line.

### The metrics are about the shoot, not the app

`shoot_*` reach Grafana Cloud over OTLP from the deployed Cloud Run service. Queried live
through the MCP server's `query_prometheus` tool:

```
shoot_days_total              2
shoot_cost_estimate_usd       26800
shoot_company_moves           6
shoot_union_violations        0
shoot_risk_index              38
shoot_locations_per_day_max   4
shoot_cast_utilization        {actor="Maeve Okonkwo"}   1.0
                              {actor="DI Tomás Reyes"}  1.0
                              {actor="Derek Halliwell"} 0.5
                              {actor="Clerk"}           0.5
```

`shoot_cast_utilization` is the one to look at: it is money. An actor under contract who is
called on half the shooting days is being paid against days they do not work, and it is the
waste a Day Out of Days schedule exists to prevent.

### The same metrics, without our credentials

Everything above is reproduced from a terminal holding a token. So that none of it has to be
taken on trust, the "Shoot Mission Control" dashboard is published read-only:

**<https://pinkcorridor3522.grafana.net/public-dashboards/1e372a04e0974e1fa34afb2e143957c3>**

Nine panels against the same Grafana Cloud stack, no login. The datasource is pinned to
`grafanacloud-prom` rather than a template variable — a public dashboard has no user to
resolve `${datasource}` for, so a templated one renders *No data* while looking healthy,
which is the same failure mode as a gauge publishing zero.

If the panels are empty, the web service has scaled to zero and nothing is exporting; see §5.

### Gemini answers from those metrics, over MCP

"Ask your shoot" on the deployed Conflict Sentinel — a private Cloud Run service, so this
needs an identity token:

```bash
TOKEN=$(gcloud auth print-identity-token)
curl -s -X POST https://stripboard-sentinel-wc7oib7k6q-ew.a.run.app/api/ask \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"question":"Which actor am I paying without using?"}'
```

```
answer:
  The following actors have the lowest utilization:
  * Clerk: 0.5
  * Derek Halliwell: 0.5
  * Dr. Priya Nair: 0.5
  * Figure: 0.5

tool_calls:
  list_datasources  {"type": "prometheus"}
  query_prometheus  {"expr": "shoot_cast_utilization", "datasourceUid": "grafanacloud-prom",
                     "queryType": "instant", "endTime": "now"}
```

The tools were **discovered from the MCP server at runtime**, not written into the agent. The
first turn is forced to call one and an answer with zero tool calls is refused, so a figure
here cannot be one the model produced from memory (ADR-014).

### Alert rules over those metrics, read back over MCP

```bash
python infra/grafana/provision-alerts.py
python demo/run_alert_loop.py
```

Four rules in folder `stripboard`, group `shoot-health`, evaluating every 60s. Read back
through the MCP server:

```
normal     Union violation in the committed schedule
normal     Cast paid to wait
normal     Schedule cost above budget
firing     Unit hopping between locations in a day

FIRING: Unit hopping between locations in a day | high | action=consolidate
         At least one shooting day visits more than two locations. Each company move costs
         about an hour of shooting light, so a day with three or more is a day largely spent
         in the van.
```

It fires because the committed schedule genuinely has that problem —
`shoot_locations_per_day_max` reads **4**. Three rules stay green because that schedule is
genuinely fine on those axes. Nothing here was arranged to fire.

Each rule carries two labels that make it actionable rather than decorative:
`stripboardTrigger` (what happened) and `stripboardAction` (what to do). The provisioner
refuses to create a rule without a trigger.

This closes the loop the project is built around:

```
the shoot emits shoot_* over OTLP
  -> Grafana Cloud evaluates the rules
    -> the sentinel reads the firing ones back over MCP
      -> the agents ask CP-SAT for options
        -> a human Producer approves
```

### The whole loop, in one run

`python demo/run_alert_loop.py` against the deployed service. Nobody typed a disruption:

```
[1/3] Conflict Sentinel asking Grafana which of the shoot's rules are firing...
   -> [high] Unit hopping between locations in a day
      At least one shooting day visits more than two locations. Each company move costs
      about an hour of shooting light, so a day with three or more is a day largely spent
      in the van.

[2/3] Handing 'Unit hopping between locations in a day' to the agents  (action: consolidate)
   handled by: replanner
   tool:       line_producer -> transfer_to_agent({'agent_name': 'replanner'})
   tool:       replanner -> consolidate_schedule({'max_locations_per_day': 2})

   1. Leave it — the worst day visits 4 locations. The committed schedule as it stands:
      2 shooting days, 6 company moves, $26,800.
   2. Consolidate — at most 2 locations a day. Adds 2 shooting days and $2,800, and
      removes 2 company moves.

   I recommend consolidating to reduce travel time, as it cuts two company moves. The cost
   is two extra shooting days and $2,800.

[3/3] The agent tries to commit its own recommendation...
   -> committed=False  'sa-stripboard-replanner' cannot commit a schedule.
                       Only the Producer role may commit — agents propose, humans decide.
```

`stripboardAction: consolidate` is why the replanner reached for a different tool. Nothing is
blocked, so there is no disruption to absorb — only a constraint to price. Both figures come
from separate CP-SAT runs, and the last step is the one that matters: the agent asked, and the
scheduling service said no.

---

## 3. The agents

### Delegation, and a commit that is refused

```bash
STRIPBOARD_URL=http://localhost:5164 python demo/run_orchestrator.py
```

```
A producer asking where the shoot stands
  > What does the shooting schedule look like right now?
  handled by: scheduler
  tool:       line_producer -> transfer_to_agent({'agent_name': 'scheduler'})
  tool:       scheduler -> get_schedule({})

  The shooting schedule is 3 days long. There are 2 day units and 1 night unit.
  There are 8 company moves. The estimated cost is 41600 USD. There are no union violations.

A disruption arriving mid-shoot
  > Sherlock Holmes has called in sick and is unavailable for 1 day from 2026-08-10.
  handled by: replanner
  tool:       line_producer -> transfer_to_agent({'agent_name': 'replanner'})
  tool:       replanner -> propose_replan({'trigger_type': 'CastUnavailability',
                'person_name': 'Sherlock Holmes', 'start_date': '2026-08-10',
                'duration_days': 1, 'description': 'Sherlock Holmes sick'})

  Option A — absorb within the existing window: 3 shooting days, 8 company moves,
  0 union violations, $40,100 — $1,500 less than the original plan.
  Option B — extend the schedule: the same outcome as Option A.
  I recommend Option A.

An agent trying to commit, which it must not be able to do
  > Commit schedule version 4e43f904-…. My identity is sa-stripboard-replanner.
  handled by: governance
  tool:       line_producer -> transfer_to_agent({'agent_name': 'governance'})
  tool:       governance -> commit_schedule({'version_id': '4e43f904-…',
                                             'identity': 'sa-stripboard-replanner'})

  The commit was refused because 'sa-stripboard-replanner' cannot commit a schedule.
  Only the Producer role may commit. Agents can propose, but humans decide.
```

Three things worth reading twice:

- **The root agent has no tools.** It routed all three and answered none of them. Every figure
  above came from a specialist that called something.
- **Every number is traceable.** `$40,100` and `−$1,500` are the difference between two solved
  CP-SAT schedules, not an estimate. The replanner has no arithmetic available to it.
- **The commit was attempted and refused with HTTP 403.** The governance agent *has* the tool.
  The check lives in `ScheduleService.CommitAsync`, behind the HTTP boundary, where no prompt
  can reach it. A rule that has never been tested by being broken is not a rule.

---

## 4. Reproducing all of it

```bash
# .NET: solver, domain rules, service contracts, telemetry, scheduling
dotnet test Stripboard.slnx                       # 93 tests

# Python agents. The Gemini and Grafana tests make real calls and FAIL — not skip —
# when the service is configured but broken.
python -m unittest discover -s agents/breakdown    -p "test_*.py"   # 25
python -m unittest discover -s agents/sentinel     -p "test_*.py"   # 27
python -m unittest discover -s agents/replanner    -p "test_*.py"   # 12
python -m unittest discover -s agents/orchestrator -p "test_*.py"   # 10
```

| Surface | Command | Needs |
|---|---|---|
| Gemini breakdown | `python -m agents.breakdown --file demo/screenplay-nightfall.fountain -v` | ADC + `GOOGLE_CLOUD_PROJECT` |
| Grafana MCP | `python -m unittest discover -s agents/sentinel -p "test_*.py"` | sidecar + `glsa_` token |
| Alert rules | `python infra/grafana/provision-alerts.py` | `GRAFANA_URL` + `glsa_` token |
| Alert-driven replan | `python demo/run_alert_loop.py` | both of the above |
| Orchestration | `python demo/run_orchestrator.py` | `STRIPBOARD_URL` |
| Consolidation trade | `curl -X POST $STRIPBOARD_URL/api/schedule/consolidate -H 'Content-Type: application/json' -d '{"maxLocationsPerDay":2}'` | a running service |
| Hosted product | <https://stripboard-web-wc7oib7k6q-ew.a.run.app> | a browser |

## 5. Cost posture while waiting on hackathon credits

The two services that cost money when nobody is using them are the always-on web instance
(a vCPU that never throttles) and the Cloud SQL instance. Commands to stop and restart both are
in the README under **Stopping the paid services**.

Stopping the database is safe in the sense that matters: the app still starts, `/api/health`
answers 503 naming the database as the reason, and the data is preserved in the stopped
instance. It used to crash-loop instead, which is why this is written down.

Restart both and confirm `/api/health` reports a committed schedule before recording anything.

## 6. Stripboard is an MCP server too

The four `Stripboard.Mcp.*` services used to be REST endpoints under a path beginning
`/mcp/`, which is not the same thing as speaking the protocol. They now use the official
`ModelContextProtocol.AspNetCore` SDK. Against a running server:

```bash
dotnet run --project src/Stripboard.Mcp.Weather

curl -s -X POST http://localhost:5075/mcp \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05",
       "capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}'
```

```
event: message
data: {"result":{"protocolVersion":"2024-11-05",
                 "capabilities":{"logging":{},"tools":{}},
                 "serverInfo":{"name":"Stripboard.Mcp.Weather","version":"1.0.0.0"}},
       "id":1,"jsonrpc":"2.0"}
```

```
tools/list ->
  {"tools":[
    {"name":"get_forecast",
     "description":"Weather for a location on a date … SYNTHETIC — generated
                    deterministically for demo reproducibility, not fetched from any
                    weather service.",
     "inputSchema":{"type":"object",
       "properties":{"locationName":{"type":"string","description":"…"},
                     "date":{"type":"string","description":"The date, ISO format (YYYY-MM-DD)."}},
       "required":["locationName","date"]}}, … ]}
```

Two things in that output are the point. The schema is **generated from the method
signature**, so it cannot drift from what the tool accepts. And the word SYNTHETIC is in the
*description*, not only in the payload, because a model picks a tool from its description and
may never read the provenance field underneath.

### And the agents consume it

A server with no client is a second interface that drifts. The orchestrator's `scheduler` and
`governance` specialists take their tools from this server rather than from Python
(ADR-023):

```bash
dotnet run --project src/Stripboard.Mcp.Schedule
STRIPBOARD_MCP_SCHEDULE_ENDPOINT=http://localhost:5067/mcp python demo/run_orchestrator.py
```

```
Engine reached over: MCP — tools/call against http://localhost:5067/mcp
Tools discovered:    commit_schedule, consolidate_schedule, create_schedule,
                     get_schedule, validate_rules
Committed schedule:  v2, 3 days, 6 company moves, $36,600

  handled by: scheduler
  tool:       line_producer -> transfer_to_agent({'agent_name': 'scheduler'})
  tool:       scheduler     -> get_schedule({})

  handled by: governance
  tool:       line_producer -> transfer_to_agent({'agent_name': 'governance'})
  tool:       governance    -> commit_schedule({'identity': 'sa-stripboard-replanner',
                                                'versionId': '11bb15cd-…'})
```

The last line is the one to read. The refusal that comes back is the **server's**, not a
sentence the model produced from its instructions:

```
'Producer' claims the Producer role but nothing verified it. A commit requires an
authenticated caller — an identity supplied in the request body is a claim, not a credential.
```

The tool-call log is what distinguishes those two cases, which is why the demo prints it. An
agent that had simply repeated its instruction would show no `commit_schedule` line at all.

### All four are on Cloud Run, and private

```bash
bash infra/deploy-mcp.sh
```

```
stripboard-mcp-schedule-00001-dl8    anonymous -> HTTP 403, as intended
                                     authenticated initialize -> "name":"Stripboard.Mcp.Schedule"
stripboard-mcp-people-00001-7p9      anonymous -> HTTP 403, as intended
                                     authenticated initialize -> "name":"Stripboard.Mcp.People"
stripboard-mcp-locations-00001-cmz   anonymous -> HTTP 403, as intended
                                     authenticated initialize -> "name":"Stripboard.Mcp.Locations"
stripboard-mcp-weather-00001-c8c     anonymous -> HTTP 403, as intended
                                     authenticated initialize -> "name":"Stripboard.Mcp.Weather"
```

Each returns **its own** `serverInfo`, which is the check that matters when one parameterised
Dockerfile builds four images: a wrong build argument produces four services that all start
the schedule server and answer `tools/list` with somebody else's tools. A working protocol
giving confidently wrong answers is harder to notice than a broken one.

Least privilege, as the project's policy actually reads:

```bash
for n in schedule people locations weather; do
  gcloud projects get-iam-policy stripboard-hack --flatten="bindings[].members" \
    --format="value(bindings.role)" --filter="bindings.members:sa-mcp-${n}@"
done
```

```
sa-mcp-schedule    roles/cloudsql.client
sa-mcp-people      roles/cloudsql.client
sa-mcp-locations   roles/cloudsql.client
sa-mcp-weather     (no project role at all)
```

The weather server generates a deterministic synthetic forecast and touches no data, so it
holds nothing. *Cannot reach the schedule* is a stronger claim than *does not*, and it is the
one a policy dump can settle.

### Deployment is what gives the governance rule something to refuse

`CallerIdentityResolver` only trusts a principal when `K_SERVICE` says it is on Cloud Run.
The same tool call, from the same client, against the two environments:

```
local        'Producer' claims the Producer role but nothing verified it. A commit requires
             an authenticated caller — an identity supplied in the request body is a claim,
             not a credential.

Cloud Run    'you@example.com' cannot commit a schedule. Only the Producer role may commit
             — agents propose, humans decide.
```

Locally the answer is *nobody may commit*, which is safe and proves little. Deployed, Google
validates the bearer token and the service refuses **a caller it can name**. Only in the
second case is there an identity to have a rule about.

The same shift applies to running the solver at all:

```
local        create_schedule({"identity":"sa-orchestrator", …}) -> a draft version
Cloud Run    create_schedule({"identity":"sa-orchestrator", …})
             -> 'you@example.com' is not permitted to run the solver.
```

Locally the payload's identity is taken at face value because nothing can contradict it.
Deployed, the payload is ignored and the credential decides — so an authenticated account
holding no scheduling role cannot run the solver whatever it calls itself.

Read against the live Cloud SQL schedule through the deployed server:

```
get_schedule -> {"versionNumber": 7, "days": 4, "companyMoves": 4,
                 "costUsd": 29600.0, "isCommitted": true}
```

The same version the web app serves, because both are pointed at the same database rather
than each holding a copy.

### The governance rule, over MCP

```bash
dotnet test tests/Stripboard.Mcp.Contract.Tests    # 33 tests, all through tools/call
```

The one that matters: an agent calls `commit_schedule` and sends `identity: "Producer"`. It
is refused. The identity comes from the credential on the request, not the payload
(ADR-020) — before this, an agent told not to commit only had to claim a different name.

### An identity is not a string you send — against the deployed service

```bash
curl -s -X POST https://stripboard-web-wc7oib7k6q-ew.a.run.app/api/schedule/commit \
  -H 'Content-Type: application/json' \
  -d '{"versionId":"ecfc13dd-9cce-49c3-9332-74ca32073e5e","identity":"Producer"}'
```

```
HTTP 403
{"committed": false,
 "error": "'Producer' claims the Producer role but nothing verified it. A commit requires
           an authenticated caller — an identity supplied in the request body is a claim,
           not a credential."}
```

And with an agent's own name, which is the wrong role rather than an unproved one:

```
HTTP 403
{"committed": false,
 "error": "'sa-replanner' cannot commit a schedule. Only the Producer role may commit
           — agents propose, humans decide."}
```

Two different refusals because they need two different fixes: one says *ask a Producer*, the
other says *authenticate*. A caller who cannot tell them apart will try the wrong one.

The Blazor page commits successfully because a human session is an authenticated principal.
Everything reaching the API anonymously — which is every agent, today — cannot.

## 7. Mutation testing

```bash
dotnet tool install --global dotnet-stryker
dotnet stryker
```

```
Services\UnionRulesService.cs   100.00 %   21 killed   0 survived
```

Scoped to `UnionRulesService`, which is the file that decides whether a schedule is legal.
It found two real gaps: the night-to-day rule was never tested for *not* applying (flipping
`&&` to `||` survived), and the 14-hour boundary was unpinned (`<` and `<=` were
indistinguishable). The wider domain layer scores 43% and that figure is recorded in
[ADR-022](../adr/ADR-022-mutation-testing-the-union-rules.md) rather than hidden — those are
constructors, and pointing the tool at them would measure nothing.

## 8. Least privilege, as Google sees it

`infra/iam/setup-agent-iam.sh` creates one service account per agent **and per MCP server**.
What each one is actually allowed to do, straight from the project's IAM policy:

```bash
gcloud projects get-iam-policy stripboard-hack \
  --flatten='bindings[].members' --filter='bindings.members:sa-' \
  --format='table(bindings.members,bindings.role)'
```

| Service account | Project roles | What that means |
|---|---|---|
| `sa-breakdown` | *(none)* | Exists and can do nothing |
| `sa-scheduler` | *(none)* | Exists and can do nothing |
| `sa-replanner` | *(none)* | Exists and can do nothing |
| `sa-callsheets` | *(none)* | Exists and can do nothing |
| `sa-sentinel` | `aiplatform.user`, `logging.logWriter` | Can call Gemini and write logs. **No `cloudsql.client`** — it cannot reach the database |
| `sa-orchestrator` | `aiplatform.user`, `storage.objectViewer` | Can call Gemini and read the Agent Engine staging bucket. Nothing else |
| `sa-stripboard-web` | `cloudsql.client` | Reaches the database |
| `sa-mcp-schedule`, `sa-mcp-people`, `sa-mcp-locations` | `cloudsql.client` | The three MCP servers that read and write the schedule |
| `sa-mcp-weather` | *(none)* | Generates a synthetic forecast and touches no data, so it holds nothing |

The **five** empty rows are the point. An agent with no bindings is not restrained by a
prompt or by an application check — Google refuses it. `sa-mcp-weather` is the newest and the
clearest: it is a deployed, running service that cannot reach the schedule.

`cloudsql.client` is held by exactly four principals, and `sa-sentinel` is not one of them.

And this is live rather than aspirational, because the deployed services run **as** those
identities:

```bash
gcloud run services describe stripboard-sentinel --region europe-west1 \
  --format='value(spec.template.spec.serviceAccountName)'
# sa-sentinel@stripboard-hack.iam.gserviceaccount.com
```

`sa-sentinel` also holds `secretmanager.secretAccessor` on `grafana-sentinel-token` and
nothing else, and `sa-stripboard-web` holds `run.invoker` on the sentinel service — which is
why the sentinel can stay private while the web app still reaches it (ADR-015).

Two honest limits: the Python agents that run **locally** do so under a developer's own
credentials, not these accounts, and Workload Identity bindings only become meaningful once
those agents run in GCP (EV-26). What is shown above is the deployed surface.

## 9. Scale — a feature-length screenplay

```bash
dotnet run --project src/Stripboard.Web
python demo/make_longform_screenplay.py
python demo/run_scale_benchmark.py
```

```
screenplay-longform.fountain: 112 scenes, 25 locations, 14 speaking parts, 39 night scenes

 scenes  locations  cast  8ths  days  moves       cost  proved   elapsed
------------------------------------------------------------------------
     14         12     7   101     4      9     50,100 optimal     3.40s
     28         14     9   204     7     12     81,300 feasible   10.19s
     56         19    10   409    12     16    119,800 feasible   11.44s
    112         25    14   797    29     38    276,100 feasible   11.19s
```

And the same run with `STRIPBOARD_SOLVER_SECONDS=60`:

```
     14         12     7   101     4      9     49,100 optimal     8.48s
     28         14     9   204     7     12     75,300 feasible   60.25s
     56         19    10   409    12     15    119,300 feasible   59.96s
    112         25    14   797    22     23    210,300 feasible   60.73s
```

Three things in that pair are the point.

**The solver answers at feature length.** 112 scenes across 25 locations, scheduled end to end
— import, solve, persist — in about eleven seconds.

**It stops proving optimality at around 30 scenes**, and the benchmark reads `isOptimal` rather
than only the clock so it cannot report a capped search as a solved one. Every schedule is
still legal: turnaround, Day Out of Days and permit windows are constraints of the model, not
goals of the search, so they hold whether or not the search finished. The cap costs a cheaper
plan, never a lawful one.

**Fifty more seconds is worth seven shooting days.** At 112 scenes the schedule goes from 29
days and $276,100 to 22 days and $210,300 — about a quarter of the budget. That is the honest
shape of the trade, and it is why the ten-second default is exposed as configuration
(`STRIPBOARD_SOLVER_SECONDS`) rather than compiled in: ten seconds suits a producer waiting on
a web request, and sixty suits a production planning the picture overnight.

The screenplay is generated rather than checked in as prose, because the *distribution* is what
the solver reacts to and a generator lets it be stated: two leads carry the picture, four
supporting parts recur, eight day players appear once or twice, six standing sets take about
half the scenes. A uniform screenplay would make Day Out of Days trivial and let any scheduler
look competent. The benchmark reads the generator's own breakdown rather than Gemini's, so what
is being measured is the solver and not how well an extractor did that day.

`run_scale_benchmark.py` refuses to run against anything but localhost: every import replaces
the screenplay and commits a new schedule, so pointing it at the deployed demo would destroy a
board a producer had approved.

---

## 10. The orchestrator on Agent Engine, refused by name

Invoked remotely against the deployed instance
(`reasoningEngines/5478127569393942528`), asked to commit as an agent:

```
tools called: transfer_to_agent -> commit_schedule

server response:
  Stripboard MCP tool 'commit_schedule' failed:
  'sa-orchestrator@stripboard-hack.iam.gserviceaccount.com' cannot commit a schedule.
  Only the Producer role may commit — agents propose, humans decide.

agent's answer:
  "It seems that the sa-orchestrator identity is not authorized to commit schedules.
   Only a human Producer can commit a schedule. As an agent, I am refused, which is
   the system working as designed."
```

**Read the identity in the refusal.** The prompt said `My identity is sa-orchestrator`. The
service answered with `sa-orchestrator@stripboard-hack.iam.gserviceaccount.com` — the full
principal from the OIDC token Google validated on the way in. The string in the message never
reached the decision. That is ADR-020 with every layer real at once: a managed agent, calling a
private service over MCP, authenticating as itself, and being told no by name.

Three things had to be true for that line to print, and none of them is a prompt:

- The agent runs on Agent Engine as `sa-orchestrator`, so the token it minted says so.
- `mcp-schedule` is private, so an unauthenticated caller never gets that far — it gets 403
  from Cloud Run first.
- `CallerIdentityResolver` reads the principal from the credential rather than the payload,
  which is only observable once there *is* a credential (EV-26 is what made ADR-020 testable
  end to end).

---

## 11. What this file does not claim

- The **replanner** does not reach the engine over MCP. It calls `POST /api/replan` on the
  web app, because `mcp-schedule` exposes no replan-from-disruption tool. The scheduler and
  governance specialists do go over MCP; the replanner is REST.
- Agents coordinate through ADK sub-agent transfer, not the A2A wire protocol.
- **The commit rule is enforced in the application, not by IAM.** Per-agent identities are
  real and applied (§8), but Google is not the thing stopping an agent from committing —
  deliberately, because the rule is *only a human Producer*, not *only this principal*.
- Mutation testing covers the union rules, not the codebase.
- The agent metrics in §12 are exported by the **sentinel**. The orchestrator on Agent Engine
  is not wired to the same OTLP endpoint: doing so would mean passing the push credentials as
  a plain deployment variable rather than reading them from Secret Manager, and Agent Engine
  emits its own traces already.

---

## 12. The agents, observed (EV-47)

The track asks for two things, and this is the second: *observe the agent you build*. Four
instruments in `agents/common/telemetry.py`, exported over standard OTLP to the same Grafana
Cloud stack the Conflict Sentinel queries over MCP.

**The check that matters is not that panels exist — it is that they query names that do.** The
Prometheus series below were read back from the stack after a real export, using the Grafana
MCP server itself:

```python
c.call_tool("list_prometheus_metric_names",
            {"datasourceUid": "grafanacloud-prom", "regex": "agent.*"})
```

```
['agent_llm_duration_milliseconds_bucket', 'agent_llm_duration_milliseconds_count',
 'agent_llm_duration_milliseconds_sum',    'agent_llm_tokens_total',
 'agent_mcp_calls_total',                  'agent_mcp_duration_milliseconds_bucket',
 'agent_mcp_duration_milliseconds_count',  'agent_mcp_duration_milliseconds_sum']
```

Eight series, and the dashboard queries eight. The suffixes are the exporter's doing, not
ours — `_total` on counters, `_bucket`/`_sum`/`_count` on histograms — which is exactly why
they were confirmed rather than assumed: **a panel querying a name that does not exist and a
panel with no data to draw render identically.**

### The cross-check

Series names existing is the weaker half. The stronger half is that the numbers agree with a
source that never touched Grafana. One question to the **deployed** sentinel
(`stripboard-sentinel-00007-htq`), authenticated with an identity token:

```
POST /api/ask  {"question": "Which cast member am I paying for days they do not work?"}

{"rounds": 3, "total_tokens": 13596,
 "tool_calls": [{"name": "list_datasources"}, {"name": "query_prometheus"}, …]}
```

Then the same stack, asked over MCP:

```
sum by (job) (agent_llm_tokens_total)
  {job="stripboard-sentinel"} = 13596

sum by (job, status) (agent_mcp_calls_total)
  {job="stripboard-sentinel", status="ok"} = 3

topk(6, sum by (tool) (agent_mcp_calls_total))
  {tool="list_datasources"} = 1   {tool="query_prometheus"} = 1
  {tool="alerting_manage_rules"} = 1
```

**13,596 by two independent routes** — the HTTP response the agent composed for itself, and
the counter Grafana received over OTLP from a Cloud Run container. Three tool calls, named
individually. This is the partner integration counted from the inside rather than asserted.

`agent_mcp_calls_total` carries a `status` label and is incremented on the failure path too.
That was a deliberate choice in `telemetry.mcp_call`, which is a context manager rather than a
decorator for this reason: a counter that only increments on success reports a healthy
integration right up until nothing works.

The dashboard is `infra/grafana/dashboard-agent-observability.json`, uid `stripboard-agents`,
provisioned from versioned JSON by the same script that provisions Mission Control, and
published read-only:

**<https://pinkcorridor3522.grafana.net/public-dashboards/c046a2db657a4d42bf4e243afc825bc9>**

It is a **second** dashboard on purpose — see the README for why the two are not merged.

Publishing it was done from the Grafana UI, not by the sentinel's token, which is refused with
`403 Permissions needed: dashboards.public:write`. That refusal is the credential being scoped
correctly rather than an obstacle: the token in the sentinel's sidecar exists to read alerts
and write annotations at runtime, and a leaked one should not be able to expose the stack.
