using System.Security;
using Clc.Configuration;

namespace SsrsPortal.Configuration;

internal static class ApplicationPathCommandLine
{
    internal static bool TryHandle(string[] args, out int exitCode)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        if (args.Length != 1 || !string.Equals(args[0], "--clc-appdata-directory", StringComparison.Ordinal))
        {
            if (!ClcConfigurationCommandLine.TryHandle(args, Program.ApplicationDirectoryName, out exitCode,
                standardOutput, standardError))
            {
                exitCode = 0;
                return false;
            }
            Console.Out.Write(standardOutput.ToString());
            Console.Error.Write(standardError.ToString());
            return true;
        }

        var packageExitCode = 0;
        bool handled;
        try
        {
            handled = ProgramDataPath.WithVerifiedCommonApplicationData(() =>
                ClcConfigurationCommandLine.TryHandle(args, Program.ApplicationDirectoryName, out packageExitCode,
                    standardOutput, standardError));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or NotSupportedException or SecurityException or System.Runtime.InteropServices.ExternalException)
        {
            Console.Error.WriteLine("Unable to resolve the application's CommonApplicationData directory.");
            exitCode = 1;
            return true;
        }
        if (!handled)
        {
            exitCode = 0;
            return false;
        }
        exitCode = packageExitCode;

        if (exitCode != 0)
        {
            Console.Out.Write(standardOutput.ToString());
            Console.Error.Write(standardError.ToString());
            return true;
        }

        try
        {
            var paths = ProgramDataPath.Paths;
            var reported = standardOutput.ToString().TrimEnd('\r', '\n');
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.IsPathFullyQualified(reported) ||
                !string.Equals(Path.GetFullPath(reported), paths.ApplicationDataDirectory, comparison))
            {
                throw new InvalidOperationException();
            }
            Console.Out.Write(standardOutput.ToString());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or NotSupportedException or SecurityException or System.Runtime.InteropServices.ExternalException)
        {
            Console.Error.WriteLine("Unable to resolve the application's CommonApplicationData directory.");
            exitCode = 1;
        }

        return true;
    }
}
