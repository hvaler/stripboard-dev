# Shot list — what is on screen, in order

Pairs with `narration.md`. Seven shots, seven audio files. Record the screen **silently**; the
voice is added afterwards, so a page that loads slowly costs a trim rather than a retake.

---

## Before you press record

**Wake everything up.** Cold Cloud Run and a cold dashboard both look broken on camera.

Commands are **PowerShell 7**, which is the shell this was recorded from. `gcloud` is the same
executable either way; the shell only changes how variables are set and how the health check
is spelled.

Read the state first — warming what is already warm costs money and proves nothing:

```powershell
gcloud sql instances describe stripboard-db --project stripboard-hack --format="value(state,settings.activationPolicy)"
gcloud run services list --project stripboard-hack --region europe-west1 --format="table(metadata.name,spec.template.metadata.annotations['autoscaling.knative.dev/minScale']:label=MIN)"
```

Then warm whatever the two commands above did not already report as warm:

```powershell
gcloud sql instances patch stripboard-db --activation-policy=ALWAYS --project stripboard-hack
gcloud run services update stripboard-web      --min-instances=1 --project stripboard-hack --region europe-west1
gcloud run services update stripboard-sentinel --min-instances=1 --project stripboard-hack --region europe-west1

Invoke-RestMethod https://stripboard-web-wc7oib7k6q-ew.a.run.app/api/health   # must report a committed schedule
```

`curl` is not a PowerShell alias in 7.x, so `Invoke-RestMethod` is the honest spelling —
and it parses the JSON, which is what you actually want to read.

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

**Start the Grafana MCP sidecar, once.** It runs detached, so it occupies no terminal and does
not have to be kept off camera:

```powershell
$env:GRAFANA_URL = "https://pinkcorridor3522.grafana.net"
& "C:\Program Files\Git\bin\bash.exe" ./infra/grafana/run-mcp-sidecar.sh
```

It finds the token in `.secrets/grafana-token` on its own and finishes with
`Grafana MCP server ready at http://localhost:8000/mcp`.

> **Why Git Bash rather than a PowerShell rewrite of the script.** The script resolves the
> token from three places in order, warns when it is a `glc_` access policy token instead of
> a `glsa_` service account one — that was DT-009 — and waits for a real `initialize`
> handshake before it claims to be ready. A second copy of that logic in PowerShell is a
> second thing to keep correct, and the one that rots is the one nobody runs. `docker` is the
> same `docker.exe` from either shell.

**Then the terminal you record.** PowerShell, three variables, **not one of them secret**:

```powershell
$env:GOOGLE_CLOUD_PROJECT = "stripboard-hack"
$env:STRIPBOARD_URL       = "https://stripboard-web-wc7oib7k6q-ew.a.run.app"
$env:GRAFANA_MCP_ENDPOINT = "http://localhost:8000/mcp"
```

Confirm without printing anything: `if ($env:GRAFANA_MCP_ENDPOINT) { "endpoint set" }`.

> **The token never enters the terminal you record, so there is nothing there to leak.**
>
> No agent reads `GRAFANA_SERVICE_ACCOUNT_TOKEN`; searching `agents/` for it returns nothing.
> `grafana_mcp_client.py` says so in a comment — *the sidecar holds the Grafana service
> account token itself* — and the recorded shell only ever talks to `http://localhost:8000/mcp`,
> which carries no credential.
>
> This is why there is one terminal here and not two. The earlier instruction to keep a
> separate, never-recorded shell existed because this block used to export the token. It no
> longer does. Open a fresh window before a take if you want a clean prompt, but that is now
> a question of the prompt and not of a credential.

> **`STRIPBOARD_MCP_SCHEDULE_ENDPOINT` is deliberately not set, and shot 5 pays a small price
> for it.**
>
> `run_orchestrator.py` reads the board over MCP only when that variable is set, and over the
> web app's REST API otherwise. Unset, it prints
> `Engine reached over: REST — the web app's API (the MCP servers are not running)`.
>
> Setting it is worse, not better. It would need `dotnet run --project
> src/Stripboard.Mcp.Schedule` locally, and a local server without `STRIPBOARD_DB_CONNECTION`
> falls back to an **in-memory** database — `DatabaseRegistration` says so and logs it. The
> board it served would be empty, so the shot would print *the scheduling service has no
> schedule to talk about* instead. Pointing it at the real Cloud SQL means the Auth Proxy and
> a connection string, which is an hour of setup an hour before recording, and the deployed
> `mcp-schedule` is private, so reaching that one instead would put a bearer token back in the
> shell that is on camera.
>
> **The MCP evidence does not depend on this shot.** Shot 4 already shows
> `tools/call alerting_manage_rules` going out in a terminal, which is the qualifying evidence
> for the partner track. Shot 5 is about the specialists and the solver, and the REST line is
> a true statement about how it was run. Leave it, and do not crop it — a cropped terminal is
> a worse problem than an honest line.

---

## 0:00 – 0:19 · "It is a film shoot"

**Screen:** the public Grafana dashboard, full screen, already populated.
`https://pinkcorridor3522.grafana.net/public-dashboards/1e372a04e0974e1fa34afb2e143957c3`

