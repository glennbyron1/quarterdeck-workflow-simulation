using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationCompositionRootTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("live")]
    [InlineData("adcs")]
    [InlineData("piv")]
    [InlineData("broker")]
    [InlineData("Fake")]
    public void LiveOrAmbiguousSelectionIsRejectedBeforeStoreComposition(string? adapter)
    {
        var fixture = PathsWithoutStores();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                SimulationCompositionRoot.Compose(adapter, fixture.Paths, "chunk8-test"));
            Assert.Equal("live_adapter_prohibited", error.Message);
            Assert.False(File.Exists(fixture.Paths.Workflow));
            Assert.False(File.Exists(fixture.Paths.Journal));
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Paths.SourceRoot));
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [Fact]
    public void ExactFakeSelectionComposesOnlyTruthfullyLabelledFakeRuntime()
    {
        var fixture = PathsWithoutStores();
        try
        {
            var runtime = SimulationCompositionRoot.Compose("fake", fixture.Paths,
                "chunk8-test");
            Assert.Equal(FakeAdapterTokenPolicy.AdapterName, runtime.AdapterName);
            Assert.Equal("Simulation only — no live operation", runtime.DisplayLabel);
            Assert.NotNull(runtime.Workflow);
            Assert.NotNull(runtime.Recovery);
            Assert.NotNull(runtime.Intent);
            Assert.DoesNotContain(typeof(SimulationRuntime).GetProperties(), property =>
                property.PropertyType == typeof(SimulationAuthorityJournalRepository));
            Assert.True(File.Exists(fixture.Paths.Workflow));
            Assert.True(File.Exists(fixture.Paths.Journal));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(fixture.Root, true);
        }
    }

    [Fact]
    public void CompositionSurfaceContainsNoLiveAdapterOrCredentialMaterialType()
    {
        var assembly = typeof(SimulationCompositionRoot).Assembly;
        var names = assembly.GetTypes().Select(type => type.FullName ?? type.Name).ToArray();
        Assert.DoesNotContain(names, name =>
            name.Contains("ActiveDirectory", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Adcs", StringComparison.OrdinalIgnoreCase)
            || name.Contains("CertificateAuthority", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SmartCard", StringComparison.OrdinalIgnoreCase)
            || name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase));
    }

    private static (string Root, SimulationStorePaths Paths) PathsWithoutStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "qd-compose-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        return (root, SimulationStorePaths.Create(
            Path.Combine(root, "workflow", "data.db"),
            Path.Combine(root, "journal", "data.db"), source));
    }
}
