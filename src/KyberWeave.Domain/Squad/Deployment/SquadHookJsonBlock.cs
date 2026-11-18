using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The shared hook-file shape owned through a JSON block splice.</summary>
public enum SquadHookBlockFormat
{
    Cursor,
    Codex,
    Factory,
    Devin,
    Antigravity,
    Claude,
}

/// <summary>One managed entry written by a splice: its JSON pointer and canonical digest.</summary>
/// <remarks>
/// The member is named <c>Location</c> rather than <c>Pointer</c> because the latter trips
/// CA1720 (<c>System.Reflection.Pointer</c>); the value is still an RFC 6901 JSON pointer.
/// </remarks>
public sealed record SquadHookOwnedEntry(string Location, string Digest);

/// <summary>One owned entry whose current file content no longer matches its record.</summary>
public sealed record SquadHookDrift(string Location, string Reason);
