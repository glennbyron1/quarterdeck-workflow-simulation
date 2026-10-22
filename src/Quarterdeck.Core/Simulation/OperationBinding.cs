using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Quarterdeck.Core.Simulation;

/// <summary>
/// Immutable, already-validated inputs for one fake-dispatch attempt. The
/// operation identifier is deliberately not part of this binding: the approved
/// tuple binds the authority-bearing request, approval and attempt instead.
/// </summary>
public sealed class OperationBindingInput
{
    private const int MaximumReferenceLength = 160;
    private const int MaximumGateIdLength = 80;

    public OperationBindingInput(
        string caseId,
        long formalVersion,
        string approvalId,
        long attemptNumber,
        FormalRequestFields fields,
        string syntheticPublicRequestReference,
        SimulationPolicySnapshot policy,
        IEnumerable<string>? unresolvedRequiredGateIds = null)
    {
        CaseId = RequireToken(caseId, 80, "case_id_invalid");
        FormalVersion = RequirePositive(formalVersion, nameof(formalVersion));
        ApprovalId = RequireGuid(approvalId, "approval_id_invalid");
        AttemptNumber = RequirePositive(attemptNumber, nameof(attemptNumber));
        ArgumentNullException.ThrowIfNull(fields);
        fields.Validate();
        ArgumentNullException.ThrowIfNull(policy);
        if (!string.Equals(fields.PolicyOptionsVersion, policy.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("policy_snapshot_version_mismatch");
        }

        Fields = fields;
        SyntheticPublicRequestReference = RequireToken(
            syntheticPublicRequestReference,
            MaximumReferenceLength,
            "public_request_reference_invalid");
        Policy = SimulationPolicySnapshot.CreateClassified(
            policy.AuthorityId,
            policy.Version,
            policy.Selections);
        UnresolvedRequiredGateIds = NormalizeGateIds(unresolvedRequiredGateIds);
    }

    public string CaseId { get; }
    public long FormalVersion { get; }
    public string ApprovalId { get; }
    public long AttemptNumber { get; }
    public FormalRequestFields Fields { get; }
    public string SyntheticPublicRequestReference { get; }
    public SimulationPolicySnapshot Policy { get; }
    public IReadOnlyList<string> UnresolvedRequiredGateIds { get; }

    private static long RequirePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "operation_binding_number_not_positive");
        }

        return value;
    }

    private static string RequireGuid(string value, string code)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new ArgumentException(code);
        }

        return value;
    }

    private static string RequireToken(string value, int maximumLength, string code)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException(code);
        }

        return value;
    }

    private static IReadOnlyList<string> NormalizeGateIds(IEnumerable<string>? gateIds)
    {
        var normalized = (gateIds ?? [])
            .Select(gateId => RequireToken(
                gateId,
                MaximumGateIdLength,
                "unresolved_required_gate_id_invalid"))
            .ToArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("unresolved_required_gate_id_duplicate", nameof(gateIds));
        }

        Array.Sort(normalized, StringComparer.Ordinal);
        return Array.AsReadOnly(normalized);
    }
}

public static class OperationBinding
{
    private const string Domain = "Quarterdeck.V3.FakeDispatch.v1";

    public static string Compute(OperationBindingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Domain);
        AppendPair(hash, "case-id", input.CaseId);
        AppendPair(hash, "formal-version", input.FormalVersion.ToString(CultureInfo.InvariantCulture));
        AppendPair(hash, "approval-id", input.ApprovalId);
        AppendPair(hash, "attempt-number", input.AttemptNumber.ToString(CultureInfo.InvariantCulture));
        AppendPair(hash, "site-id", input.Fields.SiteId.Value);
        AppendPair(hash, "person-id", input.Fields.PersonId.Value);
        AppendPair(hash, "account-id", input.Fields.AccountId.Value);
        AppendPair(hash, "intended-use", input.Fields.IntendedUse);
        AppendPair(hash, "fake-device-id", input.Fields.FakeDeviceId);
        AppendPair(hash, "slot", input.Fields.Slot);
        AppendPair(hash, "synthetic-public-request-reference", input.SyntheticPublicRequestReference);
        AppendPair(hash, "synthetic-public-request-digest", input.Fields.SyntheticPublicRequestDigest);
        AppendPair(hash, "fake-authority", input.Fields.FakeAuthority);
        AppendPair(hash, "template", input.Fields.Template);
        AppendPair(hash, "policy-options-version", input.Fields.PolicyOptionsVersion);
        foreach (var selection in input.Policy.Selections
                     .OrderBy(selection => selection.OptionCode, StringComparer.Ordinal))
        {
            AppendPair(hash, "policy-option-code", selection.OptionCode);
            AppendPair(hash, "policy-option-classification", selection.Classification.ToString());
        }

        foreach (var gateId in input.UnresolvedRequiredGateIds)
        {
            AppendPair(hash, "unresolved-required-gate-id", gateId);
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
