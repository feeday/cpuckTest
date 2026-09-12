using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CPUCKTest;

internal static class DashboardEnhancer
{
    static bool attached;
    static TaskbarStatsForm? taskbarStats;
    static ModernTempGraph? modernGraph;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Application.Idle += TryAttach;
    }

    static void TryAttach(object? sender, EventArgs e)
    {
        if (attached) return;
        var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main == null) return;

        var uiTimer = GetField<System.Windows.Forms.Timer>(main, "uiTimer");
        var oldGraph = GetField<TempGraph>(main, "graph");
        if (uiTimer == null || oldGraph?.Parent is not GroupBox graphGroup ||
            !graphGroup.Text.Contains("TEMPERATURE", StringComparison.OrdinalIgnoreCase))
            return;

        attached = true;
        Application.Idle -= TryAttach;

        // Replace the old GroupBox entirely. This removes the leftover rectangular frame
        // instead of merely hiding the dashed warning line inside the chart.
        modernGraph = new ModernTempGraph { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var graphHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = graphGroup.Margin,
            Padding = new Padding(0),
            BackColor = Color.FromArgb(13, 15, 18),
            BorderStyle = BorderStyle.None
        };
        var graphTitle = new Label
        {
            Text = "TEMPERATURE HISTORY",
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            ForeColor = Color.FromArgb(180, 188, 200),
            BackColor = Color.FromArgb(13, 15, 18),
            Font = new Font("Segoe UI Semibold", 8.5f)
        };
        graphHost.Controls.Add(modernGraph);
        graphHost.Controls.Add(graphTitle);
        modernGraph.BringToFront();

        if (graphGroup.Parent is TableLayoutPanel table)
        {
            var pos = table.GetPositionFromControl(graphGroup);
            table.Controls.Remove(graphGroup);
            graphGroup.Visible = false;
            table.Controls.Add(graphHost, pos.Column, pos.Row);
        }
        else if (graphGroup.Parent is Control parent)
        {
            int index = parent.Controls.GetChildIndex(graphGroup);
            parent.Controls.Remove(graphGroup);
            graphGroup.Visible = false;
            parent.Controls.Add(graphHost);
            parent.Controls.SetChildIndex(graphHost, index);
        }

        uiTimer.Tick += (_, _) =>
        {
            if (modernGraph == null || main.IsDisposed) return;
            modernGraph.Add(
                ReadTemperature(GetField<Label>(main, "lblCpu")?.Text),
                ReadTemperature(GetField<Label>(main, "lblGpu")?.Text),
                ReadTemperature(GetField<Label>(main, "lblRamTemp")?.Text),
                ReadTemperature(GetField<Label>(main, "lblDisk")?.Text));
        };

        taskbarStats = new TaskbarStatsForm(main);
        taskbarStats.Show();

        main.FormClosed += (_, _) =>
        {
            try { taskbarStats?.Close(); } catch { }
            taskbarStats = null;
        };
    }

    internal static T? GetField<T>(MainForm form, string name) where T : class
        => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as T;

    static readonly Regex TempRegex = new(@"(-?\d+(?:\.\d+)?)\s*°", RegexOptions.Compiled);

    internal static double? ReadTemperature(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains("N/A", StringComparison.OrdinalIgnoreCase) || text.Contains("--"))
            return null;
        var m = TempRegex.Match(text);
        return m.Success && double.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }
}

internal sealed class ModernTempGraph : Panel
{
    readonly ConcurrentQueue<Sample> samples = new();

    readonly record struct Sample(double? Cpu, double? Gpu, double? Ram, double? Ssd);
    readonly record struct Track(string Name, Color Color, Func<Sample, double?> Pick);

    static readonly Track[] Tracks =
    {
        new("CPU", Color.FromArgb(80, 230, 110), s => s.Cpu),
        new("GPU", Color.FromArgb(70, 185, 255), s => s.Gpu),
        new("RAM", Color.FromArgb(255, 195, 70), s => s.Ram),
        new("SSD", Color.FromArgb(190, 120, 255), s => s.Ssd)
    };

