using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CPUCKTest;

internal static class ExitCleanup
{
    static bool attached;
    static int shuttingDown;
    static MainForm? mainForm;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Application.Idle += Attach;
        Application.ApplicationExit += (_, _) => Shutdown();
    }

    static void Attach(object? sender, EventArgs e)
    {
        if (attached) return;
        var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main == null) return;

        attached = true;
        mainForm = main;
        Application.Idle -= Attach;

        // Close the LibreHardwareMonitor Computer before Windows tears down the form.
        // This releases WinRing0/CPUCKTest.sys instead of leaving the kernel driver open
        // until the next reboot.
        main.FormClosing += (_, _) => Shutdown();
        main.FormClosed += (_, _) => DeleteDriverFileAfterClose();
    }

    static void Shutdown()
    {
        if (Interlocked.Exchange(ref shuttingDown, 1) != 0) return;

        try
        {
            if (mainForm != null)
            {
                var timerField = typeof(MainForm).GetField("uiTimer", BindingFlags.Instance | BindingFlags.NonPublic);
                if (timerField?.GetValue(mainForm) is System.Windows.Forms.Timer timer)
                    timer.Stop();

                var monitorField = typeof(MainForm).GetField("monitor", BindingFlags.Instance | BindingFlags.NonPublic);
                if (monitorField?.GetValue(mainForm) is IDisposable monitor)
                {
                    monitor.Dispose();
                    monitorField.SetValue(mainForm, null);
                }
            }
        }
        catch
        {
            // Exit must continue even if a sensor backend fails to close cleanly.
        }

        // In the normal case Computer.Close() has already unloaded WinRing0 and this
        // succeeds immediately. Retry briefly because Windows may release the image
        // section a few milliseconds later.
        TryDeleteDriverFile(8, 35);
    }

    static void DeleteDriverFileAfterClose()
    {
        if (TryDeleteDriverFile(4, 30)) return;

        // Last-resort cleanup after this process has completely exited. The helper is
        // hidden and normally lives for less than a second. It prevents CPUCKTest.sys
        // being left behind/locked if Windows releases the driver image only at process exit.
        try
        {
            string driver = Path.Combine(AppContext.BaseDirectory, "CPUCKTest.sys");
            if (!File.Exists(driver)) return;

            string escaped = driver.Replace("'", "''");
            string command =
                "$p='" + escaped + "';" +
                "for($i=0;$i -lt 20;$i++){" +
                "Start-Sleep -Milliseconds 150;" +
                "try{if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Force -ErrorAction Stop};break}catch{}" +
                "}";

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + command.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            // Best-effort cleanup only. The important part is that the driver handle
            // has already been released by monitor.Dispose().
        }
    }

    static bool TryDeleteDriverFile(int attempts, int delayMs)
    {
        string driver = Path.Combine(AppContext.BaseDirectory, "CPUCKTest.sys");
        if (!File.Exists(driver)) return true;

        for (int i = 0; i < attempts; i++)
        {
            try
            {
                File.Delete(driver);
                if (!File.Exists(driver)) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (delayMs > 0) Thread.Sleep(delayMs);
        }

        return !File.Exists(driver);
    }
}
