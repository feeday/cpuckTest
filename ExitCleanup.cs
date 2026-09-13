using System.Diagnostics;
using Microsoft.Win32;

namespace CPUCKTest;

internal static class DriverCleanup
{
    static int released;

    public static void ReleaseDriver()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
            return;

        StopServicePointingToDriver();
        TryDeleteDriverFile(12, 50);
    }

    public static void SchedulePostExitDelete()
    {
        if (TryDeleteDriverFile(4, 50))
            return;

        try
        {
            string driver = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CPUCKTest.sys"));
            if (!File.Exists(driver))
                return;

            string escaped = driver.Replace("'", "''");
            string command =
                "$p='" + escaped + "';" +
                "$svcs=Get-ChildItem 'HKLM:\\SYSTEM\\CurrentControlSet\\Services' -ErrorAction SilentlyContinue;" +
                "foreach($s in $svcs){" +
                "$ip=(Get-ItemProperty -LiteralPath $s.PSPath -Name ImagePath -ErrorAction SilentlyContinue).ImagePath;" +
                "if($ip){" +
                "$n=[Environment]::ExpandEnvironmentVariables(($ip -replace '^\\\\\\?\\\\','' -replace '^\\\\\\\\\\?\\\\','').Trim('\"'));" +
                "try{$n=[IO.Path]::GetFullPath($n)}catch{};" +
                "if($n -ieq $p){& sc.exe stop $s.PSChildName | Out-Null;Start-Sleep -Milliseconds 120;& sc.exe delete $s.PSChildName | Out-Null}" +
                "}}" +
                "for($i=0;$i -lt 30;$i++){" +
                "Start-Sleep -Milliseconds 100;" +
                "try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force -ErrorAction Stop};break}catch{}" +
                "}";

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" +
                            command.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    static void StopServicePointingToDriver()
    {
        string driver = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CPUCKTest.sys"));

        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services == null)
                return;

            foreach (string serviceName in services.GetSubKeyNames())
            {
                try
                {
                    using var service = services.OpenSubKey(serviceName);
                    string? imagePath = service?.GetValue("ImagePath") as string;
                    if (string.IsNullOrWhiteSpace(imagePath))
                        continue;

                    string normalized = NormalizeDriverPath(imagePath);
                    if (!string.Equals(normalized, driver, StringComparison.OrdinalIgnoreCase))
                        continue;

                    RunSc("stop", serviceName);
                    Thread.Sleep(80);
                    RunSc("delete", serviceName);
                }
                catch { }
            }
        }
        catch { }
    }

    static string NormalizeDriverPath(string imagePath)
    {
        string path = imagePath.Trim().Trim('"');

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            path = path[4..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];

        path = Environment.ExpandEnvironmentVariables(path);

        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    static void RunSc(string verb, string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"{verb} \"{serviceName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(1200);
        }
        catch { }
    }

    static bool TryDeleteDriverFile(int attempts, int delayMs)
    {
        string driver = Path.Combine(AppContext.BaseDirectory, "CPUCKTest.sys");
        if (!File.Exists(driver))
            return true;

        for (int i = 0; i < attempts; i++)
        {
            try
            {
                File.SetAttributes(driver, FileAttributes.Normal);
                File.Delete(driver);
                if (!File.Exists(driver))
                    return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (delayMs > 0)
                Thread.Sleep(delayMs);
        }

        return !File.Exists(driver);
    }
}