    public ModernTempGraph()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(13, 15, 18);
    }

    public void Add(double? cpu, double? gpu, double? ram, double? ssd)
    {
        samples.Enqueue(new Sample(cpu, gpu, ram, ssd));
        while (samples.Count > 300) samples.TryDequeue(out _);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var data = samples.ToArray();
        using var font = new Font("Segoe UI", 8.5f);
        using var small = new Font("Segoe UI", 7.5f);
        using var gridPen = new Pen(Color.FromArgb(22, 255, 255, 255), 1f);
        using var dividerPen = new Pen(Color.FromArgb(34, 255, 255, 255), 1f);

        if (data.Length == 0)
        {
            TextRenderer.DrawText(g, "Waiting for temperature samples…", font, ClientRectangle,
                Color.FromArgb(125, 135, 148), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var visibleTracks = Tracks.Where(t => data.Any(s => t.Pick(s).HasValue)).ToArray();
        if (visibleTracks.Length == 0) return;

        int left = 112;
        int right = 18;
        int top = 3;
        int bottom = 4;
        int usableH = Math.Max(1, Height - top - bottom);
        float bandH = usableH / (float)visibleTracks.Length;
        int plotW = Math.Max(1, Width - left - right);

        for (int ti = 0; ti < visibleTracks.Length; ti++)
        {
            var track = visibleTracks[ti];
            float y0 = top + ti * bandH;
            float y1 = y0 + bandH;
            var values = data.Select(track.Pick).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            if (values.Length == 0) continue;

            double actualMin = values.Min();
            double actualMax = values.Max();
            double center = (actualMin + actualMax) / 2.0;
            double range = Math.Max(6.0, actualMax - actualMin + 4.0);
            double min = Math.Max(0, Math.Floor((center - range / 2.0) / 2.0) * 2.0);
            double max = Math.Min(110, Math.Ceiling((center + range / 2.0) / 2.0) * 2.0);
            if (max - min < 6) max = Math.Min(110, min + 6);

            if (ti > 0) g.DrawLine(dividerPen, 0, y0, Width, y0);

            for (int x = 0; x <= 6; x++)
            {
                float gx = left + plotW * x / 6f;
                g.DrawLine(gridPen, gx, y0 + 3, gx, y1 - 3);
            }
            for (int y = 1; y <= 2; y++)
            {
                float gy = y0 + bandH * y / 3f;
                g.DrawLine(gridPen, left, gy, Width - right, gy);
            }

            double latest = values[^1];
            using var colorBrush = new SolidBrush(track.Color);
            using var mutedBrush = new SolidBrush(Color.FromArgb(145, 155, 168));
            using var pen = new Pen(track.Color, 2f) { LineJoin = LineJoin.Round };
            using var valueFont = new Font("Segoe UI Semibold", 11f);

            g.DrawString(track.Name, font, colorBrush, 10, y0 + 7);
            g.DrawString($"{latest:0}°C", valueFont, colorBrush, 48, y0 + 3);
            g.DrawString($"{actualMin:0}–{actualMax:0}°", small, mutedBrush, 48, y0 + 25);
            g.DrawString($"{min:0}°", small, mutedBrush, left - 31, y1 - 17);
            g.DrawString($"{max:0}°", small, mutedBrush, left - 31, y0 + 2);

            // No warning/dashed line. The graph is now purely a temperature trend view.
            PointF? prev = null;
            PointF lastPoint = default;
            int count = data.Length;
            for (int i = 0; i < count; i++)
            {
                var v = track.Pick(data[i]);
                if (!v.HasValue) { prev = null; continue; }
                float x = left + (count <= 1 ? plotW : plotW * i / (float)(count - 1));
                float y = MapY(v.Value, min, max, y0 + 5, y1 - 5);
                var p = new PointF(x, y);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, p);
                prev = p;
                lastPoint = p;
            }

            if (prev.HasValue)
                g.FillEllipse(colorBrush, lastPoint.X - 3, lastPoint.Y - 3, 6, 6);
        }

        using var timeBrush = new SolidBrush(Color.FromArgb(110, 120, 132));
        string seconds = $"last {Math.Min(300, data.Length)} s";
        var size = g.MeasureString(seconds, small);
        g.DrawString(seconds, small, timeBrush, Width - size.Width - 8, Height - size.Height - 2);
    }

    static float MapY(double value, double min, double max, float top, float bottom)
    {
        double t = (value - min) / Math.Max(0.001, max - min);
        t = Math.Clamp(t, 0, 1);
        return bottom - (float)t * (bottom - top);
    }
}

