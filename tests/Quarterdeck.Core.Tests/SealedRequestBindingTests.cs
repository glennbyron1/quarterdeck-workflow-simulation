using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Core.Tests;

public sealed class SealedRequestBindingTests
{
    private const string PublicRequestDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly FormalRequestFields BaselineFields = new(
        new SyntheticSiteId("test-site"),
        new SyntheticPersonId("subject-person"),
        new SyntheticAccountId("subject-account"),
        FormalRequestFields.RequiredIntendedUse,
        "fake-device-1",
        FormalRequestFields.RequiredSlot,
        PublicRequestDigest,
        "fake-authority-1",
        "template-v1",
        "policy-v1");

    private static readonly SimulationPolicySnapshot BaselinePolicy =
        SimulationPolicySnapshot.Create(
            "quarterdeck-test-policy",
            "policy-v1",
            ["physical-piv-only"]);

    [Fact]
    public void CanonicalBindingMatchesGoldenVector()
    {
        Assert.Equal(
            "sha256:0f41a90f58c9a0ccdbb88b47cdfb9375dc2c1849d5d560ec27ac6a9ba45f2f70",
            SealedRequestBinding.Compute(BaselineFields, BaselinePolicy, 1));
    }

    [Fact]
    public void EveryVariableFormalFieldChangesBinding()
    {
        var baseline = Compute(BaselineFields);
        var changes = new[]
        {
            BaselineFields with { SiteId = new("other-site") },
            BaselineFields with { PersonId = new("other-person") },
            BaselineFields with { AccountId = new("other-account") },
            BaselineFields with { FakeDeviceId = "other-device" },
            BaselineFields with { SyntheticPublicRequestDigest = new string('a', 64) },
            BaselineFields with { FakeAuthority = "other-authority" },
            BaselineFields with { Template = "other-template" },
            BaselineFields with { PolicyOptionsVersion = "policy-v2" }
        };

        Assert.All(changes, changed => Assert.NotEqual(baseline, Compute(changed)));
        Assert.Equal(changes.Length, changes.Select(Compute).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CompletePolicyIdentityAndDirectoryRevisionChangeBinding()
    {
        var baseline = Compute(BaselineFields);
        Assert.NotEqual(baseline, SealedRequestBinding.Compute(BaselineFields, BaselinePolicy, 2));
        Assert.NotEqual(baseline, ComputePolicy("other-policy", "policy-v1", ["physical-piv-only"]));
        Assert.NotEqual(
            baseline,
            SealedRequestBinding.Compute(
                BaselineFields with { PolicyOptionsVersion = "policy-v2" },
                SimulationPolicySnapshot.Create("quarterdeck-test-policy", "policy-v2", ["physical-piv-only"]),
                1));
        Assert.NotEqual(baseline, ComputePolicy("quarterdeck-test-policy", "policy-v1", ["physical-piv-only", "pin-required"]));
    }

    [Fact]
    public void SelectedOptionOrderingCannotChangeBinding()
    {
        var first = SimulationPolicySnapshot.Create("policy", "policy-v1", ["z-option", "a-option"]);
        var second = SimulationPolicySnapshot.Create("policy", "policy-v1", ["a-option", "z-option"]);

        Assert.Equal(
            SealedRequestBinding.Compute(BaselineFields, first, 1),
            SealedRequestBinding.Compute(BaselineFields, second, 1));
    }

    [Fact]
    public void RequiredAndOptionalSelectedClassificationsHaveDifferentBindings()
    {
        var required = SimulationPolicySnapshot.CreateClassified(
            "quarterdeck-test-policy",
            "policy-v1",
            [new("physical-piv-only", SimulationPolicyOptionClassification.Required)]);
        var optional = SimulationPolicySnapshot.CreateClassified(
            "quarterdeck-test-policy",
            "policy-v1",
            [new("physical-piv-only", SimulationPolicyOptionClassification.OptionalSelected)]);

        Assert.NotEqual(required.ContentDigest, optional.ContentDigest);
        Assert.NotEqual(
            SealedRequestBinding.Compute(BaselineFields, required, 1),
            SealedRequestBinding.Compute(BaselineFields, optional, 1));
    }

    [Fact]
    public void InvalidInputsAreRejectedBeforeHashing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SealedRequestBinding.Compute(BaselineFields, BaselinePolicy, 0));
        var mismatch = SimulationPolicySnapshot.Create("quarterdeck-test-policy", "policy-v2");
        Assert.Equal(
            "policy_snapshot_version_mismatch",
            Assert.Throws<InvalidOperationException>(() =>
                SealedRequestBinding.Compute(BaselineFields, mismatch, 1)).Message);
        Assert.Equal(
            "intended_use_invalid",
            Assert.Throws<ArgumentException>(() =>
                Compute(BaselineFields with { IntendedUse = "other-use" })).Message);
        Assert.Equal(
            "slot_invalid",
            Assert.Throws<ArgumentException>(() =>
                Compute(BaselineFields with { Slot = "9c" })).Message);
    }

    private static string Compute(FormalRequestFields fields)
    {
        var policy = fields.PolicyOptionsVersion == BaselinePolicy.Version
            ? BaselinePolicy
            : SimulationPolicySnapshot.CreateClassified(
                BaselinePolicy.AuthorityId,
                fields.PolicyOptionsVersion,
                BaselinePolicy.Selections);
        return SealedRequestBinding.Compute(fields, policy, 1);
    }

    private static string ComputePolicy(string authority, string version, string[] options) =>
        SealedRequestBinding.Compute(
            BaselineFields,
            SimulationPolicySnapshot.Create(authority, version, options),
            1);
}
