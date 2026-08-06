# 🗄️ Entity-Relationship Diagram — Data Domain Model

> **Scope**: PostgreSQL 16 Entity Data Model (`Stripboard.Domain` & `Stripboard.Infrastructure`)  
> **Format**: Mermaid Entity-Relationship Diagram  
> **Language**: English

---

## 1. Domain Entity Relationship Diagram

```mermaid
erDiagram
    SCHEDULE_VERSION ||--o{ SHOOT_DAY : contains
    SHOOT_DAY ||--o{ SCENE : includes
    SCENE ||--o{ SCENE_CAST : features
    PERSON ||--o{ SCENE_CAST : plays
    LOCATION ||--o{ SET : has
    SET ||--o{ SCENE : hosts
    DISRUPTION_EVENT ||--o{ SCHEDULE_VERSION : triggers

    SCHEDULE_VERSION {
        uuid Id PK
        int VersionNumber
        boolean IsCommitted
        string CreatedBy
        decimal EstimatedCostUsd
        int TotalDays
        int CompanyMoves
        int UnionViolations
        datetime CreatedAt
    }

    SHOOT_DAY {
        uuid Id PK
        uuid ScheduleVersionId FK
        int DayNumber
        date Date
        time CallTime
        time WrapTime
        string LocationName
    }

    SCENE {
        uuid Id PK
        uuid ShootDayId FK
        uuid SetId FK
        string SceneNumber
        string Header
        int Eighths
        string DayNight
        string IntExt
    }

    PERSON {
        uuid Id PK
        string Name
        string CharacterName
        string RoleType
        decimal DailyRateUsd
    }

    SCENE_CAST {
        uuid SceneId PK,FK
        uuid PersonId PK,FK
    }

    LOCATION {
        uuid Id PK
        string Name
        string Address
        decimal MaxDailyBudgetUsd
    }

    SET {
        uuid Id PK
        uuid LocationId FK
        string Name
    }

    DISRUPTION_EVENT {
        uuid Id PK
        string TriggerType
        string Description
        date StartDate
        int DurationDays
        datetime TriggeredAt
    }
```
