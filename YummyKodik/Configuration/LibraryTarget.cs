namespace YummyKodik.Configuration;

/// <summary>A list and its output scope. Manual titles belong only to the original library.</summary>
internal sealed record LibraryTarget(
    string OutputRootPath,
    bool UseUserListSubscription,
    int YummyUserListId,
    IReadOnlyList<string> Slugs)
{
    internal bool ContainsPath(string? path) => ContainsPath(OutputRootPath, path);

    internal static bool ContainsPath(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim()));
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(fullRoot, fullPath, comparison) ||
                   fullPath.StartsWith(Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    internal static void Validate(IReadOnlyList<LibraryTarget> targets)
    {
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.OutputRootPath) || !Path.IsPathFullyQualified(target.OutputRootPath))
                throw new ArgumentException("Each enabled library needs an absolute output folder.");
            _ = Path.GetFullPath(target.OutputRootPath);
        }

        for (var i = 0; i < targets.Count; i++)
        for (var j = i + 1; j < targets.Count; j++)
        {
            if (targets[i].ContainsPath(targets[j].OutputRootPath) || targets[j].ContainsPath(targets[i].OutputRootPath))
                throw new ArgumentException("Library output folders must be separate: they cannot be equal or nested.");
        }
    }
}
