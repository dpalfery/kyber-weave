using System.Text.Json.Serialization;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// The task file beside a plan — <c>&lt;plan&gt;.tasks.json</c> — or beside a specification's
/// <c>tasks.md</c> as <c>tasks.json</c>, as the architect writes it and <c>docs validate</c> reads it.
/// </summary>
/// <remarks>
/// <para>
/// The file duplicates the plan on purpose: each task's id, role, dependencies and file scope
/// are restated so the conductor can dispatch without reading the plan, and the plan stays the
/// authority the file is checked against. The duplication is what
/// <see cref="PlanTaskFileValidator" /> checks, because a file that has drifted hands a worker
/// work the plan does not describe. In a specification folder the file is checked against that
/// folder's <c>tasks.md</c>, and its <see cref="Plan"/> field then holds that <c>tasks.md</c> path.
/// </para>
/// <para>
/// The names are kebab-case to match the rest of this repository's configuration vocabulary, and
/// each is pinned by a <see cref="JsonPropertyNameAttribute" /> rather than by a global naming
/// policy, so this file's shape cannot move when another document's does.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">
/// The version of this file's shape. A version this build does not read is malformed rather than
/// silently valid, because the version decides which fields exist.
/// </param>
/// <param name="Plan">
/// The plan this file was written from, repository-relative. For a specification's <c>tasks.json</c>,
/// the specification's <c>tasks.md</c> path.
/// </param>
/// <param name="ExecutionDigest">
/// The <c>ExecutionDigest</c> of the plan as parsed when the file was written, from
/// <see cref="KyberWeave.Core.Arbiter.Plans.PlanDocumentParser" />.
/// </param>
/// <param name="Tasks">One entry per plan task, in plan order; null when the file declares none.</param>
public sealed record PlanTaskFile(
    [property: JsonPropertyName("schema-version")] int SchemaVersion,
    [property: JsonPropertyName("plan")] string? Plan = null,
    [property: JsonPropertyName("execution-digest")] string? ExecutionDigest = null,
    [property: JsonPropertyName("tasks")] IReadOnlyList<PlanTaskFileEntry>? Tasks = null);

/// <summary>
/// One task of a <see cref="PlanTaskFile" />: what the conductor needs to dispatch it, and the
/// prompt it pastes unchanged.
/// </summary>
/// <param name="Id">The task id, matching the plan's.</param>
/// <param name="Role">The agent the task dispatches to.</param>
/// <param name="DependsOn">The task ids that must finish first, matching the plan's; null when none.</param>
/// <param name="Files">The task's file scope, matching the plan's; null when none.</param>
/// <param name="Session">
/// The worker session group: tasks sharing it continue one worker session where the harness
/// allows it, which is why a plan's RED and GREEN tasks do not share one.
/// </param>
/// <param name="Prompt">The worker's whole brief, pasted unchanged at dispatch.</param>
public sealed record PlanTaskFileEntry(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("depends-on")] IReadOnlyList<string>? DependsOn = null,
    [property: JsonPropertyName("files")] IReadOnlyList<string>? Files = null,
    [property: JsonPropertyName("session")] string? Session = null,
    [property: JsonPropertyName("prompt")] string? Prompt = null);
