namespace Quarterdeck.Core.Simulation;

/// <summary>
/// Validates the adapter-selection token used by a later composition root.
/// This primitive does not itself prove that live adapter code is absent.
/// </summary>
public static class FakeAdapterTokenPolicy
{
    public const string AdapterName = "fake";
    public const string DisplayLabel = "Simulation only — no live operation";

    public static string Validate(string? configuredAdapter)
    {
        if (!string.Equals(configuredAdapter, AdapterName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("live_adapter_prohibited");
        }

        return AdapterName;
    }
}
