# Release Sentinel — Detailed Design Document

**Project:** Release Sentinel — AI-Powered Release Risk Governance System  
**Version:** 1.0  
**Date:** April 2026  
**Status:** Design Review  
**Prepared by:** AI Innovation Team  

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Problem Statement](#2-problem-statement)
3. [Solution Overview](#3-solution-overview)
4. [System Architecture](#4-system-architecture)
5. [JIRA Integration — Dual Role Design](#5-jira-integration--dual-role-design)
6. [S3 Incident Knowledge Base — Role B](#6-s3-incident-knowledge-base--role-b)
7. [Database Schema — pgvector](#7-database-schema--pgvector)
8. [Document Parsing and Chunking Strategy](#8-document-parsing-and-chunking-strategy)
9. [Dual Embedding Strategy](#9-dual-embedding-strategy)
10. [RAG Risk Analysis Pipeline](#10-rag-risk-analysis-pipeline)
11. [Governance Gate Design](#11-governance-gate-design)
12. [Integration Layer — Direct REST + Webhooks](#12-integration-layer--direct-rest--webhooks)
13. [Infrastructure and Deployment](#13-infrastructure-and-deployment)
14. [Delivery Roadmap](#14-delivery-roadmap)
15. [Appendix — Key Design Decisions](#15-appendix--key-design-decisions)

---

## 1. Executive Summary

Release Sentinel is an AI-powered release risk intelligence system that evaluates upcoming production deployments by analysing the code changes in a release against a searchable knowledge base of past incidents, bugs, and escaped defects.

The system answers one question before every release: **does this code look like something that has broken production before?**

### Investment

| Item | Value |
|------|-------|
| Development cost | $350K |
| Infrastructure (annual) | $15K/year |
| Team size | 3.25 FTE |
| Timeline | 4–6 months |
| Payback period | < 12 months |

### Expected returns

| Outcome | Value |
|---------|-------|
| P1 incidents prevented annually | 4–6 |
| Avoided SLA penalties | $400K–$600K |
| Engineering hours saved per quarter | 200–400 hrs |
| MTTR reduction | 50% |

---

## 2. Problem Statement

### 2.1 Current state pain points

Release managers today rely on three signals when evaluating a release candidate:

- **Test coverage percentage** — tells you what is tested, not what will break
- **Code review completion** — human reviewers miss subtle cross-module patterns
- **Deployment checklist compliance** — static rules with no predictive capability

What is missing is intelligence about which specific code changes historically cause production failures.

### 2.2 Incident pattern analysis (last 12 months)

| Incident | Root cause | AI-preventable? |
|----------|------------|-----------------|
| NAV-2547 | Fee calculation edge case during leap year | Yes — similar logic error in prior incident |
| REC-8901 | Reconciliation break auto-resolution bug | Yes — same module as 3 prior P1s |
| REG-5432 | Regulatory report missing data after schema change | Yes — insufficient test coverage on migration |
| PERF-7721 | Database deadlock from new query pattern | Partial — timing/load dependent |

**3 out of 4 incidents had detectable patterns in historical data.**

### 2.3 Business impact

| Metric | Value |
|--------|-------|
| Average cost per P1 incident | $100K–$250K |
| Engineering hours lost per incident | ~80 hrs across 6–8 team members |
| Regulatory exposure | Increasing auditor scrutiny on change management |

---

## 3. Solution Overview

### 3.1 What Release Sentinel provides

1. **Predictive risk score (0–100):** Quantifies similarity between the current release and past deployments that caused incidents
2. **Evidence-based insights:** Surfaces specific past incidents whose code patterns resemble the current release
3. **Automated governance gates:** Score thresholds trigger VP approval or auto-deployment block
4. **Post-deployment monitoring:** 48-hour anomaly correlation with auto-generated rollback runbooks

### 3.2 Why RAG over a trained ML model

| Capability | Traditional ML | Release Sentinel RAG |
|------------|---------------|----------------------|
| Time to value | 3–4 months (data labelling + training) | 1–2 months (reuse Blink platform) |
| Data requirements | 6–12 months labelled deployments | Works with unlabelled historical data |
| Explainability | Feature importance scores | Exact past incident that matches |
| Maintenance | Retrain monthly | Auto-updates as incidents are ingested |
| Cold start | Poor until 100s of labels | Intelligent from day one |

### 3.3 Platform reuse strategy

**70% of the system reuses the existing Blink RAG platform.** Only 30% is net-new Release Sentinel code.

Reused Blink components:
- PostgreSQL + pgvector (vector store)
- Azure OpenAI GPT-4 (LLM analysis)
- Celery + Redis (async job queue)
- FastAPI (REST API framework)
- React (dashboard UI)

---

## 4. System Architecture

### 4.1 Layer diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    External Integrations                     │
│   GitHub API    Jenkins Webhook    JIRA API    Slack API    │
└─────────────────────────┬───────────────────────────────────┘
                          │ webhooks + REST calls
┌─────────────────────────▼───────────────────────────────────┐
│                  Integration Layer (new)                     │
│   Webhook Receiver   REST API Clients   Outbound Notifier   │
└─────────────────────────┬───────────────────────────────────┘
                          │
┌─────────────────────────▼───────────────────────────────────┐
│              Release Sentinel Service (30% new)              │
│   Data Ingestion    RAG Risk Analyzer    Governance Engine  │
│   FastAPI Endpoints              React Dashboard            │
└─────────────────────────┬───────────────────────────────────┘
                          │
┌─────────────────────────▼───────────────────────────────────┐
│              Existing Blink Platform (70% reuse)             │
│   PostgreSQL + pgvector    Azure OpenAI GPT-4               │
│   CodeBERT Embeddings      Celery + Redis    Auth + RBAC    │
└─────────────────────────────────────────────────────────────┘
                          │
┌─────────────────────────▼───────────────────────────────────┐
│                   AWS S3 (new)                               │
│         incident-kb/  (Role B knowledge base)               │
└─────────────────────────────────────────────────────────────┘
```

### 4.2 End-to-end analysis flow

```
Release candidate submitted (JIRA fix version / Jenkins pre-deploy trigger)
        │
        ▼
Step 1: Resolve release manifest from JIRA
  └── Fetch all stories in fix version (JQL)
  └── Resolve linked GitHub PRs per story
  └── Fetch diff per PR via GitHub API
  └── Aggregate full code change surface
        │
        ▼
Step 2: RAG risk analysis
  ├── Embed code diffs → CodeBERT → vector(768)
  ├── Search code_embeddings table (pgvector ANN)
  ├── Embed PR summaries → text-embedding-3-large → vector(3072)
  ├── Search incident_chunks table (pgvector ANN)
  └── GPT-4 fuses both retrievals → risk score + evidence report
        │
        ▼
Step 3: Governance gate evaluation
  ├── Score 0–70   → auto-proceed, report posted to PR
  ├── Score 70–85  → VP approval required via Slack
  └── Score 85–100 → auto-deploy blocked
        │
        ▼
Step 4: Post-deployment monitoring (48h window)
  └── Anomaly detection → root cause correlation → rollback runbook
        │
        ▼
Step 5: Feedback loop
  └── Escaped bugs ingested into S3 knowledge base → re-embedded
```

---

## 5. JIRA Integration — Dual Role Design

JIRA serves two completely separate roles in Release Sentinel. These must never be conflated.

### 5.1 Role A — Release manifest (input)

**Purpose:** Determine what is being shipped in the upcoming release.

**Trigger:** Release manager submits a JIRA fix version, epic, or sprint identifier to Release Sentinel.

**API call:**
```
GET /rest/api/3/search
  jql: fixVersion = "release-2.4.1" AND issuetype in (Story, Task, Bug)
  fields: summary, description, status, assignee, customfield_github_pr
```

**What is extracted:**
- Story/epic identifiers
- Linked GitHub PR numbers (via JIRA-GitHub integration or `[PROJ-123]` PR title convention)
- Story summary (used to enrich the GPT-4 context window)
- Acceptance criteria (used to identify missing test coverage)

**Flow:**
```
JIRA fix version
    │
    ▼
Fetch all stories/epics in scope
    │
    ▼
Resolve linked PRs per story (GitHub API)
    │
    ▼
Fetch diff per PR → aggregate code change surface
    │
    ▼
Feed into RAG risk analysis
```

### 5.2 Role B — Incident knowledge base (RAG corpus)

**Purpose:** Provide historical incident context for the vector similarity search.

**Important:** In this organisation, incidents and RCAs are **not** maintained in JIRA. Role B is therefore fulfilled entirely by the **S3 incident knowledge base** (see Section 6). JIRA is not used for Role B.

### 5.3 PR resolution convention

PRs are linked to JIRA stories via one of two methods, in priority order:

1. **JIRA native GitHub integration** — JIRA automatically tracks linked PRs when the integration is enabled. Use `GET /rest/api/3/issue/{issueId}/remotelink` to retrieve them.
2. **PR title convention** — developers include the JIRA story ID in the PR title (e.g. `[PROJ-456] Add null guard to auto_resolve()`). Release Sentinel searches GitHub for PRs matching this pattern against the base branch.

---

## 6. S3 Incident Knowledge Base — Role B

### 6.1 Why S3, not JIRA

The organisation does not maintain structured RCAs or incident records in JIRA. S3 is a format-agnostic document store that accepts whatever artefacts the team already produces during and after incidents — postmortem notes, runbooks, Slack thread exports, email threads, Word documents — without requiring anyone to fill in structured forms under incident pressure.

### 6.2 Bucket structure

```
s3://release-sentinel-incident-kb/
├── postmortems/          # Markdown, PDF, Confluence exports
├── runbooks/             # Markdown, YAML, Word documents
├── slack-exports/        # JSON thread exports from incident channels
└── adhoc/                # Emails, screenshots, free-form notes
```

### 6.3 S3 object tagging schema

Every file uploaded to the bucket must carry the following S3 object tags. These become filterable metadata fields in pgvector.

| Tag key | Example value | Required |
|---------|--------------|----------|
| `severity` | P1, P2, P3 | Yes |
| `service` | reconciliation-service | Yes |
| `incident_date` | 2025-03-12 | Yes |
| `linked_pr` | 4821 | No |
| `resolved_by` | U002 | No |

### 6.4 Ingestion trigger

An S3 `PutObject` event fires a Celery task the moment a file is uploaded. The ingestion pipeline runs fully automatically — no manual indexing step required.

```
S3 PutObject event
    │
    ▼
Celery task: ingest_document(s3_key)
    │
    ├── Check kb_documents registry (skip if content_hash unchanged)
    ├── Download file from S3
    ├── Parse by file type (see Section 8)
    ├── Chunk by document type strategy (see Section 8)
    ├── Prepend context header to each chunk
    ├── Embed each chunk (text-embedding-3-large)
    ├── Upsert into incident_chunks (pgvector)
    └── Update kb_documents registry (status = indexed)
```

### 6.5 Bootstrapping the knowledge base

Since no structured incident history exists today, the bootstrapping sequence is:

1. Bulk export any existing Confluence spaces covering incidents or postmortems → upload to `postmortems/`
2. Export Git-tracked runbooks → upload to `runbooks/`
3. Export Slack incident channel histories (90-day export) → upload to `slack-exports/`
4. Upload any ad hoc incident notes, email threads → `adhoc/`
5. Run the full ingestion pipeline — the knowledge base is operational on day one

Going forward, the post-incident ritual is: **drop the artefact in the S3 bucket**. No forms, no structured templates required.

### 6.6 S3 bucket configuration

```yaml
Versioning: enabled
Lifecycle policy: archive to Glacier after 2 years
Encryption: SSE-S3 (AES-256)
Bucket policy: write access restricted to CI/CD IAM role only
                read access restricted to Release Sentinel service role
Event notifications: PutObject → SQS → Celery worker
```

---

## 7. Database Schema — pgvector

All tables live in the existing Blink PostgreSQL instance with the `pgvector` extension enabled.

### 7.1 Table: `code_embeddings`

Stores embeddings of code diffs from merged PRs. Used to find past code patterns that resemble a new release's changes.

```sql
CREATE TABLE code_embeddings (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    pr_id           TEXT NOT NULL,                    -- GitHub PR number
    repo            TEXT NOT NULL,                    -- org/repo-name
    file_path       TEXT NOT NULL,                    -- src/module/file.py
    chunk_type      TEXT NOT NULL,                    -- hunk | function | file
    chunk_text      TEXT NOT NULL,                    -- raw diff content
    embedding       VECTOR(768) NOT NULL,             -- CodeBERT output
    outcome         TEXT,                             -- clean | bug_escaped | reverted
    meta            JSONB,                            -- jira_story, author, lines_changed
    merged_at       TIMESTAMPTZ
);

-- Indexes
CREATE INDEX ON code_embeddings USING hnsw (embedding vector_cosine_ops);
CREATE INDEX ON code_embeddings (repo, outcome, merged_at);
```

**Notes:**
- `outcome` is back-filled when a bug is reported post-merge. This is the label that makes a past PR a "known bad" pattern.
- `chunk_type` determines the chunking strategy used (see Section 8.4).
- `meta.jira_story` cross-references Role A JIRA stories.

### 7.2 Table: `incident_chunks`

Stores embeddings of incident artefacts from S3. Used to find past postmortems, runbooks, and Slack threads that relate to the current release's risk profile.

```sql
CREATE TABLE incident_chunks (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    s3_key           TEXT NOT NULL,                   -- full S3 object key
    chunk_index      INT NOT NULL,                    -- order within document
    doc_type         TEXT NOT NULL,                   -- postmortem | runbook | slack | adhoc
    section_heading  TEXT,                            -- Root cause | Timeline | Investigation
    chunk_text       TEXT NOT NULL,                   -- 512-token window + 64 overlap
    embedding        VECTOR(3072) NOT NULL,           -- text-embedding-3-large output
    severity         TEXT,                            -- P1 | P2 | P3 (from S3 tag)
    affected_service TEXT[],                          -- array for multi-service incidents
    incident_date    TIMESTAMPTZ,                     -- from S3 object tag
    meta             JSONB                            -- pr_refs, linked_jira, channel
);

-- Indexes
CREATE INDEX ON incident_chunks USING hnsw (embedding vector_cosine_ops);
CREATE INDEX ON incident_chunks USING GIN (affected_service);
CREATE INDEX ON incident_chunks (doc_type, severity, incident_date);
```

**Notes:**
- `affected_service` is an array to handle multi-service incidents (e.g. an incident that affected both `reconciliation-service` and `nav-service`).
- `meta.pr_refs` stores any PR numbers mentioned in the document. This enables a direct join with `code_embeddings` at query time — a precise signal on top of fuzzy vector similarity.
- `section_heading` preserves the structural context of where in the document this chunk came from (e.g. "Root cause" vs. "Communications timeline").

### 7.3 Table: `kb_documents`

Registry table — one row per S3 file. The control plane for the ingestion pipeline.

```sql
CREATE TABLE kb_documents (
    s3_key         TEXT PRIMARY KEY,                  -- unique doc identifier
    content_hash   TEXT NOT NULL,                     -- SHA-256, skip re-embed if unchanged
    status         TEXT NOT NULL DEFAULT 'pending',   -- pending | processing | indexed | failed
    doc_type       TEXT,
    chunk_count    INT,
    total_tokens   INT,
    indexed_at     TIMESTAMPTZ,
    error_msg      TEXT                               -- populated on failure for retry
);
```

**Purpose:** Prevents re-embedding unchanged documents on re-upload. Provides an operator dashboard of what is in the knowledge base. Enables retry logic on failed ingestion.

### 7.4 Hybrid retrieval query

At analysis time, both tables are queried in parallel and results are fused by GPT-4.

```sql
-- Incident document retrieval (runs against incident_chunks)
SELECT
    chunk_text,
    s3_key,
    section_heading,
    severity,
    incident_date,
    meta ->> 'pr_refs' AS pr_refs,
    1 - (embedding <=> $query_vec) AS similarity_score
FROM incident_chunks
WHERE affected_service && $services          -- filter to relevant services
  AND incident_date > NOW() - INTERVAL '2 years'
ORDER BY embedding <=> $query_vec            -- cosine ANN via HNSW
LIMIT 8;

-- Code pattern retrieval (runs against code_embeddings)
SELECT
    chunk_text,
    pr_id,
    file_path,
    outcome,
    merged_at,
    1 - (embedding <=> $code_vec) AS similarity_score
FROM code_embeddings
WHERE repo = $repo
  AND outcome IN ('bug_escaped', 'reverted')  -- only retrieve known-bad patterns
ORDER BY embedding <=> $code_vec
LIMIT 5;
```

### 7.5 Index strategy

HNSW is used over IVFFlat for the following reasons:

- **No training step required** — HNSW builds incrementally; IVFFlat needs a minimum corpus size to cluster effectively
- **Better recall at small corpus sizes** — the knowledge base starts with tens to low hundreds of documents
- **Superior query-time performance** at the expected scale (< 100K chunks)

Switch to IVFFlat only if the corpus exceeds 100K chunks and query latency becomes a concern.

---

## 8. Document Parsing and Chunking Strategy

### 8.1 Pre-processing (all document types)

Before any type-specific parsing, every document goes through:

1. **Bot/system message removal** — drop automated messages, join/leave events
2. **PII scrubbing** — remove email addresses, phone numbers, customer identifiers
3. **Whitespace normalisation** — strip excess blank lines, normalise line endings
4. **Language detection** — tag non-English documents for downstream handling
5. **Context header prepend** — add structured header before embedding (see 8.5)

### 8.2 Postmortem documents (`postmortems/`)

**Supported formats:** Markdown (`.md`), PDF (`.pdf`), Confluence HTML export (`.html`)

**Parser:** `markdown-it` for Markdown, `pdfplumber` for PDF, `beautifulsoup4` for HTML

**Chunking strategy: section-aware**

Chunk at heading boundaries (H2 / H3). Each section becomes one chunk. This is superior to sliding window because postmortem sections have very different information densities — the "Root Cause" section is worth 10x the "Communications Timeline" section for risk prediction.

Target sections to extract (mapped to `section_heading`):

| Section | Priority | Rationale |
|---------|----------|-----------|
| Root cause | Critical | Most predictive for future risk |
| Contributing factors | Critical | Secondary patterns |
| What went wrong | Critical | Narrative description of failure |
| Fix / remediation | High | What code change resolved it |
| Timeline | Medium | When signals appeared |
| Impact | Medium | Blast radius context |
| Action items | Low | Process improvements |

**Fallback:** If no headings are detected, use 512-token sliding window with 64-token overlap.

### 8.3 Runbooks (`runbooks/`)

**Supported formats:** Markdown (`.md`), YAML (`.yaml`), Word (`.docx`)

**Parser:** `markdown-it` for Markdown, `PyYAML` for YAML, `python-docx` for Word

**Chunking strategy: step-aware**

Each numbered step or logical section becomes one chunk. This ensures that retrieval surfaces actionable steps ("restart the reconciliation worker") rather than partial procedure fragments.

For YAML runbooks, each top-level key is treated as a section.

### 8.4 Slack thread exports (`slack-exports/`)

**Format:** JSON array of Slack message objects (standard Slack export format)

**Parser:** `json.loads()` — native Python

**Chunking strategy: phase-aware (primary) with sliding window fallback**

Slack incident threads follow a natural five-phase narrative. Chunking by phase produces semantically coherent chunks that are far more useful for retrieval than arbitrary message windows.

#### The five incident phases

| Phase | Keyword signals | Reaction signals |
|-------|----------------|-----------------|
| Detection | "alert", "firing", "pagerduty", "error rate", "latency spike", "5xx" | `:rotating_light:`, `:fire:` |
| Triage | "who owns", "taking this", "on call", "impact", "customers affected" | — |
| Investigation | "looking at logs", "hypothesis", "root cause", "traced to", "exception" | — |
| Mitigation | "rolled back", "deployed fix", "restarted", "mitigation applied" | — |
| Resolution | "resolved", "all clear", "postmortem", "rca", "action items" | `:white_check_mark:` |

#### Phase detection algorithm

1. Score each message against the keyword signal lists for all five phases
2. Assign the highest-scoring phase label to each message
3. Enforce **monotonic progression** — once in "investigation", never go back to "detection". Real incidents do not regress through phases.
4. Treat a time gap > 10 minutes as a possible phase boundary (advance by one phase)
5. Apply reaction signal overrides (`:white_check_mark:` forces "resolution")

#### Chunk assembly rules

- Group messages by assigned phase → one chunk per phase
- Merge phase groups with < 2 messages into the adjacent phase (avoids noise embeddings)
- **Special case — short threads (< 8 messages):** single chunk, no phase split
- **Special case — no phase signals detected:** fall back to 20-message sliding window with 5-message overlap

#### PR reference extraction

Any mention of `PR #NNNN` or `pull request #NNNN` in the thread is extracted and stored in `meta.pr_refs`. This enables a direct join with `code_embeddings` at query time, providing a precise link between an incident thread and the exact PR that caused it.

#### Chunk output format

```
[SLACK-INCIDENT · P1 · reconciliation-service · 2025-03-12 · INVESTIGATION]
eng-oncall (14:23): checking reconciliation worker logs, seeing NullPointerException
eng-oncall (14:25): traced to PR #4821 — auto_resolve() not handling null trade_id
senior-eng (14:27): confirmed, same pattern as Feb incident in matching engine
senior-eng (14:29): recommending rollback of PR #4821 before applying fix
```

#### User ID resolution

Slack exports contain user IDs (`U0ABC123`), not names. At ingestion time, resolve user IDs to human-readable roles using the Slack Users API or a locally maintained `user_map` dictionary. Store roles rather than personal names (e.g. `sre-oncall`, `eng-lead`) to preserve readability without embedding PII.

### 8.5 Ad hoc documents (`adhoc/`)

**Supported formats:** Plain text (`.txt`), Word (`.docx`), PDF (`.pdf`), Email (`.eml`)

**Parser:** `python-docx` for Word, `pdfplumber` for PDF, `email` stdlib for `.eml`, plain `open()` for `.txt`

**Chunking strategy: 512-token sliding window with 64-token overlap**

Ad hoc documents lack consistent structure, so sliding window is the correct fallback. The overlap preserves context across chunk boundaries.

**Metadata extraction fallback:** Ad hoc documents often lack S3 tags because contributors upload them without tagging. In this case, make a single `gpt-4o-mini` call on the raw text to extract:
- Severity (P1 / P2 / P3)
- Affected service(s)
- Incident date

This keeps metadata consistent across all document types without requiring contributors to fill in forms.

### 8.6 Context header prepend (all types)

Before embedding, prepend a structured context header to every chunk:

```
[{DOC_TYPE} · {SEVERITY} · {SERVICE} · {INCIDENT_DATE} · {SECTION}]
```

Examples:
```
[POSTMORTEM · P1 · reconciliation-service · 2025-03-12 · ROOT CAUSE]
[SLACK-INCIDENT · P1 · nav-service · 2025-01-08 · INVESTIGATION]
[RUNBOOK · P2 · reconciliation-service · 2024-11-20 · STEP 3]
```

This is one of the highest-leverage techniques in the system. The header encodes doc type, severity, service, and section into the embedding itself — so a query about a reconciliation PR will naturally surface P1 postmortems about `reconciliation-service` over P3 runbooks about unrelated services, without any additional filtering logic.

---

## 9. Dual Embedding Strategy

Two embedding models are used in parallel. They operate on different semantic spaces and are stored in separate pgvector tables.

### 9.1 CodeBERT — for code diffs

| Property | Value |
|----------|-------|
| Model | `microsoft/codebert-base` |
| Dimensions | 768 |
| Table | `code_embeddings` |
| Input | Raw diff hunks, function bodies, file contents |
| Strength | Understands syntax, variable naming, control flow, code logic patterns |

**Why not a general embedding model for code?**

General-purpose models like `text-embedding-ada-002` treat code as text. They will not recognise that `auto_resolve(txn)` and `auto_match(trade)` are semantically equivalent risky patterns — both are auto-resolution loops on a collection without null guards. CodeBERT was pretrained on code and understands these structural equivalences.

### 9.2 text-embedding-3-large — for incident documents

| Property | Value |
|----------|-------|
| Model | `text-embedding-3-large` (Azure OpenAI) |
| Dimensions | 3072 |
| Table | `incident_chunks` |
| Input | Postmortem text, runbook steps, Slack thread phases, ad hoc notes |
| Strength | Deep semantic understanding of narrative, causal language, technical prose |

### 9.3 Query-time dual retrieval

At analysis time, both searches run in parallel (asyncio) and results are passed to GPT-4 together:

```python
async def retrieve_context(pr_diff: str, pr_summary: str, services: list[str]):
    code_vec = await embed_code(pr_diff)           # CodeBERT
    text_vec = await embed_text(pr_summary)        # text-embedding-3-large

    code_hits, incident_hits = await asyncio.gather(
        search_code_embeddings(code_vec, top_k=5),
        search_incident_chunks(text_vec, services, top_k=8),
    )
    return code_hits, incident_hits
```

GPT-4 receives both result sets in the prompt and synthesises them into a single risk score and evidence report.

---

## 10. RAG Risk Analysis Pipeline

### 10.1 Full pipeline (per release)

```
Release candidate input (JIRA fix version or Jenkins trigger)
    │
    ▼
1. Resolve release manifest
   ├── JIRA: fetch all stories in fix version
   ├── GitHub: fetch diff per linked PR
   └── Aggregate: full code change surface

    │
    ▼
2. Per-PR analysis (parallelised via Celery)
   ├── Chunk diff by strategy (function / hunk / file)
   ├── Embed each chunk with CodeBERT
   ├── Search code_embeddings (ANN, top-5 known-bad patterns)
   ├── Embed PR summary with text-embedding-3-large
   └── Search incident_chunks (ANN, top-8 relevant incidents)

    │
    ▼
3. GPT-4 synthesis (per PR, then rolled up per release)
   ├── Input: PR diff + retrieved code patterns + retrieved incident chunks
   ├── Output: risk score (0–100), evidence list, recommendations
   └── Roll up: weighted average across all PRs → release-level score

    │
    ▼
4. Report generation
   ├── Per-story risk breakdown (which JIRA story is riskiest)
   ├── Per-PR checklist (specific bugs, gaps, similar past issues)
   └── Release-level go / no-go recommendation

    │
    ▼
5. Governance gate evaluation (see Section 11)
```

### 10.2 GPT-4 prompt structure

The GPT-4 call for each PR uses the following prompt structure:

```
System:
You are a release risk analyst. Evaluate the following code change
against retrieved historical incidents and code patterns.
Output a JSON object with: risk_score (0-100), evidence (list of
incident references), recommendations (list of strings),
summary (one paragraph).

Be specific. If you identify a similar past incident, name it by
its document source and explain exactly what the similarity is.
A score above 70 must be justified by at least one concrete evidence item.

User:
## PR being evaluated
{pr_title}
{pr_diff_truncated}

## Similar past code patterns (from code_embeddings)
{code_hits_formatted}

## Related incident documents (from incident_chunks)
{incident_hits_formatted}

## PR metadata
- JIRA story: {jira_story}
- Files changed: {file_list}
- Services affected: {services}
- Test coverage delta: {coverage_delta}
```

### 10.3 Risk score calibration

After Phase 1 delivery, run the scorer retrospectively against the last 12 months of deployments to calibrate the 70 and 85 thresholds. The goal is to find thresholds that would have flagged NAV-2547, REC-8901, and REG-5432 without generating an unacceptable false positive rate (target: < 15% false positives at the 70 threshold).

### 10.4 Recency weighting

More recent incidents are more predictive than older ones. Apply a recency decay in the retrieval step:

```python
def recency_weight(incident_date: datetime, half_life_days: int = 180) -> float:
    age_days = (datetime.utcnow() - incident_date).days
    return 0.5 ** (age_days / half_life_days)

# Final score = similarity_score * recency_weight
```

---

## 11. Governance Gate Design

### 11.1 Score thresholds

| Score range | Action | Notification |
|-------------|--------|-------------|
| 0–70 | Auto-proceed | Risk report posted as PR comment |
| 70–85 | VP approval required | Slack message to VP + release manager |
| 85–100 | Auto-deploy blocked | Slack alert + Jenkins pipeline halted |

### 11.2 Approval workflow

For scores in the 70–85 range:

1. Release Sentinel posts a Slack message to the designated approval channel with the risk report, evidence summary, and an approve/reject button (Slack Block Kit interactive component)
2. VP clicks "Approve" → Slack sends callback to Release Sentinel webhook → deployment proceeds
3. VP clicks "Reject" → deployment halted, release manager notified with required remediation steps
4. If no response within 4 hours → escalate to Head of Engineering

All approval decisions are written to the audit log with timestamp, approver, and risk score at time of approval.

### 11.3 Audit trail

Every risk assessment is persisted in a `risk_assessments` table:

```sql
CREATE TABLE risk_assessments (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    release_id      TEXT NOT NULL,         -- JIRA fix version or Jenkins build ID
    risk_score      INT NOT NULL,
    evidence        JSONB,                 -- list of incident references
    recommendations JSONB,
    gate_outcome    TEXT,                  -- auto_proceed | approved | blocked
    approved_by     TEXT,                  -- VP user ID if manually approved
    approved_at     TIMESTAMPTZ,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);
```

This table satisfies the regulatory requirement for an auditable change management trail.

### 11.4 Post-deployment monitoring

After a release proceeds to production:

1. Release Sentinel registers the deployment with the monitoring service
2. For 48 hours, anomaly signals (error rate spikes, latency increases, reconciliation breaks) are correlated against the deployed changes
3. If an anomaly is detected that correlates with a specific PR in the release, Release Sentinel:
   - Links the anomaly to the PR in the risk assessment record
   - Auto-generates a rollback runbook referencing the specific PR
   - Sends a Slack alert to the on-call channel
4. If the release causes a production incident, the incident artefact is automatically uploaded to the S3 knowledge base, closing the feedback loop

---

## 12. Integration Layer — Direct REST + Webhooks

MCP servers are not used in this implementation. All external system integrations use direct REST API calls and webhooks.

### 12.1 GitHub integration

**Inbound (webhook):**
- Register `pull_request` and `push` events on the organisation
- Endpoint: `POST /api/v1/webhooks/github`
- Validate `X-Hub-Signature-256` HMAC header before processing

**Outbound (REST API):**
- Fetch PR diff: `GET /repos/{owner}/{repo}/pulls/{pr_number}/files`
- Post risk report as PR comment: `POST /repos/{owner}/{repo}/issues/{pr_number}/comments`
- Authentication: GitHub App installation token (preferred over PAT for org-wide access)

**Rate limits:** GitHub enforces 5,000 requests/hour per token. The ingestion pipeline batches PR fetches and uses conditional requests (`If-None-Match` ETag) to minimise API calls.

### 12.2 JIRA integration

**Usage:** Role A only (release manifest resolution). Read-only.

**API calls:**
```
# Fetch stories in a fix version
GET /rest/api/3/search
  ?jql=fixVersion="{version}" AND issuetype in (Story, Task, Bug)
  &fields=summary,description,status,assignee,customfield_github_pr

# Fetch linked remote issues (PRs via JIRA-GitHub integration)
GET /rest/api/3/issue/{issueId}/remotelink
```

**Authentication:** OAuth 2.0 (3-legged) with `read:jira-work` scope  
**Polling cadence:** On-demand only — triggered by release submission, not scheduled

### 12.3 Slack integration

**Outbound only** — Release Sentinel sends messages to Slack but does not read from Slack in real time (Slack exports are handled separately via S3 upload).

**Notification types:**
- Risk report summary (all releases)
- VP approval request (score 70–85, Block Kit interactive message)
- Auto-block alert (score > 85)
- Post-deployment anomaly alert

**Authentication:** Slack Incoming Webhook URL for notifications; Slack Bot Token for interactive approval callbacks

### 12.4 Jenkins integration

**Inbound (webhook):** Jenkins calls `POST /api/v1/webhooks/jenkins` at the pre-deploy stage  
**Outbound:** Release Sentinel calls Jenkins API to halt the pipeline if score > 85:
```
POST /job/{job_name}/build/stop
```

### 12.5 Shared API client design

All four integrations share a base `APIClient` class that handles:

```python
class APIClient:
    async def get(self, url: str, **kwargs) -> dict:
        for attempt in range(self.max_retries):
            try:
                response = await self.session.get(url, **kwargs)
                if response.status == 429:            # rate limited
                    retry_after = int(response.headers.get("Retry-After", 60))
                    await asyncio.sleep(retry_after)
                    continue
                response.raise_for_status()
                return await response.json()
            except aiohttp.ClientError as e:
                if attempt == self.max_retries - 1:
                    raise
                await asyncio.sleep(2 ** attempt)     # exponential backoff
```

Retry logic, exponential backoff, rate limit handling, and token refresh are handled once in this base class rather than duplicated per integration.

---

## 13. Infrastructure and Deployment

### 13.1 Runtime environment

| Component | Technology | Hosting |
|-----------|-----------|---------|
| API service | FastAPI + Uvicorn | Azure Kubernetes Service |
| Async workers | Celery + Redis | AKS (separate worker pool) |
| Database | PostgreSQL 15 + pgvector | Azure Database for PostgreSQL |
| LLM | GPT-4 | Azure OpenAI |
| Embeddings (text) | text-embedding-3-large | Azure OpenAI |
| Embeddings (code) | CodeBERT | Self-hosted on AKS (GPU node) |
| Incident KB storage | S3-compatible | AWS S3 (existing org account) |
| React dashboard | Static build | Azure Static Web Apps |

### 13.2 Scalability considerations

- **Celery worker pool:** Scale horizontally during release windows. Each PR diff analysis is an independent Celery task — 10 PRs in a release = 10 parallel tasks.
- **pgvector HNSW index:** HNSW supports concurrent reads with no locking. Write throughput is lower — acceptable given the low ingestion rate (a few documents per week).
- **CodeBERT inference:** Self-hosted on a GPU-enabled AKS node. Batch PR diffs for efficient GPU utilisation. Cache embeddings by content hash to avoid re-computing unchanged files.
- **GPT-4 latency:** Target < 30 seconds end-to-end per release analysis. Parallelise per-PR GPT-4 calls using `asyncio.gather`. Use `gpt-4o` for speed where `gpt-4` is not required.

### 13.3 SLA targets

| Metric | Target |
|--------|--------|
| Risk analysis latency | < 30 seconds per release |
| System availability | 99.9% |
| S3 ingestion lag | < 5 minutes from upload to indexed |
| Audit log retention | 7 years |

### 13.4 Security

- All secrets (API keys, tokens) stored in Azure Key Vault — never in environment variables or code
- pgvector database access restricted to Release Sentinel service identity (managed identity)
- S3 bucket write access restricted to CI/CD IAM role only
- All API endpoints require JWT authentication (reuse Blink auth layer)
- Risk assessment audit log is write-once (append-only table policy)

---

## 14. Delivery Roadmap

### Phase 1 — Months 1–2: Data foundation and RAG core

**Deliverables:**
- S3 bucket provisioned with lifecycle policy and event notifications
- `kb_documents`, `incident_chunks`, `code_embeddings` tables created in pgvector
- Document ingestion pipeline (Celery tasks for all four document types)
- Slack thread phase-based chunker (implemented and tested)
- CodeBERT embedding service deployed on AKS GPU node
- text-embedding-3-large calls wired via Azure OpenAI
- MVP risk score API — testable against historical incident data
- Knowledge base bootstrapped with existing postmortems and runbooks

**Success criterion:** Running the scorer retrospectively against last 12 months produces risk scores > 70 for NAV-2547, REC-8901, and REG-5432.

### Phase 2 — Month 3: Governance gates

**Deliverables:**
- JIRA Role A integration (release manifest resolution)
- GitHub PR diff fetcher and code chunker
- GPT-4 synthesis prompt and risk report schema
- Governance gate logic (score thresholds, approve/block)
- Slack approval workflow (Block Kit interactive messages)
- `risk_assessments` audit table

**Success criterion:** End-to-end flow from JIRA fix version submission to Slack risk report in < 60 seconds.

### Phase 3 — Month 4: UI, integrations, and hardening

**Deliverables:**
- React dashboard (risk reports, per-story breakdown, audit trail)
- Jenkins pre-deploy webhook integration
- GitHub PR comment posting
- Post-deployment 48-hour monitoring
- Rollback runbook auto-generation
- Observability: Datadog dashboards for ingestion lag, analysis latency, false positive rate

**Success criterion:** Full integration test with a real release candidate from a non-production environment.

### Phase 4 — Months 5–6: Pilot and production launch

**Deliverables:**
- Pilot with one release team (shadow mode — scores computed but gates not enforced)
- Threshold calibration based on pilot data
- Risk score false positive analysis and prompt refinement
- Gate enforcement enabled
- Runbook and operator documentation
- Go-live

**Success criterion:** Gate enforcement active on all production releases. < 15% false positive rate at the 70-score threshold.

### Team allocation

| Role | FTE | Phases |
|------|-----|--------|
| ML Engineer | 1.0 | All phases — embeddings, RAG pipeline, prompt engineering |
| Backend Developer | 1.0 | All phases — FastAPI, Celery, integrations, database |
| Frontend Developer | 0.75 | Phase 3–4 — React dashboard |
| DevOps | 0.25 | All phases — AKS, S3, CI/CD, monitoring |

---

## 15. Appendix — Key Design Decisions

### A. Why HNSW over IVFFlat

HNSW (Hierarchical Navigable Small World) was chosen over IVFFlat for the pgvector index because:
- No training/clustering step required — IVFFlat needs minimum corpus size
- Better recall at small-to-medium corpus sizes (< 100K chunks)
- Supports concurrent reads without locking
- Incremental inserts without index rebuild

Revisit this decision if the corpus exceeds 100K chunks.

### B. Why section-aware chunking over sliding window for postmortems

A postmortem's "Root Cause" section is 10x more predictive for risk than its "Communications Timeline". Sliding window blends these together and degrades retrieval quality. Section-aware chunking ensures that root cause text stays semantically coherent as a unit and surfaces specifically when relevant.

### C. Why monotonic phase progression in Slack chunking

Slack threads do not truly regress through incident phases — an engineer posting "what was the original alert?" during the resolution phase is not actually re-entering detection. Enforcing monotonic progression prevents a late-thread message from flipping the phase label and producing a misleading chunk.

### D. Why the context header prepend matters

Prepending `[POSTMORTEM · P1 · reconciliation-service · 2025-03-12 · ROOT CAUSE]` before embedding encodes doc type, severity, service, and section into the vector space itself. This means a query about a reconciliation PR surfaces P1 postmortems about `reconciliation-service` over unrelated documents without any post-retrieval filtering — the structural information is baked into the embedding, not bolted on afterwards.

### E. MCP servers — deferred to Phase 2 / v2

MCP servers were considered for GitHub and JIRA integration but deferred for the following reasons:
- Direct REST + webhook integration is simpler, more debuggable, and faster to ship
- MCP adds value when Claude agents need to query these systems interactively during live incident investigation — a v2 use case
- The direct integration pattern owns its own retry, auth, and rate limit logic, which is standard boilerplate and requires no additional infrastructure

### F. PR reference cross-referencing

When a Slack thread mentions `PR #4821` and that PR is also in `code_embeddings`, a direct join between the two tables provides a precision signal that vector similarity alone cannot. This cross-referencing is stored in `incident_chunks.meta.pr_refs` and queried at analysis time alongside the ANN search.

---

*Document maintained by the AI Innovation Team. Update after each phase delivery.*