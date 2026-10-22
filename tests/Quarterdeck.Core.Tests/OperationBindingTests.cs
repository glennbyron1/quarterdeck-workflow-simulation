using System.Globalization;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Core.Tests;

public sealed class OperationBindingTests
{
    private const string ApprovalId = "11111111-2222-3333-4444-555555555555";
    private const string PublicRequestDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void CanonicalBindingMatchesGoldenVector()
    {
        Assert.Equal(
            "sha256:84f168e7f96501db0f008672cb7bddf079dafad87c5edf89da7e538d416184fe",
            OperationBinding.Compute(Baseline()));
    }

    [Fact]
    public void EquivalentOrderingAndCallerMutationCannotChangeBinding()
    {
        var gates = new List<string> { "gate-z", "gate-a" };
        var input = Baseline(
            policy: Policy(
                new("z-option", SimulationPolicyOptionClassification.NotSelected),
                new("a-option", SimulationPolicyOptionClassification.Required)),
            gates: gates);
        var first = OperationBinding.Compute(input);
        gates.Clear();

        var reordered = Baseline(
            policy: Policy(
                new("a-option", SimulationPolicyOptionClassification.Required),
                new("z-option", SimulationPolicyOptionClassification.NotSelected)),
            gates: ["gate-a", "gate-z"]);

        Assert.Equal(first, OperationBinding.Compute(input));
        Assert.Equal(first, OperationBinding.Compute(reordered));
    }

