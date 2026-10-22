using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Quarterdeck.Application;
using Quarterdeck.Presentation;

namespace Quarterdeck.App.Pages.Cases;

public sealed class ReviewModel(ISimulationUseCases source) : PageModel
{
    public SimulationWorkflowProjection? Case { get; private set; }
    public FormalVersionReviewProjection? Review { get; private set; }
    public PolicyApprovalProjection? PolicyApproval { get; private set; }
    public long Version { get; private set; }
    [BindProperty] public long ExpectedCaseRevision { get; set; }
    [BindProperty] public string SiteId { get; set; } = string.Empty;
    [BindProperty] public string PersonId { get; set; } = string.Empty;
    [BindProperty] public string AccountId { get; set; } = string.Empty;
    [BindProperty] public string IntendedUse { get; set; } = string.Empty;
    [BindProperty] public string FakeDeviceId { get; set; } = string.Empty;
    [BindProperty] public string Slot { get; set; } = string.Empty;
    [BindProperty] public string SyntheticPublicRequestDigest { get; set; } = string.Empty;
    [BindProperty] public string FakeAuthority { get; set; } = string.Empty;
    [BindProperty] public string Template { get; set; } = string.Empty;
    [BindProperty] public string PolicyOptionsVersion { get; set; } = string.Empty;
    [TempData] public string? CommandResultCode { get; set; }

    public void OnGet(string caseId, long version)
    {
        Version = version;
        Case = source.ReadCase(caseId);
        Review = source.ReadVersionReview(caseId, version);
        PolicyApproval = source.ReadPolicyApproval(caseId);
        if (Review is null) return;
        ExpectedCaseRevision = Review.CaseRevision;
        var fields = Review.BoundFields.ToDictionary(item => item.Label, item => item.Value);
        SiteId = fields["Site ID"];
        PersonId = fields["Person ID"];
        AccountId = fields["Account ID"];
        IntendedUse = fields["Intended use"];
        FakeDeviceId = fields["Fake device ID"];
        Slot = fields["Slot"];
        SyntheticPublicRequestDigest = fields["Synthetic public-request digest"];
        FakeAuthority = fields["Fake authority"];
        Template = fields["Template"];
        PolicyOptionsVersion = fields["Policy/options version"];
    }

    public IActionResult OnPostVerify(string caseId, long version)
    {
        var result = source.VerifyIdentity(new(caseId, ExpectedCaseRevision, version,
            "TEST-HR-ACCOUNT"));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Review", new { caseId, version });
    }

    public IActionResult OnPostApprove(string caseId, long version)
    {
        var fields = new FormalRequestFieldInput(SiteId, PersonId, AccountId,
            IntendedUse, FakeDeviceId, Slot, SyntheticPublicRequestDigest,
            FakeAuthority, Template, PolicyOptionsVersion);
        var result = source.ApproveExactVersion(new(caseId, ExpectedCaseRevision,
            version, "TEST-HR-ACCOUNT", fields));
        CommandResultCode = result.SafeResultCode;
        return RedirectToPage("/Cases/Review", new { caseId, version });
    }
}
