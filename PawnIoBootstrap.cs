using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace CPUCKTest;

internal static class PawnIoBootstrap
{
    private const string ResourceName = "CPUCKTest.PawnIO_setup.exe";
    private const string InstallerUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    private const string InstallerSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
    private static bool checkedOnce;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Do not touch LibreHardwareMonitor.PawnIo.PawnIo before installation.
        // That type caches the installed PawnIO version when it is first initialized.
        if (checkedOnce) return;
        checkedOnce = true;

        try
        {
            Version? installed = GetInstalledVersion();
            if (installed != null && installed >= new Version(2, 0, 0, 0)) return;

            string message = installed == null
                ? "CPU temperature / package power / CPU clock require the official PawnIO driver.\n\nPawnIO is not installed. Install the signed official PawnIO 2.2.0 driver now?"
                : $"PawnIO {installed} is outdated. CPU sensor readings may be unavailable.\n\nUpdate to official PawnIO 2.2.0 now?";

            if (MessageBox.Show(message, "CPUCK Test - CPU sensor driver", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

            string? installer = AcquireInstaller();
            if (installer == null)
            {
                MessageBox.Show(
                    "CPUCK Test could not obtain the official PawnIO installer.\n\nCheck the internet connection and try again. CPU temperature may remain unavailable.",
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
                return;

            if (exitCode is 3010 or 1641)
            {
                MessageBox.Show(
                    "PawnIO was installed successfully, but Windows requires a restart before CPU sensors can be read.",
                    "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            MessageBox.Show(
                $"PawnIO installation returned code {exitCode}. CPU temperature may remain unavailable.",
                "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "PawnIO setup check failed. CPU temperature may remain unavailable.\n\n" + ex.Message,
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

    private static string? AcquireInstaller()
    {
        // Prefer the embedded installer so CPUCK Test can work offline.
        string? embedded = ExtractEmbeddedInstaller();
        if (embedded != null)
        {
            if (VerifySha256(embedded)) return embedded;
            try { File.Delete(embedded); } catch { }
        }

        // Fallback for builds where the resource was accidentally omitted: download only
        // the pinned official 2.2.0 release and verify its published SHA-256 before running.
        string path = Path.Combine(Path.GetTempPath(), $"CPUCK_PawnIO_{Guid.NewGuid():N}.exe");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CPUCKTest/1.0");
            byte[] bytes = client.GetByteArrayAsync(InstallerUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(path, bytes);

            if (VerifySha256(path)) return path;

            try { File.Delete(path); } catch { }
            MessageBox.Show(
                "The downloaded PawnIO installer failed SHA-256 verification and was not executed.",
                "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return null;
        }
    }

    private static string? ExtractEmbeddedInstaller()
    {
        try
        {
            using Stream? source = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (source == null) return null;

            string path = Path.Combine(Path.GetTempPath(), $"CPUCK_PawnIO_{Guid.NewGuid():N}.exe");
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static bool VerifySha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            string actual = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(actual, InstallerSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
