# Release Sentinel — Presentation Script
### Architecture Walkthrough for Senior Technical Manager
**Format:** Zoom call · Estimated duration: 30–45 minutes  
**Tone:** Conversational, confident, technical but accessible  
**Tip:** Pause after each section and invite questions before moving on

---

## Before You Start — Setup Notes

- Share your screen with the architecture diagrams open
- Have the design document (`release_sentinel_design.md`) ready as a backup reference
- Keep the Slack chunker code (`slack_chunker.py`) ready if they want to see implementation detail
- Suggested tool: use the visual diagrams we built — walk through each one as you speak

---

## Opening — Set the Stage (2 minutes)

> *"Thanks for making time for this. What I want to walk you through today is a system we're calling Release Sentinel — an AI-powered release risk governance system. The core idea is straightforward: before every production deployment, the system automatically evaluates the code changes in that release and tells us whether they look like something that has broken production before.*
>
> *I'll cover the problem we're solving, the overall architecture, the key technical decisions we made and why, and then the delivery plan. Feel free to stop me at any point — I'd rather have this be a conversation than a monologue."*

---

## Section 1 — The Problem (4 minutes)

> *"Let me start with the problem, because I want to make sure we're aligned on why this is worth building.*
>
> *Right now, when a release manager evaluates a release candidate, they have three signals available to them. Test coverage percentage, code review completion, and a deployment checklist. The issue is that none of these are predictive. Test coverage tells you what was tested, not what will break. Code review relies on humans catching subtle cross-module patterns — and they often don't. And the checklist is static rules that don't adapt to what's actually in the release.*
>
> *What's missing is intelligence about which specific code changes historically cause production failures.*
>
> *To make this concrete — when we looked back at the last 12 months of incidents, we found that 3 out of 4 of our major P1s had detectable patterns in historical data. NAV-2547, the fee calculation edge case during the leap year — there was a similar logic error in a prior incident. REC-8901, the reconciliation break — the change touched the same module that had caused 3 prior P1s. REG-5432, the regulatory report failure — there was insufficient test coverage on schema migrations, a pattern we'd seen before.*
>
> *Each of those incidents cost between $100K and $250K in SLA penalties and remediation. Engineers spent around 80 hours firefighting each one. The argument for Release Sentinel is simple: if we can prevent even 4 to 6 of these per year, we get our investment back inside 12 months."*

---

## Section 2 — The Solution Approach (4 minutes)

> *"So the question is — how do we build something that can detect these patterns before a release ships?*
>
> *We evaluated two approaches. The first was a traditional machine learning model — train a classifier on historical deployments, have it predict whether a release will cause an incident. We ruled this out for a few reasons. You need 6 to 12 months of labelled training data before the model becomes useful. You need to retrain it monthly as patterns shift. And critically, when it flags a release as high risk, it can only tell you that 'feature X contributed 32% to the score' — which isn't actionable for a release manager.*
>
> *The second approach is RAG — Retrieval Augmented Generation. This is the pattern we're going with, and it solves all three of those problems. Instead of training a model, we build a searchable knowledge base of past incidents. When a new release comes in, we embed the code changes into vectors, search the knowledge base for similar past patterns, and then use GPT-4 to synthesise the results into a risk score and a human-readable evidence report.*
>
> *The big advantage is explainability. Instead of a black-box score, the system can say: 'This PR resembles PR #4821 which caused the reconciliation incident in March 2025 — auto_resolve() was called without a null guard, same pattern is present here.' That's something a release manager can act on immediately.*
>
> *The other advantage is that we already have the RAG infrastructure built — it's the Blink platform we use for our AI knowledge systems. So 70% of what we need already exists. We're writing 30% of net-new code."*

---

## Section 3 — System Architecture Overview (5 minutes)