Let it sit. Move the mouse slowly across *Cast utilisation — who is being paid to wait* when
the narration reaches "sit in a trailer". Do not click anything.

## 0:22 – 0:38 · Ask your shoot

**Screen:** `/ask` on the deployed app.

Type **"How much of the budget have we burned so far?"** and let it answer. It comes back
quickly — two Gemini rounds and a single MCP call — and the page says it is working while you
wait.

> **This used to say fifteen to twenty seconds, and that is no longer true.** EV-47 put the
> Prometheus datasource uid in the prompt instead of leaving the model to discover it, which
> removed an entire round from every answer: the four demo questions went from three rounds and
> two tool calls to two and one. The old figure described the system before that fix.
>
> **It only holds with the sentinel warm.** Cold, the first question pays for a container start
> and an MCP handshake on top, and then no take is long enough. That is the warm-up step, and
> it is why it is not optional.

The answer is **$29,600**. Then scroll to the queries printed underneath it: `query_prometheus`
against `shoot_cost_estimate_usd`. **Hold on those.** They are the whole point of the shot — the
figure was read off a metric over MCP, live, not composed by a model.

> **Ask this question and not the cast one.** "Which actor am I paying for days they do not
> work?" is the better sentence, and it is already the alert in shot 4 — asking it here spends
> the reveal twice. The budget question is the one that used to fail: the model invented a
> metric name that had never existed and reported the budget as unavailable. It is fixed, and
> it is the one to rehearse once before recording.

## 0:42 – 1:01 · Screenplay to stripboard

**Screen:** two halves **of time — not a split screen.** The terminal fills the frame, then the
board fills the frame, and the cut between them happens in the editor.

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
   **Do not cut this.** It is the qualifying evidence for the partner track: the MCP call, on
   screen, in a terminal. What prints, and what to let finish:

   ```
   Grafana MCP: mcp-grafana (devel)
   [1/3] Conflict Sentinel asking Grafana which of the shoot's rules are firing...
      -> [high] Unit hopping between locations in a day
   [2/3] Handing 'Unit hopping…' to the agents  (action: consolidate)...
     tool:  line_producer -> transfer_to_agent({'agent_name': 'replanner'})
     tool:  replanner -> consolidate_schedule({'max_locations_per_day': 2})
   [3/3] The agent tries to commit its own recommendation...
      -> committed=False  'sa-stripboard-replanner' cannot commit a schedule.
   ```

   > **An earlier version of this list said to wait for a literal `tools/call
   > alerting_manage_rules` line. It never prints.** `tools/call` is the JSON-RPC method the
   > client sends over the wire (`agents/common/mcp_client.py:187`) and `run_alert_loop.py`
   > configures no logging, so nothing at that level reaches stdout. What does reach it is the
   > list above, which names the Grafana MCP server and the tools the agents call — and it says
   > it in a sentence a judge can read, which the wire-level line does not.
   >
   > `[3/3]` is a bonus this shot was not planned around: the refusal shows up here too, before
   > shot 6 stages it on screen. Let it print.

   An ADK `UserWarning` about `SCHEMA_FOR_FUNC_DECL` lands in the middle of `[2/3]`. It is
   library noise, not a failure, and it is not worth a retake.
3. Back to the **public Mission Control dashboard** if there is room — the same page as shot 1,
   not the Stripboard board. The narration closes this shot on *Grafana does not receive the
   result. Grafana starts the work*, and returning to Grafana is what closes that loop on screen.

## 1:37 – 2:03 · Agents and the solver

**Screen:** the terminal, `python demo/run_orchestrator.py`, then `/proposals`.

The terminal prints `handled by: replanner` and the tool calls. Then switch to the browser and
show the two option cards side by side, with the deltas. Pause on the grey note that says
*Same outcome as Option A on every figure below — this is not a second choice.*

**Near the top it will print** `Engine reached over: REST — the web app's API (the MCP servers
are not running)`, and that is the accepted trade rather than a mistake — the setup block above
says why, and why the alternatives are worse. Do not crop it. What must be right in this shot is
`handled by: replanner` and the tool calls under it.

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

**And put the minimum instances back.** You set them to 1 to record; at 1 they are billed around
the clock, and there are weeks left until 7 September.

```powershell
gcloud run services update stripboard-web      --min-instances=0 --project stripboard-hack --region europe-west1
gcloud run services update stripboard-sentinel --min-instances=0 --project stripboard-hack --region europe-west1
```

**And stop the sidecar**, which is a container that outlives the session otherwise:

```powershell
docker rm -f stripboard-mcp-grafana
```

The trade is that a judge's first visit starts cold and takes a few seconds. That is the right
price: the video is recorded once, and the URL has to live for a month.

---

## Recording as seven separate clips

This is the intended way. The narration is already one MP3 per shot, so a clip that goes wrong
costs one retake instead of a re-cut.

**Each screen clip must outlast its voice.** The voice files total 2:19; the finished video runs
2:43 because there are four seconds of air between shots, and the screen keeps moving through
that air rather than cutting to black. So a clip has to cover its own narration *plus* the pause
after it, and a few seconds more at each end that you will trim away.

