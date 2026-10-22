using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Quarterdeck.Application;

namespace Quarterdeck.App.Pages.Requests;

public sealed class NewUserModel(ISimulationUseCases source) : PageModel
{
    private const string SyntheticHrActor = "TEST-HR-ACCOUNT";

    [BindProperty, Required, StringLength(160), Display(Name = "Synthetic display name")]
    public string SyntheticDisplayName { get; set; } = string.Empty;

    [BindProperty, Required, StringLength(160),
     RegularExpression("^TEST-[A-Z0-9][A-Z0-9-]*$",
         ErrorMessage = "Use an uppercase synthetic person ID beginning TEST-."),
     Display(Name = "Synthetic person name/reference")]
    public string PersonId { get; set; } = string.Empty;

    [BindProperty, Required, StringLength(40), Display(Name = "Department")]
    public string DepartmentCode { get; set; } = string.Empty;

    [BindProperty, StringLength(80), Display(Name = "Other department (when selected)")]
    public string? OtherDepartmentLabel { get; set; }

    [BindProperty, Required, StringLength(160), Display(Name = "Simulated site")]
    public string SiteId { get; set; } = "SITE-A";

    [BindProperty, StringLength(160),
     RegularExpression("^$|^TEST-[A-Z0-9][A-Z0-9-]*$",
         ErrorMessage = "Use an uppercase synthetic account ID beginning TEST-."),
     Display(Name = "Proposed synthetic account ID (optional)")]
    public string? CandidateAccountId { get; set; }

    [BindProperty, StringLength(100), Display(Name = "Job title (optional)")]
    public string? JobTitle { get; set; }

    [BindProperty, StringLength(80), Display(Name = "Employee reference (optional)")]
    public string? EmployeeReference { get; set; }

    public IReadOnlyList<SelectListItem> Departments { get; private set; } = [];

    public string? SafeResultCode { get; private set; }

    public void OnGet() => LoadDepartments();

    public IActionResult OnPost()
    {
        if (!string.Equals(SiteId, "SITE-A", StringComparison.Ordinal))
            ModelState.AddModelError(nameof(SiteId), "Only the configured synthetic site is available.");
        var other = string.Equals(DepartmentCode, "OTHER", StringComparison.Ordinal);
        if (other && string.IsNullOrWhiteSpace(OtherDepartmentLabel))
            ModelState.AddModelError(nameof(OtherDepartmentLabel), "Enter the temporary department label.");
        if (!other && !string.IsNullOrWhiteSpace(OtherDepartmentLabel))
            ModelState.AddModelError(nameof(OtherDepartmentLabel), "Use this field only when Department is Other.");
        if (!ModelState.IsValid)
        {
            LoadDepartments();
            return Page();
        }

        var candidates = string.IsNullOrWhiteSpace(CandidateAccountId)
            ? Array.Empty<string>()
            : new[] { CandidateAccountId.Trim() };
        var result = source.CreateIntake(new(
            string.Empty, PersonId.Trim(), SiteId, SyntheticHrActor, candidates,
            SyntheticDisplayName.Trim(), DepartmentCode, OtherDepartmentLabel?.Trim(),
            JobTitle?.Trim(), EmployeeReference?.Trim()));
        if (result.Succeeded)
            return RedirectToPage("/Cases/Details", new { caseId = result.CaseId });

        SafeResultCode = result.SafeResultCode;
        LoadDepartments();
        ModelState.AddModelError(string.Empty,
            $"The synthetic request was not recorded ({result.SafeResultCode}).");
        return Page();
    }

    private void LoadDepartments() => Departments = source.ReadDepartments()
        .Select(value => new SelectListItem(value.DisplayName, value.Code))
        .ToArray();
}
