using System.Runtime.InteropServices;
using System.Text;
using Clc.Configuration;

namespace SsrsPortal.Configuration;

internal static class ProgramDataPath
{
    private static readonly Guid ProgramDataFolderId = new("62AB5D82-FDC1-4DC3-A9DD-070D1D495D97");
    private const uint DoNotVerifyFolderPath = 0x00004000;
    private static readonly object CommonApplicationDataGate = new();

    internal static string Root
    {
        get
        {
            return ResolveLegacyRoot();
        }
    }

    internal static ClcApplicationPaths Paths
    {
        get
        {
            var operatingSystemRoot = ResolveLegacyRoot();
            var paths = WithVerifiedCommonApplicationData(
                operatingSystemRoot,
                () => ClcApplicationPaths.Resolve(Program.ApplicationDirectoryName));
            var packageRoot = Path.GetDirectoryName(paths.ApplicationDataDirectory)
                ?? throw new InvalidOperationException("The CLC application data directory has no operating-system root.");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(operatingSystemRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot)), comparison))
            {
                throw new InvalidOperationException("The operating-system ProgramData resolvers disagree; application paths cannot be established safely.");
            }
            return paths;
        }
    }

    internal static T WithVerifiedCommonApplicationData<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!OperatingSystem.IsWindows()) { return action(); }
        return WithVerifiedCommonApplicationData(ResolveLegacyRoot(), action);
    }

    private static T WithVerifiedCommonApplicationData<T>(string operatingSystemRoot, Func<T> action)
    {
        if (!OperatingSystem.IsWindows()) { return action(); }
        lock (CommonApplicationDataGate)
        {
            var original = Environment.GetEnvironmentVariable("ProgramData");
            try
            {
                // The package resolves CommonApplicationData through .NET's special-folder
                // API, which can depend on ProgramData being present. Pin that lookup to the
                // native, SystemDrive-validated location while the shared resolver runs.
                Environment.SetEnvironmentVariable("ProgramData", operatingSystemRoot);
                return action();
            }
            finally { Environment.SetEnvironmentVariable("ProgramData", original); }
        }
    }

    internal static string ResolveLegacyRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                ? throw new InvalidOperationException("The operating-system CommonApplicationData directory is unavailable.")
                : path;
        }

        var folderId = ProgramDataFolderId;
        var result = SHGetKnownFolderPath(ref folderId, DoNotVerifyFolderPath, IntPtr.Zero, out var buffer);
        if (result < 0 || buffer == IntPtr.Zero)
        { throw new InvalidOperationException("The operating-system ProgramData known folder is unavailable."); }
        try
        {
            var path = Marshal.PtrToStringUni(buffer);
            if (string.IsNullOrWhiteSpace(path))
            { throw new InvalidOperationException("The operating-system ProgramData known folder is unavailable."); }
            var systemDirectory = GetSystemWindowsDirectory();
            RequireSystemDriveAgreement(systemDirectory, Environment.GetEnvironmentVariable("SystemDrive"));
            var normalized = NormalizeWindowsPath(path, systemDirectory);
            return Path.GetFullPath(normalized);
        }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    internal static string NormalizeWindowsPath(string path, string? systemDirectory)
    {
        if (path.Contains("%SystemDrive%", StringComparison.OrdinalIgnoreCase))
        {
            if (systemDirectory is null || systemDirectory.Length < 3 || !char.IsAsciiLetter(systemDirectory[0]) ||
                systemDirectory[1] != ':' || systemDirectory[2] != '\\' || systemDirectory.StartsWith("\\\\", StringComparison.Ordinal))
            { throw new InvalidOperationException("The operating-system Windows volume is unavailable."); }
            path = path.Replace("%SystemDrive%", systemDirectory[..2], StringComparison.OrdinalIgnoreCase);
        }

        if (path.Contains('%') || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        { throw new InvalidOperationException("The operating-system ProgramData known folder is unavailable."); }
        return path;
    }

    internal static void RequireSystemDriveAgreement(string systemDirectory, string? configuredSystemDrive)
    {
        if (systemDirectory.Length < 3 || !char.IsAsciiLetter(systemDirectory[0]) || systemDirectory[1] != ':' || systemDirectory[2] != '\\')
        { throw new InvalidOperationException("The operating-system Windows volume is unavailable."); }
        if (string.IsNullOrWhiteSpace(configuredSystemDrive)) { return; }
        var normalizedDrive = configuredSystemDrive.TrimEnd('\\');
        if (normalizedDrive.Length != 2 || !char.IsAsciiLetter(normalizedDrive[0]) || normalizedDrive[1] != ':' ||
            !string.Equals(normalizedDrive, systemDirectory[..2], StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("The configured Windows system drive disagrees with the operating system."); }
    }

    private static string GetSystemWindowsDirectory()
    {
        var buffer = new StringBuilder(32768);
        var length = GetSystemWindowsDirectoryW(buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
        { throw new InvalidOperationException("The operating-system Windows directory is unavailable."); }
        return buffer.ToString();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetSystemWindowsDirectoryW(StringBuilder buffer, uint size);
}
