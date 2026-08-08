# Shot list — what is on screen, in order

Pairs with `narration.md`. Six shots, six audio files. Record the screen **silently**; the
voice is added afterwards, so a page that loads slowly costs a trim rather than a retake.

---

## Before you press record

**Wake everything up.** Cold Cloud Run and a cold dashboard both look broken on camera.

```bash
gcloud sql instances patch stripboard-db --activation-policy=ALWAYS --project stripboard-hack
gcloud run services update stripboard-web --min-instances=1 --project stripboard-hack --region europe-west1
curl https://stripboard-web-wc7oib7k6q-ew.a.run.app/api/health     # must report a committed schedule
```

Open the public dashboard and **leave it open for a minute** before recording it. Its panels
draw their frames before their data; a screenshot taken at eight seconds shows an empty board.

**Arm the alert, then wait.** This is the step that decides whether shot 3 works.

1. Go to `/inject-disruption`, section **Rehearsal**, press **Arm**.
2. Wait **five full minutes**. Grafana evaluates every minute and the rule needs five
   consecutive ones before it fires. Make coffee.
3. Confirm on the home page: the amber strip should name *Unit hopping between locations in a day*.

> **Why Arm rather than the alert already firing.** *Cast paid to wait* fires today, and it is
> a good story, but its `stripboardAction` is `replan` — and there is nothing to replan around,
> because no scene is blocked. The replanner would correctly answer "there is nothing I can do",
> which is honest and makes a confusing thirty seconds of video. *Unit hopping* carries
> `consolidate`, which is the action the loop actually completes. Arm gives you that one.

**Two terminals ready**, both with the environment set, because shot 3 shows one of them:

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
> `agents.breakdown` at 0:25, `run_alert_loop.py` at 0:50, `run_orchestrator.py` at 1:30.
>
> A shared terminal keeps the token in its scrollback. One stray scroll, or a tall prompt,
> and a live credential is on YouTube. `clear` before each take, and never `echo` the token
> to check it — `[ -n "$GRAFANA_SERVICE_ACCOUNT_TOKEN" ] && echo set` tells you what you need
> to know without printing it.

---

## 0:00 – 0:25 · "It is a film shoot"

**Screen:** the public Grafana dashboard, full screen, already populated.
`https://pinkcorridor3522.grafana.net/public-dashboards/1e372a04e0974e1fa34afb2e143957c3`

Let it sit. Move the mouse slowly across *Cast utilisation — who is being paid to wait* when
the narration reaches "sit in a trailer". Do not click anything.

## 0:25 – 0:50 · Screenplay to stripboard

**Screen:** two halves.

- First ten seconds: the terminal, running
  `python -m agents.breakdown --file demo/screenplay-nightfall.fountain`
  Let the typed scene table print. That is Gemini's output on screen.
- Then: the board at `https://stripboard-web-wc7oib7k6q-ew.a.run.app/`
  Scroll slowly through the strips so the colour code and the turnaround figures are visible.

## 0:50 – 1:30 · Grafana starts the loop  ← **the shot that wins the track**

**Screen:** three beats, in this order.

1. The home page, on the **amber alert strip**. This is a judge-visible page saying a rule is
   firing. Two seconds.
2. The terminal: `python demo/run_alert_loop.py`
   Let the `tools/call alerting_manage_rules` line print. **Do not cut this.** It is the
   qualifying evidence for the partner track: the MCP call, on screen, in a terminal.
3. Back to the dashboard if there is room.

## 1:30 – 2:15 · Agents and the solver

**Screen:** the terminal, `python demo/run_orchestrator.py`, then `/proposals`.

The terminal prints `handled by: replanner` and the tool calls. Then switch to the browser and
show the two option cards side by side, with the deltas. Pause on the grey note that says
*Same outcome as Option A on every figure below — this is not a second choice.*

## 2:15 – 2:45 · The refusal

**Screen:** `/proposals`, with **Acting as** set to `sa-replanner (agent)`.

Press **Approve** on an option. The red panel appears with the service's own words. Hold on it
— this is the shot that carries the whole governance argument, and it needs a beat to be read.

## 2:45 – 3:00 · The human, and back to Grafana

**Screen:** three quick cuts.

1. Change **Acting as** to `Producer (human)`, press **Approve**. Green confirmation.
2. `/` — the board header now reads *proposed by sa-replanner · approved by Producer*.
3. `/audittrail` — the green `ScheduleCommitted · Producer` row at the top.

If there is a second left, the Grafana annotation on the dashboard timeline.

---

## Afterwards

**Settle the schedule again** so the demo is left in its good state for judges:
`/inject-disruption` → **Settle**. The board goes back to four days at two locations a day.
