using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

        main.FormClosing += (_, _) =>
        {
            try { hideTimer.Stop(); hideTimer.Dispose(); } catch { }
            try { if (overlay != null && !overlay.IsDisposed) overlay.Close(); } catch { }
            try
            {
                var field = typeof(MainForm).GetField("monitor", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(main) is IDisposable disposable)
                {
                    disposable.Dispose();
                    field.SetValue(main, null);
                }
            }
            catch { }
        };
    }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    static void ToggleOverlay(MainForm main)
    {
        if (overlay == null || overlay.IsDisposed)
        {
            overlay = new OverlayForm(main);
            overlay.Show(main);
            return;
        }
        if (overlay.Visible) overlay.Hide();
        else overlay.Show(main);
    }
}

public sealed class OverlayForm : Form
{
    readonly Label line1 = new();
    readonly Label line2 = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    readonly FpsProvider fps = new();

    readonly Label? srcCpu;
    readonly Label? srcGpu;
    readonly Label? srcCpuPower;
    readonly Label? srcGpuPower;
    readonly Label? srcCpuLoad;
    readonly Label? srcGpuLoad;
    readonly Label? srcRam;

    Point dragStart;
    bool dragging;

    public OverlayForm(MainForm main)
    {
        srcCpu = FieldLabel(main, "lblCpu");
        srcGpu = FieldLabel(main, "lblGpu");
        srcCpuPower = FieldLabel(main, "lblCpuPower");
        srcGpuPower = FieldLabel(main, "lblGpuPower");
        srcCpuLoad = FieldLabel(main, "lblCpuLoad");
        srcGpuLoad = FieldLabel(main, "lblGpuLoad");
        srcRam = FieldLabel(main, "lblRam");

        Text = "CPUCK Overlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 500;
        Height = 54;
        BackColor = Color.FromArgb(18, 18, 18);
        Opacity = 0.90;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Location = new Point(Screen.PrimaryScreen?.WorkingArea.Right - Width - 20 ?? 20, 30);

        line1.Dock = DockStyle.Top;
        line1.Height = 27;
        line1.TextAlign = ContentAlignment.MiddleLeft;
        line1.Font = new Font("Segoe UI Semibold", 10f);
        line1.ForeColor = Color.White;
        line1.Padding = new Padding(10, 0, 6, 0);

        line2.Dock = DockStyle.Fill;
        line2.TextAlign = ContentAlignment.MiddleLeft;
        line2.Font = new Font("Segoe UI", 9f);
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

        fps.Start();
        timer.Tick += (_, _) => RefreshOverlay();
        timer.Start();
        FormClosed += (_, _) =>
        {
            try { timer.Stop(); timer.Dispose(); } catch { }
            try { fps.Dispose(); } catch { }
        };
        RefreshOverlay();
    }

    static Label? FieldLabel(MainForm main, string name) =>
        typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main) as Label;

    static string Value(string? text, string prefix, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var v = text.Trim();
        return v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? v[prefix.Length..].Trim() : v;
    }

    void RefreshOverlay()
    {
        // Important: overlay reads the main form's cached labels. It does NOT poll hardware again.
        // This avoids a second LibreHardwareMonitor sensor walk every 500 ms, which caused stutter.
        string cpuTemp = Value(srcCpu?.Text, "CPU", "--°C");
        string gpuTemp = Value(srcGpu?.Text, "GPU", "--°C");
        string cpuLoad = Value(srcCpuLoad?.Text, "CPU Load", "-- %");
        string gpuLoad = Value(srcGpuLoad?.Text, "GPU Load", "-- %");
        string ram = Value(srcRam?.Text, "RAM", "-- %");
        string cpuPower = Value(srcCpuPower?.Text, "CPU Power", "-- W");
        string gpuPower = Value(srcGpuPower?.Text, "GPU Power", "-- W");
        string fpsText = fps.CurrentFps is double f ? $"{f:0} FPS" : "FPS --";

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
    System.Threading.Timer? foregroundTimer;
    string? extractedExe;
    double? fps;
    DateTime lastFrameUtc;
    int foregroundPid;
    int processIdColumn = -1;
    int frameTimeColumn = -1;
    bool headerSeen;

    public double? CurrentFps
    {
        get
        {
            lock (gate)
            {
                if (!fps.HasValue || DateTime.UtcNow - lastFrameUtc > TimeSpan.FromSeconds(2)) return null;
                return fps;
            }
        }
    }

    public void Start()
    {
        try
        {
            extractedExe = ExtractEmbeddedPresentMon();
            if (string.IsNullOrWhiteSpace(extractedExe) || !File.Exists(extractedExe)) return;

            UpdateForegroundPid();
            foregroundTimer = new System.Threading.Timer(_ => UpdateForegroundPid(), null, 250, 250);

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = extractedExe,
                    Arguments = "--output_stdout --no_console_stats --exclude_dropped --session_name CPUCKTestFPS --stop_existing_session",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) ParseCsvLine(e.Data);
            };
            process.Exited += (_, _) => { lock (gate) fps = null; };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            lock (gate) fps = null;
        }
    }

    void UpdateForegroundPid()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { Volatile.Write(ref foregroundPid, 0); return; }
            GetWindowThreadProcessId(hwnd, out uint pid);
            Volatile.Write(ref foregroundPid, unchecked((int)pid));
        }
        catch { Volatile.Write(ref foregroundPid, 0); }
    }

    void ParseCsvLine(string line)
    {
        var parts = SplitCsv(line);
        if (parts.Count == 0) return;

        if (!headerSeen || parts.Any(x => x.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)))
        {
            processIdColumn = parts.FindIndex(x => x.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
            frameTimeColumn = parts.FindIndex(x => x.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
            headerSeen = processIdColumn >= 0 && frameTimeColumn >= 0;
            return;
        }

        if (!headerSeen || processIdColumn >= parts.Count || frameTimeColumn >= parts.Count) return;
        if (!int.TryParse(parts[processIdColumn], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return;
        int target = Volatile.Read(ref foregroundPid);
        if (target <= 0 || pid != target || pid == Environment.ProcessId) return;

        if (!double.TryParse(parts[frameTimeColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) return;
        if (ms <= 1.0 || ms > 1000.0) return;
        double candidate = 1000.0 / ms;
        if (candidate < 1 || candidate > 1000) return;

        lock (gate)
        {
            fps = fps.HasValue ? fps.Value * 0.80 + candidate * 0.20 : candidate;
            lastFrameUtc = DateTime.UtcNow;
        }
    }

    static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else sb.Append(c);
        }
        result.Add(sb.ToString().Trim());
        return result;
    }

    static string? ExtractEmbeddedPresentMon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var src = asm.GetManifestResourceStream("CPUCKTest.PresentMon.exe");
            if (src == null) return null;

            string dir = Path.Combine(Path.GetTempPath(), "CPUCKTest");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "PresentMon-2.5.1-x64.exe");

            if (!File.Exists(path) || new FileInfo(path).Length != src.Length)
            {
                using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                src.CopyTo(dst);
            }
            return path;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { foregroundTimer?.Dispose(); } catch { }
        try
        {
            if (process != null && !process.HasExited) process.Kill(true);
            process?.Dispose();
        }
        catch { }
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