> *"Let me walk you through the architecture at a high level, and then we'll go deeper on the interesting parts.*
>
> *The system has four layers.*
>
> *At the top are the external integrations — GitHub, Jenkins, JIRA, and Slack. These are all direct REST API integrations and webhooks. No middleware, no MCP servers — just standard API calls. I'll explain the reasoning for that in a moment.*
>
> *Below that is the integration layer — a thin set of connectors that receive webhook events, make outbound API calls, and send notifications. This is new code we're writing.*
>
> *Below that is the Release Sentinel service itself — the core product. This has three main components: the data ingestion pipeline, the RAG risk analyser, and the governance workflow engine. We also have a FastAPI REST layer and a React dashboard. This is the 30% of new code I mentioned.*
>
> *At the bottom is the existing Blink platform — PostgreSQL with pgvector for vector storage, Azure OpenAI for GPT-4, Celery and Redis for async job processing, and the existing auth and RBAC layer. We reuse all of this as-is.*
>
> *And separately, we have an AWS S3 bucket that serves as our incident knowledge base — I'll go into that in detail because it's one of the more interesting design decisions.*
>
> *The end-to-end flow takes under 30 seconds. A release candidate comes in, we resolve the JIRA stories in scope, fetch the linked PRs and their diffs from GitHub, run the RAG analysis, and post a risk report back to the PR and to Slack."*

---

## Section 4 — JIRA Integration (3 minutes)

> *"JIRA plays two completely different roles in this system and I want to be precise about this because it's easy to conflate them.*
>
> *Role A is what I call the release manifest. When a release manager wants to evaluate an upcoming release, they give us a JIRA fix version — say 'release-2.4.1'. We call the JIRA search API with a JQL query to pull all the stories and epics in that fix version. From each story, we resolve the linked GitHub PRs — either via the JIRA-GitHub native integration, or by matching the JIRA story ID in the PR title. We then fetch the code diff for each PR from GitHub. That gives us the full code change surface for the release — every line that's being shipped.*
>
> *Role B would be using JIRA as the incident knowledge base — searching past tickets to find similar issues. We're explicitly not doing this. The reason is pragmatic: our org doesn't maintain structured RCAs or incident records in JIRA. Forcing the team to maintain that discipline isn't realistic. So we solved Role B differently — with S3."*

---

## Section 5 — S3 Incident Knowledge Base (5 minutes)

> *"This is one of the design decisions I'm most confident in, so let me explain it carefully.*
>
> *The incident knowledge base is an S3 bucket with four prefixes: postmortems, runbooks, slack-exports, and adhoc. The idea is that we accept whatever artefacts the team already produces after an incident — a Confluence page someone exported, a Word doc someone wrote up, a Slack thread export, an email chain. You drop it in the bucket and the pipeline handles everything else automatically.*
>
> *The moment a file lands in the bucket, an S3 PutObject event fires a Celery task. That task downloads the file, parses it based on file type, chunks it into semantic pieces, prepends a context header, embeds it using our text embedding model, and stores the resulting vectors in pgvector. No manual indexing step, no forms to fill in.*
>
> *Each file gets S3 object tags — severity, affected service, incident date, and optionally the linked PR number. These tags become filterable metadata in pgvector, so when we search the knowledge base at query time, we can filter to incidents that affected the same service as the PR being evaluated.*
>
> *For bootstrapping — since we don't have a structured incident history today — the plan is to do a one-time bulk export of any existing Confluence spaces covering incidents, export runbooks from Git, export 90 days of Slack incident channel history, and upload any ad hoc notes we have. The knowledge base is operational on day one, and it gets smarter with every incident going forward.*
>
> *The post-incident ritual going forward is just: drop the artefact in the S3 bucket. Nothing else required."*

---

## Section 6 — The Dual Embedding Strategy (5 minutes)

