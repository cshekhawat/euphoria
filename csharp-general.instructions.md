---
name: 'C# Documentation Baseline'
description: 'Base XML-doc and inline comment rules for all C# files, extended per layer'
applyTo: '**/*.cs'
---
# C# documentation baseline (applies to every .cs file)

These are the rules every layer inherits. Layer-specific files
(`csharp-controllers.instructions.md`, `csharp-services.instructions.md`, etc.)
add to — never contradict — these.

## XML doc comments (`///`)
- Every `public` and `internal` type, method, and property gets an XML doc comment.
- `<summary>` describes **what the member does in business terms**, not the
  mechanics ("Approves a pending loan application" not "Sets Status to Approved
  and calls SaveChangesAsync").
- Use `<param name="...">`, `<returns>`, and `<exception cref="...">` for every
  parameter, return value, and checked exception.
- Use `<remarks>` for anything a future maintainer needs but that doesn't belong
  in the one-line summary: business rules, regulatory references, non-obvious
  constraints, links to ADRs.
- Never restate the signature in prose ("Gets or sets the Id" for `int Id { get; set; }`
  is noise — omit or say what the Id identifies and its origin).

## Inline `//` comments
- Explain **why**, not what. If a comment just narrates the next line of code,
  delete it — the code already says that.
- No commented-out code, ever. Delete it; source control remembers it.
- No TODO/FIXME without a ticket reference: `// TODO(JIRA-1234): ...`
- Keep comments and code in sync — a stale comment is worse than none. If you
  change logic, update or remove the comment in the same edit.

## General
- No inline comments in test files beyond XML `<summary>` on the test class;
  test names should be self-documenting (`MethodName_Scenario_ExpectedResult`).
- Never document secrets, connection strings, internal hostnames, or customer
  data in comments — comments are code and ship with the repo.

## Documentation tag taxonomy (this is what makes comments stand out)
Use these six ASCII tags — and only these six — as the first word of a comment
or `<remarks>` line when it applies. They render as colored highlights
in-editor via the team's Better Comments config, and are mapped to icons
automatically when the docs site is generated — so source stays plain ASCII.
Never type emoji directly into `.cs` files (encoding/accessibility/security
reasons — see the docs-only agent notes).

| Tag | Use for | Icon on published docs site only |
|---|---|---|
| `WARNING:` | Genuine gotchas — will break something if ignored | ⚠️ |
| `SECURITY:` | Auth, PII, secrets, injection-sensitive code | 🔒 |
| `BUSINESS-RULE:` | A rule that exists because of policy/regulation, not code logic | 📌 |
| `INVARIANT:` | A condition that must always hold, enforced or assumed | ✅ |
| `PERF:` | Deliberate performance trade-off, non-obvious complexity | ⚡ |
| `TODO:` | Known gap, must reference a ticket: `TODO(JIRA-1234): ...` | 🚧 |

Rules of restraint:
- One tag per comment block, at the **start** of the line — never mid-sentence.
- Tag only what's genuinely notable. If every method has a tag, none of them
  mean anything — most methods should have zero tags.
- A tag is a signal, not a substitute for reasoning — always follow it with
  the actual explanation, not just the label.
