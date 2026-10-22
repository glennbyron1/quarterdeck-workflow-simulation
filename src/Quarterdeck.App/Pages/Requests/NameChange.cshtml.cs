using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Quarterdeck.Application;
using Quarterdeck.Presentation;

namespace Quarterdeck.App.Pages.Requests;

public sealed class NameChangeModel(ISimulationUseCases source) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? CaseId { get; set; }
    [BindProperty] public long ExpectedRevision { get; set; }
    [BindProperty, Required, StringLength(160)] public string RequestedName { get; set; } = string.Empty;
    [BindProperty, StringLength(500)] public string? Comment { get; set; }
    [TempData] public string? Result { get; set; }
    public IReadOnlyList<SimulationWorkflowProjection> People { get; private set; } = [];
    public IReadOnlyList<NameChangeView> Requests { get; private set; } = [];
    public SimulationWorkflowProjection? Selected { get; private set; }
    public void OnGet()
    {
        People = source.ReadWorkQueue(new()).Where(p => p.ResolvedAccountId is not null).ToArray();
        Selected = People.SingleOrDefault(p => p.CaseId == CaseId);
        ExpectedRevision = Selected?.CaseRevision ?? 0;
        Requests = source.ReadNameChanges();
    }
    public IActionResult OnPost()
    {
        if (!ModelState.IsValid) { OnGet(); return Page(); }
        Result = source.RequestNameChange(new(CaseId ?? string.Empty,ExpectedRevision,
            RequestedName,string.IsNullOrWhiteSpace(Comment) ? null : Comment,"TEST-HR-ACCOUNT")).SafeResultCode;
        return RedirectToPage(new { CaseId });
    }
    public IActionResult OnPostComplete(string requestId, string completionComment)
    {
        Result = source.CompleteNameChange(requestId,"TEST-IT-ACCOUNT",completionComment).SafeResultCode;
        return RedirectToPage();
    }
}
