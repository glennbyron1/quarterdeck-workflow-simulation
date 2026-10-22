namespace Quarterdeck.Persistence;

/// <summary>Host-selected synthetic store locations, not an authorization boundary.</summary>
public sealed class SimulationStorePaths
{
    private SimulationStorePaths(string workflow, string journal, string sourceRoot)
    {
        Workflow = workflow;
        Journal = journal;
        SourceRoot = sourceRoot;
    }

    internal string Workflow { get; }
    internal string Journal { get; }
    internal string SourceRoot { get; }
    internal string StoreSetIdentity => string.Concat(
        Workflow, "\0", Journal, "\0", SourceRoot);
    internal string StoreSetFoldedIdentity => StoreSetIdentity.ToUpperInvariant();

    public static SimulationStorePaths Create(string workflow, string journal, string sourceRoot)
    {
        var source = Normalize(sourceRoot);
        var first = Normalize(workflow);
        var second = Normalize(journal);
        if (Same(first, second)
            || Same(Path.GetDirectoryName(first)!, Path.GetDirectoryName(second)!))
        {
            throw new ArgumentException("simulation_stores_not_independent");
        }

        ValidateLocation(first, source);
        ValidateLocation(second, source);
        return new(first, second, source);
    }

    internal void Revalidate()
    {
        ValidateLocation(Workflow, SourceRoot);
        ValidateLocation(Journal, SourceRoot);
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("simulation_store_path_invalid");
        }

        if (OperatingSystem.IsWindows()
            && (value.AsSpan(2).Contains(':')
                || value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment.EndsWith('.') || segment.EndsWith(' '))))
        {
            throw new ArgumentException("simulation_store_path_invalid");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static void ValidateLocation(string path, string sourceRoot)
    {
        var sourcePrefix = Path.EndsInDirectorySeparator(sourceRoot)
            ? sourceRoot : sourceRoot + Path.DirectorySeparatorChar;
        if (Same(path, sourceRoot)
            || path.StartsWith(sourcePrefix, Comparison))
        {
            throw new ArgumentException("simulation_store_under_source");
        }
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || Directory.Exists(path))
        {
            throw new ArgumentException("simulation_store_path_invalid");
        }

        // Detect existing links/junctions at every component. This does not claim
        // to defeat a privileged concurrent filesystem writer or hard-link alias.
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException("simulation_store_reparse_path");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        if (OperatingSystem.IsWindows()
            && new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
        {
            throw new ArgumentException("simulation_store_fixed_disk_required");
        }
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool Same(string first, string second) =>
        string.Equals(first, second, Comparison);

}
