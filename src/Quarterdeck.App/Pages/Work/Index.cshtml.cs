using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Quarterdeck.Presentation;
using Quarterdeck.Application;

namespace Quarterdeck.App.Pages.Work;

public sealed class IndexModel(ISimulationUseCases source) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Site { get; set; }

    [BindProperty(SupportsGet = true)]
    public WorkflowNextRole? Role { get; set; }
    public IReadOnlyList<SimulationWorkflowProjection> Items { get; private set; } = [];
    public IReadOnlyList<NameChangeView> NameChanges { get; private set; } = [];

    public void OnGet()
    {
        Items = source.ReadWorkQueue(new(Role, Site));
        NameChanges = source.ReadNameChanges().Where(r => r.CompletedBy is null
            && (string.IsNullOrEmpty(Site) || r.SiteId == Site)
            && (Role is null || Role == WorkflowNextRole.ItFormalRequester)).ToArray();
    }

    public string GroupName(WorkflowQueueGroup group) => group switch
    {
        WorkflowQueueGroup.NeedsAttention => "Needs attention / recovery",
        WorkflowQueueGroup.WaitingForIt => "Waiting for IT",
        WorkflowQueueGroup.HrIntake => "HR intake",
        WorkflowQueueGroup.HrVerificationAndApproval => "HR verification and approval",
        WorkflowQueueGroup.ItSimulatedIssuance => "IT simulated issuance",
        WorkflowQueueGroup.Complete => "Complete simulations",
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "queue_group_unmapped")
    };

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

    public string PersonName(string personReference) =>
        SyntheticPersonLabel.ForDisplay(personReference);
}
