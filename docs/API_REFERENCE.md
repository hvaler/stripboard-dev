# 🔌 Stripboard API & MCP Services Technical Reference

> **Platform**: Stripboard ASP.NET Core Minimal APIs & Native MCP Servers  
> **Repository**: [stripboard-dev](https://github.com/hvaler/stripboard-dev)  
> **Authentication**: Platform Claims (`CallerIdentityResolver` / IAM Tokens) & Native JSON-RPC 2.0  
> **Version**: 1.0 (Updated for ADR-020 IAM Governance & ADR-023 Native Agent MCP Consumption)

---

## Table of Contents

- [1. Authentication & IAM Governance](#1-authentication--iam-governance)
- [2. REST API Endpoints Reference](#2-rest-api-endpoints-reference)
  - [2.1 GET /api/health](#21-get-apihealth)
  - [2.2 POST /api/breakdown/import](#22-post-apibreakdownimport)
  - [2.3 GET /api/schedule](#23-get-apischedule)
  - [2.4 POST /api/schedule/commit](#24-post-apischedulecommit)
  - [2.5 POST /api/replan](#25-post-apireplan)
  - [2.6 POST /api/schedule/consolidate](#26-post-apischeduleconsolidate)
- [3. Native MCP Servers Protocol Reference (.NET 10)](#3-native-mcp-servers-protocol-reference-net-10)
  - [3.1 Protocol Architecture](#31-protocol-architecture)
  - [3.2 MCP Server Capabilities & Tools](#32-mcp-server-capabilities--tools)
- [4. Error Codes & HTTP Status Matrix](#4-error-codes--http-status-matrix)

---

## 1. Authentication & IAM Governance

Stripboard distinguishes **identity claims** from **verified platform credentials** (ADR-020).

- **Caller Identity Resolution**:
  - `CallerIdentityResolver` extracts the verified identity from platform HTTP headers (e.g. Cloud Run JWT tokens).
  - Unauthenticated or agent claims attempting to commit a schedule version receive an immutable **HTTP 403 Forbidden**.
- **Agent Self-Commit Rejection**:
  - `POST /api/schedule/commit` enforces that only the `Producer` principal can commit a schedule to production status.

---

## 2. REST API Endpoints Reference

### 2.1 GET `/api/health`

Readiness and health probe for Cloud Run instances and load balancers.

- **Method**: `GET`
- **Authentication**: None (Public Probe)
- **Response `200 OK` (Healthy Board Active)**:
  ```json
  {
    "status": "ok",
    "versionNumber": 1,
    "isCommitted": true,
    "days": 12,
    "scenes": 45
  }
  ```
- **Response `503 Service Unavailable` (Database Unreachable or No Schedule)**:
  ```json
  {
    "status": "degraded",
    "reason": "database unreachable",
    "detail": "Npgsql.NpgsqlException: Connection refused"
  }
  ```

---

### 2.2 POST `/api/breakdown/import`

Imports a screenplay breakdown produced by Gemini 2.5 Flash (`Breakdown Agent`) and triggers immediate CP-SAT schedule solving.

- **Method**: `POST`
- **Content-Type**: `application/json`
- **Request Body**: Raw JSON output from Gemini Pydantic breakdown parser containing scenes, cast, and elements.
- **Response `200 OK`**:
  ```json
  {
    "scenes": 45,
    "castCreated": 8,
    "source": "screenplay-harbour.fountain",
    "versionNumber": 1,
    "totalDays": 12,
    "companyMoves": 3,
    "estimatedCostUsd": 145000.00
  }
  ```
- **Response `400 Bad Request`**:
  ```json
  {
    "error": "The JSON payload is missing mandatory field 'scenes'."
  }
  ```

---

### 2.3 GET `/api/schedule`

Retrieves the currently committed active schedule board and metrics.

- **Method**: `GET`
- **Response `200 OK`**:
  ```json
  {
    "versionId": "e3b0c442-98fc-11ee-b9d1-0242ac120002",
    "versionNumber": 1,
    "isCommitted": true,
    "createdBy": "Producer",
    "days": 12,
    "companyMoves": 3,
    "unionViolations": 0,
    "costUsd": 145000.00,
    "scenes": 45,
    "locations": 6,
    "schedule": [
      {
        "dayNumber": 1,
        "date": "2026-08-10",
        "unit": "day",
        "call": "07:00",
        "wrap": "19:00",
        "locations": ["Harbour Dock 4"],
        "scenes": ["SCENE 1", "SCENE 2"]
      }
    ]
  }
  ```
- **Response `404 Not Found`**:
  ```json
  {
    "error": "No schedule exists yet. Import a screenplay breakdown first."
  }
  ```

---

### 2.4 POST `/api/schedule/commit`

Commits a proposed schedule version to official production status. **Human Producer Authorization Mandatory**.

- **Method**: `POST`
- **Content-Type**: `application/json`
- **Request Body**:
  ```json
  {
    "versionId": "e3b0c442-98fc-11ee-b9d1-0242ac120002",
    "identity": "Producer"
  }
  ```
- **Response `200 OK` (Committed)**:
  ```json
  {
    "committed": true,
    "versionNumber": 2,
    "days": 12,
    "costUsd": 145000.00
  }
  ```
- **Response `403 Forbidden` (Agent or Unauthorized Identity)**:
  ```json
  {
    "committed": false,
    "error": "Only the Producer principal may commit a schedule version."
  }
  ```

---

### 2.5 POST `/api/replan`

Triggers CP-SAT re-solving to produce alternative schedules facing a disruption event (e.g. Actor Illness, Weather Event, Location Loss).

- **Method**: `POST`
- **Content-Type**: `application/json`
- **Request Body**:
  ```json
  {
    "triggerType": "CastIllness",
    "startDate": "2026-08-12",
    "durationDays": 3,
    "personName": "Cap'n Jack",
    "description": "Lead actor ill with flu"
  }
  ```
- **Response `200 OK`**:
  ```json
  {
    "disruption": {
      "id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
      "trigger": "CastIllness",
      "description": "Lead actor ill with flu"
    },
    "options": [
      {
        "versionId": "a1b2c3d4-e5f6-7890-1234-56789abcdef0",
        "title": "Option A: Shift Scenes",
        "strategy": "Push Affected Scenes",
        "justification": "Pushes Cap'n Jack scenes past August 15.",
        "isFeasible": true,
        "days": 13,
        "costUsd": 152000.00,
        "delta": {
          "extraShootDays": 1,
          "extraCompanyMoves": 0,
          "extraUnionViolations": 0,
          "costDeltaUsd": 7000.00
        }
      }
    ]
  }
  ```

---

### 2.6 POST `/api/schedule/consolidate`

Triggers CP-SAT re-solving with a hard constraint cap on maximum locations per shooting day (e.g., in response to Grafana company move alerts).

- **Method**: `POST`
- **Content-Type**: `application/json`
- **Request Body**:
  ```json
  {
    "maxLocationsPerDay": 2
  }
  ```
- **Response `200 OK`**:
  ```json
  {
    "options": [
      {
        "title": "Current Schedule",
        "isFeasible": true,
        "days": 12,
        "companyMoves": 5,
        "costUsd": 145000.00
      },
      {
        "title": "Consolidated Schedule (Max 2 Locations/Day)",
        "isFeasible": true,
        "days": 13,
        "companyMoves": 2,
        "costUsd": 149500.00,
        "delta": {
          "extraShootDays": 1,
          "extraCompanyMoves": -3,
          "extraUnionViolations": 0,
          "costDeltaUsd": 4500.00
        }
      }
    ]
  }
  ```

---

## 3. Native MCP Servers Protocol Reference (.NET 10)

### 3.1 Protocol Architecture

Stripboard hosts 4 native microservice MCP servers built with `ModelContextProtocol.AspNetCore v2.1.0` (ADR-021 / ADR-023). Python agents connect directly over **HTTP Streamable JSON-RPC 2.0** via `agents/common/mcp_client.py`.

```
[Python Agent (ADK)]
       │
       ├── JSON-RPC 2.0 over HTTP Streamable
       ▼
[Stripboard.Mcp.* (.NET 10 Web Endpoints)]
       ├── POST /mcp/schedule  --> mcp-schedule
       ├── POST /mcp/people    --> mcp-people
       ├── POST /mcp/locations --> mcp-locations
       └── POST /mcp/weather   --> mcp-weather
```

---

### 3.2 MCP Server Capabilities & Tools

#### 1. `mcp-schedule` (`/mcp/schedule`)
- **`get_active_schedule`**: Returns active schedule version, days, scenes, and metrics.
- **`validate_rules`**: Runs `UnionRulesService` against the current schedule to audit turnaround, meal penalties, and rest days.

#### 2. `mcp-people` (`/mcp/people`)
- **`get_dood_report`**: Generates Day Out of Days (DOOD) cast availability report (Hold, Drop, Pickup, Work).
- **`list_cast_members`**: Lists registered cast members and assigned characters.

#### 3. `mcp-locations` (`/mcp/locations`)
- **`get_location_usage`**: Calculates daily location density and identifies company moves.
- **`list_locations`**: Returns all breakdown locations and sets.

#### 4. `mcp-weather` (`/mcp/weather`)
- **`get_forecast`**: Returns weather forecasts and rain risks for shooting locations.

---

## 4. Error Codes & HTTP Status Matrix

| Status Code | Description | Reason |
|:---:|---|---|
| `200 OK` | Request succeeded | Normal operation. |
| `201 Created` | Resource created | Successful schedule generation or import. |
| `400 Bad Request` | Invalid payload or parameter | Malformed JSON or invalid trigger type. |
| `401 Unauthorized` | Missing authentication token | Request missing identity headers. |
| `403 Forbidden` | Non-Producer commitment attempt | Agent attempting to invoke `/api/schedule/commit`. |
| `404 Not Found` | Resource does not exist | No active schedule board in database. |
| `503 Service Unavailable` | Degraded service | Database unreachable or initial schedule un-solved. |
