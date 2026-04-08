# Release Sentinel — Trigger Design

**Document:** Trigger Architecture  
**Project:** Release Sentinel — AI-Powered Release Risk Governance System  
**Version:** 1.1  
**Date:** April 2026  
**Status:** Design Review  
**Prepared by:** AI Innovation Team  
**Change from v1.0:** GitHub replaced with Bitbucket throughout. Webhook internals section added (Section 9).

---

## Table of Contents

1. [Overview](#1-overview)
2. [Trigger 1 — Bitbucket PR Merge](#2-trigger-1--bitbucket-pr-merge)
3. [Trigger 2 — Release Manager Submission](#3-trigger-2--release-manager-submission)
4. [Trigger 3 — Jenkins Pre-Deploy Gate](#4-trigger-3--jenkins-pre-deploy-gate)
5. [Payload Normalisation](#5-payload-normalisation)
6. [Recommended End-to-End Workflow](#6-recommended-end-to-end-workflow)
7. [FastAPI Webhook Handlers](#7-fastapi-webhook-handlers)
8. [Trigger Comparison Summary](#8-trigger-comparison-summary)
9. [How Webhook Events Work — Internals](#9-how-webhook-events-work--internals)

---

## 1. Overview

Release Sentinel supports three distinct trigger points. Each serves a different stage of the release lifecycle and a different audience. All three feed into the same RAG analysis engine and produce the same output format — a risk score, evidence report, and governance gate decision.

```
Trigger 1: PR merged to release branch     → earliest per-PR warning
Trigger 2: Release manager submits release → full release-level go/no-go
Trigger 3: Jenkins pre-deploy pipeline     → hard enforcement gate
```

### Design principle

**All three triggers are additive, not alternatives.** In a healthy release workflow, all three fire in sequence for every release. Trigger 1 gives early warning at the PR level. Trigger 2 gives the release manager a full picture before approving the release. Trigger 3 is the backstop — a safety net that physically prevents a high-risk deployment from proceeding even if earlier signals were ignored.

### Output — same regardless of trigger

Regardless of which trigger fires the analysis, the output is always:

- Risk score (0–100) per PR and rolled up per release
- Evidence report — specific past incidents and code patterns that match
- Recommendations — actionable steps the team can take before deploying
- Governance gate decision — auto-proceed, VP approval required, or blocked
- PR comment posted to Bitbucket
- Slack notification to the release channel

---

## 2. Trigger 1 — Bitbucket PR Merge

### What it is

The earliest trigger. Fires automatically the moment a developer merges a PR into the release branch. No human action required beyond the merge itself.

### When it fires

```
Developer opens PR → code review → PR approved → merged to release/2.4.1
                                                          ↓
                                         Bitbucket fires webhook event
                                                          ↓
                                      Release Sentinel receives + analyses
```

### Bitbucket webhook configuration

Webhooks in Bitbucket are configured at the **repository level** (Bitbucket Cloud) or **repository / project level** (Bitbucket Server / Data Center). Navigate to:

```
Repository → Repository settings → Webhooks → Add webhook
```

```yaml
Title:       Release Sentinel
URL:         https://release-sentinel.internal/api/v1/webhooks/bitbucket
Active:      true
SSL/TLS:     enabled — verify certificate
Secret:      <shared secret — stored in Azure Key Vault>
Triggers:
  - Pull Request: Fulfilled    # PR merged (primary trigger)
  - Pull Request: Created      # optional — for early draft scoring
```

> **Bitbucket Cloud vs Bitbucket Server / Data Center:**  
> The webhook payload structure differs slightly between the two variants. The event key is `pullrequest:fulfilled` on Cloud and `pr:merged` on Server. The FastAPI handler in Section 7 detects and normalises both formats automatically.

### Inbound payload — Bitbucket Cloud

```json
POST /api/v1/webhooks/bitbucket
X-Hub-Signature: sha256=<HMAC signature>
X-Event-Key: pullrequest:fulfilled

{
  "event": "pullrequest:fulfilled",
  "actor": { "display_name": "Dev Name", "uuid": "{uuid}" },
  "pullrequest": {
    "id":    4821,
    "title": "[PROJ-456] Add auto-resolve to break handler",
    "state": "MERGED",
    "merge_commit": { "hash": "a3f9c12b" },
    "source": {
      "branch":     { "name": "feature/PROJ-456-auto-resolve" },
      "repository": { "full_name": "org/reconciliation-service" }
    },
    "destination": {
      "branch":     { "name": "release/2.4.1" },
      "repository": { "full_name": "org/reconciliation-service" }
    },
    "created_on": "2026-04-07T14:20:00Z",
    "updated_on": "2026-04-07T14:23:00Z"
  }
}
```

### Inbound payload — Bitbucket Server / Data Center

```json
POST /api/v1/webhooks/bitbucket
X-Hub-Signature: sha256=<HMAC signature>
X-Event-Key: pr:merged

{
  "eventKey": "pr:merged",
  "actor":    { "name": "dev-username", "emailAddress": "dev@org.com" },
  "pullRequest": {
    "id":    4821,
    "title": "[PROJ-456] Add auto-resolve to break handler",
    "state": "MERGED",
    "fromRef": {
      "displayId":    "feature/PROJ-456-auto-resolve",
      "latestCommit": "a3f9c12b",
      "repository": {
        "slug":    "reconciliation-service",
        "project": { "key": "ORG" }
      }
    },
    "toRef": {
      "displayId": "release/2.4.1",
      "repository": {
        "slug":    "reconciliation-service",
        "project": { "key": "ORG" }
      }
    },
    "updatedDate": 1744035780000
  }
}
```

### Signature validation

Bitbucket signs every payload with HMAC-SHA256 using the shared secret you configure in the webhook settings. The signature arrives in the `X-Hub-Signature` header.

```python
import hashlib
import hmac

def validate_bitbucket_signature(
    payload_body: bytes,
    signature_header: str,
    secret: str,
) -> bool:
    """
    Bitbucket sends: X-Hub-Signature: sha256=<hex digest>
    Works identically for both Bitbucket Cloud and Server.
    IMPORTANT: always compute over raw bytes — before any JSON parsing.
    """
    if not signature_header.startswith("sha256="):
        return False
    expected = "sha256=" + hmac.new(
        secret.encode(), payload_body, hashlib.sha256
    ).hexdigest()
    return hmac.compare_digest(expected, signature_header)
```

**If validation fails — return HTTP 401 immediately. Do not process the payload.**

### Fetching the PR diff — Bitbucket REST API

After receiving the webhook, Release Sentinel fetches the actual code diff:

```
# Bitbucket Cloud
GET https://api.bitbucket.org/2.0/repositories/{workspace}/{repo_slug}/pullrequests/{pr_id}/diff
Authorization: Bearer <access_token>

# Bitbucket Server / Data Center
GET https://{bitbucket-host}/rest/api/1.0/projects/{projectKey}/repos/{repoSlug}/pull-requests/{pr_id}/diff
Authorization: Bearer <personal_access_token>
```

### Posting the risk report as a PR comment

After analysis completes, Release Sentinel posts the risk score back to the Bitbucket PR:

```
# Bitbucket Cloud
POST https://api.bitbucket.org/2.0/repositories/{workspace}/{repo_slug}/pullrequests/{pr_id}/comments
Authorization: Bearer <access_token>
Content-Type: application/json

{ "content": { "raw": "⚠️ **Release Sentinel Risk Score: 87/100**\n\nSimilar to REC-8901..." } }

# Bitbucket Server / Data Center
POST https://{bitbucket-host}/rest/api/1.0/projects/{key}/repos/{slug}/pull-requests/{pr_id}/comments
Authorization: Bearer <personal_access_token>
Content-Type: application/json

{ "text": "⚠️ Release Sentinel Risk Score: 87/100\n\nSimilar to REC-8901..." }
```

### What Release Sentinel does — step by step

1. Receives webhook POST from Bitbucket
2. Validates HMAC-SHA256 signature — rejects with HTTP 401 if invalid
3. Detects Bitbucket Cloud vs Server format from event key
4. Confirms destination branch matches a watched pattern (`release/*`, `main`, `hotfix/*`)
5. Extracts PR ID, repo slug, and JIRA story ID from PR title (`[PROJ-456]` convention)
6. Fetches PR diff via Bitbucket REST API
7. Enqueues Celery task: `analyse_pr.delay(pr_id, repo, diff, jira_story)`
8. Returns HTTP 202 Accepted immediately — analysis runs asynchronously

### Output

- Per-PR risk score posted as a comment on the Bitbucket PR within 30 seconds
- If score > 70, Slack notification sent to the release channel

### Limitations

Trigger 1 analyses one PR at a time. It does not assess the cumulative risk of all PRs in the release together — that is the job of Trigger 2.

---

## 3. Trigger 2 — Release Manager Submission

### What it is

A deliberate human trigger. The release manager submits the full release for evaluation when they are ready to assess the complete release candidate. This produces a release-level risk report across all PRs and all JIRA stories in scope.

### When it fires

```
Release manager opens Release Sentinel dashboard
    │
    ▼
Enters JIRA fix version: "release-2.4.1"   (or epic ID / sprint ID)
    │
    ▼
Clicks "Analyse Release"
    │
    ▼
Release Sentinel fetches all stories → resolves all PRs → runs full analysis
```

### Inbound payload

```json
POST /api/v1/releases/analyze
Authorization: Bearer <JWT token>

{
  "trigger_type":     "manual",
  "jira_fix_version": "release-2.4.1",
  "requested_by":     "release-manager@org.com",
  "options": {
    "include_low_risk_prs":  true,
    "notify_slack_channel":  "release-approvals"
  }
}
```

Alternatively triggered by JIRA epic ID or sprint ID:

```json
{ "trigger_type": "manual", "jira_epic_id":   "PROJ-400"  }
{ "trigger_type": "manual", "jira_sprint_id": "Sprint 42" }
```

### What Release Sentinel does

```
1. Query JIRA API
   GET /rest/api/3/search
   ?jql=fixVersion="release-2.4.1" AND issuetype in (Story, Task, Bug)
   &fields=summary,description,status,assignee,customfield_bitbucket_pr
        │
        ▼
2. For each story — resolve linked Bitbucket PRs
   GET /rest/api/3/issue/{issueId}/remotelink    (JIRA-Bitbucket integration)
   OR search Bitbucket for PRs matching [PROJ-NNN] in title   (fallback)
        │
        ▼
3. For each PR — fetch diff via Bitbucket REST API
   Cloud:  GET /2.0/repositories/{workspace}/{slug}/pullrequests/{id}/diff
   Server: GET /rest/api/1.0/projects/{key}/repos/{slug}/pull-requests/{id}/diff
        │
        ▼
4. Run RAG analysis across all PRs in parallel (Celery)
   ├── Per-PR risk score
   ├── Cross-PR pattern detection (same module touched by multiple PRs)
   └── Release-level roll-up score (weighted average)
        │
        ▼
5. Generate release report
   ├── Per-story risk breakdown (which JIRA story is riskiest)
   ├── Per-PR checklist (code patterns flagged, similar past incidents)
   └── Release-level go / no-go recommendation
        │
        ▼
6. Post report to Release Sentinel dashboard + Slack release channel
```

### JIRA ↔ Bitbucket PR resolution

PRs are linked to JIRA stories via one of two methods, in priority order:

**Method 1 — JIRA native Bitbucket integration (preferred)**  
When the JIRA-Bitbucket integration is enabled, JIRA automatically tracks linked PRs. Retrieve them via:
```
GET /rest/api/3/issue/{issueId}/remotelink
```

**Method 2 — PR title convention (fallback)**  
Developers include the JIRA story ID in the PR title:
```
[PROJ-456] Add null guard to auto_resolve()
```
Release Sentinel searches Bitbucket for PRs on the release branch matching this pattern:
```
# Bitbucket Cloud
GET /2.0/repositories/{workspace}/{slug}/pullrequests
  ?q=title~"PROJ-456" AND state="MERGED" AND destination.branch.name="release/2.4.1"

# Bitbucket Server
GET /rest/api/1.0/projects/{key}/repos/{slug}/pull-requests
  ?filterText=PROJ-456&state=MERGED&at=release/2.4.1
```

---

## 4. Trigger 3 — Jenkins Pre-Deploy Gate

### What it is

The hard enforcement trigger. Jenkins automatically calls Release Sentinel at the pre-deploy stage of every production pipeline run. Release Sentinel evaluates the release and responds with a gate decision — proceed, pause for approval, or halt.

This is the **last line of defence**. Even if the release manager skipped Trigger 2, Jenkins enforces the gate automatically.

### Jenkins pipeline configuration

```groovy
pipeline {
    stages {
        stage('Build') { ... }
        stage('Test')  { ... }

        stage('Release Sentinel Gate') {
            steps {
                script {
                    def response = httpRequest(
                        url:         'https://release-sentinel.internal/api/v1/webhooks/jenkins',
                        httpMode:    'POST',
                        contentType: 'APPLICATION_JSON',
                        requestBody: groovy.json.JsonOutput.toJson([
                            trigger_type: 'pre_deploy',
                            build_id:     env.BUILD_ID,
                            job_name:     env.JOB_NAME,
                            branch:       env.BRANCH_NAME,
                            commit_sha:   env.GIT_COMMIT,
                            environment:  'production'
                        ]),
                        authentication: 'release-sentinel-token'
                    )

                    def result = readJSON text: response.content
                    env.SENTINEL_SCORE   = result.risk_score
                    env.SENTINEL_OUTCOME = result.gate_outcome

                    if (result.gate_outcome == 'blocked') {
                        error("Release Sentinel blocked deployment. " +
                              "Risk score: ${result.risk_score}/100. " +
                              "Reason: ${result.summary}")
                    }

                    if (result.gate_outcome == 'pending') {
                        input message: "Release Sentinel requires VP approval. " +
                                       "Risk score: ${result.risk_score}/100. Approve to continue.",
                              ok: 'Approved'
                    }
                }
            }
        }

        stage('Deploy to Production') { ... }
    }
}
```

### Score-to-action mapping

| Risk score | `gate_outcome` | Jenkins action | Notification |
|------------|---------------|----------------|-------------|
| 0–70 | `proceed` | Pipeline continues | Risk report posted to Slack |
| 70–85 | `pending` | Pipeline pauses — waits for VP approval | Slack approval request sent |
| 85–100 | `blocked` | Pipeline halted via `error()` | Slack alert to on-call + release manager |

### Caching behaviour

If Trigger 2 was already run for this release and the score is less than 30 minutes old, Trigger 3 returns the cached score immediately — no re-analysis. If Trigger 2 was skipped, Trigger 3 runs the full analysis synchronously. Target latency: under 30 seconds.

### Fail-open policy

If Release Sentinel is unavailable or times out, the deployment proceeds with a Slack warning rather than blocking. Deployments are never blocked by infrastructure failures — only by actual risk scores.

---

## 5. Payload Normalisation

All three triggers produce different inbound payloads but are normalised into the same internal `AnalysisJob` object before being handed to the RAG engine.

```python
from dataclasses import dataclass
from typing import Optional

@dataclass
class AnalysisJob:
    release_id:   str            # JIRA fix version, Jenkins build ID, or PR number
    trigger_type: str            # bitbucket_pr | manual | pre_deploy
    pr_list:      list[str]      # Bitbucket PR IDs to analyse
    jira_stories: list[str]      # JIRA story IDs in scope
    repo:         str            # workspace/repo-slug (Cloud) or projectKey/repoSlug (Server)
    branch:       str            # release/2.4.1
    services:     list[str]      # affected services — used for pgvector filter
    requested_by: str            # user or system that triggered
    commit_sha:   Optional[str]  # git commit hash if available
    build_id:     Optional[str]  # Jenkins build ID if applicable
    bb_flavour:   str            # "cloud" | "server"
```

### Normalisation logic per trigger

```python
def normalise_bitbucket_webhook(payload: dict) -> AnalysisJob:
    """Handles both Bitbucket Cloud and Server payload shapes."""

    event_key = payload.get("event") or payload.get("eventKey", "")

    if event_key == "pullrequest:fulfilled":          # Bitbucket Cloud
        pr      = payload["pullrequest"]
        pr_id   = str(pr["id"])
        title   = pr["title"]
        branch  = pr["destination"]["branch"]["name"]
        repo    = pr["destination"]["repository"]["full_name"]
        commit  = pr["merge_commit"]["hash"]
        author  = pr["actor"]["display_name"]
        flavour = "cloud"
    else:                                              # Bitbucket Server
        pr      = payload["pullRequest"]
        pr_id   = str(pr["id"])
        title   = pr["title"]
        branch  = pr["toRef"]["displayId"]
        proj    = pr["toRef"]["repository"]["project"]["key"]
        slug    = pr["toRef"]["repository"]["slug"]
        repo    = f"{proj}/{slug}"
        commit  = pr["fromRef"]["latestCommit"]
        author  = payload["actor"]["name"]
        flavour = "server"

    return AnalysisJob(
        release_id   = f"pr-{pr_id}",
        trigger_type = "bitbucket_pr",
        pr_list      = [pr_id],
        jira_stories = extract_jira_ids(title),
        repo         = repo,
        branch       = branch,
        services     = resolve_services_from_repo(repo),
        requested_by = author,
        commit_sha   = commit,
        build_id     = None,
        bb_flavour   = flavour,
    )
```

---

## 6. Recommended End-to-End Workflow

```
┌─────────────────────────────────────────────────────────────────────┐
│  DAY -7 to -2 (development)                                         │
│                                                                     │
│  Developer merges PR #4821 → release/2.4.1 in Bitbucket            │
│      ↓                                                              │
│  Trigger 1 fires automatically (Bitbucket webhook)                  │
│      ↓                                                              │
│  Risk score: 87/100 posted as Bitbucket PR comment                 │
│  "Similar to REC-8901 — auto_resolve() without null guard"         │
│      ↓                                                              │
│  Developer sees warning, adds null guard, merges fix PR #4830      │
│      ↓                                                              │
│  Trigger 1 fires again — new score: 34/100. All clear.             │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  DAY -1 (release sign-off)                                          │
│                                                                     │
│  Release manager submits release-2.4.1 to Release Sentinel         │
│      ↓                                                              │
│  Trigger 2 fires — full release analysis across all 12 PRs         │
│      ↓                                                              │
│  Release-level score: 42/100 → auto-proceed                        │
│  Report shows per-story breakdown, all PRs green                   │
│      ↓                                                              │
│  Release manager approves the release plan                         │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  DAY 0 (deployment)                                                 │
│                                                                     │
│  Jenkins pipeline reaches pre-deploy stage                         │
│      ↓                                                              │
│  Trigger 3 fires — cached score from Trigger 2 returned: 42/100   │
│      ↓                                                              │
│  Gate outcome: proceed                                              │
│  Deployment continues. Risk report written to audit log.           │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 7. FastAPI Webhook Handlers

```python
from fastapi import APIRouter, Request, HTTPException, BackgroundTasks
from datetime import datetime
from app.services.analysis import enqueue_analysis, run_analysis_synchronous
from app.services.normaliser import (
    normalise_bitbucket_webhook,
    normalise_manual_submission,
    normalise_jenkins_webhook,
)
from app.services.cache import get_cached_score
from app.services.auth import validate_bitbucket_signature, validate_jwt
from app.models import AnalysisJob, ReleaseSubmission

router = APIRouter(prefix="/api/v1")

WATCHED_BRANCHES        = {"main", "master"}
WATCHED_BRANCH_PREFIXES = ("release/", "hotfix/")

def is_watched_branch(branch: str) -> bool:
    return branch in WATCHED_BRANCHES or any(
        branch.startswith(p) for p in WATCHED_BRANCH_PREFIXES
    )


# ---------------------------------------------------------------------------
# Trigger 1 — Bitbucket webhook (Cloud + Server)
# ---------------------------------------------------------------------------

@router.post("/webhooks/bitbucket", status_code=202)
async def bitbucket_webhook(
    request: Request,
    background_tasks: BackgroundTasks,
):
    body      = await request.body()          # read raw bytes FIRST
    signature = request.headers.get("X-Hub-Signature", "")
    event_key = request.headers.get("X-Event-Key", "")

    # Step 1: validate HMAC signature before anything else
    if not validate_bitbucket_signature(body, signature):
        raise HTTPException(status_code=401, detail="Invalid signature")

    payload = await request.json()

    # Step 2: only process merged PR events
    merged_events = {"pullrequest:fulfilled", "pr:merged"}
    if event_key not in merged_events:
        return {"status": "ignored", "reason": f"event {event_key} not tracked"}

    # Step 3: only process watched release branches
    if event_key == "pullrequest:fulfilled":
        dest_branch = payload["pullrequest"]["destination"]["branch"]["name"]
    else:
        dest_branch = payload["pullRequest"]["toRef"]["displayId"]

    if not is_watched_branch(dest_branch):
        return {"status": "ignored", "reason": f"branch {dest_branch} not watched"}

    # Step 4: deduplicate (Bitbucket may retry on timeout)
    pr_id      = payload.get("pullrequest", payload.get("pullRequest", {})).get("id")
    updated_on = payload.get("pullrequest", {}).get("updated_on") or \
                 payload.get("pullRequest", {}).get("updatedDate", "")
    dedup_key  = f"bb-pr-{pr_id}-{updated_on}"

    if is_already_processed(dedup_key):
        return {"status": "duplicate", "reason": "already processed"}
    mark_as_processed(dedup_key, ttl_seconds=3600)

    # Step 5: normalise and enqueue
    job = normalise_bitbucket_webhook(payload)
    background_tasks.add_task(enqueue_analysis, job)

    return {"status": "accepted", "release_id": job.release_id}


# ---------------------------------------------------------------------------
# Trigger 2 — Release manager manual submission
# ---------------------------------------------------------------------------

@router.post("/releases/analyze", status_code=202)
async def analyze_release(
    submission: ReleaseSubmission,
    request: Request,
    background_tasks: BackgroundTasks,
):
    token = request.headers.get("Authorization", "").replace("Bearer ", "")
    user  = validate_jwt(token)
    if not user:
        raise HTTPException(status_code=401, detail="Unauthorised")

    job              = normalise_manual_submission(submission.dict())
    job.requested_by = user.email

    background_tasks.add_task(enqueue_analysis, job)

    return {
        "status":     "accepted",
        "release_id": job.release_id,
        "report_url": f"https://release-sentinel.internal/reports/{job.release_id}",
    }


# ---------------------------------------------------------------------------
# Trigger 3 — Jenkins pre-deploy gate (synchronous — blocks pipeline)
# ---------------------------------------------------------------------------

@router.post("/webhooks/jenkins", status_code=200)
async def jenkins_gate(request: Request):
    token = request.headers.get("Authorization", "").replace("Bearer ", "")
    if not validate_jwt(token):
        raise HTTPException(status_code=401, detail="Unauthorised")

    payload = await request.json()

    cached = get_cached_score(
        branch          = payload["branch"],
        commit_sha      = payload.get("commit_sha"),
        max_age_seconds = 1800,
    )

    score = cached if cached else await run_analysis_synchronous(
        normalise_jenkins_webhook(payload)
    )

    if score.risk_score >= 85:
        gate_outcome = "blocked"
    elif score.risk_score >= 70:
        gate_outcome = "pending"
    else:
        gate_outcome = "proceed"

    return {
        "gate_outcome": gate_outcome,
        "risk_score":   score.risk_score,
        "summary":      score.summary,
        "evidence":     score.evidence,
        "report_url":   f"https://release-sentinel.internal/reports/{payload['build_id']}",
        "requested_at": payload["timestamp"],
        "completed_at": datetime.utcnow().isoformat() + "Z",
    }
```

---

## 8. Trigger Comparison Summary

| Property | Trigger 1 — Bitbucket PR | Trigger 2 — Manual submission | Trigger 3 — Jenkins gate |
|----------|--------------------------|-------------------------------|--------------------------|
| Who initiates | Automatic on PR merge | Release manager | Automatic — Jenkins pipeline |
| Granularity | Per-PR | Full release (all PRs) | Full release (all PRs) |
| Timing | Earliest — during development | Pre-release sign-off | At deploy time |
| Mode | Asynchronous | Asynchronous | Synchronous — blocks pipeline |
| Primary audience | Developer | Release manager | DevOps / pipeline |
| Caches prior score | No | No | Yes — 30-minute window |
| Can block deployment | No — informational | No — informational | Yes — hard gate |
| JIRA stories resolved | From PR title convention | From fix version / epic | From branch + commit SHA |
| Bitbucket variant | Cloud + Server supported | Cloud + Server supported | N/A — Jenkins agnostic |
| Output destination | Bitbucket PR comment + Slack | Dashboard + Slack | JSON response to Jenkins + Slack |
| Fail behaviour | Analysis skipped silently | Error returned to user | Fail open — proceeds with warning |

---

## 9. How Webhook Events Work — Internals

This section explains exactly what happens under the hood when Bitbucket fires a webhook — from the developer clicking "Merge" to Release Sentinel receiving the event and starting the analysis.

### The full journey — every step

```
Developer clicks "Merge PR" in Bitbucket UI
        │
        │  (1) Bitbucket writes the merge commit to the git repository
        │
        ▼
Bitbucket internal event system detects the state change
        │
        │  (2) Emits internal event: pullrequest:fulfilled (Cloud)
        │                         or pr:merged            (Server)
        │
        ▼
Bitbucket webhook dispatcher reads the webhook config for this repo
        │
        │  (3) Finds your registered webhook:
        │      URL:    https://release-sentinel.internal/api/v1/webhooks/bitbucket
        │      Secret: your shared HMAC secret
        │      Events: Pull Request: Fulfilled
        │
        ▼
Bitbucket serialises the event into a JSON payload
        │
        │  (4) Computes HMAC-SHA256 signature over raw payload bytes
        │      using the shared secret you configured
        │      Sets header: X-Hub-Signature: sha256=a1b2c3...
        │
        ▼
Bitbucket makes an HTTPS POST to your endpoint
        │
        │  (5) POST https://release-sentinel.internal/api/v1/webhooks/bitbucket
        │      Headers: X-Hub-Signature, X-Event-Key, Content-Type: application/json
        │      Body: JSON payload
        │      Timeout: Bitbucket waits max 10 seconds for a response
        │
        ▼
FastAPI receives the request at the /webhooks/bitbucket endpoint
        │
        │  (6) Reads raw body bytes (before JSON parsing)
        │      Validates HMAC signature
        │      Checks event key and destination branch
        │      Checks deduplication key (in case of retry)
        │
        ▼
FastAPI returns HTTP 202 Accepted  ← must happen within 10 seconds
        │
        │  (7) Bitbucket receives 202 → marks delivery as successful
        │      Bitbucket does NOT wait for analysis to complete
        │      From Bitbucket's perspective — its job is done here
        │
        ▼
FastAPI BackgroundTask enqueues a Celery job onto Redis queue
        │
        │  (8) Job payload: pr_id, repo, branch, jira_story, commit_sha
        │
        ▼
Celery worker picks up the job from Redis
        │
        │  (9) Calls Bitbucket REST API to fetch the full PR diff
        │      Runs RAG analysis:
        │        embed diff with CodeBERT
        │        search code_embeddings (pgvector ANN)
        │        embed PR summary with text-embedding-3-large
        │        search incident_chunks (pgvector ANN)
        │        GPT-4 synthesises risk score + evidence report
        │
        ▼
Celery worker posts risk report back to Bitbucket PR as a comment
        │
        │  (10) Developer sees risk score comment appear on their PR
        │
        ▼
If score > 70 — Celery worker also posts Slack notification to release channel
```

### The 10-second rule — why you must respond immediately

Bitbucket has a hard delivery timeout. If your endpoint does not respond within **10 seconds**, Bitbucket marks the delivery as failed and schedules a retry. This is why the FastAPI handler returns `202 Accepted` immediately and hands work off to Celery — never running the full 30-second analysis inside the request handler itself.

```
Bitbucket POSTs to your endpoint
    │
    ├── 10 second window ──────────────────────────────────────────┐
    │                                                               │
    │  FastAPI: validate signature (fast — ~1ms)                   │
    │  FastAPI: check event type and branch (fast — ~1ms)          │
    │  FastAPI: enqueue Celery task (fast — ~5ms Redis write)      │
    │  FastAPI: return HTTP 202 ◄── must happen here ──────────────┘
    │
    │  Bitbucket receives 202 → delivery successful → done
    │
    ▼
    Celery worker: fetch diff + run RAG analysis (up to 30 seconds — no timeout pressure)
    Celery worker: post result back to Bitbucket PR via REST API
```

If you return anything other than a 2xx response within 10 seconds — or return nothing at all — Bitbucket will retry the delivery.

### How HMAC signature validation works

HMAC proves two things simultaneously: the payload came from Bitbucket (authentication) and was not modified in transit (integrity). Here is exactly what happens on both sides:

```
BITBUCKET SIDE — signing the payload:
  shared_secret  = "your-secret-configured-in-webhook-settings"
  raw_body_bytes = b'{"event": "pullrequest:fulfilled", ...}'
  signature      = HMAC-SHA256(key=shared_secret, message=raw_body_bytes)
  hex_signature  = signature.hexdigest()
  header_sent    = f"sha256={hex_signature}"

RELEASE SENTINEL SIDE — verifying:
  1. Read X-Hub-Signature header  →  "sha256=a1b2c3d4..."
  2. Read raw request body bytes  →  same bytes Bitbucket signed
  3. Recompute: HMAC-SHA256(our_secret, raw_body_bytes)
  4. Compare using hmac.compare_digest()   ← constant-time, prevents timing attacks
  5. Match  →  payload is authentic, process it
     No match →  HTTP 401, discard immediately

CRITICAL — why raw bytes matter:
  The signature is computed over the exact raw byte sequence Bitbucket sent.
  If you JSON-parse the body and then re-serialise it before verifying,
  the byte sequence changes (different key order, whitespace, encoding)
  and the signature will never match.
  Always call request.body() BEFORE request.json().
```

### Webhook retry behaviour

Bitbucket retries failed deliveries automatically using exponential backoff:

```
Attempt 1  →  your server is down  →  connection refused or timeout
    │
    │  Bitbucket waits (exponential backoff — minutes between retries)
    │
Attempt 2  →  your server is back  →  HTTP 202 received  →  success
```

**Bitbucket Cloud** retries up to 3 times. **Bitbucket Server / Data Center** retry behaviour is configurable by your admin. You can inspect every delivery attempt — including the request payload, response code, and error — in:

```
Repository → Repository settings → Webhooks → [your webhook] → View requests
```

This is your first debugging stop when a webhook is not arriving. Check the delivery log there before looking at your application logs.

### Idempotency — handling duplicate deliveries

Because Bitbucket retries on failure, your endpoint may receive the **same merge event twice** — once during an outage and once on retry. Processing it twice would produce duplicate analysis jobs and duplicate PR comments. Your handler must be idempotent.

```python
from app.services.redis_client import redis

def is_already_processed(dedup_key: str) -> bool:
    return redis.exists(dedup_key) == 1

def mark_as_processed(dedup_key: str, ttl_seconds: int = 3600) -> None:
    redis.setex(dedup_key, ttl_seconds, "1")

# In the handler — build a dedup key from PR ID + updated timestamp
# These two fields together uniquely identify a specific merge event
dedup_key = f"bb-pr-{pr_id}-{updated_on}"

if is_already_processed(dedup_key):
    return {"status": "duplicate", "reason": "already processed"}

mark_as_processed(dedup_key, ttl_seconds=3600)
# ... proceed with analysis
```

### Common webhook debugging checklist

| Symptom | Most likely cause | Fix |
|---------|------------------|-----|
| Webhook not firing at all | Wrong trigger type subscribed | Bitbucket settings → ensure `Pull Request: Fulfilled` is ticked |
| HTTP 401 from your endpoint | HMAC secret mismatch | Confirm the secret in Bitbucket matches the one in Azure Key Vault exactly — no trailing spaces |
| HTTP 200 but no analysis job created | Branch filter dropping the event | Log `dest_branch` in your handler and check it matches your `WATCHED_BRANCH_PREFIXES` |
| Delivery marked as failed, keeps retrying | Handler taking over 10 seconds | Ensure you return 202 immediately before any async work — never run analysis in the request handler |
| Duplicate analysis jobs and PR comments | Retry delivered twice | Add the idempotency key check using Redis (see above) |
| Analysis runs but no PR comment appears | Bitbucket API auth error | Check the access token has `pullrequest:write` scope (Cloud) or `REPO_WRITE` permission (Server) |
| Webhook fires but wrong PR analysed | Event key not differentiated | Confirm your handler checks `X-Event-Key` and routes Cloud vs Server payloads separately |

---

*Document maintained by the AI Innovation Team. Update as integration patterns evolve.*  
*v1.1 — Bitbucket replaces GitHub throughout. Section 9 (webhook internals) added.*