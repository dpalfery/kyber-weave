---
id: todo/read-global-targets-yaml-failure-is-silent
title: ReadGlobalTargets turns a YAML problem into no targets without a word
doc-type: todo
component: Distribution
status: draft
owner: dpalfery
last-reviewed: 2026-11-09
---

# ReadGlobalTargets turns a YAML problem into no targets without a word

`UserHostConfig.ReadGlobalTargets` (`src/KyberWeave.Core/Configuration/UserHostConfig.cs`) parses the
global arbiter targets file with YamlDotNet. A malformed file gives back an empty target list, so the
arbiter hook refresh does nothing and says nothing.

Its catch filter names `YamlException` first. Evaluating that filter needs YamlDotNet to be loaded. When
YamlDotNet cannot be loaded (for example after a self-update swaps the program file), the failed load is
swallowed inside the filter and the original exception escapes instead. This was inferred from the code
while closing out [the self-update hand-off plan](../archive/plans/2026-11-08-self-update-finishes-in-the-new-binary.md)
and matches the reported behaviour; it has not been reproduced in isolation.

The hand-off plan did not fix this (out of scope). The step that calls it now sits under one handler, so an
escaping exception is reported as a warning, but a malformed file still reads as "no targets".

**Decide:** report a malformed global targets file as an error that names the file path; order the catch
so that no type has to load to decide whether to catch it.
