using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Core.Tests;

public sealed class SimulationGateCatalogueTests
{
    [Fact]
    public void FirstSliceCatalogueIsCompiledVersionedAndMandatory()
    {
        Assert.Equal("quarterdeck-v3-simulation-gates-v1", SimulationGateCatalogue.Version);
        var requirement = Assert.Single(SimulationGateCatalogue.Requirements);
        Assert.Equal("directory_ready", requirement.GateCode);
        Assert.Equal(SimulationGateRequirementClassification.Required, requirement.Classification);
        Assert.IsAssignableFrom<IReadOnlyList<SimulationGateRequirement>>(
            SimulationGateCatalogue.Requirements);
    }
}
