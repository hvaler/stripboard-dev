# Shot list — what is on screen, in order

Pairs with `narration.md`. Seven shots, seven audio files. Record the screen **silently**; the
voice is added afterwards, so a page that loads slowly costs a trim rather than a retake.

---

## Before you press record

**Wake everything up.** Cold Cloud Run and a cold dashboard both look broken on camera.

```bash
gcloud sql instances patch stripboard-db --activation-policy=ALWAYS --project stripboard-hack
gcloud run services update stripboard-web --min-instances=1 --project stripboard-hack --region europe-west1
gcloud run services update stripboard-sentinel --min-instances=1 --project stripboard-hack --region europe-west1
curl https://stripboard-web-wc7oib7k6q-ew.a.run.app/api/health     # must report a committed schedule
```

> **The sentinel matters as much as the web app here.** It scales to zero too, and **the amber
> alert strip on the front page is served by it** — read back from Grafana over MCP on every
> page load. A cold sentinel has to start a container, a sidecar and an MCP handshake; the
> client waits thirty seconds and then gives up **silently**, so the page renders with no strip
> and reads as *no alerts are firing*. That is shot 4.

Open the public dashboard and **leave it open for a minute** before recording it. Its panels
draw their frames before their data; a screenshot taken at eight seconds shows an empty board.

**Arm the alert, then wait.** This is the step that decides whether shot 4 works.

1. Go to `/inject-disruption`, section **Rehearsal**, press **Arm**.
2. Wait **five full minutes**. Grafana evaluates every minute and the rule needs five
   consecutive ones before it fires. Make coffee.
3. Confirm on the home page: the amber strip should name *Unit hopping between locations in a day*.

> **Why Arm rather than the alert already firing.** *Cast paid to wait* fires today, and it is
> a good story, but its `stripboardAction` is `replan` — and there is nothing to replan around,
> because no scene is blocked. The replanner would correctly answer "there is nothing I can do",
> which is honest and makes a confusing thirty seconds of video. *Unit hopping* carries
> `consolidate`, which is the action the loop actually completes. Arm gives you that one.

**Two terminals ready**, both with the environment set, because shot 4 shows one of them:

```bash
export GOOGLE_CLOUD_PROJECT=stripboard-hack
export STRIPBOARD_URL=https://stripboard-web-wc7oib7k6q-ew.a.run.app
export GRAFANA_URL=https://pinkcorridor3522.grafana.net
export GRAFANA_SERVICE_ACCOUNT_TOKEN=<from .secrets/grafana-token>
./infra/grafana/run-mcp-sidecar.sh
export GRAFANA_MCP_ENDPOINT=http://localhost:8000/mcp
```

> **Two terminals, and only one of them is ever on camera.**
>
> The block above exports `GRAFANA_SERVICE_ACCOUNT_TOKEN`. Set it up in a terminal you will
> **never record**, and use a second, clean one for the three commands that do appear —
> `agents.breakdown` at 0:42, `run_alert_loop.py` at 1:04, `run_orchestrator.py` at 1:37.
>
> A shared terminal keeps the token in its scrollback. One stray scroll, or a tall prompt,
> and a live credential is on YouTube. `clear` before each take, and never `echo` the token
> to check it — `[ -n "$GRAFANA_SERVICE_ACCOUNT_TOKEN" ] && echo set` tells you what you need
> to know without printing it.

---

## 0:00 – 0:19 · "It is a film shoot"

**Screen:** the public Grafana dashboard, full screen, already populated.
`https://pinkcorridor3522.grafana.net/public-dashboards/1e372a04e0974e1fa34afb2e143957c3`

Let it sit. Move the mouse slowly across *Cast utilisation — who is being paid to wait* when
the narration reaches "sit in a trailer". Do not click anything.

## 0:22 – 0:38 · Ask your shoot

**Screen:** `/ask` on the deployed app.

Type **"How much of the budget have we burned so far?"** and let it answer. Fifteen to twenty
seconds — that is several Gemini rounds with MCP calls between them, and the page says so while
you wait.

The answer is **$29,600**. Then scroll to the queries printed underneath it: `query_prometheus`
against `shoot_cost_estimate_usd`. **Hold on those.** They are the whole point of the shot — the
figure was read off a metric over MCP, live, not composed by a model.

> **Ask this question and not the cast one.** "Which actor am I paying for days they do not
> work?" is the better sentence, and it is already the alert in shot 4 — asking it here spends
> the reveal twice. The budget question is the one that used to fail: the model invented a
> metric name that had never existed and reported the budget as unavailable. It is fixed, and
> it is the one to rehearse once before recording.

## 0:42 – 1:01 · Screenplay to stripboard

**Screen:** two halves.

- First ten seconds: the terminal, running
  `python -m agents.breakdown --file demo/screenplay-nightfall.fountain`
  Let the typed scene table print. That is Gemini's output on screen.
- Then: the board at `https://stripboard-web-wc7oib7k6q-ew.a.run.app/`
  Scroll slowly through the strips so the colour code and the turnaround figures are visible.

## 1:04 – 1:33 · Grafana starts the loop  ← **the shot that wins the track**

**Screen:** three beats, in this order.

1. The home page, on the **amber alert strip**. This is a judge-visible page saying a rule is
   firing. Two seconds.
2. The terminal: `python demo/run_alert_loop.py`
   Let the `tools/call alerting_manage_rules` line print. **Do not cut this.** It is the
   qualifying evidence for the partner track: the MCP call, on screen, in a terminal.
3. Back to the dashboard if there is room.

## 1:37 – 2:03 · Agents and the solver

**Screen:** the terminal, `python demo/run_orchestrator.py`, then `/proposals`.

The terminal prints `handled by: replanner` and the tool calls. Then switch to the browser and
show the two option cards side by side, with the deltas. Pause on the grey note that says
*Same outcome as Option A on every figure below — this is not a second choice.*

## 2:07 – 2:28 · The refusal

**Screen:** `/proposals`, with **Acting as** set to `sa-replanner (agent)`.

Press **Approve** on an option. The red panel appears with the service's own words. Hold on it
— this is the shot that carries the whole governance argument, and it needs a beat to be read.

## 2:31 – 2:44 · The human, and back to Grafana

**Screen:** three quick cuts.

1. Change **Acting as** to `Producer (human)`, press **Approve**. Green confirmation.
2. `/` — the board header now reads *proposed by sa-replanner · approved by Producer*.
3. `/audittrail` — the green `ScheduleCommitted · Producer` row at the top.

If there is a second left, the Grafana annotation on the dashboard timeline.

---

## Afterwards

**Settle the schedule again** so the demo is left in its good state for judges:
`/inject-disruption` → **Settle**. The board goes back to four days at two locations a day.
