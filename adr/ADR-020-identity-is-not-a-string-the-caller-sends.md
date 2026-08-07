# ADR-020 — A caller's identity is not a string the caller sends

**Status:** Accepted · 2026-08-05 · Makes the human-in-the-loop rule enforceable ·
Completes [ADR-018](ADR-018-orchestration-and-delegated-authority.md)

## Context

The rule this project is built around is that only a human Producer may commit a schedule.
It was checked like this:

```csharp
public async Task<ScheduleBoard> CommitAsync(Guid versionId, string identity, …)
{
    if (!_authorization.CanExecuteCommit(identity)) throw new NotAuthorizedException(…);
```

and reached from an endpoint whose body was `{ "versionId": …, "identity": "…" }`.

The role check was never wrong. It was answering a question whose premise nobody had
verified: **the identity was whatever the caller typed.** An agent instructed not to commit
had only to send `identity: "Producer"`.

Every demonstration of the refusal was honest — `sa-stripboard-replanner` really was
refused — and every one of them relied on the agent naming itself accurately. The system was
polite, not secure.

[ADR-018](ADR-018-orchestration-and-delegated-authority.md) argued that authority must live
where a prompt cannot reach it, and put it behind an HTTP boundary. That was necessary and
not sufficient: the boundary was reading the claim out of the envelope.

## Decision

### Two kinds of identity, and only one of them can commit

`CallerIdentity` carries a name, a source, and the bit that matters:

| Factory | Source | Meaning |
|---|---|---|
| `CallerIdentity.FromToken(sa)` | `oidc` | The platform validated an identity token |
| `CallerIdentity.FromHumanSession(role)` | `human-session` | A human at an authenticated session |
| `CallerIdentity.Asserted(name)` | `asserted` | A name that arrived in a payload |

`CanExecuteCommit` now requires `IsAuthenticated` **and** the Producer role. An asserted
identity can propose all it likes and can never commit.

Solving is deliberately left open to asserted identities. A draft schedule binds nobody — it
is a proposal a human still has to accept — so demanding proof to produce one would add
friction without protecting anything. The line is drawn where a schedule starts costing
money.

### Where the identity actually comes from

`CallerIdentityResolver` reads it from the request: the IAP header when a human is in front
of one, otherwise the `email` claim of the bearer token.

It reads that token **without verifying the signature**, which is worth being explicit about.
These services run on Cloud Run with `--no-allow-unauthenticated`; Google's front end
validates signature, audience and expiry and rejects the request before our container sees
it. Re-verifying would mean fetching and caching Google's JWKS to re-derive a decision the
platform already made.

That reasoning only holds *behind Cloud Run*, so it is gated on `K_SERVICE` — set by the
runtime and by nothing else. On a laptop the header is ignored entirely and every caller is
asserted, therefore unable to commit. A misconfigured deployment fails the same way. The
safe direction to fail in is the one where nobody can commit, not the one where everybody can.

### The string overload survives, and now refuses

`CommitAsync(versionId, string)` still compiles, and builds an **asserted** identity. So the
old call shape fails, loudly, with a message that distinguishes the two problems:

- *"cannot commit a schedule. Only the Producer role may commit"* → wrong role; ask a Producer.
- *"claims the Producer role but nothing verified it"* → right role, no proof; authenticate.

Two refusals because they need two different fixes, and a caller who cannot tell them apart
will try the wrong one.

### Service accounts arrive as email addresses

Google presents the caller as `sa-replanner@stripboard-hack.iam.gserviceaccount.com`.
Comparing the whole string to `"sa-replanner"` would have refused the very identity the
platform had just proved — the agent locked out of solving, looking like a broken deployment
rather than a policy. `AgentAuthorizationService` compares the local part.

## Consequences

- **The screen has to agree with the rule.** Proving the identity was not enough while the board
  still rendered `Committed · created by sa-replanner`: one field carried both the proposer and
  the approver, so a service account appeared as the approver of a commit it was refused.
  `ScheduleVersion` now has `ApprovedBy`/`ApprovedAt`, set only by `Commit(approvedBy)` from the
  identity the platform proved, and the board prints *proposed by* and *approved by* separately.
  The bootstrap schedule a fresh instance solves at startup is committed and approved by nobody,
  so it prints **"not recorded"** rather than borrowing the proposer's name — filling that gap
  from `CreatedBy` is the exact collapse this removes.

- The governance demonstration is now a guarantee rather than a convention. An agent that
  sends `identity: "Producer"` over MCP is refused, and there is a test that does exactly that.
- The Blazor page constructs `FromHumanSession` from its role selector. That is the honest
  description of what it is: a human choosing which role they are acting as, recorded on the
  audit trail with every commit. A production deployment would put the page behind IAP and
  take the role from the login; the governance story is about agents being unable to decide,
  not about the operator's password.
- `Stripboard.Infrastructure` now takes a `FrameworkReference` on ASP.NET Core, because
  resolving a caller means reading a request.
- IAM per agent is still only a script. That is the outer ring — which principals exist and
  what Google will let them touch. This ADR is the inner one, and it is the ring that decides
  whether a schedule gets committed.
