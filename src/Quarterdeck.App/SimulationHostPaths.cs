namespace Quarterdeck.App;

public static class SimulationHostPaths
{
    public static string ResolveStorageRoot(string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Quarterdeck", "V3", "simulation");
        }

        if (!Path.IsPathFullyQualified(configuredRoot))
            throw new InvalidOperationException("simulation_storage_root_must_be_absolute");

        var full = Path.GetFullPath(configuredRoot);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)
            || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("simulation_storage_root_too_broad");
        }
        return full;
    }

    public static void RequireUiAssets(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        var webRoot = Path.Combine(contentRoot, "wwwroot", "css");
        if (!File.Exists(Path.Combine(webRoot, "site.css"))
            || !File.Exists(Path.Combine(webRoot, "case.css")))
        {
            throw new InvalidOperationException("simulation_ui_assets_missing");
        }
    }
}