> *"Now I want to go into the technical heart of the system — how we actually measure similarity between code.*
>
> *We use two completely separate embedding models, and this is a deliberate choice.*
>
> *The first model is CodeBERT — microsoft/codebert-base. This is a transformer model pretrained on 6 million code and documentation pairs across Python, Java, JavaScript, Go, PHP, and Ruby. When you pass a code diff through CodeBERT, it outputs a 768-dimensional vector — essentially a semantic fingerprint of that code. The key property is that CodeBERT understands code structure, not just text. Two functions with completely different variable names and even different languages, but the same structural pattern — say, a loop iterating over a collection with an unguarded auto-resolution call — will produce vectors that are very close in that 768-dimensional space.*
>
> *To make this concrete: if PR #4821 from last March had a function called auto_resolve() that caused a reconciliation incident because it didn't handle null trade IDs — and a new PR today has a function called process_breaks() with structurally identical logic — CodeBERT will give those a cosine similarity of around 0.91. A general text embedding model would see different words and score them as dissimilar. That's the gap CodeBERT fills.*
>
> *CodeBERT is used for code diffs. We self-host it on a GPU node in AKS.*
>
> *The second model is text-embedding-3-large from Azure OpenAI, with 3072 dimensions. This is used for the incident documents — postmortems, runbooks, Slack thread chunks. It has deep semantic understanding of narrative prose, causal language, technical descriptions. 'The auto-resolution function silently swallowed null values causing downstream NAV breaks' and 'unhandled null in the resolution loop led to reconciliation failures' are semantically equivalent — this model understands that.*
>
> *At query time, both searches run in parallel using asyncio, and the results are fused together by GPT-4 into a single coherent risk report. So GPT-4 is receiving both: 'here are the 5 most similar past code patterns' and 'here are the 8 most relevant incident documents'. It synthesises all of that into a risk score with evidence."*

---

## Section 7 — pgvector and the Search Mechanism (4 minutes)

> *"Let me briefly cover how the vector search actually works, because I know this is often a black box.*
>
> *pgvector is a PostgreSQL extension that adds a vector data type and approximate nearest neighbour search. We store our embeddings in two tables — code_embeddings for code diffs, and incident_chunks for incident documents.*
>
> *The search uses an index type called HNSW — Hierarchical Navigable Small World. You can think of it as a multi-layered graph where each node is a stored embedding. The top layers are sparse — big jumps, coarse navigation. The bottom layer is dense — fine-grained comparison. When a query vector comes in, the algorithm enters at the top, greedily navigates toward the query's neighbourhood, descends through the layers, and returns the approximate nearest neighbours. This runs in logarithmic time rather than scanning every row in the table.*
>
> *The search operator in PostgreSQL looks like this: `embedding <=> $query_vec` — that's cosine distance. Results are ordered ascending, so the closest vectors — the most similar code — come first. We filter to only return chunks where outcome is 'bug_escaped' or 'reverted' — meaning we only retrieve code patterns that are known to have caused problems. That filter dramatically improves signal quality.*
>
> *After retrieval, we apply a recency decay in Python — incidents from 6 months ago are weighted at half the score of recent incidents. This reflects the reality that recent patterns are more predictive than old ones.*
>
> *We chose HNSW over the alternative index type, IVFFlat, because HNSW builds incrementally with no training step, has better recall at small corpus sizes — which is where we start — and supports concurrent reads without locking. We can revisit if the corpus ever exceeds 100,000 chunks."*

---

## Section 8 — Slack Thread Chunking (3 minutes)

> *"One of the more interesting engineering problems was how to chunk Slack incident thread exports. I want to mention this because it illustrates the level of design thinking that goes into making RAG actually work well.*
>
> *The naive approach would be a sliding window — take every 20 messages, overlap by 5, embed each window. The problem is that a Slack incident thread has a natural narrative structure that a sliding window completely destroys. The first few messages are the detection phase — someone posted an alert. Then there's triage — who owns this, what's the impact. Then investigation — checking logs, forming hypotheses. Then mitigation — rolling back or deploying a fix. Then resolution.*
>
> *If you chunk by message count, you'll embed chunks that mix investigation messages with mitigation messages, losing the semantic coherence of each phase. When you then search for 'root cause of reconciliation break', you want to retrieve the investigation chunk from a relevant incident — not a mixed bag.*
>
> *So we built a phase detector. It scores each message against keyword signal lists for each of the five phases, applies monotonic phase progression — meaning once the thread moves into investigation it can't go back to detection — and uses time gaps between messages as phase boundary signals. The result is one chunk per incident phase, each semantically coherent.*
>
> *As a bonus, the chunker extracts any PR references mentioned in the thread — 'PR #4821', 'pull request 3102' — and stores those in the chunk metadata. At query time, if a new PR being evaluated matches a PR number that appears in an incident thread, that's a direct link — a precision signal on top of the fuzzy vector similarity."*

