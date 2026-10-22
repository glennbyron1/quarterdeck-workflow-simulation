using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using Quarterdeck.Application;
using Quarterdeck.Presentation;

namespace Quarterdeck.App.Pages.Cases;

public sealed class DetailsModel(ISimulationUseCases source) : PageModel
{
    [BindProperty]
    public long ExpectedCaseRevision { get; set; }
    [BindProperty]
    [RegularExpression("^TEST-[A-Z0-9][A-Z0-9-]*$")]
    public string ExactAccountId { get; set; } = string.Empty;
    [BindProperty]
    [RegularExpression("^TEST-[A-Z0-9][A-Z0-9-]*$")]
    public string SimulationAccountId { get; set; } = string.Empty;
    [BindProperty]
    [RegularExpression("^(account_created|account_corrected)$")]
    public string? CorrectionReason { get; set; }
    [BindProperty, RegularExpression("^TEST-[A-Z0-9][A-Z0-9-]*$")] public string FormalAccountId { get; set; } = "TEST-SUBJECT-ACCOUNT";
    [BindProperty, RegularExpression("^FAKE-DEVICE-[0-9]{3}$")] public string FakeDeviceId { get; set; } = "FAKE-DEVICE-001";
    [BindProperty, RegularExpression("^[0-9a-f]{64}$")] public string SyntheticPublicRequestDigest { get; set; } = new('a', 64);
    [BindProperty]
    [RegularExpression("^REQ-[A-Z0-9][A-Z0-9-]*$")]
    public string SyntheticPublicRequestReference { get; set; } = string.Empty;
    [BindProperty]
    [RegularExpression("^[0-9a-fA-F-]{36}$")]
    public string RecoveryFindingId { get; set; } = string.Empty;
    [BindProperty]
    [RegularExpression("^terminal_evidence_confirmed$")]
    public string RecoveryReasonCode { get; set; } = string.Empty;
    [BindProperty, StringLength(500)] public string? HrCorrectionComment { get; set; }
    [BindProperty, StringLength(80), RegularExpression("^TEST-[A-Z0-9][A-Z0-9-]*$")]
    public string? ProposedUsername { get; set; }
    [TempData]
    public string? CommandResultCode { get; set; }
    public string ResultMessage => CommandResultCode switch
    {
        "returned_to_hr" => "Sent back to HR. Your reason is saved below.",
        "hr_resubmitted" => "Resubmitted to IT. IT must confirm the exact account again.",
        "account_resolution_recorded" => "Account confirmed. You can prepare the formal request below.",
        "exact_account_unresolved" => "This account is not an enabled match for this person and site. Check the ID, or use Simulation fixture setup for a new test person.",
        "case_revision_stale" => "This case changed. Review its current state and try the available action again.",
        "actor_not_authorized" => "The current operator cannot perform this action.",
        "command_input_invalid" => "Check the fields in the form you submitted. Nothing was confirmed.",
        _ => "Command result:"
    };
    public SimulationWorkflowProjection? Case { get; private set; }
    public PolicyApprovalProjection? PolicyApproval { get; private set; }
    public FakeIssuanceProjection? FakeIssuance { get; private set; }
    public IReadOnlyList<AuditEventProjection> AuditTrail { get; private set; } = [];
    public IReadOnlyList<HrCorrectionView> Corrections { get; private set; } = [];
    public void OnGet(string caseId)
    {
        Case = source.ReadCase(caseId);
        if (Case is not null)
        {
            var suggestion = $"{Case.PersonReference}-ACCOUNT";
            if (suggestion.Length <= 80)
            {
                ExactAccountId = suggestion;
                SimulationAccountId = suggestion;
            }
            FormalAccountId = Case.ResolvedAccountId ?? string.Empty;
        }
        PolicyApproval = source.ReadPolicyApproval(caseId);
        FakeIssuance = source.ReadFakeIssuance(caseId);
        AuditTrail = source.ReadAuditTrail(caseId);
        Corrections = source.ReadHrCorrections(caseId);
        var suggested = Corrections.LastOrDefault(r => r.Action == "hr_resubmitted")?.ProposedUsername;
        if (suggested is not null && Case?.Condition == WorkflowPresentationCondition.WaitingForIt)
            ExactAccountId = suggested;
    }

