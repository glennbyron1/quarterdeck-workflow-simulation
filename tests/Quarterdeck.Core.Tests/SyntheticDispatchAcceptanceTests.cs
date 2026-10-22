using Quarterdeck.Core.Simulation;
using System.Reflection;

namespace Quarterdeck.Core.Tests;

public sealed class SyntheticDispatchAcceptanceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 6, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcceptanceHoldsClockAndDirectoryMutationUntilExactCommitReturns()
    {
        var fixture = Fixture.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var clockStarted = new ManualResetEventSlim();
        using var directoryStarted = new ManualResetEventSlim();
        var committer = new BlockingCommitter(entered, release);

        var acceptance = Task.Factory.StartNew(() =>
            fixture.Authority.CommitSynchronizedAcceptance(fixture.Admission, committer),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var clockMutation = Task.Factory.StartNew(() =>
        {
            clockStarted.Set();
            fixture.Clock.SetUtc(Now.AddMinutes(1));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var directoryMutation = Task.Factory.StartNew(() =>
        {
            directoryStarted.Set();
            fixture.Directory.SetRoleState(
                fixture.Issuer.AccountId,
                SyntheticWorkflowRole.ItIssuer,
                SyntheticRoleState.Removed);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(clockStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(directoryStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotSame(clockMutation,
            await Task.WhenAny(clockMutation, Task.Delay(TimeSpan.FromMilliseconds(100))));
        Assert.NotSame(directoryMutation,
            await Task.WhenAny(directoryMutation, Task.Delay(TimeSpan.FromMilliseconds(100))));

        release.Set();
        await acceptance;
        await Task.WhenAll(clockMutation, directoryMutation);

        Assert.Equal(Now, committer.Context.ObservedAt);
        Assert.Equal(fixture.Admission.Readiness.DirectoryRevision,
            committer.Context.DirectoryRevision);
    }

    [Fact]
    public void AcceptanceRejectsUnsupportedClockCompositionBeforeCommit()
    {
        var fixture = Fixture.Create(new FixedClock(Now));
        var committer = new RecordingCommitter();

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Authority.CommitSynchronizedAcceptance(fixture.Admission, committer));

        Assert.Equal("simulation_acceptance_clock_unsupported", error.Message);
        Assert.Equal(0, committer.Calls);
    }

    [Fact]
    public void AcceptanceBlocksAuthorityClockAndDirectoryReentry()
    {
        var fixture = Fixture.Create();
        var committer = new ReenteringCommitter(fixture);

        fixture.Authority.CommitSynchronizedAcceptance(fixture.Admission, committer);

        Assert.Equal("simulation_dependency_reentry", committer.AuthorityError?.Message);
        Assert.Equal("simulation_acceptance_reentry", committer.ClockError?.Message);
        Assert.Equal("simulation_acceptance_reentry", committer.DirectoryError?.Message);
        Assert.Equal(1, committer.Calls);
    }

    [Fact]
    public void AcceptanceBridgeExposesOnlyExactCommitAndImmutableObservations()
    {
        var methods = typeof(ISyntheticDispatchAcceptanceCommitter)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public);
        var properties = typeof(SyntheticDispatchAcceptanceContext)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        var method = Assert.Single(methods);
        Assert.Equal("CommitAccepted", method.Name);
        Assert.Equal(typeof(void), method.ReturnType);
        Assert.Equal(
            [typeof(SyntheticDispatchAcceptanceContext)],
            method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.Equal(
            [nameof(SyntheticDispatchAcceptanceContext.DirectoryRevision),
                nameof(SyntheticDispatchAcceptanceContext.ObservedAt)],
            properties.Select(property => property.Name).OrderBy(name => name).ToArray());
        Assert.DoesNotContain(properties, property =>
            property.PropertyType == typeof(SyntheticIdentityDirectory)
            || property.PropertyType == typeof(ISimulationApprovalClock)
            || property.PropertyType == typeof(SyntheticDispatchAdmissionAuthority));
    }

    private sealed class BlockingCommitter(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : ISyntheticDispatchAcceptanceCommitter
    {
        public SyntheticDispatchAcceptanceContext Context { get; private set; }

        public void CommitAccepted(SyntheticDispatchAcceptanceContext context)
        {
            Context = context;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    private sealed class RecordingCommitter : ISyntheticDispatchAcceptanceCommitter
    {
        public int Calls { get; private set; }
        public void CommitAccepted(SyntheticDispatchAcceptanceContext context) => Calls++;
    }

    private sealed class ReenteringCommitter(Fixture fixture)
        : ISyntheticDispatchAcceptanceCommitter
    {
        public int Calls { get; private set; }
        public InvalidOperationException? AuthorityError { get; private set; }
        public InvalidOperationException? ClockError { get; private set; }
        public InvalidOperationException? DirectoryError { get; private set; }

        public void CommitAccepted(SyntheticDispatchAcceptanceContext context)
        {
            Calls++;
            AuthorityError = Assert.Throws<InvalidOperationException>(() =>
                fixture.Authority.CommitSynchronizedAcceptance(
                    fixture.Admission, new RecordingCommitter()));
            ClockError = Assert.Throws<InvalidOperationException>(() =>
                fixture.Clock.SetUtc(Now.AddSeconds(1)));
            DirectoryError = Assert.Throws<InvalidOperationException>(() =>
                fixture.Directory.SetAccountState(
                    fixture.Issuer.AccountId, SyntheticAccountState.Disabled));
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISimulationApprovalClock
    {
        public SimulationClockObservation Observe() => SimulationClockObservation.At(utcNow);
    }

    private sealed record Fixture(
        SyntheticIdentityDirectory Directory,
        SyntheticSimulationApprovalClock Clock,
        SyntheticDispatchAdmissionAuthority Authority,
        SyntheticDispatchAdmission Admission,
        SyntheticActor Issuer)
    {
        public static Fixture Create(ISimulationApprovalClock? clockOverride = null)
        {
            var site = new SyntheticSiteId("acceptance-site");
            var directory = SyntheticIdentityDirectory.Create(
            [
                Binding("subject", "subject-account", site),
                Binding("requester", "requester-account", site,
                    SyntheticWorkflowRole.ItFormalRequester),
                Binding("verifier", "verifier-account", site,
                    SyntheticWorkflowRole.HrVerifier),
                Binding("approver", "approver-account", site,
                    SyntheticWorkflowRole.HrApprover),
                Binding("issuer", "issuer-account", site,
                    SyntheticWorkflowRole.ItIssuer)
            ]);
            directory.SetContainmentState(
                new("subject-account"), SyntheticContainmentState.Clear);
            directory.SetDeviceQualificationState(
                "fake-device", SyntheticDeviceQualificationState.Qualified);
            directory.SetAuthorityAvailabilityState(
                "fake-authority", SyntheticAuthorityAvailabilityState.Available);
            var syntheticClock = new SyntheticSimulationApprovalClock(Now);
            var clock = clockOverride ?? syntheticClock;
            var authority = new SyntheticDispatchAdmissionAuthority(directory, clock);
            var issuer = directory.Resolve(new("issuer-account"));
            var admission = authority.Capture(
                "case-1", 1, "approval-1", 5, 1, "request-reference",
                directory.Resolve(new("requester-account")),
                directory.Resolve(new("verifier-account")),
                directory.Resolve(new("approver-account")),
                issuer,
                new FormalRequestFields(site, new("subject"), new("subject-account"),
                    FormalRequestFields.RequiredIntendedUse, "fake-device",
                    FormalRequestFields.RequiredSlot, new string('a', 64),
                    "fake-authority", "template", "v1"),
                Now.AddMinutes(-1), Now.AddMinutes(14));
            return new(directory, syntheticClock, authority, admission, issuer);
        }

        private static SyntheticIdentityBinding Binding(
            string person, string account, SyntheticSiteId site,
            params SyntheticWorkflowRole[] roles) =>
            new(new(person), new(account), person, site, roles.ToHashSet());
    }
}