internal sealed class TaskbarStatsForm : Form
{
    const string RegistryPath = @"Software\CPUCKTest";

    readonly MainForm main;
    readonly Label text = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    IntPtr parentTaskbar;
    StatsSize statsSize;

    enum StatsSize
    {
        Compact,
        Normal,
        Large
    }

    public TaskbarStatsForm(MainForm main)
    {
        this.main = main;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;

        bool light = IsLightTaskbar();
        BackColor = light ? Color.FromArgb(242, 242, 242) : Color.FromArgb(32, 32, 32);
        ForeColor = light ? Color.FromArgb(25, 25, 25) : Color.White;

        text.Dock = DockStyle.Fill;
        text.TextAlign = ContentAlignment.MiddleCenter;
        text.ForeColor = ForeColor;
        text.BackColor = BackColor;
        text.Cursor = Cursors.Hand;
        Controls.Add(text);

        statsSize = LoadSize();
        ApplySize(statsSize, false);
        BuildSizeMenu();

        timer.Tick += (_, _) =>
        {
            UpdateStats();
            PositionOnTaskbar();
        };
        timer.Start();
        Shown += (_, _) => { UpdateStats(); PositionOnTaskbar(); };
        FormClosed += (_, _) => timer.Dispose();
    }

    void BuildSizeMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(MakeSizeItem("Small", StatsSize.Compact));
        menu.Items.Add(MakeSizeItem("Medium", StatsSize.Normal));
        menu.Items.Add(MakeSizeItem("Large", StatsSize.Large));
        text.ContextMenuStrip = menu;
        ContextMenuStrip = menu;
    }

    ToolStripMenuItem MakeSizeItem(string title, StatsSize value)
    {
        var item = new ToolStripMenuItem(title) { Checked = statsSize == value };
        item.Click += (_, _) =>
        {
            statsSize = value;
            ApplySize(value, true);
            if (ContextMenuStrip != null)
            {
                foreach (ToolStripItem raw in ContextMenuStrip.Items)
                    if (raw is ToolStripMenuItem mi) mi.Checked = mi.Text == title;
            }
            PositionOnTaskbar();
        };
        return item;
    }

    void ApplySize(StatsSize size, bool persist)
    {
        (int width, int height, float font) = size switch
        {
            StatsSize.Compact => (450, 24, 8.0f),
            StatsSize.Large => (800, 34, 10.5f),
            _ => (620, 28, 9.0f)
        };
        Width = width;
        Height = height;
        text.Font?.Dispose();
        text.Font = new Font("Segoe UI Semibold", font);

        if (persist)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key?.SetValue("TaskbarStatsSize", size.ToString(), RegistryValueKind.String);
            }
            catch { }
        }
    }

    static StatsSize LoadSize()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            string? value = key?.GetValue("TaskbarStatsSize")?.ToString();
            if (Enum.TryParse(value, true, out StatsSize parsed)) return parsed;
        }
        catch { }
        return StatsSize.Normal;
    }

    void UpdateStats()
    {
        if (main.IsDisposed) { Close(); return; }

        double? cpuTemp = DashboardEnhancer.ReadTemperature(DashboardEnhancer.GetField<Label>(main, "lblCpu")?.Text);
        double? gpuTemp = DashboardEnhancer.ReadTemperature(DashboardEnhancer.GetField<Label>(main, "lblGpu")?.Text);
        double? ramTemp = DashboardEnhancer.ReadTemperature(DashboardEnhancer.GetField<Label>(main, "lblRamTemp")?.Text);
        double? ssdTemp = DashboardEnhancer.ReadTemperature(DashboardEnhancer.GetField<Label>(main, "lblDisk")?.Text);

        double? cpuClock = ReadNumber(DashboardEnhancer.GetField<Label>(main, "lblCpuClock")?.Text, "Clock");
        double? gpuClock = ReadNumber(DashboardEnhancer.GetField<Label>(main, "lblGpuClock")?.Text, "Clock");
        double? cpuLoad = ReadPercent(DashboardEnhancer.GetField<Label>(main, "lblCpuLoad")?.Text);
        double? gpuLoad = ReadPercent(DashboardEnhancer.GetField<Label>(main, "lblGpuLoad")?.Text);
        double? ramLoad = ReadPercent(DashboardEnhancer.GetField<Label>(main, "lblRam")?.Text);

        string cpu = $"CPU {FmtTemp(cpuTemp)} {FmtClock(cpuClock)} {FmtPct(cpuLoad)}";
        string gpu = $"GPU {FmtTemp(gpuTemp)} {FmtClock(gpuClock)} {FmtPct(gpuLoad)}";
        string ram = ramTemp.HasValue ? $"RAM {FmtPct(ramLoad)} {FmtTemp(ramTemp)}" : $"RAM {FmtPct(ramLoad)}";
        string ssd = $"SSD {FmtTemp(ssdTemp)}";
        text.Text = statsSize == StatsSize.Compact
            ? $"{cpu} | {gpu} | {ram} | {ssd}"
            : $"{cpu}   |   {gpu}   |   {ram}   |   {ssd}";
    }

    void PositionOnTaskbar()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out RECT tb))
        {
            Hide();
            return;
        }

        int tbWidth = tb.Right - tb.Left;
        int tbHeight = tb.Bottom - tb.Top;
        if (tbWidth < 400 || tbHeight > 120)
        {
            Hide();
            return;
        }

        if (parentTaskbar != taskbar)
        {
            // Make this a real child of the Windows taskbar instead of a floating TopMost
            // window. It now stays physically attached to the taskbar and follows it.
            SetParent(Handle, taskbar);
            parentTaskbar = taskbar;
        }

        IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int rightEdgeScreen = tb.Right - 250;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out RECT tr)) rightEdgeScreen = tr.Left - 6;

        int rightEdge = rightEdgeScreen - tb.Left;
        int available = Math.Max(260, rightEdge - 8);
        int width = Math.Min(Width, available);
        int height = Math.Min(Height, Math.Max(22, tbHeight - 2));
        int x = Math.Max(4, rightEdge - width);
        int y = Math.Max(0, (tbHeight - height) / 2);
        SetBounds(x, y, width, height);

        if (!Visible) Show();
    }

    static double? ReadNumber(string? valueText, string after)
    {
        if (string.IsNullOrWhiteSpace(valueText) || valueText.Contains("--")) return null;
        var m = Regex.Match(valueText, Regex.Escape(after) + @"\s+([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    static double? ReadPercent(string? valueText)
    {
        if (string.IsNullOrWhiteSpace(valueText) || valueText.Contains("--")) return null;
        var m = Regex.Match(valueText, @"([0-9]+(?:\.[0-9]+)?)\s*%");
        return m.Success && double.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    static string FmtTemp(double? v) => v.HasValue ? $"{v.Value:0}°" : "--°";
    static string FmtPct(double? v) => v.HasValue ? $"{v.Value:0}%" : "--%";

    static string FmtClock(double? mhz)
    {
        if (!mhz.HasValue) return "--";
        return mhz.Value >= 1000 ? $"{mhz.Value / 1000.0:0.00}G" : $"{mhz.Value:0}M";
    }

    static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("SystemUsesLightTheme", 1)) != 0;
        }
        catch { return false; }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
}
