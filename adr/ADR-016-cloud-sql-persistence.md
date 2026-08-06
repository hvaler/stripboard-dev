# ADR-016 — Persistence on Cloud SQL

**Status:** Accepted · 2026-08-05 · Implements EV-22 · Supersedes the in-memory note in the never-written ADR-006

## Context

Five services each called `UseInMemoryDatabase`, and the README claimed Cloud SQL. The gap
was not only documentation: every deployment wiped the schedule, the disruptions and the
audit trail. A governance feature whose audit trail disappears on redeploy is not a
governance feature, and it forced `--max-instances 1` (ADR-011), because a second instance
would have served a different film.

## Decision

### One place decides how the database is reached

`DatabaseRegistration.AddStripboardDatabase` replaces five copies of the same choice. The
connection string decides: present means PostgreSQL with snake_case naming and retry on
failure, absent means an in-memory database **that logs a warning saying so**. An in-memory
database looks identical to a working one right up until the restart that empties it, so it
now announces itself.

### The connection never carries a password over the network

Cloud Run mounts the instance through the Cloud SQL connector on a Unix socket
(`Host=/cloudsql/<instance>`), and the connection string itself lives in Secret Manager,
injected as `ConnectionStrings__Stripboard`. No code reads a secret and no password appears
in the repository, in an image, or in a deploy script's arguments.

`sa-stripboard-web` gained exactly `roles/cloudsql.client` and read access to the one
secret.

### Migrations are applied at startup

`MigrateAsync` runs before anything is served and is a no-op against the in-memory
provider. For a single small service this beats a separate migration step: there is no
window where a new revision runs against an old schema.

### `--max-instances` raised from 1 to 3

That cap existed only because state was per-instance. With the schedule in Postgres and
session affinity keeping a Blazor circuit on its own instance, a second instance is no
longer a second reality.

## Model mapping

Five properties are primitive collections — `List<Guid>` for cast, elements and strips,
`List<DateOnly>` for cast availability. They were configured as `jsonb`, which needs a value
converter that was never written; the mapping would have failed the first time a real
provider touched it. Npgsql maps them natively instead: `uuid[]` and `date[]`. The generated
migration confirms it, along with `numeric(18,2)` for day rates and snake_case throughout.

## Verification

**Locally, against Postgres 16 in Docker:** `dotnet ef database update` applies
`InitialCreate` and creates ten tables including `__EFMigrationsHistory`. The app then
starts against it and writes real rows — 12 scenes, 6 people, 6 shoot days, 2 audit events.

**In production, the test that matters:** a screenplay was imported through the deployed
app, the service was then restarted onto a new revision, and afterwards the stripboard still
showed the imported film (2 days, 2 company moves, $12,800) with `BreakdownImported` still
on the audit trail. Before this change a restart returned the app to its seed data.

## A vulnerability found on the way

Generating the migration surfaced two advisories against `OpenTelemetry.Api` 1.10.0.
Upgrading the family to 1.12.0 cleared one; the other, **CVE-2026-40894**, is a
remotely-triggerable over-allocation when parsing baggage and B3 propagation headers, fixed
in 1.15.3. The whole OpenTelemetry family now sits at 1.15.3, and `OpenTelemetry.Api` is
pinned explicitly in central package management because the instrumentation packages
release on their own cadence and would otherwise drag the vulnerable version back in
transitively. `dotnet restore` reports zero vulnerability warnings.

## Consequences

- **This costs money continuously.** `db-f1-micro`, 10 GB HDD, zonal, no backups — the
  cheapest configuration that runs, roughly $9–10 a month. It should be deleted once the
  submission is judged: `gcloud sql instances delete stripboard-db`.
- Backups are off. That is right for a hackathon fixture and wrong for anything real; the
  data here is reproducible from the seeder and a screenplay.
- A developer with no connection string still gets a working app on the in-memory provider,
  now with a warning rather than a silent surprise.
- The demo can be prepared before recording and will survive until the video is made, which
  removes the "record immediately after deploying" constraint noted in ADR-011.
