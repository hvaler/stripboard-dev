# 🔀 State Diagrams — Schedule Version & Disruption Lifecycle

> **Scope**: Lifecycle of Schedule Versions and Disruption Events in Stripboard  
> **Format**: Mermaid State Diagrams  
> **Language**: English

---

## 1. Schedule Version Lifecycle State Diagram

```mermaid
stateDiagram-v2
    [*] --> Draft: Screenplay Import / Initial Solve
    
    Draft --> UncommittedProposed: CP-SAT Optimization Complete
    
    UncommittedProposed --> Rejected: Producer Discards Proposal
    UncommittedProposed --> Committed: Producer Approves (/api/schedule/commit)
    
    Committed --> Superseded: New Version Committed
    
    Rejected --> [*]
    Superseded --> [*]

    note right of Draft
        Initial breakdown parsed by Gemini.
        Stored in PostgreSQL uncommitted.
    end note

    note right of Committed
        Enforced by CallerIdentityResolver.
        Only Producer principal can transition.
        Triggers Grafana MCP create_annotation.
    end note
```

---

## 2. Disruption Event Lifecycle State Diagram

```mermaid
stateDiagram-v2
    [*] --> Detected: Grafana Alert Firing / User Logged
    
    Detected --> ReplanInProcess: Sentinel Triggers Orchestrator Agent
    
    ReplanInProcess --> AlternativesGenerated: CP-SAT Calculates Deltas
    ReplanInProcess --> Infeasible: Constraint Violations Cannot Be Solved
    
    AlternativesGenerated --> PendingProducerReview: Proposals Rendered on UI
    
    PendingProducerReview --> Resolved: Producer Commits Preferred Version
    PendingProducerReview --> Dismissed: Producer Ignores Disruption
    
    Infeasible --> ManualInterventionRequired: Requires Human Script Changes
    
    Resolved --> [*]
    Dismissed --> [*]
    ManualInterventionRequired --> [*]
```
