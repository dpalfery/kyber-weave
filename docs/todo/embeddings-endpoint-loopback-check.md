---
id: todo/embeddings-endpoint-loopback-check
title: A non-loopback HTTPS embeddings endpoint is accepted where the test says it must be rejected
doc-type: todo
component: DocGraph
owner: dpalfery
last-reviewed: 2026-08-16
status: draft
---

# A non-loopback HTTPS embeddings endpoint is accepted where the test says it must be rejected

This is **context for planning the work, not a plan**.

## Why this exists

`DocsAnalysisConfigTests.LoadFromYamlWhenEmbeddingEndpointIsNotAbsoluteLoopbackHttpRejectsIt`
fails for one of its three rows:

```yaml
mode: prefer, endpoint: https://example.com/v1/embeddings
```

The configuration loads without the expected `YamlException`. The other two rows — a relative
path, and an `ftp://` scheme — are rejected as intended.

Found while working on
[the standards plan](../archive/plans/2026-08-16-coding-standards-and-config-reg.md); it is unrelated
to that work and predates it, confirmed by re-running the test against a clean tree.

## What is known

- The failure is a real disagreement between the test and the loader, not a flaky test: it
  reproduces every run.
- Which side is wrong is the open question. The test name asserts the endpoint must be
  *absolute loopback HTTP*, so a public HTTPS endpoint should fail; the loader evidently
  admits HTTPS regardless of host.
- The stake is the reason the restriction exists: embeddings are described in
  [analysis](../docgraph/analysis.md) as local and optional. An endpoint that can be any
  public host means documentation content leaves the machine on the strength of one
  configuration line.
- The parallel restriction on the release origin — loopback authorities only, HTTP permitted
  only under an explicit loopback override — is pinned by `ReleaseOriginTests` and is the
  precedent for what "loopback only" should mean here.

## What needs deciding

Whether a non-loopback HTTPS endpoint is legal. If it is, the test is wrong and its name
should stop claiming otherwise. If it is not, the loader is missing a host check, and that is
a behaviour change for any host already configured with a remote endpoint — so it needs a
diagnostic that says why, not a silent failure.

## How to verify

`dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter
"FullyQualifiedName~DocsAnalysisConfigTests"` passes with a test whose name states the rule
that is actually enforced.
