---
id: todo/csharp-formatter-churn-split
title: Split C# formatter churn from behaviour changes in the OpenCode capture deploy work
doc-type: todo
component: CI Pipelines
status: draft
owner: dpalfery
last-reviewed: 2026-10-26
---

# Split C# formatter churn from behaviour changes in the OpenCode capture deploy work

The OpenCode capture deploy change (pull request 435, plan
[2026-10-25-opencode-capture-deploy-kyberdash](../archive/plans/2026-10-25-opencode-capture-deploy-kyberdash.md))
combined behaviour changes with C# formatting changes in the same commits. Formatter-only edits
in unrelated C# files make the review harder, because a reviewer must separate whitespace and
ordering changes from logic changes.

Future work should land formatter-only changes (the ReSharper CleanupCode pass and any
`dotnet format` pass) as their own commits, ahead of or behind the behaviour commits, so each
review shows only one kind of change. The files touched only by formatting were not enumerated
in the closeout of that plan.

Acceptance for closing this todo: a formatter pass over the C# tree is a separate commit with no
logic change, and a later behaviour change is reviewed without formatter noise.
