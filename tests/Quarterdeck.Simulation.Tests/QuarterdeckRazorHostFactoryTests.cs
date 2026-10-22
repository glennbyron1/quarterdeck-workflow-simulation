using System.Net;
using Microsoft.AspNetCore.Http;
using Quarterdeck.App;
using Quarterdeck.Application;

namespace Quarterdeck.Persistence.Tests;

public sealed class QuarterdeckRazorHostFactoryTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1:5080", "GET", true)]
    [InlineData("127.0.0.1", "127.0.0.1:5080", "POST", true)]
    [InlineData("127.0.0.1", "attacker.example:5080", "GET", false)]
    [InlineData("127.0.0.1", "attacker.example:5080", "POST", false)]
    [InlineData("127.0.0.1", "localhost:5080", "GET", false)]
    [InlineData("127.0.0.1", "127.0.0.1", "GET", false)]
    [InlineData("127.0.0.1", "127.0.0.1:5081", "GET", false)]
    [InlineData("127.0.0.1", "127.0.0.1.:5080", "GET", false)]
    [InlineData("127.0.0.1", "127.1:5080", "GET", false)]
    [InlineData("127.0.0.1", "[::1]:5080", "GET", false)]
    [InlineData("127.0.0.1", "attacker.example,127.0.0.1:5080", "GET", false)]
    [InlineData("203.0.113.5", "127.0.0.1:5080", "GET", false)]
    public async Task Local_request_guard_requires_exact_loopback_authority(
        string peerAddress, string host, string method, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peerAddress);
        context.Request.Host = new HostString(host);
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        var nextCalled = false;

        await QuarterdeckRazorHostFactory.RequireLocalSimulationRequestAsync(
            context, () =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        Assert.Equal(allowed, nextCalled);
        Assert.Equal(allowed ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden,
            context.Response.StatusCode);
    }

    [Fact]
    public async Task Local_request_guard_rejects_missing_peer_and_host()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var nextCalled = false;

        await QuarterdeckRazorHostFactory.RequireLocalSimulationRequestAsync(
            context, () =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public void Shared_factory_registers_ui_shell_without_simulation_authority()
    {
        var builder = QuarterdeckRazorHostFactory.CreateBuilder([]);

        Assert.DoesNotContain(builder.Services,
            descriptor => descriptor.ServiceType == typeof(ISimulationUseCases));
        Assert.Contains("http://127.0.0.1:5080", builder.WebHost.GetSetting("urls") ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Shared_factory_is_public_but_simulation_composition_is_not()
    {
        var assembly = typeof(QuarterdeckRazorHostFactory).Assembly;

        Assert.True(typeof(QuarterdeckRazorHostFactory).IsPublic);
        var simulation = assembly.GetType("Quarterdeck.App.SimulationHostComposition");
        Assert.NotNull(simulation);
        Assert.False(simulation.IsPublic);
    }

    [Fact]
    public void App_project_keeps_exact_non_live_dependency_graph()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root, "src", "Quarterdeck.App", "Quarterdeck.App.csproj"));

        Assert.Contains("Quarterdeck.Application", project, StringComparison.Ordinal);
        Assert.Contains("Quarterdeck.Presentation", project, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "Quarterdeck.Broker", "Quarterdeck.Launcher",
                     "Quarterdeck.WindowsAdapters", "Quarterdeck.WindowsIdentity",
                 })
        {
            Assert.DoesNotContain(forbidden, project, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Quarterdeck.Simulation.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("repository_root_not_found");
    }
}
