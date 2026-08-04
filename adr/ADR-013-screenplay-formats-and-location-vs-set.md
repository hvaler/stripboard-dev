# ADR-013 — Screenplay formats, and telling a location from a set

**Status:** Accepted · 2026-08-04 · Implements EV-28 · Closes the limitation named in [ADR-012](ADR-012-scheduling-model.md)

## Context

Two problems, one root.

Productions do not send Fountain files. They send Final Draft documents and PDFs, often
scanned. The agent read one format, which meant the demo only worked on a script written
for the demo.

And ADR-012 recorded a limitation it could not fix: the schedule counted a company move
every time the *set description* changed, so moving from a hotel lobby to room 402 in the
same building was billed as a company move. The over-count was honest but wrong, and it
inflated both the cost figure and the shooting day.

The two connect, because the second problem can only be solved where the first one is
handled: at breakdown, by something that understands what the words mean.

## Decision

### Every format converges on screenplay text

`.fountain` is read, `.fdx` is converted from Final Draft XML, and `.pdf` is transcribed by
Gemini. All three then go through the same `FountainParser`, so scene segmentation,
time-of-day handling and page measurement have exactly one implementation.

PDF is the interesting case. A scanned page has no text layer, so a model is the only way
in — but it is asked to **transcribe**, not to interpret. Once the pages are text, the
ordinary pipeline takes over and `eighths` is still measured rather than guessed. The model
looks; deterministic code counts.

`.fdx` is parsed with **defusedxml**, not the standard library. A screenplay arrives from
outside the system, and `xml.etree` expands entities: a billion-laughs bomb would turn "a
producer sent us a script" into a denial of service. There is a test that fires one.

### Location and set are different fields, and Gemini decides which is which

`Scene` now carries `Location` — the place the unit physically travels to — alongside the
full `SetLocation` description. The solver counts company moves against `Location`.

No string rule can make this split. Consider two headings with identical punctuation:

```
221B BAKER STREET - SITTING ROOM   221B BAKER STREET - LABORATORY
LONDON STREETS - COVENT GARDEN     LONDON STREETS - PICCADILLY
```

The first pair is two sets at one location: the unit parks once. The second is two
locations that share a prefix: the unit crosses London. Splitting on the hyphen gets one
pair right and the other wrong, and there is no punctuation that distinguishes them —
only meaning does. So the breakdown agent is asked, and the prompt explains the
consequence: a wrong answer makes the schedule lie about its cost.

When there is no model, the fallback sets `Location` to the whole set description. That
over-counts moves, which is the safe direction: the schedule looks more expensive than it
is, never cheaper.

## The heading bug this uncovered

The old parser took the last hyphen-separated segment as the time of day, unconditionally.
`INT. BAKER STREET - SITTING ROOM` therefore produced `day_night = "SITTING ROOM"`. It
survived because every heading in the demo script happened to end in DAY or NIGHT.

A segment is now only read as a time of day if it *is* one. The parser also understands the
vocabulary screenwriters actually use — EVENING, MAGIC HOUR, DAWN — and Fountain's
`CONTINUOUS` / `LATER`, which inherit the previous scene's time.

## Verification

The same five-scene script, delivered three ways, produces the same film:

| Route | Scenes | Locations | Tokens |
|---|---|---|---|
| `.fdx` | 5 | 3 | 3,396 |
| `.pdf` via Gemini multimodal | 5 | 3 | 3,568 + 1,386 transcription |

Both correctly place the hotel lobby, room 402 and the return to the lobby at **one**
location, and keep the riverside walk and the market square **apart**. A test asserts the
two routes agree on scene count and INT/EXT, so a transcription that drops or invents a
scene fails the build.

On the demo screenplay the effect is direct:

| | ADR-012 | Now |
|---|---|---|
| Distinct locations | 10 | 8 |
| Company moves | 10 | **8** |
| Estimated cost | $45,200 | **$41,600** |
| Locations on the night unit | 4 | 2 |

Eight remains the optimum — the script genuinely visits eight places — but it is now the
true count rather than an upper bound.

## Consequences

- `agents/breakdown` gained one dependency, `defusedxml`, for untrusted XML.
- Breaking a PDF down costs two model calls: a transcription and an extraction.
- `BreakdownAgent.process_fountain_file` is now `process_screenplay`; the old name is kept
  as an alias so the demo harness and existing callers keep working.
- The demo cache is keyed by parsed scenes rather than file bytes, so the same script
  delivered as `.fountain` and as `.fdx` resolves to one entry.