---

## Section 9 — Governance Gates (3 minutes)

> *"The risk score on its own isn't useful unless it drives action. So we built a governance gate layer on top of the scoring.*
>
> *Scores are bucketed into three bands. Zero to 70 is auto-proceed — the risk report is posted as a comment on the GitHub PR and the deployment continues. 70 to 85 requires VP approval — Release Sentinel posts a Slack message with the risk report, evidence summary, and approve/reject buttons using Slack Block Kit. If there's no response within 4 hours, it escalates to the Head of Engineering. 85 to 100 blocks the deployment automatically — the Jenkins pipeline is halted and the on-call team is notified.*
>
> *Every decision is written to an audit table — the release ID, the risk score, the evidence, the gate outcome, who approved it and when. This is append-only and retained for 7 years. That satisfies our regulatory requirement for an auditable change management trail, which is something our auditors have been asking about.*
>
> *The 70 and 85 thresholds are starting points. In Phase 1, after we've built the scoring engine, we'll run it retrospectively against the last 12 months of deployments and calibrate the thresholds to find the values that would have caught NAV-2547, REC-8901, and REG-5432 with a false positive rate below 15%."*

---

## Section 10 — Integration Layer (2 minutes)

> *"A quick note on the integrations. We evaluated using MCP servers for GitHub and JIRA but decided against it for this phase. Direct REST API integration is simpler, more debuggable, and faster to ship. We own our retry logic, rate limit handling, and token refresh in a single shared APIClient base class — standard boilerplate.*
>
> *GitHub sends webhook events to our FastAPI endpoint when PRs are opened or merged. We validate the HMAC signature, queue a Celery task, and fetch the diff via the GitHub REST API. We post the risk report back as a PR comment.*
>
> *JIRA is read-only — we only use it to resolve the release manifest on demand. No polling.*
>
> *Slack is outbound-only for notifications and the interactive approval workflow. Jenkins calls our webhook at the pre-deploy stage and we call back to halt the pipeline if the score is above 85.*
>
> *MCP is something we'll revisit in v2 — it adds real value when you want Claude agents querying these systems interactively during a live incident investigation. That's a great Phase 2 use case."*

---

## Section 11 — Delivery Roadmap (3 minutes)

> *"We've structured the delivery into four phases over 4 to 6 months.*
>
> *Phase 1, months 1 and 2, is the data foundation and RAG core. We provision the S3 bucket, create the pgvector tables, build the ingestion pipeline for all four document types, deploy CodeBERT on a GPU node, wire up text-embedding-3-large via Azure OpenAI, and build the MVP risk scoring API. By the end of Phase 1 we run the scorer retrospectively against last year's incidents — if it scores NAV-2547, REC-8901, and REG-5432 above 70, Phase 1 is a success.*
>
> *Phase 2, month 3, is the governance layer. JIRA and GitHub integrations, the GPT-4 synthesis prompt, the scoring thresholds, the Slack approval workflow, and the audit trail table.*
>
> *Phase 3, month 4, is the React dashboard, Jenkins integration, post-deployment monitoring, and rollback runbook generation. Full integration testing against a real release candidate in a non-production environment.*
>
> *Phase 4, months 5 and 6, is a pilot in shadow mode — scores are computed but gates aren't enforced yet. We use the pilot data to calibrate thresholds, refine the prompt, and then enable gate enforcement for go-live.*
>
> *The team is 3.25 FTEs — an ML engineer, a backend developer, a frontend developer at 0.75, and DevOps at 0.25. Total investment is $350K development plus $15K per year infrastructure. Expected payback in under 12 months."*

---

## Closing — Wrap Up (2 minutes)

