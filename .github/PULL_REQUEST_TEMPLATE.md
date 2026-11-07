## Summary

<!-- What changed and why -->

## Documentation impact

<!-- Scale (new-component | extends-component | no-design-change), affected components, and the Design harvest targets. -->

## Test plan

- [ ] `dotnet build KyberWeave.sln -c Release`
- [ ] Tests covering this change pass (`dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~<TestClass>"`); CI runs the full suite
- [ ] Manual checks (describe if needed):

## Checklist

- [ ] Linked issue (if applicable): #
- [ ] Docs / samples updated when user-facing behavior changes
- [ ] No secrets or credentials in the diff