    public IActionResult OnPostReturnToHr(string caseId)
    {
        if (!ValidForm(nameof(ExpectedCaseRevision),nameof(HrCorrectionComment))
            || string.IsNullOrWhiteSpace(HrCorrectionComment)) return InvalidInput(caseId);
        CommandResultCode=source.ReturnToHr(new(caseId,ExpectedCaseRevision,"TEST-IT-ACCOUNT",HrCorrectionComment)).SafeResultCode;
        return RedirectToPage("/Cases/Details",new {caseId});
    }
    public IActionResult OnPostResubmitToIt(string caseId)
    {
        if (!ValidForm(nameof(ExpectedCaseRevision),nameof(HrCorrectionComment),nameof(ProposedUsername))
            || string.IsNullOrWhiteSpace(HrCorrectionComment)) return InvalidInput(caseId);
        CommandResultCode=source.ResubmitToIt(new(caseId,ExpectedCaseRevision,"TEST-HR-ACCOUNT",HrCorrectionComment,
            string.IsNullOrWhiteSpace(ProposedUsername) ? null : ProposedUsername.Trim())).SafeResultCode;
        return RedirectToPage("/Cases/Details",new {caseId});
    }

    public IActionResult OnPostStartFakeIssuance(string caseId, long formalVersion)
    {
        if (!ValidForm(nameof(ExpectedCaseRevision), nameof(SyntheticPublicRequestReference))) return InvalidInput(caseId);
        var result = source.StartFakeIssuance(new(caseId, ExpectedCaseRevision,
            formalVersion, "TEST-IT-ACCOUNT", SyntheticPublicRequestReference));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Details", new { caseId });
    }

    public IActionResult OnPostObserveFakeIssuance(string caseId, string operationId)
    {
        var result = source.ObserveFakeIssuance(new(caseId, operationId,
            "TEST-IT-ACCOUNT"));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Details", new { caseId });
    }
    public IActionResult OnPostReconcileFakeIssuance(string caseId, string operationId)
    {
        if (!ValidForm(nameof(RecoveryFindingId), nameof(RecoveryReasonCode))) return InvalidInput(caseId);
        var result = source.ReconcileFakeIssuance(new(caseId, RecoveryFindingId,
            operationId, "TEST-IT-ACCOUNT", RecoveryReasonCode));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Details", new { caseId });
    }

    public IActionResult OnPostResolve(string caseId)
    {
        if (!ValidForm(nameof(ExpectedCaseRevision), nameof(ExactAccountId), nameof(CorrectionReason))) return InvalidInput(caseId);
        var result = source.ResolveWaitingForIt(new(caseId, ExpectedCaseRevision,
            ExactAccountId, "TEST-IT-ACCOUNT", CorrectionReason));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Details", new { caseId });
    }
    public IActionResult OnPostRegisterSimulationAccount(string caseId)
    {
        if (!ValidForm(nameof(ExpectedCaseRevision), nameof(SimulationAccountId))) return InvalidInput(caseId);
        var result = source.RegisterSimulationAccount(new(caseId, ExpectedCaseRevision,
            SimulationAccountId, "TEST-IT-ACCOUNT"));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Details", new { caseId });
    }
    public IActionResult OnPostSealFormalRequest(string caseId)
    {
        if (!ValidForm(nameof(ExpectedCaseRevision), nameof(FormalAccountId), nameof(FakeDeviceId), nameof(SyntheticPublicRequestDigest))) return InvalidInput(caseId);
        var current = source.ReadCase(caseId);
        if (current is null) return InvalidInput(caseId);
        var fields = new FormalRequestFieldInput(current.SiteId, current.PersonReference,
            FormalAccountId, "PIV-authentication-simulation", FakeDeviceId, "9a",
            SyntheticPublicRequestDigest, "quarterdeck-fake-authority",
            "fake-piv-authentication", "simulation-policy-v1");
        var result = source.SealFormalRequest(new(caseId, ExpectedCaseRevision,
            "TEST-IT-ACCOUNT", fields));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Details", new { caseId });
    }
    public IActionResult OnPostObserveDirectoryGate(string caseId, long formalVersion)
    {
        var result = source.ObserveMandatoryGate(new(caseId, ExpectedCaseRevision,
            formalVersion, "TEST-IT-ACCOUNT", "directory_ready"));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Details", new { caseId });
    }
    private IActionResult InvalidInput(string caseId)
    {
        CommandResultCode = "command_input_invalid";
        return RedirectToPage("/Cases/Details", new { caseId });
    }
    private bool ValidForm(params string[] fields) => ModelState
        .Where(entry => fields.Contains(entry.Key, StringComparer.Ordinal))
        .All(entry => entry.Value?.Errors.Count == 0);
    public string RoleName(WorkflowNextRole role) => role switch
    {
        WorkflowNextRole.HrRequester => "HR requester",
        WorkflowNextRole.ItFormalRequester => "IT formal requester",
        WorkflowNextRole.HrVerifier => "HR verifier",
        WorkflowNextRole.HrApprover => "HR approver",
        WorkflowNextRole.ItIssuer => "IT issuer",
        WorkflowNextRole.None => "No further action",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "next_role_unmapped")
    };
}
