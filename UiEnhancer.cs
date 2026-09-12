using System.Reflection;
using System.Runtime.CompilerServices;

namespace CPUCKTest;

internal static class UiEnhancer
{
    static bool initialized;
    static MoveResizeGuard? moveResizeGuard;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Application.Idle += OnIdle;
    }

    static void OnIdle(object? sender, EventArgs e)
    {
        if (initialized) return;
        var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main == null) return;

        initialized = true;
        Application.Idle -= OnIdle;

        // The hardware polling timer performs a full LibreHardwareMonitor sensor scan
        // on the UI thread once per second. During a Windows move/resize modal loop that
        // can make the whole window hitch. Pause only the UI polling while the user is
        // physically moving/resizing the window, then resume immediately afterwards.
        var timerField = typeof(MainForm).GetField("uiTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        if (timerField?.GetValue(main) is System.Windows.Forms.Timer uiTimer)
            moveResizeGuard = new MoveResizeGuard(main, uiTimer);

        // Reduce redraw/flicker cost for the large nested WinForms layout.
        EnableDoubleBuffering(main);

        // Keep the previous behavior: don't waste a row item on RAM temperature when
        // the laptop/firmware doesn't expose a RAM temperature sensor.
        var ramTempField = typeof(MainForm).GetField("lblRamTemp", BindingFlags.Instance | BindingFlags.NonPublic);
        if (ramTempField?.GetValue(main) is Label ramTempLabel &&
            timerField?.GetValue(main) is System.Windows.Forms.Timer monitorUiTimer)
        {
            void RefreshRamTempVisibility() =>
                ramTempLabel.Visible = !ramTempLabel.Text.Contains("N/A", StringComparison.OrdinalIgnoreCase);

            monitorUiTimer.Tick += (_, _) => RefreshRamTempVisibility();
            RefreshRamTempVisibility();
        }

        main.FormClosing += (_, _) =>
        {
            try { moveResizeGuard?.Dispose(); } catch { }
            moveResizeGuard = null;
        };
    }

    static void EnableDoubleBuffering(Control root)
    {
        var prop = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
        if (prop == null) return;

        foreach (var control in DescendantsAndSelf(root))
        {
            if (control is Form or Panel or TableLayoutPanel or FlowLayoutPanel or GroupBox)
            {
                try { prop.SetValue(control, true); } catch { }
            }
        }
    }

    static IEnumerable<Control> DescendantsAndSelf(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (var nested in DescendantsAndSelf(child))
                yield return nested;
        }
    }
}

internal sealed class MoveResizeGuard : NativeWindow, IDisposable
{
    const int WM_ENTERSIZEMOVE = 0x0231;
    const int WM_EXITSIZEMOVE = 0x0232;

    readonly System.Windows.Forms.Timer uiTimer;
    bool resumeTimer;
    bool disposed;

    public MoveResizeGuard(Form form, System.Windows.Forms.Timer uiTimer)
    {
        this.uiTimer = uiTimer;
        AssignHandle(form.Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_ENTERSIZEMOVE)
        {
            resumeTimer = uiTimer.Enabled;
            if (resumeTimer) uiTimer.Stop();
        }
        else if (m.Msg == WM_EXITSIZEMOVE)
        {
            if (resumeTimer)
            {
                resumeTimer = false;
                uiTimer.Start();
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { ReleaseHandle(); } catch { }
    }
}
