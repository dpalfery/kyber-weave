# Code review configuration

Everything host-specific lives in one place — `review:` in `.kyber-weave/kyber-weave.yml`:

```yaml
review:
  gates:
    - id: test
      run: [dotnet, test, -c, Release]     # argv, never a command line
      blocking: true
    - id: build                            # .NET compiler and analyzer warnings
      run: [dotnet, build, MySolution.sln, -c, Release]
      blocking: true
  coverage:
    file-line-percent: 85
    class-line-percent: 85
  duplicates:
    minimum-lines: 4                       # normalized lines, signature and braces excluded
  policy:
    strictness: balanced                  # lenient | balanced | strict; explicit keys below override it
    # major-count-blocks: 3               # optional; overrides the strictness preset
    # min-confidence: 7                    # optional; overrides the strictness preset
    fix-level: minor                      # critical | major | minor (default) | nitpick; what a fix round takes. Fixing, not blocking
    suppressions:
      - id: correctness/generated-nullable
        reason: Regenerated client, tracked separately.
        expires: 2026-11-18                # required; re-justify or it returns
```

`run` is argv because the gate runner refuses a shell. A pipeline or a redirect cannot be
expressed here, and neither can an injection — that is the trade, and it is the right way
round.
