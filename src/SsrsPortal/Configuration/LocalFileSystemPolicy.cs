namespace SsrsPortal.Configuration;

internal static class LocalFileSystemPolicy
{
    private static readonly System.Text.RegularExpressions.Regex WindowsDrivePath = new("\\A[A-Za-z]:\\\\",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static void RequireLocalPhysicalPath(string path, string description, Func<string, DriveType>? driveClassifier = null)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        { throw new InvalidOperationException($"{description} must be an absolute local Windows path."); }

        var fullPath = Path.GetFullPath(path);
        var driveRoot = Path.GetPathRoot(fullPath);
        if (driveRoot is null || !WindowsDrivePath.IsMatch(driveRoot))
        { throw new InvalidOperationException($"{description} must use a local fixed drive."); }

        DriveType driveType;
        try { driveType = (driveClassifier ?? (root => new DriveInfo(root).DriveType))(driveRoot); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new InvalidOperationException($"{description} volume type cannot be established."); }
        if (driveType != DriveType.Fixed)
        { throw new InvalidOperationException($"{description} must use a local fixed drive."); }

        // Walk from the volume root toward the leaf. Reject each reparse point before
        // asking the filesystem about any child below it.
        var relative = fullPath[driveRoot.Length..];
        var current = driveRoot;
        CheckExistingComponent(current, description);
        var components = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            var isLeaf = index == components.Length - 1;
            var attributes = ExistingAttributes(current);
            if (attributes is null) { return; }
            if (attributes.Value.HasFlag(FileAttributes.ReparsePoint))
            { throw new InvalidOperationException($"{description} path must be physical and cannot traverse reparse points."); }
            if (!isLeaf && !attributes.Value.HasFlag(FileAttributes.Directory))
            { throw new InvalidOperationException($"{description} path has a non-directory parent."); }
        }
    }

    private static void CheckExistingComponent(string path, string description)
    {
        var attributes = ExistingAttributes(path);
        if (attributes is null || !attributes.Value.HasFlag(FileAttributes.Directory))
        { throw new InvalidOperationException($"{description} volume root is unavailable."); }
        if (attributes.Value.HasFlag(FileAttributes.ReparsePoint))
        { throw new InvalidOperationException($"{description} volume root must be physical."); }
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
