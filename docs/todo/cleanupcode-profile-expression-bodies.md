---
id: todo/cleanupcode-profile-expression-bodies
title: The KyberWeave cleanup profile rewrites expression bodies the repository never declared
doc-type: todo
component: CI Pipelines
status: draft
owner: dpalfery
last-reviewed: 2026-11-11
---

# The KyberWeave cleanup profile rewrites expression bodies the repository never declared

The `KyberWeave` ReSharper cleanup profile in `KyberWeave.sln.DotSettings` rewrites members a task
never touched. In the real run of plan `2026-11-11-cleanupcode-exit-hang` (task T4), cleaning
`src/KyberWeave.Core/Configuration/ReviewConfig.cs` to fix one misformatted line also turned
`ReviewPolicy.ForStrictness` from an expression body into a block body and reflowed its object
initializers. `.editorconfig` declares no expression-body preference, so this is the profile's
opinion rather than the repository's standard, which the `resharper-clt` skill says a cleanup
profile must not impose.

Acceptance for closing this todo: the profile's expression-body and initializer-layout settings
match what `.editorconfig` and `docs/standards/csharp/README.md` declare (or are turned off), and a
cleanup of `ReviewConfig.cs` with one misformatted line changes only that line.
