using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using ILGPU;
using ILGPU.Runtime;
using LibreHardwareMonitor.Hardware;

namespace CPUCKTest;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    readonly CheckBox chkCpu = new() { Text = "Stress CPU", Checked = true, AutoSize = true };
    readonly CheckBox chkGpu = new() { Text = "Stress GPU", Checked = true, AutoSize = true };
    readonly ComboBox cmbMinutes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 85 };
    readonly NumericUpDown numCpuLimit = new() { Minimum = 80, Maximum = 105, Value = 100, Width = 65 };
    readonly NumericUpDown numGpuLimit = new() { Minimum = 70, Maximum = 100, Value = 90, Width = 65 };
    readonly Button btnStart = new() { Text = "Start", Width = 100, Height = 36 };
    readonly Button btnStop = new() { Text = "Stop", Width = 100, Height = 36, Enabled = false };
    readonly Button btnSave = new() { Text = "Open Logs", Width = 100, Height = 36 };
    readonly Label lblCpu = MakeMetric("CPU  --°C");
    readonly Label lblGpu = MakeMetric("GPU  --°C");
    readonly Label lblCpuPower = MakeSmall("CPU Power -- W");
    readonly Label lblGpuPower = MakeSmall("GPU Power -- W");
    readonly Label lblCpuLoad = MakeSmall("CPU Load -- %");
    readonly Label lblGpuLoad = MakeSmall("GPU Load -- %");
    readonly Label lblElapsed = MakeSmall("Elapsed 00:00");
    readonly Label lblStatus = new() { Text = "Ready", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, ForeColor = Color.Gainsboro };
    readonly ListBox logBox = new() { Dock = DockStyle.Fill, IntegralHeight = false, BackColor = Color.FromArgb(20,20,20), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
    readonly TempGraph graph = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 1000 };

    HardwareMonitor? monitor;
    CancellationTokenSource? cts;
    Task? cpuTask;
    Task? gpuTask;
    Stopwatch sw = new();
    int testSeconds = 120;
    double maxCpuTemp, maxGpuTemp;
    double maxCpuPower, maxGpuPower;
    string? csvPath;
    StreamWriter? csv;
    bool stopping;

    public MainForm()
    {
        Text = "CPUCK Test - CPU / GPU Stability Test";
        Width = 1080;
        Height = 760;
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        foreach (var m in new[] { "1 min", "2 min", "5 min", "10 min", "15 min", "30 min" }) cmbMinutes.Items.Add(m);
        cmbMinutes.SelectedIndex = 1;

        BuildUi();
        btnStart.Click += (_, _) => StartTest();
        btnStop.Click += (_, _) => StopTest("Stopped by user");
        btnSave.Click += (_, _) => OpenLogs();
        uiTimer.Tick += (_, _) => UpdateUi();
        FormClosing += (_, _) => StopTest("Application closing", closeOnly: true);
        Shown += (_, _) => InitMonitor();
    }

    static Label MakeMetric(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 210,
        Height = 54,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI Semibold", 20f),
        BackColor = Color.FromArgb(45,45,45),
        ForeColor = Color.White
    };

    static Label MakeSmall(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(10, 8, 10, 8),
        ForeColor = Color.Gainsboro
    };

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0,8,0,0) };
        controls.Controls.Add(chkCpu);
        controls.Controls.Add(chkGpu);
        controls.Controls.Add(new Label { Text = "Duration", AutoSize = true, Margin = new Padding(24,5,4,0) });
        controls.Controls.Add(cmbMinutes);
        controls.Controls.Add(new Label { Text = "CPU stop ≥", AutoSize = true, Margin = new Padding(24,5,4,0) });
        controls.Controls.Add(numCpuLimit);
        controls.Controls.Add(new Label { Text = "°C", AutoSize = true, Margin = new Padding(2,5,4,0) });
        controls.Controls.Add(new Label { Text = "GPU stop ≥", AutoSize = true, Margin = new Padding(18,5,4,0) });
        controls.Controls.Add(numGpuLimit);
        controls.Controls.Add(new Label { Text = "°C", AutoSize = true, Margin = new Padding(2,5,4,0) });
        controls.Controls.Add(btnStart);
        controls.Controls.Add(btnStop);
        controls.Controls.Add(btnSave);

        var metrics = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        metrics.Controls.Add(lblCpu);
        metrics.Controls.Add(lblGpu);
        metrics.Controls.Add(lblCpuPower);
        metrics.Controls.Add(lblGpuPower);
        metrics.Controls.Add(lblCpuLoad);
        metrics.Controls.Add(lblGpuLoad);
        metrics.Controls.Add(lblElapsed);

        var graphGroup = new GroupBox { Text = "Temperature History", Dock = DockStyle.Fill, ForeColor = Color.White, Padding = new Padding(8) };
        graphGroup.Controls.Add(graph);
        var logGroup = new GroupBox { Text = "Test Log", Dock = DockStyle.Fill, ForeColor = Color.White, Padding = new Padding(8) };
        logGroup.Controls.Add(logBox);

        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 78));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        statusPanel.Controls.Add(lblStatus, 0, 0);
        statusPanel.Controls.Add(new Label { Text = "Safety auto-stop enabled", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.Orange }, 1, 0);

        root.Controls.Add(controls, 0, 0);
        root.Controls.Add(metrics, 0, 1);
        root.Controls.Add(graphGroup, 0, 2);
        root.Controls.Add(logGroup, 0, 3);
        root.Controls.Add(statusPanel, 0, 4);
        Controls.Add(root);
    }

    void InitMonitor()
    {
        try
        {
            monitor = new HardwareMonitor();
            monitor.Open();
            AddLog("Hardware monitor ready.");
            var snap = monitor.Read();
            AddLog($"CPU: {snap.CpuName ?? "Unknown"}");
            AddLog($"GPU: {snap.GpuName ?? "Unknown"}");
            uiTimer.Start();
        }
        catch (Exception ex)
        {
            AddLog("Hardware monitor error: " + ex.Message);
            MessageBox.Show("Hardware sensors could not be initialized. Stress test can still run, but temperature safety protection may be unavailable.\n\n" + ex.Message,
                "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            uiTimer.Start();
        }
    }

    void StartTest()
    {
        if (cts != null) return;
        if (!chkCpu.Checked && !chkGpu.Checked)
        {
            MessageBox.Show("Select CPU and/or GPU first.");
            return;
        }

        testSeconds = cmbMinutes.SelectedIndex switch { 0 => 60, 1 => 120, 2 => 300, 3 => 600, 4 => 900, _ => 1800 };
        maxCpuTemp = maxGpuTemp = maxCpuPower = maxGpuPower = 0;
        graph.Clear();
        logBox.Items.Clear();
        cts = new CancellationTokenSource();
        sw.Restart();
        stopping = false;
        btnStart.Enabled = false;
        btnStop.Enabled = true;
        chkCpu.Enabled = chkGpu.Enabled = cmbMinutes.Enabled = false;
        numCpuLimit.Enabled = numGpuLimit.Enabled = false;

        var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(dir);
        csvPath = Path.Combine(dir, $"CPUCK_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        csv = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
        csv.WriteLine("Time,ElapsedSec,CPUTempC,GPUTempC,CPULoadPct,GPULoadPct,CPUPowerW,GPUPowerW");
        csv.Flush();

        AddLog($"Test started: CPU={chkCpu.Checked}, GPU={chkGpu.Checked}, Duration={testSeconds}s");
        AddLog($"Safety limits: CPU {numCpuLimit.Value}°C / GPU {numGpuLimit.Value}°C");
        lblStatus.Text = "RUNNING - full load stress test";

        if (chkCpu.Checked) cpuTask = CpuStress.RunAsync(cts.Token);
        if (chkGpu.Checked) gpuTask = GpuStress.RunAsync(cts.Token, AddLogThreadSafe);
    }

    void UpdateUi()
    {
        HardwareSnapshot s = default;
        try { if (monitor != null) s = monitor.Read(); } catch { }

        lblCpu.Text = $"CPU  {(s.CpuTemp.HasValue ? s.CpuTemp.Value.ToString("0") + "°C" : "--°C")}";
        lblGpu.Text = $"GPU  {(s.GpuTemp.HasValue ? s.GpuTemp.Value.ToString("0") + "°C" : "--°C")}";
        lblCpuPower.Text = $"CPU Power {(s.CpuPower?.ToString("0.0") ?? "--")} W";
        lblGpuPower.Text = $"GPU Power {(s.GpuPower?.ToString("0.0") ?? "--")} W";
        lblCpuLoad.Text = $"CPU Load {(s.CpuLoad?.ToString("0") ?? "--")} %";
        lblGpuLoad.Text = $"GPU Load {(s.GpuLoad?.ToString("0") ?? "--")} %";
        lblElapsed.Text = $"Elapsed {sw.Elapsed:mm\\:ss}";

        if (s.CpuTemp is double ct) maxCpuTemp = Math.Max(maxCpuTemp, ct);
        if (s.GpuTemp is double gt) maxGpuTemp = Math.Max(maxGpuTemp, gt);
        if (s.CpuPower is double cp) maxCpuPower = Math.Max(maxCpuPower, cp);
        if (s.GpuPower is double gp) maxGpuPower = Math.Max(maxGpuPower, gp);
        graph.Add(s.CpuTemp, s.GpuTemp);

        if (cts != null && csv != null)
        {
            csv.WriteLine(string.Join(',',
                DateTime.Now.ToString("HH:mm:ss"),
                ((int)sw.Elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                F(s.CpuTemp), F(s.GpuTemp), F(s.CpuLoad), F(s.GpuLoad), F(s.CpuPower), F(s.GpuPower)));
            csv.Flush();

            if (s.CpuTemp >= (double)numCpuLimit.Value)
            {
                StopTest($"AUTO STOP: CPU reached {s.CpuTemp:0.0}°C");
                return;
            }
            if (s.GpuTemp >= (double)numGpuLimit.Value)
            {
                StopTest($"AUTO STOP: GPU reached {s.GpuTemp:0.0}°C");
                return;
            }
            if (sw.Elapsed.TotalSeconds >= testSeconds)
            {
                StopTest("Test completed");
                return;
            }
        }
    }

    static string F(double? v) => v?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";

    void StopTest(string reason, bool closeOnly = false)
    {
        if (stopping) return;
        stopping = true;
        var local = cts;
        cts = null;
        try { local?.Cancel(); } catch { }
        sw.Stop();
        try { csv?.Flush(); csv?.Dispose(); } catch { }
        csv = null;

        if (!closeOnly && local != null)
        {
            AddLog(reason);
            AddLog($"Result: CPU max {maxCpuTemp:0.0}°C / {maxCpuPower:0.0}W, GPU max {maxGpuTemp:0.0}°C / {maxGpuPower:0.0}W");
            lblStatus.Text = $"{reason} | CPU max {maxCpuTemp:0.0}°C | GPU max {maxGpuTemp:0.0}°C";
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            chkCpu.Enabled = chkGpu.Enabled = cmbMinutes.Enabled = true;
            numCpuLimit.Enabled = numGpuLimit.Enabled = true;
        }

        try { local?.Dispose(); } catch { }
        stopping = false;
    }

    void OpenLogs()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    void AddLog(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        logBox.Items.Add(line);
        logBox.TopIndex = Math.Max(0, logBox.Items.Count - 1);
    }

    void AddLogThreadSafe(string text)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => AddLog(text)); } catch { }
    }
}

