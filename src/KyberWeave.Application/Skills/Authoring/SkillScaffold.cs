using System.Globalization;

namespace KyberWeave.Core.Skills.Authoring;

/// <summary>The SKILL.md scaffolds <c>skill new</c> writes, and the rule a new skill's name must satisfy.</summary>
public static partial class SkillScaffold
{
    /// <summary>Whether <paramref name="name"/> is lowercase letters, digits and single hyphens.</summary>
    /// <param name="name">The proposed skill name.</param>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>
    /// The SKILL.md scaffold for <paramref name="template"/> (sop, runbook, reference or
    /// checklist, in any case), or the blank scaffold for any other value.
    /// </summary>
    public static string Template(string template, string name, bool includeLicense, bool includeMetadata)
    {
        string title = string.Join(' ', name.Split('-').Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]));
        (string description, string instructions) = template.ToUpperInvariant() switch
        {
            "SOP" => (
                $"Use to perform {title} the same compliant way every time. Use when a request matches this procedure. Do NOT use for unrelated tasks or when approval limits are exceeded.",
                "## When to use\nState the trigger condition precisely.\n\n## Procedure\n1. Step one.\n2. Step two.\n\n## Rules\n- ALWAYS verify policy windows before acting.\n- NEVER exceed the approval limit.\n\n## Example\nWalk through one concrete, end-to-end case."),
            "RUNBOOK" => (
                $"Use to run the {title} operational task with defined steps and known failure handling. Use when the operation is requested. Do NOT use for diagnosis-only or read-only questions.",
                "## When to use\nDescribe the operational trigger.\n\n## Steps\n1. Discover.\n2. Act.\n3. Validate.\n\n## Failure handling\n- If a step fails, ALWAYS roll back and report.\n\n## Example\nShow a full run including one failure path."),
            "REFERENCE" => (
                $"Use as a reference manual for {title}: schema, fields and how to query them. Use when the agent needs this domain model. Do NOT use to take actions.",
                "## Overview\nDescribe the data model the LLM cannot infer.\n\n## Fields\n| Field | Meaning |\n|---|---|\n\n## How to query\nShow the correct query shape.\n\n## Example\nA worked query and its result."),
            "CHECKLIST" => (
                $"Use to run the {title} checklist so required validations are never skipped. Use before the gated action. Do NOT use after the action has completed.",
                "## When to use\nRun before the gated step.\n\n## Checklist\n- [ ] Item one — MUST pass.\n- [ ] Item two — MUST pass.\n\n## Example\nShow the checklist applied to one case."),
            _ => (
                "Use when … (state the trigger). Do NOT use for … (state the boundary). Replace this with a specific, routable description.",
                "## When to use\n\n## Instructions\n\n## Example\n")
        };

        string licenseBlock = includeLicense ? "license: MIT\n" : string.Empty;
        string metadataBlock = includeMetadata
            ? """
metadata:
  author: your-name
  version: 0.1.0
"""
            : string.Empty;

        return $"""
---
name: {name}
description: {description}
{licenseBlock}{metadataBlock}
---

# {title}

{instructions}
""";
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex NamePattern();
}
