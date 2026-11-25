using KyberWeave.Core.Arbiter.Plans;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract tests for the Kyber Arbiter plan parser (task 1.3) over fixture plans held in memory. The archived plans
/// the parser must also survive are checked by <c>ArbiterArchivedPlanParserTests</c> in the content tests.
/// </summary>
public sealed class ArbiterPlanParserTests
{
    private static PlanDocument ParseFixture(string name)
    {
        return PlanDocumentParser.Parse(EmbeddedFixtureText.Read(typeof(ArbiterPlanParserTests).Assembly, $"arbiter-plans/{name}"));
    }

    private static PlanTask Task(PlanDocument plan, string id)
    {
        PlanTask? match = plan.Tasks.FirstOrDefault(task => task.Id == id);
        Assert.True(match is not null, $"expected a task '{id}' in the parsed plan; found: [{string.Join(", ", plan.Tasks.Select(task => task.Id))}]");
        return match!;
    }

    private static string[] DiagnosticCodes(PlanDocument plan, string code)
    {
        return [.. plan.Diagnostics.Where(diagnostic => diagnostic.Code == code).Select(diagnostic => diagnostic.Subject)];
    }

    [Fact]
    public void LabelFamilyVariantsAcrossBothGrammarsAreParsed()
    {
        PlanDocument parsed = ParseFixture("plan-label-variants.md");

        PlanTask bold = Task(parsed, "T1");
        Assert.Equal(
            ["src/KyberWeave.Core/Arbiter/Plans/PlanDocument.cs", "src/KyberWeave.Core/Arbiter/Plans/PlanDocumentParser.cs"],
            bold.Files);
        Assert.Equal(["dev-medium"], bold.Skills);
        Assert.Empty(bold.DependsOn);

        PlanTask slash = Task(parsed, "T2");
        Assert.Equal(["tests/KyberWeave.Tests/Alpha.cs", "tests/KyberWeave.Tests/Beta.cs"], slash.Files);
        Assert.Equal(["T1"], slash.DependsOn);

        PlanTask owned = Task(parsed, "T3");
        Assert.Equal(["docs/plans/**", "*/settings.yml"], owned.Files);

        PlanTask scope = Task(parsed, "T4");
        Assert.Equal(["templates/*/README.md"], scope.Files);
        Assert.Equal(["T1", "T2"], scope.DependsOn);

        PlanTask suffix = Task(parsed, "T5");
        Assert.Equal(["README.md"], suffix.Files);
        Assert.Equal(["github-cli", "devops"], suffix.Skills);

        PlanTask lowercase = Task(parsed, "T6");
        Assert.Equal(["products/README.md"], lowercase.Files);
        Assert.Equal(["dev-medium"], lowercase.Skills);

        Assert.Equal("test-first", parsed.DevelopmentMode);
    }

    /// <summary>
    /// "none" followed by an explanation is still none: a task id named there says what the task does
    /// not depend on. Run 4 of the Copilot loop wrote "none (consumes the JSON contract, not T1's code)"
    /// and the task file made the frontend task wait for the backend one.
    /// </summary>
    [Fact]
    public void DependsOnNoneFollowedByAnExplanationNamingATaskHasNoDependencies()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(
            """
            ---
            development-mode: standard
            ---
            # Plan

            ## Tasks

            ### T1: Endpoint

            - **Depends on:** none
            - **Files:**
              - `src/Api/JobController.cs`

            ### T2: Cards

            - **Depends on:** none (consumes the JSON contract fixed in D2, not T1's code)
            - **Files:**
              - `web/src/JobStatusCards.tsx`

            ### T3: Verify

            - **Depends on:** T1 (endpoint), T2 (cards)
            - **Files:** none
            """);

