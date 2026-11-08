# Docs site design

Date: 2026-11-08

## Goal

A docs site built from this repo: task-oriented guides plus generated API reference. Today all docs live in one long root `README.md`.

## Decisions

**Generator: DocFX** (`docfx` 2.81.0, pinned as a local dotnet tool in `.config/dotnet-tools.json`).
It reads the XML doc comments the SDK already has, needs no Node toolchain, and has the two features this site depends on: `[!code-csharp[](file#region)]` includes and `<xref:uid>` links that are checked at build time.

**Hosting: CI artifact now, GitHub Pages later.** The repo is private and `gh api repos/roman-ambinder/a2a/pages` returns 404, so Pages is not on. Pages on a private repo needs a paid plan. CI always uploads the built site as the `docs-site` artifact. `docs/site/README.md` says how to turn on Pages. No deploy job is added now.

**Location: `docs/site/`.** The specs and gap analysis in `docs/` stay out of the site.

**Code samples compile.** Every C# sample in a guide comes from `docs/site/snippets/` through a region include. The snippets are one library project, `A2A.DocSnippets`, listed in `A2A.slnx`, so every normal build compiles them. Warnings are errors. A guide has no C# fenced blocks; `docs/site/check-guides.py` fails the build if one appears. Shell, JSON, YAML and HTTP blocks are fine.

**Names are checked.** Guides link key types and members with `<xref:...>`. A wrong name is a DocFX warning, and warnings fail the build.

## Site map

```
docs/site/
  docfx.json, toc.yml, index.md, README.md (contributors; not in the site)
  check-guides.py
  filterConfig.yml        excludes generated gRPC stubs from the API pages
  snippets/               A2A.DocSnippets.csproj, one .cs file per guide
  guides/
    toc.yml
    getting-started.md          server in 20 lines, client
    bindings.md                 JSON-RPC, HTTP+JSON or gRPC
    auth-and-ownership.md       authentication and task ownership
    streaming.md                streaming and resume
    push-notifications.md       outbox, signatures, SSRF guard
    agent-card-signing.md       signing and trust
    multi-tenancy.md            tenants and several agents in one host
    durable-stores.md           Postgres, Redis, several instances
    retention.md                what gets purged, and when
    observability.md            tracing and metrics
    limits-and-rate-limiting.md
    task-safety.md              time limit, turn cap, message hook
    interop-and-conformance.md
    spec-decisions.md           moved from the README
  api/                    generated, not committed
```

## Guide rules

- Short sentences. Plain English. No marketing.
- Shape: what it is and when to use it; the code; what happens; options with their defaults; what can go wrong; see also.
- Every statement comes from the current code on `main`. The README can be out of date; where they differ, the code wins and the guide says what the code does.
- Do not document anything that is not on `main` yet (for example open PRs).
- Links to repo files outside `docs/site/` use the full URL `https://github.com/roman-ambinder/a2a/blob/main/<path>`. `check-guides.py` checks that the path exists.

## README

"Behavior and spec decisions" moves to `guides/spec-decisions.md`. The README keeps a one-paragraph pointer, and gains a short "Documentation" section. Other sections stay, because two other branches are adding README sections.

## CI: `docs` job

1. `dotnet tool restore`, then build `A2A.DocSnippets` (warnings as errors).
2. `check-guides.py`: no C# fences; repo links exist.
3. `docfx docs/site/docfx.json --warningsAsErrors`: API metadata, site, broken internal links and bad `xref`s.
4. `lychee --offline` over the built site: links between generated pages.
5. `lychee` over the guides' external links, skipping this private repo (`check-guides.py` covers those links).
6. Upload `docs/site/_site` as `docs-site`.

## Out of scope

Hosting decisions that need a paid plan, versioned docs, and guides for work not yet on `main` (v0.3 compatibility, Microsoft.Extensions.AI, the conformance suites in PR 17).
