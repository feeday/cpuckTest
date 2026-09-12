using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace CPUCKTest;

internal static class UiEnhancer
{
    static bool initialized;
    static OverlayForm? overlay;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Application.Idle += (_, _) => TryAttach();
    }

    static void TryAttach()
    {
        if (initialized) return;
        var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main == null) return;
        initialized = true;

        // Hide unavailable RAM temperature instead of showing N/A.
        var hideTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        hideTimer.Tick += (_, _) =>
        {
            foreach (var c in Descendants(main))
            {
                if (c is Label l && l.Text.StartsWith("RAM Temp", StringComparison.OrdinalIgnoreCase))
                    l.Visible = !l.Text.Contains("N/A", StringComparison.OrdinalIgnoreCase) && !l.Text.Contains("--", StringComparison.OrdinalIgnoreCase);
            }
        };
        hideTimer.Start();

        // Make the information rows less cramped on smaller windows.
        foreach (var fp in Descendants(main).OfType<FlowLayoutPanel>())
        {
            fp.WrapContents = true;
            fp.AutoScroll = true;
        }

        var top = Descendants(main).OfType<FlowLayoutPanel>()
            .FirstOrDefault(p => p.Controls.OfType<CheckBox>().Any(x => x.Text == "Stress CPU"));
        if (top != null)
        {
            var button = new Button { Text = "Overlay", Width = 92, Height = 36, Margin = new Padding(8, 0, 0, 0) };
            button.Click += (_, _) => ToggleOverlay(main);
            top.Controls.Add(button);
        }
    }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    static void ToggleOverlay(Form owner)
    {
        if (overlay == null || overlay.IsDisposed)
        {
            overlay = new OverlayForm();
            overlay.Show(owner);
            return;
        }
        if (overlay.Visible) overlay.Hide();
        else overlay.Show(owner);
    }
}

public sealed class OverlayForm : Form
{
    readonly Label line1 = new();
    readonly Label line2 = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    readonly HardwareMonitor monitor = new();
    readonly FpsProvider fps = new();
    Point dragStart;
    bool dragging;

    public OverlayForm()
    {
        Text = "CPUCK Overlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 520;
        Height = 58;
        BackColor = Color.FromArgb(18, 18, 18);
        Opacity = 0.90;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Screen.PrimaryScreen?.WorkingArea.Right - Width - 20 ?? 20, 30);

        line1.Dock = DockStyle.Top;
        line1.Height = 29;
        line1.TextAlign = ContentAlignment.MiddleLeft;
        line1.Font = new Font("Segoe UI Semibold", 10.5f);
        line1.ForeColor = Color.White;
        line1.Padding = new Padding(10, 0, 6, 0);

        line2.Dock = DockStyle.Fill;
        line2.TextAlign = ContentAlignment.MiddleLeft;
        line2.Font = new Font("Segoe UI", 9.5f);
        line2.ForeColor = Color.Gainsboro;
        line2.Padding = new Padding(10, 0, 6, 0);

        Controls.Add(line2);
        Controls.Add(line1);

        foreach (var c in new Control[] { this, line1, line2 })
        {
            c.MouseDown += DragDown;
            c.MouseMove += DragMove;
            c.MouseUp += (_, _) => dragging = false;
            c.DoubleClick += (_, _) => Hide();
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Hide", null, (_, _) => Hide());
        menu.Items.Add("Opacity 70%", null, (_, _) => Opacity = 0.70);
        menu.Items.Add("Opacity 90%", null, (_, _) => Opacity = 0.90);
        menu.Items.Add("Opacity 100%", null, (_, _) => Opacity = 1.0);
        ContextMenuStrip = menu;
        line1.ContextMenuStrip = menu;
        line2.ContextMenuStrip = menu;

        try { monitor.Open(); } catch { }
        fps.Start();
        timer.Tick += (_, _) => RefreshOverlay();
        timer.Start();
        FormClosed += (_, _) => { timer.Stop(); fps.Dispose(); monitor.Dispose(); };
        RefreshOverlay();
    }

    void RefreshOverlay()
    {
        HardwareSnapshot s = default;
        try { s = monitor.Read(); } catch { }
        var fpsValue = fps.CurrentFps;

        string cpuTemp = s.CpuTemp.HasValue ? $"{s.CpuTemp:0}°" : "--°";
        string gpuTemp = s.GpuTemp.HasValue ? $"{s.GpuTemp:0}°" : "--°";
        string cpuLoad = s.CpuLoad.HasValue ? $"{s.CpuLoad:0}%" : "--%";
        string gpuLoad = s.GpuLoad.HasValue ? $"{s.GpuLoad:0}%" : "--%";
        string ram = s.RamLoad.HasValue ? $"{s.RamLoad:0}%" : "--%";
        string cpuPower = s.CpuPower.HasValue ? $"{s.CpuPower:0}W" : "--W";
        string gpuPower = s.GpuPower.HasValue ? $"{s.GpuPower:0}W" : "--W";
        string fpsText = fpsValue.HasValue ? $"{fpsValue.Value:0} FPS" : "FPS --";

        line1.Text = $"CPU {cpuTemp} {cpuLoad}  |  GPU {gpuTemp} {gpuLoad}  |  RAM {ram}  |  {fpsText}";
        line2.Text = $"CPU {cpuPower}  |  GPU {gpuPower}  |  drag to move · double-click to hide";
    }

    void DragDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        dragStart = e.Location;
    }

    void DragMove(object? sender, MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        var p = PointToScreen(e.Location);
        Location = new Point(p.X - dragStart.X, p.Y - dragStart.Y);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }
}

internal sealed class FpsProvider : IDisposable
{
    readonly object gate = new();
    Process? process;
    double? fps;
    public double? CurrentFps { get { lock (gate) return fps; } }

    public void Start()
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            string? exe = new[]
            {
                Path.Combine(dir, "PresentMon.exe"),
                Path.Combine(dir, "PresentMon-x64.exe")
            }.FirstOrDefault(File.Exists);

            if (exe == null) return;

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--output_stdout --no_console_stats",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ParseCsvLine(e.Data); };
            process.Exited += (_, _) => { lock (gate) fps = null; };
            process.Start();
            process.BeginOutputReadLine();
        }
        catch { lock (gate) fps = null; }
    }

    void ParseCsvLine(string line)
    {
        // PresentMon CSV commonly exposes CPU frame-time / ms-between-presents fields.
        // Pick the first plausible millisecond frame interval from the row and smooth it.
        var parts = line.Split(',');
        foreach (var p in parts)
        {
            if (!double.TryParse(p.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) continue;
            if (ms < 2.0 || ms > 200.0) continue;
            double candidate = 1000.0 / ms;
            if (candidate < 5 || candidate > 500) continue;
            lock (gate)
            {
                fps = fps.HasValue ? fps.Value * 0.8 + candidate * 0.2 : candidate;
            }
            break;
        }
    }

    public void Dispose()
    {
        try
        {
            if (process != null && !process.HasExited) process.Kill(true);
            process?.Dispose();
        }
        catch { }
    }
}
