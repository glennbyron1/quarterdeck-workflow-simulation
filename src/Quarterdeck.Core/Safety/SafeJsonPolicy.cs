using System.Text.Json;

namespace Quarterdeck.Core.Safety;

/// <summary>
/// Rejects unrestricted JSON fields that could carry secrets or identity evidence.
/// This is a name-based guardrail for already-declared safe metadata, not a sanitizer.
/// </summary>
public static class SafeJsonPolicy
{
    private static readonly string[] ProhibitedNameFragments =
    [
        "pin",
        "puk",
        "password",
        "privatekey",
        "managementkey",
        "rawapdu",
        "apducommand",
        "identitydocument",
        "identityimage",
        "documentnumber",
        "passportnumber",
        "driverlicensenumber",
        "barcode",
        "machinereadablezone",
        "mrz",
        "rawsecret",
        "authtoken"
    ];

    public static void Validate(string json, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Safe JSON must not be empty.", parameterName);
        }

        using var document = JsonDocument.Parse(json);
        Inspect(document.RootElement, parameterName);
    }

    private static void Inspect(JsonElement element, string parameterName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = property.Name
                    .Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .ToLowerInvariant();
                if (ProhibitedNameFragments.Any(normalized.Contains))
                {
                    throw new ArgumentException(
                        $"Safe JSON contains prohibited property {property.Name}.",
                        parameterName);
                }

                Inspect(property.Value, parameterName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Inspect(item, parameterName);
            }
        }
    }
}
