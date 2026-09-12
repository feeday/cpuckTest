using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Win32;

namespace CPUCKTest;

internal static class PawnIoBootstrap
{
    private const string ResourceName = "CPUCKTest.PawnIO_setup.exe";
    private static bool checkedOnce;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Do not touch LibreHardwareMonitor.PawnIo.PawnIo here before installation.
        // Its static Version value is cached when the type is first initialized.
        if (checkedOnce) return;
        checkedOnce = true;

        try
        {
            Version? installed = GetInstalledVersion();
            if (installed != null && installed >= new Version(2, 0, 0, 0)) return;

            string message = installed == null
                ? "CPU temperature / package power / CPU clock require the official PawnIO driver.\n\nPawnIO is not installed. Install the signed official PawnIO driver now?"
                : $"PawnIO {installed} is outdated. CPU sensor readings may be unavailable.\n\nUpdate to the bundled official PawnIO driver now?";

            if (MessageBox.Show(message, "CPUCK Test - CPU sensor driver", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

            string? installer = ExtractInstaller();
            if (installer == null)
            {
                MessageBox.Show("The PawnIO installer is not embedded in this build. CPU temperature may remain unavailable.",
                    "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "-install -silent",
                UseShellExecute = true,
                Verb = "runas"
            });
            process?.WaitForExit();
            int exitCode = process?.ExitCode ?? -1;

            try { File.Delete(installer); } catch { }

            if (exitCode == 0)
            {
                // PawnIO has not been touched through LibreHardwareMonitor yet, so the
                // library will see the freshly installed driver later in MainForm.InitMonitor().
                return;
            }

            if (exitCode is 3010 or 1641)
            {
                MessageBox.Show("PawnIO was installed, but Windows reports that a restart is required before CPU sensors can be read.",
                    "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            MessageBox.Show($"PawnIO installation returned code {exitCode}. CPU temperature may remain unavailable.",
                "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show("PawnIO setup check failed. CPU temperature may remain unavailable.\n\n" + ex.Message,
                "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Version? GetInstalledVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (Version.TryParse(key?.GetValue("DisplayVersion")?.ToString(), out Version? version))
                    return version;
            }
            catch { }
        }
        return null;
    }

    private static string? ExtractInstaller()
    {
        using Stream? source = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (source == null) return null;

        string path = Path.Combine(Path.GetTempPath(), $"CPUCK_PawnIO_{Guid.NewGuid():N}.exe");
        using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
        return path;
    }
}