> *"To summarise what I've walked you through:*
>
> *We're building a RAG-based release risk system that evaluates every production deployment against a knowledge base of past incidents. It uses CodeBERT to understand code structure, pgvector for semantic search, and GPT-4 to generate evidence-backed risk reports. The governance gate layer enforces approval workflows and creates an auditable trail. 70% of the infrastructure reuses the Blink platform we already have.*
>
> *The expected outcome is 4 to 6 fewer P1 incidents per year, 50% reduction in MTTR, 200 to 400 engineering hours saved per quarter, and a stronger regulatory compliance posture.*
>
> *I'm happy to go deeper on any part of this — the vector search mechanics, the Slack chunking algorithm, the GPT-4 prompt design, the deployment architecture, anything. What questions do you have?"*

---

## Anticipated Questions and Suggested Answers

---

**Q: Why not just use an off-the-shelf tool for this?**

> *"We evaluated a few options. Most release risk tools are checklist-based or rely on static rules — they don't do semantic similarity against your own incident history. The ones that do machine learning require large amounts of labelled training data we don't have. The RAG approach lets us use our own historical incidents as the knowledge base from day one, and it produces explainable outputs — specific incident references — rather than black-box scores. Nothing off-the-shelf gives us that combination."*

---

**Q: What if the knowledge base is too small to be useful at the start?**

> *"That's the cold start problem, and it's the main reason we chose RAG over a trained ML model. RAG works with whatever data you have from day one — even 20 incident documents in S3 is enough to start producing useful signals. The bootstrapping plan — exporting Confluence, Git runbooks, and 90 days of Slack history — should give us a reasonable starting corpus before we go live. The system gets more precise with every incident added. And in shadow mode during Phase 4, we're not enforcing gates yet, so we can observe the signal quality before we commit to blocking deployments."*

---

**Q: How do we prevent false positives from blocking legitimate releases?**

> *"Two mechanisms. First, the threshold calibration in Phase 1 — we run the scorer retrospectively and tune the 70 and 85 thresholds so they would have caught our known incidents without generating an unacceptable false positive rate. Our target is under 15% false positives at the 70 threshold. Second, the 70 to 85 band requires VP approval rather than auto-blocking — so a human is always in the loop for the borderline cases. Only scores above 85 are auto-blocked, and those represent releases that are extremely similar to past disasters. We also run in shadow mode first, which gives us real data on false positive rates before enforcement goes live."*

---

**Q: What happens to the system if GPT-4 is unavailable?**

> *"We fail open, not closed. If the GPT-4 synthesis call fails, the deployment proceeds with a warning notification that the risk analysis was unavailable. We don't block releases on infrastructure failures. The Celery retry logic will attempt the analysis again, and the result will be posted asynchronously. We'd also have Datadog alerts on GPT-4 call failures so the team can see if there's a systemic issue."*

---

**Q: How do we handle sensitive incident data in S3?**

> *"The S3 bucket is encrypted at rest with SSE-S3 AES-256. Write access is restricted to the CI/CD IAM role only — developers can't write directly to the bucket. Read access is restricted to the Release Sentinel service identity via managed identity. We also run PII scrubbing in the ingestion pipeline — email addresses, phone numbers, and customer identifiers are stripped before embedding. The vectors stored in pgvector are mathematical representations — they can't be reverse-engineered back to the original text."*

---

**Q: Can this integrate with our existing deployment approval process?**

> *"Yes — and that's by design. The Jenkins webhook integration slots directly into the existing pre-deploy stage. The Slack approval workflow uses the same channels the team already uses for release approvals. We're augmenting the existing process with a risk signal, not replacing it. Release managers still make the final call — Release Sentinel gives them better information to make that call with."*

---

**Q: How does this get better over time?**

> *"There's a feedback loop built into the architecture. When a release causes a production incident, the incident artefact — postmortem, Slack thread, runbook — is uploaded to the S3 bucket. The ingestion pipeline embeds it and it becomes part of the knowledge base. The next time a similar code pattern appears in a release candidate, the system has a new data point to retrieve. The more incidents we have in the knowledge base, the more precise the similarity search becomes. It's self-improving by design."*

---

*Script prepared by AI Innovation Team · April 2026*  
*Adjust timing based on audience engagement — technical managers often want to go deep on sections 6 and 7*