internal static class CpuStress
{
    public static Task RunAsync(CancellationToken token) => Task.Run(() =>
    {
        int workers = Math.Max(1, Environment.ProcessorCount);
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, _ =>
        {
            double x = 0.123456789;
            while (!token.IsCancellationRequested)
            {
                for (int i = 1; i < 200_000; i++)
                    x = Math.Sqrt(x * x + i) * 1.00000001 + Math.Sin(x);
                if (x > 1e100 || double.IsNaN(x)) x = 0.123456789;
            }
        });
    }, token);
}

internal static class GpuStress
{
    public static Task RunAsync(CancellationToken token, Action<string> log) => Task.Run(() =>
    {
        try
        {
            using var context = Context.Create(builder => builder.Cuda());
            var devices = context.Devices.Where(d => d.AcceleratorType == AcceleratorType.Cuda).ToArray();
            if (devices.Length == 0)
            {
                log("GPU stress unavailable: no CUDA GPU detected. CPU test continues.");
                return;
            }

            using var accelerator = devices[0].CreateAccelerator(context);
            log($"GPU stress device: {accelerator.Name}");
            const int count = 16 * 1024 * 1024;
            using var buffer = accelerator.Allocate1D<float>(count);
            var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>>(Kernel);
            while (!token.IsCancellationRequested)
            {
                kernel(count, buffer.View);
                accelerator.Synchronize();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log("GPU stress error: " + ex.Message);
        }
    }, token);

    static void Kernel(Index1D index, ArrayView<float> data)
    {
        float x = data[index] + index * 0.000001f + 0.1234f;
        for (int i = 0; i < 512; i++)
            x = XMath.Sin(x) * XMath.Cos(x + 0.1f) + XMath.Sqrt(XMath.Abs(x) + 1.0f);
        data[index] = x;
    }
}

internal readonly record struct HardwareSnapshot(
    string? CpuName, string? GpuName,
    double? CpuTemp, double? GpuTemp,
    double? CpuPower, double? GpuPower,
    double? CpuLoad, double? GpuLoad);

internal sealed class HardwareMonitor : IVisitor, IDisposable
{
    readonly Computer computer = new() { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
    public void Open() { computer.Open(); computer.Accept(this); }
    public void Dispose() => computer.Close();
    public void VisitComputer(IComputer computer) { foreach (var hw in computer.Hardware) hw.Accept(this); }
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (var sub in hardware.SubHardware) sub.Accept(this); }
    public void VisitParameter(IParameter parameter) { }
    public void VisitSensor(ISensor sensor) { }

