using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Quarterdeck.Core.Simulation;

public static class SealedRequestBinding
{
    private const string Domain = "quarterdeck.sealed-request-binding.v2";

    public static string Compute(
        FormalRequestFields fields,
        SimulationPolicySnapshot policy,
        long operationBindingInputRevision)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(policy);
        fields.Validate();
        if (operationBindingInputRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationBindingInputRevision),
                "Operation binding input revision must be positive.");
        }

        if (!string.Equals(
                fields.PolicyOptionsVersion,
                policy.Version,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("policy_snapshot_version_mismatch");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Domain);
        AppendPair(hash, "directory-revision", operationBindingInputRevision.ToString(CultureInfo.InvariantCulture));
        AppendPair(hash, "site-id", fields.SiteId.Value);
        AppendPair(hash, "person-id", fields.PersonId.Value);
        AppendPair(hash, "account-id", fields.AccountId.Value);
        AppendPair(hash, "intended-use", fields.IntendedUse);
        AppendPair(hash, "fake-device-id", fields.FakeDeviceId);
        AppendPair(hash, "slot", fields.Slot);
        AppendPair(hash, "synthetic-public-request-digest", fields.SyntheticPublicRequestDigest);
        AppendPair(hash, "fake-authority", fields.FakeAuthority);
        AppendPair(hash, "template", fields.Template);
        AppendPair(hash, "policy-options-version", fields.PolicyOptionsVersion);
        AppendPair(hash, "policy-authority-id", policy.AuthorityId);
        AppendPair(hash, "policy-version", policy.Version);
        AppendPair(hash, "policy-content-digest", policy.ContentDigest);
        foreach (var selection in policy.Selections
                     .OrderBy(selection => selection.OptionCode, StringComparer.Ordinal))
        {
            AppendPair(hash, "policy-option-code", selection.OptionCode);
            AppendPair(hash, "policy-option-classification", selection.Classification.ToString());
        }

        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendPair(IncrementalHash hash, string name, string value)
    {
        Append(hash, name);
        Append(hash, value);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
