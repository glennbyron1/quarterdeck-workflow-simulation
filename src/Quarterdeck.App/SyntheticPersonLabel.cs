namespace Quarterdeck.App;

/// <summary>
/// Produces a readable label from an allowlisted synthetic person reference.
/// The exact reference remains authoritative; this output is presentation only.
/// </summary>
public static class SyntheticPersonLabel
{
    public static string ForDisplay(string personReference)
    {
        if (string.IsNullOrWhiteSpace(personReference))
            return "Unknown synthetic person";

        if (!personReference.Contains("-", StringComparison.Ordinal))
            return personReference.Trim();

        var parts = personReference.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var start = parts.Length > 1
            && string.Equals(parts[0], "TEST", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (start >= parts.Length)
            return personReference;

        return string.Join(' ', parts[start..].Select(TitlePart));
    }

    private static string TitlePart(string value) => value.Length switch
    {
        0 => value,
        1 => value.ToUpperInvariant(),
        _ => string.Concat(value[..1].ToUpperInvariant(), value[1..].ToLowerInvariant())
    };
}