    public HardwareSnapshot Read()
    {
        computer.Accept(this);
        string? cpuName = null, gpuName = null;
        double? cpuTemp = null, gpuTemp = null, cpuPower = null, gpuPower = null, cpuLoad = null, gpuLoad = null;

        foreach (var hw in Flatten(computer.Hardware))
        {
            bool isCpu = hw.HardwareType == HardwareType.Cpu;
            bool isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
            if (isCpu) cpuName ??= hw.Name;
            if (isGpu && hw.HardwareType != HardwareType.GpuIntel) gpuName ??= hw.Name;

            foreach (var s in hw.Sensors)
            {
                if (!s.Value.HasValue) continue;
                double v = s.Value.Value;
                if (isCpu)
                {
                    if (s.SensorType == SensorType.Temperature && (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || cpuTemp == null)) cpuTemp = Math.Max(cpuTemp ?? double.MinValue, v);
                    if (s.SensorType == SensorType.Power && (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) || cpuPower == null)) cpuPower = Math.Max(cpuPower ?? 0, v);
                    if (s.SensorType == SensorType.Load && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)) cpuLoad = v;
                }
                if (isGpu && hw.HardwareType != HardwareType.GpuIntel)
                {
                    if (s.SensorType == SensorType.Temperature && (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || gpuTemp == null)) gpuTemp = Math.Max(gpuTemp ?? double.MinValue, v);
                    if (s.SensorType == SensorType.Power && (s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) || gpuPower == null)) gpuPower = Math.Max(gpuPower ?? 0, v);
                    if (s.SensorType == SensorType.Load && (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase))) gpuLoad = Math.Max(gpuLoad ?? 0, v);
                }
            }
        }
        return new(cpuName, gpuName, cpuTemp, gpuTemp, cpuPower, gpuPower, cpuLoad, gpuLoad);
    }

    static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> source)
    {
        foreach (var h in source)
        {
            yield return h;
            foreach (var s in Flatten(h.SubHardware)) yield return s;
        }
    }
}

