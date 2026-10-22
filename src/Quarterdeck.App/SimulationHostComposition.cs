using Quarterdeck.Application;
using Quarterdeck.Core.Simulation;
using Quarterdeck.Persistence;

namespace Quarterdeck.App;

internal static class SimulationHostComposition
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        FakeAdapterTokenPolicy.Validate(builder.Configuration["Quarterdeck:SimulationAdapter"]);
        SimulationHostPaths.RequireUiAssets(builder.Environment.ContentRootPath);
        builder.Services.AddSingleton<ISimulationUseCases>(_ =>
        {
            var root = SimulationHostPaths.ResolveStorageRoot(
                builder.Configuration["Quarterdeck:SimulationStorageRoot"]);
            var sourceRoot = builder.Environment.ContentRootPath;
            return SimulationUseCases.Create(
                SimulationStorePaths.Create(
                    Path.Combine(root, "workflow", "workflow.db"),
                    Path.Combine(root, "journal", "journal.db"),
                    sourceRoot),
                typeof(Program).Assembly.GetName().Version?.ToString() ?? "development",
                [
                    new(new("TEST-HR-PERSON"), new("TEST-HR-ACCOUNT"),
                        "Synthetic HR operator", new("SITE-A"),
                        new HashSet<SyntheticWorkflowRole>
                        {
                            SyntheticWorkflowRole.HrIntake,
                            SyntheticWorkflowRole.HrVerifier,
                            SyntheticWorkflowRole.HrApprover
                        }),
                    new(new("TEST-IT-PERSON"), new("TEST-IT-ACCOUNT"),
                        "Synthetic IT operator", new("SITE-A"),
                        new HashSet<SyntheticWorkflowRole>
                        {
                            SyntheticWorkflowRole.ItFormalRequester,
                            SyntheticWorkflowRole.ItIssuer
                        }),
                    new(new("TEST-SUBJECT"), new("TEST-SUBJECT-ACCOUNT"),
                        "Synthetic subject", new("SITE-A"))
                ],
                qualifiedFakeDeviceIds: ["FAKE-DEVICE-001"],
                availableFakeAuthorityIds: ["quarterdeck-fake-authority"],
                clearSubjectAccountIds: ["TEST-SUBJECT-ACCOUNT"],
                completeFakeAuthorityAtDispatch:
                    builder.Configuration.GetValue<bool>(
                        "Quarterdeck:Evidence:CompleteFakeAuthorityAtDispatch"),
                deferResultPersistence:
                    builder.Configuration.GetValue<bool>(
                        "Quarterdeck:Evidence:DeferResultPersistence"));
        });
    }
}
