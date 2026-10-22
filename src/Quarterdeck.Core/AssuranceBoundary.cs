using System.Collections.Frozen;

namespace Quarterdeck.Core;

/// <summary>
/// Names the non-negotiable boundary of the V3 foundation.
/// </summary>
public static class AssuranceBoundary
{
    public static string RequirementsAuthority => "StrictDoc";
    public static string SourceAndEvidenceAuthority => "Signed Git";

    public static IReadOnlySet<string> ProhibitedSensitiveData { get; } =
        new[]
        {
            "PIN",
            "PUK",
            "CA private key",
            "credential private key",
            "management key"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