public sealed class TempGraph : Panel
{
    readonly ConcurrentQueue<(double? cpu, double? gpu)> samples = new();
    public TempGraph() { DoubleBuffered = true; ResizeRedraw = true; }
    public void Add(double? cpu, double? gpu)
    {
        samples.Enqueue((cpu, gpu));
        while (samples.Count > 300) samples.TryDequeue(out _);
        Invalidate();
    }
    public void Clear() { while (samples.TryDequeue(out _)) { } Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(Color.Black);
        using var grid = new Pen(Color.FromArgb(35, 0, 180, 0));
        using var cpuPen = new Pen(Color.Lime, 2f);
        using var gpuPen = new Pen(Color.DeepSkyBlue, 2f);
        using var warnPen = new Pen(Color.Red, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        using var font = new Font("Segoe UI", 8f);
        for (int i = 0; i <= 10; i++)
        {
            float y = i * Height / 10f;
            g.DrawLine(grid, 0, y, Width, y);
            if (i < 10) g.DrawString((100 - i * 10).ToString(), font, Brushes.Gray, 2, y + 1);
        }
        for (int i = 0; i <= 20; i++) g.DrawLine(grid, i * Width / 20f, 0, i * Width / 20f, Height);
        g.DrawLine(warnPen, 0, Height * 0.1f, Width, Height * 0.1f);

        var arr = samples.ToArray();
        if (arr.Length < 2) return;
        float dx = Width / (float)Math.Max(1, arr.Length - 1);
        PointF? pc = null, pg = null;
        for (int i = 0; i < arr.Length; i++)
        {
            float x = i * dx;
            if (arr[i].cpu is double c)
            {
                var p = new PointF(x, Height - (float)Math.Clamp(c, 0, 100) / 100f * Height);
                if (pc != null) g.DrawLine(cpuPen, pc.Value, p);
                pc = p;
            }
            if (arr[i].gpu is double v)
            {
                var p = new PointF(x, Height - (float)Math.Clamp(v, 0, 100) / 100f * Height);
                if (pg != null) g.DrawLine(gpuPen, pg.Value, p);
                pg = p;
            }
        }
        g.DrawString("CPU", font, Brushes.Lime, Width - 90, 5);
        g.DrawString("GPU", font, Brushes.DeepSkyBlue, Width - 45, 5);
    }
}