| Clip | Voice | Screen must cover | Record at least |
|---|---:|---:|---:|
| 1 · Mission Control | 18.9s | 23s | 30s |
| 2 · Ask your shoot | 15.5s | 20s | 30s — see the note in that shot |
| 3 · Screenplay to stripboard | 18.5s | 23s | 30s |
| 4 · Grafana starts the loop | 28.6s | 33s | 45s |
| 5 · Agents and the solver | 25.3s | 29s | 40s |
| 6 · The refusal | 20.2s | 24s | 30s |
| 7 · The human | 12.2s | 12s | 20s |

**What matters is the middle column, not the last one.** The right-hand figures are insurance
against a slow answer or a fumbled start, not a length the edit needs. A clip that covers its
voice plus the four seconds of air after it is usable, however short the take was: 28 seconds is
a good clip 2, because clip 2 needs 20.

Clip 2 is still the one where the take can come out short of its own accord, because it is the
only one waiting on a model. It is no longer the one to over-record by a wide margin — that
advice belonged to the fifteen-to-twenty-second era described above.

**What makes seven clips cut together like one recording:**

- **One application per take, maximised with `Win+↑`. Never tile two side by side.** The shots
  that name a terminal *and* a browser mean one after the other, cut in the editor — not a split
  screen. Half of a 1440-logical-pixel desktop is about 720, and `/proposals` puts two option
  cards side by side: they do not fit, and the second one is clipped by the browser's own
  viewport before OBS sees anything.
- **Maximise; do not drag windows into place.** A window nudged by hand can sit a few pixels off
  the left edge of the desktop, and Windows only draws what is on screen — so the recording is
  faithfully capturing a window that is genuinely cut. This cost a full set of takes once: the
  frame read `oot Mission Control` instead of *Shoot Mission Control*, and the fault was the
  window position, not the capture.
- **Never resize the window between takes.** Set the browser once, at one zoom level, and leave
  it. A frame that jumps two pixels between clips is the most visible amateur tell there is.
- **Record every clip to a 1920×1080 output, and get there through OBS rather than through the
  window size.** This was recorded on a 2880×1800 panel at 200% scaling, so the *logical*
  desktop is 1440×900: no window can be 1920 wide, and the panel is 16:10 rather than 16:9.
  Set OBS **base canvas 2880×1620** — the 16:9 crop of the panel — and **output 1920×1080**,
  which is an exact two-thirds downscale with no dirty interpolation. Place the Display Capture
  source at 0,0 and the bottom 180 physical pixels, taskbar included, fall outside the canvas
  on their own. **Check the taskbar is really gone in the OBS preview**: leaving the canvas at
  the full 2880×1800 outputs 1920×1200, which is 16:10 and ships your clock, your tray icons and
  whatever notification badge is showing to YouTube.
- **Pick one browser zoom and keep it across all six browser clips.** At 1440×900 logical the
  viewport is small and the Grafana dashboard comes out cramped. Whatever zoom makes shot 1 fit
  is the zoom shots 2, 6 and 7 use too. Choosing it per shot is the same jump-cut tell as
  resizing the window. Decide it against shot 1, which is the one starved for room.
- **Same browser chrome throughout**: same tabs, same bookmarks bar, and no notification
  arriving during take four. Close everything else first.
- **Start each clip on a still frame and end on one.** Trimming into motion is what produces a
  jump cut; trimming into stillness produces a cut nobody notices.
- Screen audio is discarded, so the room need not be quiet. The takes do.

**Assembly order:** lay the seven audio files end to end with four seconds between them, then
fit each screen clip to its own voice. Never the other way round — stretching a clip to reach a
line is how a demo starts looking slowed down. Import `subtitles.srt` last; it is timed against
exactly this layout.

## Tools

| For | Use |
|---|---|
| Recording | **OBS Studio** — free, no watermark, scenes prepared in advance and a clean switch between them. Settings above |
| Fallback | **ShareX**, pinning one capture region and never moving it. No scenes, so the app-switching shots are harder |
| Editing | **Clipchamp**, ships with Windows 11 and imports `.srt` directly |
| Voice | Already generated: **seven MP3s** in `demo/video/audio/`. To rebuild, `python demo/video/make_voiceover.py` |
| **Not this** | **Xbox Game Bar** (`Win+Alt+R`). It captures **no mouse pointer**, records only the app that had focus when recording started, and sizes the file to the window. Shot 1 is a mouse moving across a panel, and shots 3, 4 and 5 switch between terminal and browser |

DaVinci Resolve is better and will cost you a Sunday to learn. For seven clips and seven voice
tracks, Clipchamp is more than enough.

## The order that saves retakes

1. Wake everything up and **arm the alert**. Wait the five minutes.
2. Record the seven screens **silently**, without hurrying.
3. Generate the voice — or reuse what is already in `demo/video/audio/`.
4. Assemble: the screen fits the audio, never the other way round.
5. Import the subtitles.
6. **Settle**, and put the minimum instances back.
