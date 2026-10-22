using System.Collections.ObjectModel;

namespace Quarterdeck.Core.Simulation;

public enum SimulationGateRequirementClassification
{
    Required = 0
}

public readonly record struct SimulationGateRequirement(
    string GateCode,
    SimulationGateRequirementClassification Classification);

/// <summary>
/// Compiled, versioned authority for system gates required by the first simulation slice.
/// Callers select neither the catalogue nor its contents.
/// </summary>
public static class SimulationGateCatalogue
{
    public const string Version = "quarterdeck-v3-simulation-gates-v1";

    public static IReadOnlyList<SimulationGateRequirement> Requirements { get; } =
        new ReadOnlyCollection<SimulationGateRequirement>(
        [new("directory_ready", SimulationGateRequirementClassification.Required)]);
}
