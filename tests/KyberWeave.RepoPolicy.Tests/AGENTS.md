# KyberWeave.RepoPolicy.Tests

Tests whose subject is the repository, not the product code: the scripts (`proc-guard.sh`,
`fast-loop.sh`, release and SBOM tooling), CI workflows, `.kyber-weave/kyber-weave.yml`, docs
prose, and source scans such as the spawn-site audit. Read [`/AGENTS.md`](../../AGENTS.md)
first, and [`../KyberWeave.Tests/AGENTS.md`](../KyberWeave.Tests/AGENTS.md) for the shared
fixtures.

- A test belongs here when it reads a repository file as its subject. A test that calls product
  code to check its behaviour belongs in `KyberWeave.Tests`, even if it reads a fixture.
- The project references `KyberWeave.Core` only, for `ProcessRunner`. Needing another product
  assembly means the test is a behaviour test.
- Classes keep the `KyberWeave.Tests` namespace, so a fully-qualified class name means the same
  test in either project. `KyberWeaveTestPaths`, `TempDirectory` and `TestTempRoot` are linked
  from `KyberWeave.Tests`, not copied.
- It runs as the `test-policy` gate, its own CI step, and in `scripts/fast-loop.sh` whenever a
  change is more than C# under `src/` or `tests/KyberWeave.Tests/`.