    [Fact]
    public void EveryAuthorityBearingScalarChangesBinding()
    {
        var baseline = OperationBinding.Compute(Baseline());
        var changes = new[]
        {
            Baseline(caseId: "case-2"),
            Baseline(formalVersion: 2),
            Baseline(approvalId: "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            Baseline(attemptNumber: 2),
            Baseline(fields: Fields() with { SiteId = new("site-2") }),
            Baseline(fields: Fields() with { PersonId = new("person-2") }),
            Baseline(fields: Fields() with { AccountId = new("account-2") }),
            Baseline(fields: Fields() with { FakeDeviceId = "device-2" }),
            Baseline(fields: Fields() with { SyntheticPublicRequestDigest = new string('a', 64) }),
            Baseline(fields: Fields() with { FakeAuthority = "authority-2" }),
            Baseline(fields: Fields() with { Template = "template-2" }),
            Baseline(publicRequestReference: "request-ref-2"),
            Baseline(fields: Fields("policy-v2"), policy: PolicyAtVersion("policy-v2")),
        };

        Assert.All(changes, changed =>
            Assert.NotEqual(baseline, OperationBinding.Compute(changed)));
    }

    [Fact]
    public void ClassificationAndUnresolvedGateDifferencesChangeBinding()
    {
        var baseline = OperationBinding.Compute(Baseline());
        Assert.NotEqual(
            baseline,
            OperationBinding.Compute(Baseline(policy: Policy(
                new SimulationPolicySelection(
                    "physical-piv-only",
                    SimulationPolicyOptionClassification.OptionalSelected)))));
        Assert.NotEqual(
            baseline,
            OperationBinding.Compute(Baseline(gates: ["identity-proofing"])));
        Assert.NotEqual(
            OperationBinding.Compute(Baseline(gates: ["gate-a"])),
            OperationBinding.Compute(Baseline(gates: ["gate-b"])));
    }

    [Fact]
    public void LengthPrefixPreventsDelimiterAmbiguity()
    {
        Assert.NotEqual(
            OperationBinding.Compute(Baseline(caseId: "case-a", publicRequestReference: "b-c")),
            OperationBinding.Compute(Baseline(caseId: "case-a-b", publicRequestReference: "c")));
    }

    [Fact]
    public void NumericFormattingIsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var input = Baseline(formalVersion: 1234, attemptNumber: 5678);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var arabic = OperationBinding.Compute(input);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(arabic, OperationBinding.Compute(input));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("café")]
    [InlineData("case:delimiter")]
    public void MalformedCaseIdsAreRejected(string caseId)
    {
        Assert.Equal(
            "case_id_invalid",
            Assert.Throws<ArgumentException>(() => Baseline(caseId: caseId)).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11111111-2222-3333-4444-55555555555")]
    [InlineData("11111111-2222-3333-4444-555555555555x")]
    [InlineData("11111111-2222-3333-4444-555555555555" + " ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE")]
    public void ApprovalIdMustBeNonemptyCanonicalLowercaseGuidD(string approvalId)
    {
        Assert.Equal(
            "approval_id_invalid",
            Assert.Throws<ArgumentException>(() => Baseline(approvalId: approvalId)).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void VersionsAndAttemptsMustBePositive(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Baseline(formalVersion: value));
        Assert.Throws<ArgumentOutOfRangeException>(() => Baseline(attemptNumber: value));
    }

    [Fact]
    public void DuplicateOrMalformedGateIdsAreRejected()
    {
        Assert.Equal(
            "unresolved_required_gate_id_duplicate (Parameter 'gateIds')",
            Assert.Throws<ArgumentException>(() => Baseline(gates: ["gate-a", "gate-a"])).Message);
        Assert.Equal(
            "unresolved_required_gate_id_invalid",
            Assert.Throws<ArgumentException>(() => Baseline(gates: ["gate a"])).Message);
        Assert.Equal(
            "unresolved_required_gate_id_invalid",
            Assert.Throws<ArgumentException>(() => Baseline(gates: ["gäte"])).Message);
    }

    [Fact]
    public void MalformedReferenceDigestAndPolicyMismatchAreRejected()
    {
        Assert.Equal(
            "public_request_reference_invalid",
            Assert.Throws<ArgumentException>(() => Baseline(publicRequestReference: "ref/value")).Message);
        Assert.Equal(
            "public_request_digest_invalid",
            Assert.Throws<ArgumentException>(() =>
                Baseline(fields: Fields() with { SyntheticPublicRequestDigest = new string('A', 64) })).Message);
        Assert.Equal(
            "policy_snapshot_version_mismatch",
            Assert.Throws<InvalidOperationException>(() =>
                Baseline(fields: Fields("policy-v2"), policy: PolicyAtVersion("policy-v1"))).Message);
    }

    [Fact]
    public void PolicyDuplicateNormalizationIsCanonicalButConflictsAreRejected()
    {
        var duplicate = Policy(
            new("physical-piv-only", SimulationPolicyOptionClassification.Required),
            new("physical-piv-only", SimulationPolicyOptionClassification.Required));
        Assert.Equal(
            OperationBinding.Compute(Baseline()),
            OperationBinding.Compute(Baseline(policy: duplicate)));

        Assert.Equal(
            "policy_option_classification_conflict (Parameter 'selections')",
            Assert.Throws<ArgumentException>(() => Policy(
                new("physical-piv-only", SimulationPolicyOptionClassification.Required),
                new("physical-piv-only", SimulationPolicyOptionClassification.NotSelected))).Message);
    }

    private static OperationBindingInput Baseline(
        string caseId = "case-1",
        long formalVersion = 1,
        string approvalId = ApprovalId,
        long attemptNumber = 1,
        FormalRequestFields? fields = null,
        string publicRequestReference = "request-ref-1",
        SimulationPolicySnapshot? policy = null,
        IEnumerable<string>? gates = null) =>
        new(
            caseId,
            formalVersion,
            approvalId,
            attemptNumber,
            fields ?? Fields(),
            publicRequestReference,
            policy ?? Policy(),
            gates);

    private static FormalRequestFields Fields(string policyVersion = "policy-v1") => new(
        new SyntheticSiteId("test-site"),
        new SyntheticPersonId("subject-person"),
        new SyntheticAccountId("subject-account"),
        FormalRequestFields.RequiredIntendedUse,
        "fake-device-1",
        FormalRequestFields.RequiredSlot,
        PublicRequestDigest,
        "fake-authority-1",
        "template-v1",
        policyVersion);

    private static SimulationPolicySnapshot Policy(
        params SimulationPolicySelection[] selections) =>
        PolicyAtVersion("policy-v1", selections.Length == 0
            ? [new("physical-piv-only", SimulationPolicyOptionClassification.Required)]
            : selections);

    private static SimulationPolicySnapshot PolicyAtVersion(
        string version,
        params SimulationPolicySelection[] selections) =>
        SimulationPolicySnapshot.CreateClassified(
            "quarterdeck-test-policy",
            version,
            selections.Length == 0
                ? [new("physical-piv-only", SimulationPolicyOptionClassification.Required)]
                : selections);
}
