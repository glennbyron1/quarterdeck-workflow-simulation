namespace Quarterdeck.Persistence.Tests;

public sealed class RazorAccessibilityContractTests
{
    [Fact]
    public void Shell_has_language_skip_target_landmarks_and_non_color_simulation_text()
    {
        var layout = Read("src", "Quarterdeck.App", "Pages", "Shared", "_Layout.cshtml");
        Assert.Contains("<html lang=\"en\">", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"skip\" href=\"#main\"", layout, StringComparison.Ordinal);
        Assert.Contains("<main id=\"main\" tabindex=\"-1\">", layout, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Primary\"", layout, StringComparison.Ordinal);
        Assert.Contains("SIMULATION ONLY", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("tabindex=\"1\"", layout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V2_visual_shell_is_mapped_to_the_v3_simulation_boundary()
    {
        var layout = Read("src", "Quarterdeck.App", "Pages", "Shared", "_Layout.cshtml");
        var home = Read("src", "Quarterdeck.App", "Pages", "Index.cshtml");
        var model = Read("src", "Quarterdeck.App", "Pages", "Index.cshtml.cs");

        Assert.Contains("brand-mark", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"nav-item\"", layout, StringComparison.Ordinal);
        Assert.Contains("Request new user", layout, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Requests/NewUser\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"mobile-nav\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"station-strip\"", layout, StringComparison.Ordinal);
        Assert.Contains("~/css/v2-visual-baseline.css", layout, StringComparison.Ordinal);
        Assert.Contains("~/css/v3-visual-adapter.css", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Signed in as", layout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows authority", layout, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("class=\"signal-board\"", home, StringComparison.Ordinal);
        Assert.Contains("class=\"bridge-cluster\"", home, StringComparison.Ordinal);
        Assert.Contains("class=\"boundary-strip\"", home, StringComparison.Ordinal);
        Assert.Contains("Fake only", home, StringComparison.Ordinal);
        Assert.Contains("source.ReadWorkQueue(new())", model, StringComparison.Ordinal);
        Assert.DoesNotContain("RedirectToPage", model, StringComparison.Ordinal);
    }

    [Fact]
    public void New_user_intake_is_labeled_simulated_and_cannot_claim_ad_authority()
    {
        var page = Read("src", "Quarterdeck.App", "Pages", "Requests", "NewUser.cshtml");
        var model = Read("src", "Quarterdeck.App", "Pages", "Requests", "NewUser.cshtml.cs");

        Assert.Contains("SIM-QD-000001", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<label asp-for=\"CaseId\">", page, StringComparison.Ordinal);
        Assert.Contains("<label asp-for=\"SyntheticDisplayName\">", page, StringComparison.Ordinal);
        Assert.Contains("<label asp-for=\"PersonId\">", page, StringComparison.Ordinal);
        Assert.Contains("<label asp-for=\"DepartmentCode\">", page, StringComparison.Ordinal);
        Assert.Contains("<label asp-for=\"SiteId\">", page, StringComparison.Ordinal);
        Assert.Contains("<label asp-for=\"CandidateAccountId\">", page, StringComparison.Ordinal);
        Assert.Contains("Waiting for IT", page, StringComparison.Ordinal);
        Assert.Contains("outside Quarterdeck", page, StringComparison.Ordinal);
        Assert.Contains("Never enter a real name", page, StringComparison.Ordinal);
        Assert.Contains("source.CreateIntake", model, StringComparison.Ordinal);
        Assert.Contains("SyntheticHrActor", model, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateUser", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DirectoryEntry", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.DirectoryServices", model, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Proposed_account_binding_is_genuinely_optional_at_the_server_boundary()
    {
        var property = typeof(Quarterdeck.App.Pages.Requests.NewUserModel)
            .GetProperty(nameof(Quarterdeck.App.Pages.Requests.NewUserModel.CandidateAccountId));
        Assert.NotNull(property);

        var nullability = new System.Reflection.NullabilityInfoContext().Create(property);
        Assert.Equal(System.Reflection.NullabilityState.Nullable, nullability.WriteState);
        Assert.DoesNotContain(property.GetCustomAttributes(inherit: true),
            attribute => attribute is System.ComponentModel.DataAnnotations.RequiredAttribute);
    }

    [Fact]
    public void Work_queue_and_case_workspace_use_v2_hierarchy_without_generic_mutation()
    {
        var queue = Read("src", "Quarterdeck.App", "Pages", "Work", "Index.cshtml");
        var queueModel = Read("src", "Quarterdeck.App", "Pages", "Work", "Index.cshtml.cs");
        var details = Read("src", "Quarterdeck.App", "Pages", "Cases", "Details.cshtml");

        Assert.Contains("class=\"breadcrumb\"", queue, StringComparison.Ordinal);
        Assert.Contains("Request new user", queue, StringComparison.Ordinal);
        Assert.Contains("Open lifecycle case", queue, StringComparison.Ordinal);
        Assert.Contains("Synthetic person", queue, StringComparison.Ordinal);
        Assert.Contains("Person reference", queue, StringComparison.Ordinal);
        Assert.Contains("Author-produced simulation view", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPostCreateIntake", queueModel, StringComparison.Ordinal);

        Assert.Contains("Lifecycle workspace", details, StringComparison.Ordinal);
        Assert.Contains("class=\"identity\"", details, StringComparison.Ordinal);
        Assert.Contains("1 · Summary and action", details, StringComparison.Ordinal);
        Assert.Contains("2 · Recorded request", details, StringComparison.Ordinal);
        Assert.Contains("3 · Case history", details, StringComparison.Ordinal);
        Assert.Contains("4 · Evidence", details, StringComparison.Ordinal);
        Assert.Contains("Author-produced simulation evidence only", details,
            StringComparison.Ordinal);
        Assert.DoesNotContain("asp-page-handler=\"Advance\"", details,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Synthetic_person_reference_has_a_readable_non_authoritative_queue_label()
    {
        Assert.Equal("Jordan Lee", Quarterdeck.App.SyntheticPersonLabel.ForDisplay("TEST-JORDAN-LEE"));
        Assert.Equal("Person 1042", Quarterdeck.App.SyntheticPersonLabel.ForDisplay("TEST-PERSON-1042"));
        Assert.Equal("Unknown synthetic person", Quarterdeck.App.SyntheticPersonLabel.ForDisplay(" "));
    }

    [Fact]
    public void Exact_review_and_help_preserve_authority_privacy_and_recovery_boundaries()
    {
        var review = Read("src", "Quarterdeck.App", "Pages", "Cases", "Review.cshtml");
        var help = Read("src", "Quarterdeck.App", "Pages", "Help.cshtml");
        var layout = Read("src", "Quarterdeck.App", "Pages", "Shared", "_Layout.cshtml");

        Assert.Contains("1 · Bound request", review, StringComparison.Ordinal);
        Assert.Contains("2 · Decision", review, StringComparison.Ordinal);
        Assert.Contains("revision @Model.Case.CaseRevision", review, StringComparison.Ordinal);
        Assert.Contains("all ten fields", review, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authoritative simulated time", review, StringComparison.Ordinal);

        Assert.Contains("How this simulation works", help, StringComparison.Ordinal);
        Assert.Contains("IT creates the account with existing site tools", help,
            StringComparison.Ordinal);
        Assert.Contains("blind retry is blocked", help, StringComparison.Ordinal);
        Assert.Contains("No PIN, PUK, password", help, StringComparison.Ordinal);
        Assert.Contains("not a browser choice", help, StringComparison.Ordinal);
        Assert.Contains("asp-page=\"/Help\"", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void Simulation_host_is_loopback_only_and_persisted_inputs_are_allowlisted()
    {
        var program = Read("src", "Quarterdeck.App", "Program.cs");
        var hostFactory = Read("src", "Quarterdeck.App", "QuarterdeckRazorHostFactory.cs");
        var layout = Read("src", "Quarterdeck.App", "Pages", "Shared", "_Layout.cshtml");
        var intake = Read("src", "Quarterdeck.App", "Pages", "Requests", "NewUser.cshtml.cs");
        var details = Read("src", "Quarterdeck.App", "Pages", "Cases", "Details.cshtml.cs");

        Assert.Contains("QuarterdeckRazorHostFactory.CreateBuilder(args)", program,
            StringComparison.Ordinal);
        Assert.Contains("SimulationHostComposition.Configure(builder)", program,
            StringComparison.Ordinal);
        Assert.Contains("UseUrls(\"http://127.0.0.1:5080\")", hostFactory,
            StringComparison.Ordinal);
        Assert.Contains("IPAddress.IsLoopback", hostFactory, StringComparison.Ordinal);
        Assert.Contains("simulation_loopback_required", hostFactory, StringComparison.Ordinal);
        Assert.Contains("Scenario actors", layout, StringComparison.Ordinal);
        Assert.Contains("Not authenticated people", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("public string CaseId", intake, StringComparison.Ordinal);
        Assert.Contains("^TEST-", intake, StringComparison.Ordinal);
        Assert.Contains("^(account_created|account_corrected)$", details,
            StringComparison.Ordinal);
        Assert.Contains("^terminal_evidence_confirmed$", details,
            StringComparison.Ordinal);
        Assert.Contains("if (!ValidForm(nameof(ExpectedCaseRevision), nameof(ExactAccountId), nameof(CorrectionReason))) return InvalidInput(caseId);", details,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Forms_are_labeled_and_pages_avoid_inline_behavior()
    {
        var pages = Root().GetDirectories("Pages", SearchOption.AllDirectories)
            .Single(value => value.Parent?.Name == "Quarterdeck.App");
        var details = File.ReadAllText(Path.Combine(pages.FullName, "Cases", "Details.cshtml"));
        Assert.Contains("<label asp-for=\"ExactAccountId\">", details, StringComparison.Ordinal);
        Assert.Contains("<label asp-for=\"RecoveryFindingId\">", details, StringComparison.Ordinal);
        foreach (var page in pages.EnumerateFiles("*.cshtml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(page.FullName);
            Assert.DoesNotContain("onclick=", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<style", markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Revision_bound_forms_render_the_authoritative_case_revision()
    {
        var details = Read("src", "Quarterdeck.App", "Pages", "Cases", "Details.cshtml");
        var review = Read("src", "Quarterdeck.App", "Pages", "Cases", "Review.cshtml");

        Assert.DoesNotContain("asp-for=\"ExpectedCaseRevision\"", details,
            StringComparison.Ordinal);
        Assert.DoesNotContain("asp-for=\"ExpectedCaseRevision\"", review,
            StringComparison.Ordinal);
        Assert.Equal(7, Count(details,
            "name=\"ExpectedCaseRevision\" value=\"@Model.Case.CaseRevision\""));
        Assert.Equal(2, Count(review,
            "name=\"ExpectedCaseRevision\" value=\"@Model.Case.CaseRevision\""));
    }

    [Fact]
    public void Styles_preserve_focus_reflow_reduced_motion_forced_colors_and_state_text()
    {
        var css = Read("src", "Quarterdeck.App", "wwwroot", "css", "site.css")
            + Read("src", "Quarterdeck.App", "wwwroot", "css", "v2-visual-baseline.css")
            + Read("src", "Quarterdeck.App", "wwwroot", "css", "case.css")
            + Read("src", "Quarterdeck.App", "wwwroot", "css", "v3-visual-adapter.css");
        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("min-width:0", css, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion:reduce", css, StringComparison.Ordinal);
        Assert.Contains("forced-colors:active", css, StringComparison.Ordinal);
        foreach (var state in Enum.GetValues<Quarterdeck.Presentation.AuditStateKind>())
            Assert.Contains(".audit-" + state.ToString().ToLowerInvariant(), css,
                StringComparison.Ordinal);
    }

    [Fact]
    public void V2_visual_baseline_is_the_reviewed_byte_exact_stylesheet()
    {
        var path = Path.Combine(Root().FullName, "src", "Quarterdeck.App", "wwwroot",
            "css", "v2-visual-baseline.css");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(path)));

        Assert.Equal("D62CD16A9FDBD30F7922EB96940D5C9220FA85700A503C4944F82E511F385A4A",
            digest);
    }

    [Fact]
    public void Home_uses_the_V2_station_gauges_with_V3_simulation_truth()
    {
        var home = Read("src", "Quarterdeck.App", "Pages", "Index.cshtml");

        Assert.Equal(3, Count(home, "class=\"status-ring"));
        Assert.Contains("<span>Fake only</span>", home, StringComparison.Ordinal);
        Assert.Contains("<small>Authority</small>", home, StringComparison.Ordinal);
        Assert.Contains("Request new user", home, StringComparison.Ordinal);
        Assert.Contains("No live AD, CA, PIV, broker, credential or Windows-logon operation",
            home, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_storage_root_must_be_explicitly_absolute_and_not_a_volume_root()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Quarterdeck.App.SimulationHostPaths.ResolveStorageRoot("relative-evidence"));
        Assert.Throws<InvalidOperationException>(() =>
            Quarterdeck.App.SimulationHostPaths.ResolveStorageRoot(
                Path.GetPathRoot(Path.GetFullPath("."))));

        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "quarterdeck-phase1-evidence"));
        Assert.Equal(expected,
            Quarterdeck.App.SimulationHostPaths.ResolveStorageRoot(expected));
    }

    [Fact]
    public void Ui_asset_preflight_rejects_an_unstyled_content_root()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));
        var error = Assert.Throws<InvalidOperationException>(() =>
            Quarterdeck.App.SimulationHostPaths.RequireUiAssets(missing));
        Assert.Equal("simulation_ui_assets_missing", error.Message);

        Quarterdeck.App.SimulationHostPaths.RequireUiAssets(
            Path.Combine(Root().FullName, "src", "Quarterdeck.App"));
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root().FullName, .. parts]));

    private static int Count(string value, string marker) =>
        (value.Length - value.Replace(marker, string.Empty, StringComparison.Ordinal).Length)
        / marker.Length;

    private static DirectoryInfo Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Quarterdeck.Simulation.slnx")))
            directory = directory.Parent;
        return directory ?? throw new InvalidOperationException("repository_root_not_found");
    }
}
