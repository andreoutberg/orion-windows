using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Orion
{
    internal static class PrivateBrowsing
    {
        private const string EnvironmentFlag = "ORION_PRIVATE_SESSION";
        private const string DirectoryPrefix = "orion-searchplus-private-";

        public static bool IsActive => OrionBackend.IsPrivateSession;

        public static void Initialize()
        {
            if (Environment.GetEnvironmentVariable(EnvironmentFlag) != "1")
                return;

            string path = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
            if (!IsPrivateDirectory(path) || Directory.EnumerateFileSystemEntries(path).Any()
                || !OrionBackend.ConfigurePrivateSession())
            {
                throw new InvalidOperationException("Orion could not start with isolated private storage.");
            }

            OrionBackend.SetAppSetting("settings::general::app_opens_with", ((int)AppOpensWith.NewWindow).ToString());
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RemovePrivateDirectory(path);
        }

        public static void OpenWindow()
        {
            string directory = Directory.CreateTempSubdirectory(DirectoryPrefix).FullName;
            try
            {
                var launch = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Orion.exe"))
                {
                    UseShellExecute = false,
                };
                launch.ArgumentList.Add("--engine=blink");
                launch.Environment[EnvironmentFlag] = "1";
                launch.Environment["LOCALAPPDATA"] = directory;
                launch.Environment["ORION_DESIGN_FIRST_RUN"] = "1";
                using Process process = Process.Start(launch)
                    ?? throw new InvalidOperationException("Orion did not start a private window.");
            }
            catch
            {
                RemovePrivateDirectory(directory);
                throw;
            }
        }

        private static bool IsPrivateDirectory(string path)
        {
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                return false;

            var directory = new DirectoryInfo(path);
            return directory.Name.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
                && string.Equals(directory.Parent?.FullName, Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                    StringComparison.OrdinalIgnoreCase)
                && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }

        private static void RemovePrivateDirectory(string path)
        {
            try
            {
                if (IsPrivateDirectory(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // A terminating Chromium child can still hold a file open in its temporary root.
            }
            catch (UnauthorizedAccessException)
            {
                // Windows can retain handles to runtime files past the private window's lifetime.
            }
        }
    }
}