        Assert.Empty(Task(parsed, "T1").DependsOn);
        Assert.Empty(Task(parsed, "T2").DependsOn);
        Assert.Equal(["T1", "T2"], Task(parsed, "T3").DependsOn);
    }

    [Fact]
    public void BothTitleSeparatorsAndIdFormsAreParsed()
    {
        PlanDocument parsed = ParseFixture("plan-title-separators.md");

        Assert.Equal("RED, colon separator contract", Task(parsed, "T1").Title);
        Assert.Equal("Establish the failing em-dash contract", Task(parsed, "T2").Title);
        Assert.Equal("sub and suffix forms", Task(parsed, "T3a-sub-suffix-id").Title);
        Assert.Equal("two digit number", Task(parsed, "T14").Title);
        Assert.Equal("standard", parsed.DevelopmentMode);

        // T14 depends on T99, which names no task in the document.
        Assert.Equal(["T14"], DiagnosticCodes(parsed, PlanDocumentParser.UnknownDependencyRule));
    }

    [Fact]
    public void ContractTableRowsKeepCellsByHeaderName()
    {
        PlanDocument parsed = ParseFixture("plan-contract-and-out-of-scope.md");

        Assert.Equal(2, parsed.ContractRows.Count);
        PlanContractRow first = parsed.ContractRows[0];
        Assert.Equal("T1", first.TaskId);
        Assert.Equal("tests/One.cs", first.TestProjectOrFile);
        Assert.Equal("dotnet test --filter One", first.RunnerFilter);
        Assert.Equal("One behaves.", first.ObservableBehaviour);
        Assert.Equal("Fails before One exists.", first.RedEvidenceRequired);
        Assert.Equal("One passes.", first.GreenAcceptance);

        Assert.Equal(["docs/generated/**", "artifacts/**"], parsed.OutOfScope);
        Assert.Equal("standard", parsed.DevelopmentMode);
    }

    [Fact]
    public void SpecTaskWithoutFilesRaisesMissingFilesDiagnostic()
    {
        PlanDocument parsed = ParseFixture("spec-tasks.md");

        PlanTask body = Task(parsed, "1.1");
        Assert.False(body.Checked);
        Assert.Equal(["src/KyberWeave.Core/Arbiter/engine.ts", "src/KyberWeave.Core/Arbiter/helpers.ts"], body.Files);

        PlanTask labelled = Task(parsed, "1.2");
        Assert.True(labelled.Checked);
        Assert.Equal(["src/one.ts", "src/two.ts"], labelled.Files);
        Assert.Equal(["1.1"], labelled.DependsOn);

        PlanTask silent = Task(parsed, "2");
        Assert.Empty(silent.Files);
        Assert.Equal(["1", "2"], [.. DiagnosticCodes(parsed, PlanDocumentParser.MissingFilesRule).OrderBy(id => id, StringComparer.Ordinal)]);

        Assert.Equal(["3"], DiagnosticCodes(parsed, PlanDocumentParser.UnknownDependencyRule));
    }

    /// <summary>
    /// A spec's <c>Depends on: none</c> is followed by the requirements reference, a plain bullet
    /// with no label. That bullet is not part of the dependency value, so task 1.1 has none, and
    /// the reference's numbers are not read as dependencies on it. Without the boundary, 1.1 would
    /// depend on itself.
    /// </summary>
    [Fact]
    public void SpecDependsNoneFollowedByRequirementsHasNoDependencies()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(SpecTaskFixture(
            dependsOnFirst: "none",
            filesFirst: "`src/one.cs`"));

        Assert.Empty(Task(parsed, "1.1").DependsOn);
        Assert.DoesNotContain("1.1", DiagnosticCodes(parsed, PlanDocumentParser.UnknownDependencyRule));
        Assert.Equal(["1.1"], Task(parsed, "1.2").DependsOn);
    }

    /// <summary>
    /// A spec's Files list stops at the next bold-labelled bullet, so the dotted symbol under
    /// <c>**Acceptance:**</c> is not read as a file. Without the stop, a worker's file scope grows
    /// by whatever its acceptance criteria happen to name.
    /// </summary>
    [Fact]
    public void SpecFilesBulletListStopsAtTheNextBoldLabelledBullet()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(SpecTaskFixture(
            dependsOnFirst: "none",
            filesFirst: "`src/one.cs`"));

        Assert.Equal(["src/one.cs"], Task(parsed, "1.1").Files);
    }

    /// <summary>
    /// The plan grammar has the same stop: a Files list ends at a bold-labelled bullet at its own
    /// indent, so the dotted symbol under <c>**Acceptance criteria:**</c> is not a file scope.
    /// </summary>
    [Fact]
    public void PlanFilesBulletListStopsAtAnAcceptanceBullet()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(
            """
            # Plan

            ## Tasks

            ### T1: Plan task

            - **Files:**
              - `src/KyberWeave.Core/Widget.cs`
            - **Acceptance criteria:**
              - `Widget.Flag` is reported.
            """);

        Assert.Equal(["src/KyberWeave.Core/Widget.cs"], Task(parsed, "T1").Files);
    }

    /// <summary>
    /// The stops must not swallow what the author nested. A dependency list whose sub-bullets are
    /// task ids keeps both ids, and a Files list whose sub-bullet opens with a bold label keeps that
    /// bullet's path together with the bullet after it. Both are regression guards for the stop
    /// rules above.
    /// </summary>
    [Fact]
    public void NestedBulletsStillCount()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(
            """
            ## Tasks

            ### Phase 1

            - [ ] 1.1 RED: First item
              - **Files:**
                - `src/a.cs`
            - [ ] 1.2 GREEN: Second item
              - **Files:**
                - `src/b.cs`
            - [ ] 2.1 GREEN: Third item
              - **Depends on:**
                - 1.1
                - 1.2
              - **Files:**
                - **New:** `src/c.cs`
                - `src/d.cs`
            """);

        PlanTask third = Task(parsed, "2.1");
        Assert.Equal(["1.1", "1.2"], third.DependsOn);
        Assert.Equal(["src/c.cs", "src/d.cs"], third.Files);
    }

    /// <summary>
    /// A Files bullet's description is prose, not file scope: the version expression and the matrix
    /// key it names are not paths. Both match the path rule's file-extension shape, so before the fix
    /// they were returned with the bullet's directory in front of them and a task that edits one
    /// workflow file took scope of two files that do not exist.
    /// </summary>
    [Fact]
    public void FilesBulletDescriptionNamesAreNotPaths()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(
            """
            ## Tasks

            ### T1: Release workflow

            - **Files:**
              - `.github/workflows/release.yml`: reads `process.env.VERSION` and `matrix.target`.
            """);

        Assert.Equal([".github/workflows/release.yml"], Task(parsed, "T1").Files);
    }

    /// <summary>
    /// A description may still name a path: a span with a slash is file scope wherever it sits in
    /// the bullet, and it is taken as written rather than under the lead path's directory.
    /// </summary>
    [Fact]
    public void FilesBulletDescriptionSpanWithASlashIsStillAPath()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(
            """
            ## Tasks

            ### T1: Release notes

            - **Files:**
              - `src/Release.cs`: see `tests/ReleaseTests.cs` and `Notes.md` for the shape.
            """);

        Assert.Equal(["src/Release.cs", "tests/ReleaseTests.cs"], Task(parsed, "T1").Files);
    }

    /// <summary>
    /// Lead paths sit before the bullet's colon and keep the path rule, so every one of them counts.
    /// </summary>
    [Fact]
    public void FilesBulletLeadPathsBeforeTheColonAllCount()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(
            """
            ## Tasks

            ### T1: Two files

            - **Files:**
              - `src/One.cs` and `tests/Two.cs`: the two files the task changes.
            """);

        Assert.Equal(["src/One.cs", "tests/Two.cs"], Task(parsed, "T1").Files);
    }

    /// <summary>
    /// A bare file name in the lead list still inherits the directory named before it.
    /// </summary>
    [Fact]
    public void FilesBulletBareLeadNameStillInheritsItsDirectory()
    {
        PlanDocument parsed = PlanDocumentParser.Parse(
            """
            ## Tasks

            ### T1: Widget model

            - **Files:**
              - `src/Widgets/` and `Widget.cs`: the model the task changes.
            """);

        Assert.Equal(["src/Widgets/**", "src/Widgets/Widget.cs"], Task(parsed, "T1").Files);
    }

    /// <summary>
    /// A spec task list with two checkbox tasks in the layout the spec task files use: task 1.1
    /// depends on nothing, task 1.2 depends on 1.1, and each names one file. The dependency and
    /// file lines are passed in so a test can vary exactly the line it proves.
    /// </summary>
    private static string SpecTaskFixture(string dependsOnFirst, string filesFirst) =>
        $"""
        ## Tasks

        ### Phase 1

        - [ ] 1.1 RED: The first task
          - **Objective:** Pin the first behaviour.
          - **Files:**
            - {filesFirst}
          - **Acceptance:**
            - `Widget.Flag` is reported.
          - **Skills:** `dev-medium`
          - **Depends on:** {dependsOnFirst}
          - _Requirements: 1.1, 1.2_
        - [ ] 1.2 GREEN: The second task
          - **Objective:** Make the second behaviour pass.
          - **Files:**
            - `src/two.cs`
          - **Skills:** `dev-medium`
          - **Depends on:** 1.1
          - _Requirements: 1.1, 1.2_
        """;

    /// <summary>Labels agents write besides the canonical ones: a file list under File, Paths or Files to change.</summary>
    [Theory]
    [InlineData("File")]
    [InlineData("Paths")]
    [InlineData("Files to change")]
    [InlineData("Files")]
    public void AFilesLabelVariant_StillReadsTheFiles(string label)
    {
        PlanDocument plan = PlanDocumentParser.Parse(
            $"# Plan\n\n## Tasks\n\n### T1 — One\n{label}: `src/a.cs`\n\n### T2 — Two\nDepends: T1\n{label}: `src/b.cs`\n");

        Assert.Equal(["src/a.cs"], plan.Tasks[0].Files);
        Assert.Equal(["src/b.cs"], plan.Tasks[1].Files);
    }

    [Theory]
    [InlineData("Depends")]
    [InlineData("Depends on")]
    [InlineData("Dependencies")]
    public void ADependsLabelVariant_StillReadsTheDependencies(string label)
    {
        PlanDocument plan = PlanDocumentParser.Parse(
            $"# Plan\n\n## Tasks\n\n### T1 — One\nFiles: `src/a.cs`\n\n### T2 — Two\n{label}: T1\nFiles: `src/b.cs`\n");

        Assert.Equal(["T1"], plan.Tasks[1].DependsOn);
    }
}
