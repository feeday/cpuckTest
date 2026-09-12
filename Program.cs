using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
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
    readonly CheckBox chkRam = new() { Text = "Stress RAM", Checked = false, AutoSize = true };
    readonly CheckBox chkDisk = new() { Text = "Stress Disk R/W", Checked = false, AutoSize = true };
    readonly ComboBox cmbMinutes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 85 };
    readonly NumericUpDown numCpuLimit = new() { Minimum = 80, Maximum = 105, Value = 100, Width = 65 };
    readonly NumericUpDown numGpuLimit = new() { Minimum = 70, Maximum = 100, Value = 90, Width = 65 };
    readonly Button btnStart = new() { Text = "Start", Width = 92, Height = 36 };
    readonly Button btnStop = new() { Text = "Stop", Width = 92, Height = 36, Enabled = false };
    readonly Button btnExport = new() { Text = "Export CSV", Width = 110, Height = 36 };
    readonly Button btnLogs = new() { Text = "Open Logs", Width = 105, Height = 36 };

    readonly Label lblCpu = MakeMetric("CPU  --°C");
    readonly Label lblGpu = MakeMetric("GPU  --°C");
    readonly Label lblCpuPower = MakeSmall("CPU Power -- W");
    readonly Label lblGpuPower = MakeSmall("GPU Power -- W");
    readonly Label lblCpuClock = MakeSmall("CPU Clock -- MHz");
    readonly Label lblGpuClock = MakeSmall("GPU Clock -- MHz");
    readonly Label lblCpuLoad = MakeSmall("CPU Load -- %");
    readonly Label lblGpuLoad = MakeSmall("GPU Load -- %");

    readonly Label lblRam = MakeSmall("RAM -- %");
    readonly Label lblRamClock = MakeSmall("RAM Speed -- MT/s");
    readonly Label lblRamTemp = MakeSmall("RAM Temp --°C");
    readonly Label lblRamRead = MakeSmall("RAM R -- GB/s");
    readonly Label lblRamWrite = MakeSmall("RAM W -- GB/s");
    readonly Label lblDisk = MakeSmall("SSD --°C");
    readonly Label lblDiskRead = MakeSmall("Disk R -- MB/s");
    readonly Label lblDiskWrite = MakeSmall("Disk W -- MB/s");
    readonly Label lblDiskLoad = MakeSmall("Disk Load -- %");
    readonly Label lblElapsed = MakeSmall("Elapsed 00:00");

    readonly Label lblStatus = new() { Text = "Ready", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, ForeColor = Color.Gainsboro };
    readonly ListBox logBox = new() { Dock = DockStyle.Fill, IntegralHeight = false, BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
    readonly TempGraph graph = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 1000 };

    HardwareMonitor? monitor;
    CancellationTokenSource? cts;
    Task? cpuTask;
    Task? gpuTask;
    Task? ramTask;
    Task? diskTask;
    readonly Stopwatch sw = new();
    int testSeconds = 120;
    double maxCpuTemp, maxGpuTemp, maxRamTemp, maxDiskTemp;
    double maxCpuPower, maxGpuPower, maxCpuClock, maxGpuClock;
    double maxRamRead, maxRamWrite, maxDiskRead, maxDiskWrite;
    string? csvPath;
    StreamWriter? csv;
    bool stopping;

    public MainForm()
    {
        Text = "CPUCK Test - CPU / GPU / RAM / Disk Stability & Hardware Monitor";
        Width = 1500;
        Height = 850;
        MinimumSize = new Size(1180, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        foreach (var m in new[] { "1 min", "2 min", "5 min", "10 min", "15 min", "30 min" }) cmbMinutes.Items.Add(m);
        cmbMinutes.SelectedIndex = 1;

        BuildUi();
        btnStart.Click += (_, _) => StartTest();
        btnStop.Click += (_, _) => StopTest("Stopped by user");
        btnExport.Click += (_, _) => ExportCsv();
        btnLogs.Click += (_, _) => OpenLogs();
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
        BackColor = Color.FromArgb(45, 45, 45),
        ForeColor = Color.White
    };

    static Label MakeSmall(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(9, 8, 9, 8),
        ForeColor = Color.Gainsboro
    };

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 57));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        controls.Controls.Add(chkCpu);
        controls.Controls.Add(chkGpu);
        controls.Controls.Add(chkRam);
        controls.Controls.Add(chkDisk);
        controls.Controls.Add(new Label { Text = "Duration", AutoSize = true, Margin = new Padding(18, 5, 4, 0) });
        controls.Controls.Add(cmbMinutes);
        controls.Controls.Add(btnStart);
        controls.Controls.Add(btnStop);
        controls.Controls.Add(btnExport);
        controls.Controls.Add(btnLogs);

        var limits = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        limits.Controls.Add(new Label { Text = "CPU stop ≥", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
        limits.Controls.Add(numCpuLimit);
        limits.Controls.Add(new Label { Text = "°C", AutoSize = true, Margin = new Padding(2, 5, 16, 0) });
        limits.Controls.Add(new Label { Text = "GPU stop ≥", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
        limits.Controls.Add(numGpuLimit);
        limits.Controls.Add(new Label { Text = "°C", AutoSize = true, Margin = new Padding(2, 5, 18, 0) });
        limits.Controls.Add(new Label { Text = "Disk stress writes max 8 GB/test; temporary file is deleted after stop", AutoSize = true, ForeColor = Color.Orange, Margin = new Padding(10, 5, 0, 0) });

        var metrics = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        metrics.Controls.Add(lblCpu);
        metrics.Controls.Add(lblGpu);
        metrics.Controls.Add(lblCpuClock);
        metrics.Controls.Add(lblGpuClock);
        metrics.Controls.Add(lblCpuPower);
        metrics.Controls.Add(lblGpuPower);
        metrics.Controls.Add(lblCpuLoad);
        metrics.Controls.Add(lblGpuLoad);

        var secondary = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        secondary.Controls.Add(lblRam);
        secondary.Controls.Add(lblRamClock);
        secondary.Controls.Add(lblRamTemp);
        secondary.Controls.Add(lblRamRead);
        secondary.Controls.Add(lblRamWrite);
        secondary.Controls.Add(lblDisk);
        secondary.Controls.Add(lblDiskRead);
        secondary.Controls.Add(lblDiskWrite);
        secondary.Controls.Add(lblDiskLoad);
        secondary.Controls.Add(lblElapsed);

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
        root.Controls.Add(limits, 0, 1);
        root.Controls.Add(metrics, 0, 2);
        root.Controls.Add(secondary, 0, 3);
        root.Controls.Add(graphGroup, 0, 4);
        root.Controls.Add(logGroup, 0, 5);
        root.Controls.Add(statusPanel, 0, 6);
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
            AddLog($"RAM: {snap.RamName ?? "Generic Memory"} / {FormatRamSpeed(snap.RamSpeed)}");
            if (!snap.RamTemp.HasValue) AddLog("RAM temperature: sensor not exposed by this laptop/firmware (N/A is normal).");
            AddLog($"Disk: {snap.DiskName ?? "Unknown"}");
            uiTimer.Start();
        }
        catch (Exception ex)
        {
            AddLog("Hardware monitor error: " + ex.Message);
            MessageBox.Show("Hardware sensors could not be initialized. Stress tests can still run, but some monitoring and safety protection may be unavailable.\n\n" + ex.Message,
                "CPUCK Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            uiTimer.Start();
        }
    }

    void StartTest()
    {
        if (cts != null) return;
        if (!chkCpu.Checked && !chkGpu.Checked && !chkRam.Checked && !chkDisk.Checked)
        {
            MessageBox.Show("Select CPU, GPU, RAM and/or Disk first.");
            return;
        }

        if (chkDisk.Checked)
        {
            var answer = MessageBox.Show(
                "Disk R/W stress writes a temporary test file to the Windows TEMP drive.\n\nWrites are capped at 8 GB per test to reduce SSD wear. Continue?",
                "CPUCK Test - Disk Stress", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        testSeconds = cmbMinutes.SelectedIndex switch { 0 => 60, 1 => 120, 2 => 300, 3 => 600, 4 => 900, _ => 1800 };
        maxCpuTemp = maxGpuTemp = maxRamTemp = maxDiskTemp = 0;
        maxCpuPower = maxGpuPower = maxCpuClock = maxGpuClock = 0;
        maxRamRead = maxRamWrite = maxDiskRead = maxDiskWrite = 0;
        MemoryStress.ResetSpeeds();
        DiskStress.ResetSpeeds();
        graph.Clear();
        logBox.Items.Clear();
        cts = new CancellationTokenSource();
        sw.Restart();
        stopping = false;
        btnStart.Enabled = false;
        btnStop.Enabled = true;
        chkCpu.Enabled = chkGpu.Enabled = chkRam.Enabled = chkDisk.Enabled = cmbMinutes.Enabled = false;
        numCpuLimit.Enabled = numGpuLimit.Enabled = false;

        var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(dir);
        csvPath = Path.Combine(dir, $"CPUCK_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        csv = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(true));
        csv.WriteLine("Time,ElapsedSec,CPUTempC,CPUClockMHz,CPULoadPct,CPUPowerW,GPUTempC,GPUClockMHz,GPUMemoryClockMHz,GPULoadPct,GPUPowerW,RAMLoadPct,RAMSpeedMTs,RAMTempC,RAMReadGBps,RAMWriteGBps,DiskName,DiskTempC,DiskReadMBps,DiskWriteMBps,DiskLoadPct");
        csv.Flush();

        AddLog($"Test started: CPU={chkCpu.Checked}, GPU={chkGpu.Checked}, RAM={chkRam.Checked}, Disk={chkDisk.Checked}, Duration={testSeconds}s");
        AddLog($"Safety limits: CPU {numCpuLimit.Value}°C / GPU {numGpuLimit.Value}°C");
        lblStatus.Text = "RUNNING - stability stress test";

        if (chkCpu.Checked) cpuTask = CpuStress.RunAsync(cts.Token);
        if (chkGpu.Checked) gpuTask = GpuStress.RunAsync(cts.Token, AddLogThreadSafe);
        if (chkRam.Checked) ramTask = MemoryStress.RunAsync(cts.Token, AddLogThreadSafe);
        if (chkDisk.Checked) diskTask = DiskStress.RunAsync(cts.Token, AddLogThreadSafe);
    }

    void UpdateUi()
    {
        HardwareSnapshot s = default;
        try { if (monitor != null) s = monitor.Read(); } catch { }

        double? ramRead = chkRam.Checked && cts != null ? MemoryStress.LastReadGBps : null;
        double? ramWrite = chkRam.Checked && cts != null ? MemoryStress.LastWriteGBps : null;
        double? diskReadStress = chkDisk.Checked && cts != null ? DiskStress.LastReadMBps : null;
        double? diskWriteStress = chkDisk.Checked && cts != null ? DiskStress.LastWriteMBps : null;
        double? displayDiskRead = diskReadStress is > 0 ? diskReadStress : s.DiskReadMBps;
        double? displayDiskWrite = diskWriteStress is > 0 ? diskWriteStress : s.DiskWriteMBps;

        lblCpu.Text = $"CPU  {(s.CpuTemp.HasValue ? s.CpuTemp.Value.ToString("0") + "°C" : "--°C")}";
        lblGpu.Text = $"GPU  {(s.GpuTemp.HasValue ? s.GpuTemp.Value.ToString("0") + "°C" : "--°C")}";
        lblCpuClock.Text = $"CPU Clock {(s.CpuClock?.ToString("0") ?? "--")} MHz";
        lblGpuClock.Text = $"GPU Clock {(s.GpuClock?.ToString("0") ?? "--")} MHz";
        lblCpuPower.Text = $"CPU Power {(s.CpuPower?.ToString("0.0") ?? "--")} W";
        lblGpuPower.Text = $"GPU Power {(s.GpuPower?.ToString("0.0") ?? "--")} W";
        lblCpuLoad.Text = $"CPU Load {(s.CpuLoad?.ToString("0") ?? "--")} %";
        lblGpuLoad.Text = $"GPU Load {(s.GpuLoad?.ToString("0") ?? "--")} %";

        lblRam.Text = $"RAM {(s.RamLoad?.ToString("0") ?? "--")} %";
        lblRamClock.Text = $"RAM Speed {FormatRamSpeed(s.RamSpeed)}";
        lblRamTemp.Text = s.RamTemp.HasValue ? $"RAM Temp {s.RamTemp.Value:0}°C" : "RAM Temp N/A";
        lblRamRead.Text = $"RAM R {(ramRead is > 0 ? ramRead.Value.ToString("0.0") : "--")} GB/s";
        lblRamWrite.Text = $"RAM W {(ramWrite is > 0 ? ramWrite.Value.ToString("0.0") : "--")} GB/s";
        lblDisk.Text = $"SSD {(s.DiskTemp?.ToString("0") ?? "--")}°C";
        lblDiskRead.Text = $"Disk R {(displayDiskRead?.ToString("0.0") ?? "--")} MB/s";
        lblDiskWrite.Text = $"Disk W {(displayDiskWrite?.ToString("0.0") ?? "--")} MB/s";
        lblDiskLoad.Text = $"Disk Load {(s.DiskLoad?.ToString("0") ?? "--")} %";
        lblElapsed.Text = $"Elapsed {sw.Elapsed:mm\\:ss}";

        if (s.CpuTemp is double ct) maxCpuTemp = Math.Max(maxCpuTemp, ct);
        if (s.GpuTemp is double gt) maxGpuTemp = Math.Max(maxGpuTemp, gt);
        if (s.RamTemp is double rt) maxRamTemp = Math.Max(maxRamTemp, rt);
        if (s.DiskTemp is double dt) maxDiskTemp = Math.Max(maxDiskTemp, dt);
        if (s.CpuPower is double cp) maxCpuPower = Math.Max(maxCpuPower, cp);
        if (s.GpuPower is double gp) maxGpuPower = Math.Max(maxGpuPower, gp);
        if (s.CpuClock is double cc) maxCpuClock = Math.Max(maxCpuClock, cc);
        if (s.GpuClock is double gc) maxGpuClock = Math.Max(maxGpuClock, gc);
        if (ramRead is double rr) maxRamRead = Math.Max(maxRamRead, rr);
        if (ramWrite is double rw) maxRamWrite = Math.Max(maxRamWrite, rw);
        if (displayDiskRead is double dr) maxDiskRead = Math.Max(maxDiskRead, dr);
        if (displayDiskWrite is double dw) maxDiskWrite = Math.Max(maxDiskWrite, dw);

        graph.Add(s.CpuTemp, s.GpuTemp, s.RamTemp, s.DiskTemp);

        if (cts != null && csv != null)
        {
            csv.WriteLine(string.Join(',',
                DateTime.Now.ToString("HH:mm:ss"),
                ((int)sw.Elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                F(s.CpuTemp), F(s.CpuClock), F(s.CpuLoad), F(s.CpuPower),
                F(s.GpuTemp), F(s.GpuClock), F(s.GpuMemoryClock), F(s.GpuLoad), F(s.GpuPower),
                F(s.RamLoad), F(s.RamSpeed), F(s.RamTemp), F(ramRead), F(ramWrite), Csv(s.DiskName), F(s.DiskTemp),
                F(displayDiskRead), F(displayDiskWrite), F(s.DiskLoad)));
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
            if (chkDisk.Checked && s.DiskTemp >= 80)
            {
                StopTest($"AUTO STOP: SSD reached {s.DiskTemp:0.0}°C");
                return;
            }
            if (sw.Elapsed.TotalSeconds >= testSeconds)
            {
                StopTest("Test completed");
                return;
            }
        }
    }

    static string FormatRamSpeed(double? speed) => speed.HasValue && speed.Value > 0 ? $"{speed.Value:0} MT/s" : "-- MT/s";
    static string F(double? v) => v?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
    static string Csv(string? text) => string.IsNullOrWhiteSpace(text) ? "" : "\"" + text.Replace("\"", "\"\"") + "\"";

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
            AddLog($"CPU: max {maxCpuTemp:0.0}°C / {maxCpuPower:0.0}W / {maxCpuClock:0}MHz");
            AddLog($"GPU: max {maxGpuTemp:0.0}°C / {maxGpuPower:0.0}W / {maxGpuClock:0}MHz");
            AddLog($"RAM: temp {(maxRamTemp > 0 ? maxRamTemp.ToString("0.0") + "°C" : "N/A")}, read max {maxRamRead:0.0} GB/s, write max {maxRamWrite:0.0} GB/s");
            AddLog($"Disk: temp {(maxDiskTemp > 0 ? maxDiskTemp.ToString("0.0") + "°C" : "N/A")}, read max {maxDiskRead:0.0} MB/s, write max {maxDiskWrite:0.0} MB/s");
            if (csvPath != null) AddLog($"CSV saved: {csvPath}");
            lblStatus.Text = $"{reason} | CPU {maxCpuTemp:0.0}°C | GPU {maxGpuTemp:0.0}°C | SSD {maxDiskTemp:0.0}°C";
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            chkCpu.Enabled = chkGpu.Enabled = chkRam.Enabled = chkDisk.Enabled = cmbMinutes.Enabled = true;
            numCpuLimit.Enabled = numGpuLimit.Enabled = true;
        }

        try { local?.Dispose(); } catch { }
        stopping = false;
    }

    void ExportCsv()
    {
        try { csv?.Flush(); } catch { }
        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
        {
            MessageBox.Show("No test CSV exists yet. Start a test first.", "CPUCK Test");
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Filter = "CSV table (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = Path.GetFileName(csvPath),
            Title = "Export test table"
        };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            File.Copy(csvPath, dlg.FileName, true);
            AddLog("CSV exported: " + dlg.FileName);
        }
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
        try
        {
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
        }
        catch (OperationCanceledException) { }
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
                log("GPU stress unavailable: no CUDA GPU detected. Other tests continue.");
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

internal static class MemoryStress
{
    static double lastReadGBps;
    static double lastWriteGBps;
    static long sink;

    public static double LastReadGBps => Volatile.Read(ref lastReadGBps);
    public static double LastWriteGBps => Volatile.Read(ref lastWriteGBps);
    public static void ResetSpeeds()
    {
        Volatile.Write(ref lastReadGBps, 0);
        Volatile.Write(ref lastWriteGBps, 0);
    }

    public static Task RunAsync(CancellationToken token, Action<string> log) => Task.Run(() =>
    {
        var chunks = new List<byte[]>();
        try
        {
            var (total, free) = ReadSystemMemory();
            long reserve = 2L * 1024 * 1024 * 1024;
            long usable = Math.Max(512L * 1024 * 1024, free - reserve);
            long target = Math.Min(16L * 1024 * 1024 * 1024, (long)(usable * 0.60));
            if (total > 0) target = Math.Min(target, (long)(total * 0.60));
            target = Math.Max(512L * 1024 * 1024, target);

            const int chunkSize = 64 * 1024 * 1024;
            int wanted = (int)Math.Max(8, target / chunkSize);
            log($"RAM stress target: about {wanted * chunkSize / 1024.0 / 1024 / 1024:0.0} GB (keeps system headroom).");

            for (int i = 0; i < wanted && !token.IsCancellationRequested; i++)
            {
                try { chunks.Add(new byte[chunkSize]); }
                catch (OutOfMemoryException) { break; }
            }
            if (chunks.Count == 0)
            {
                log("RAM stress could not allocate test memory.");
                return;
            }

            long bytes = (long)chunks.Count * chunkSize;
            int workers = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
            int round = 1;
            while (!token.IsCancellationRequested)
            {
                var w = Stopwatch.StartNew();
                Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, i =>
                {
                    chunks[i].AsSpan().Fill((byte)(round + i));
                });
                w.Stop();
                if (w.Elapsed.TotalSeconds > 0)
                    Volatile.Write(ref lastWriteGBps, bytes / w.Elapsed.TotalSeconds / 1_000_000_000.0);

                var r = Stopwatch.StartNew();
                long roundSink = 0;
                Parallel.For<long>(0, chunks.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token },
                    () => 0L,
                    (i, _, local) =>
                    {
                        ulong x = 0;
                        foreach (ulong v in MemoryMarshal.Cast<byte, ulong>(chunks[i].AsSpan())) x ^= v;
                        return local ^ unchecked((long)x);
                    },
                    local => Interlocked.Add(ref roundSink, local));
                r.Stop();
                Interlocked.Exchange(ref sink, roundSink);
                if (r.Elapsed.TotalSeconds > 0)
                    Volatile.Write(ref lastReadGBps, bytes / r.Elapsed.TotalSeconds / 1_000_000_000.0);
                round++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log("RAM stress error: " + ex.Message); }
        finally
        {
            chunks.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            log("RAM stress stopped and test memory released.");
        }
    }, token);

    static (long total, long free) ReadSystemMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject o in searcher.Get())
            {
                long totalKb = Convert.ToInt64(o["TotalVisibleMemorySize"] ?? 0, CultureInfo.InvariantCulture);
                long freeKb = Convert.ToInt64(o["FreePhysicalMemory"] ?? 0, CultureInfo.InvariantCulture);
                return (totalKb * 1024, freeKb * 1024);
            }
        }
        catch { }
        long fallback = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (fallback, fallback / 2);
    }
}

internal static class DiskStress
{
    static double lastReadMBps;
    static double lastWriteMBps;
    public static double LastReadMBps => Volatile.Read(ref lastReadMBps);
    public static double LastWriteMBps => Volatile.Read(ref lastWriteMBps);
    public static void ResetSpeeds()
    {
        Volatile.Write(ref lastReadMBps, 0);
        Volatile.Write(ref lastWriteMBps, 0);
    }

    public static Task RunAsync(CancellationToken token, Action<string> log) => Task.Run(() =>
    {
        string file = Path.Combine(Path.GetTempPath(), "CPUCKTest_DiskStress.tmp");
        const long fileSize = 1024L * 1024 * 1024;
        const long maxWritten = 8L * 1024 * 1024 * 1024;
        const int bufferSize = 16 * 1024 * 1024;
        long totalWritten = 0;
        var buffer = new byte[bufferSize];
        Random.Shared.NextBytes(buffer);
        log($"Disk stress target: {Path.GetPathRoot(file)} TEMP drive; 1 GB test file, max writes 8 GB/test.");

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (totalWritten < maxWritten)
                {
                    var w = Stopwatch.StartNew();
                    long done = 0;
                    using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024,
                               FileOptions.SequentialScan | FileOptions.WriteThrough))
                    {
                        while (done < fileSize && !token.IsCancellationRequested)
                        {
                            int n = (int)Math.Min(buffer.Length, fileSize - done);
                            fs.Write(buffer, 0, n);
                            done += n;
                        }
                        fs.Flush(true);
                    }
                    w.Stop();
                    totalWritten += done;
                    if (w.Elapsed.TotalSeconds > 0)
                        Volatile.Write(ref lastWriteMBps, done / w.Elapsed.TotalSeconds / 1024.0 / 1024.0);
                }

                if (token.IsCancellationRequested || !File.Exists(file)) break;

                var r = Stopwatch.StartNew();
                long read = 0;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan))
                {
                    int n;
                    while (!token.IsCancellationRequested && (n = fs.Read(buffer, 0, buffer.Length)) > 0) read += n;
                }
                r.Stop();
                if (r.Elapsed.TotalSeconds > 0)
                    Volatile.Write(ref lastReadMBps, read / r.Elapsed.TotalSeconds / 1024.0 / 1024.0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log("Disk stress error: " + ex.Message); }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            log($"Disk stress stopped. Temporary file removed. Total test writes: {totalWritten / 1024.0 / 1024 / 1024:0.0} GB.");
        }
    }, token);
}

internal readonly record struct HardwareSnapshot(
    string? CpuName, string? GpuName, string? RamName, string? DiskName,
    double? CpuTemp, double? GpuTemp, double? RamTemp, double? DiskTemp,
    double? CpuPower, double? GpuPower,
    double? CpuLoad, double? GpuLoad, double? RamLoad, double? DiskLoad,
    double? CpuClock, double? GpuClock, double? GpuMemoryClock, double? RamSpeed,
    double? DiskReadMBps, double? DiskWriteMBps);

internal sealed class HardwareMonitor : IVisitor, IDisposable
{
    readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsStorageEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true
    };

    double? cachedRamSpeed;

    public void Open()
    {
        computer.Open();
        computer.Accept(this);
        cachedRamSpeed = ReadRamSpeedWmi();
    }

    public void Dispose() => computer.Close();
    public void VisitComputer(IComputer computer) { foreach (var hw in computer.Hardware) hw.Accept(this); }
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (var sub in hardware.SubHardware) sub.Accept(this); }
    public void VisitParameter(IParameter parameter) { }
    public void VisitSensor(ISensor sensor) { }

    public HardwareSnapshot Read()
    {
        computer.Accept(this);
        string? cpuName = null, gpuName = null, ramName = null, diskName = null;
        double? cpuTemp = null, gpuTemp = null, ramTemp = null, diskTemp = null;
        double? cpuPower = null, gpuPower = null, cpuLoad = null, gpuLoad = null, ramLoad = null, diskLoad = null;
        double? cpuClock = null, gpuClock = null, gpuMemoryClock = null;
        double? diskRead = null, diskWrite = null;

        foreach (var hw in Flatten(computer.Hardware))
        {
            bool isCpu = hw.HardwareType == HardwareType.Cpu;
            bool isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
            bool isDiscreteGpu = isGpu && hw.HardwareType != HardwareType.GpuIntel;
            bool isMemory = hw.HardwareType == HardwareType.Memory;
            bool isStorage = hw.HardwareType == HardwareType.Storage;

            if (isCpu) cpuName ??= hw.Name;
            if (isDiscreteGpu) gpuName ??= hw.Name;
            if (isMemory) ramName ??= hw.Name;
            if (isStorage && diskName == null) diskName = hw.Name;

            foreach (var s in hw.Sensors)
            {
                if (!s.Value.HasValue) continue;
                double v = s.Value.Value;
                string n = s.Name;

                if (isCpu)
                {
                    if (s.SensorType == SensorType.Temperature)
                    {
                        if (n.Contains("Package", StringComparison.OrdinalIgnoreCase)) cpuTemp = v;
                        else if (cpuTemp == null) cpuTemp = v;
                    }
                    if (s.SensorType == SensorType.Power)
                    {
                        if (n.Contains("Package", StringComparison.OrdinalIgnoreCase)) cpuPower = v;
                        else if (cpuPower == null) cpuPower = v;
                    }
                    if (s.SensorType == SensorType.Load && n.Contains("Total", StringComparison.OrdinalIgnoreCase)) cpuLoad = v;
                    if (s.SensorType == SensorType.Clock && !n.Contains("Bus", StringComparison.OrdinalIgnoreCase)) cpuClock = Math.Max(cpuClock ?? 0, v);
                }

                if (isDiscreteGpu)
                {
                    if (s.SensorType == SensorType.Temperature && (n.Contains("Core", StringComparison.OrdinalIgnoreCase) || gpuTemp == null)) gpuTemp = Math.Max(gpuTemp ?? double.MinValue, v);
                    if (s.SensorType == SensorType.Power && (n.Contains("GPU", StringComparison.OrdinalIgnoreCase) || n.Contains("Package", StringComparison.OrdinalIgnoreCase) || gpuPower == null)) gpuPower = Math.Max(gpuPower ?? 0, v);
                    if (s.SensorType == SensorType.Load && (n.Contains("Core", StringComparison.OrdinalIgnoreCase) || n.Contains("GPU", StringComparison.OrdinalIgnoreCase))) gpuLoad = Math.Max(gpuLoad ?? 0, v);
                    if (s.SensorType == SensorType.Clock)
                    {
                        if (n.Contains("Memory", StringComparison.OrdinalIgnoreCase)) gpuMemoryClock = Math.Max(gpuMemoryClock ?? 0, v);
                        else if (n.Contains("Core", StringComparison.OrdinalIgnoreCase) || n.Contains("Graphics", StringComparison.OrdinalIgnoreCase)) gpuClock = Math.Max(gpuClock ?? 0, v);
                    }
                }

                if (isMemory)
                {
                    if (s.SensorType == SensorType.Load && n.Contains("Memory", StringComparison.OrdinalIgnoreCase) && !n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) ramLoad = v;
                    if (s.SensorType == SensorType.Temperature) ramTemp = Math.Max(ramTemp ?? double.MinValue, v);
                    if (s.SensorType == SensorType.Clock && cachedRamSpeed == null) cachedRamSpeed = v * 2.0;
                }

                if (!isGpu && !isStorage && s.SensorType == SensorType.Temperature && LooksLikeRamTemperature(hw.Name, n))
                    ramTemp = Math.Max(ramTemp ?? double.MinValue, v);

                if (isStorage)
                {
                    if (s.SensorType == SensorType.Temperature && diskTemp == null) diskTemp = v;
                    if (s.SensorType == SensorType.Load && (n.Contains("Total Activity", StringComparison.OrdinalIgnoreCase) || n.Contains("Activity", StringComparison.OrdinalIgnoreCase))) diskLoad = Math.Max(diskLoad ?? 0, v);
                    if (s.SensorType == SensorType.Throughput)
                    {
                        double mbps = v / 1024.0 / 1024.0;
                        if (n.Contains("Read", StringComparison.OrdinalIgnoreCase)) diskRead = Math.Max(diskRead ?? 0, mbps);
                        if (n.Contains("Write", StringComparison.OrdinalIgnoreCase)) diskWrite = Math.Max(diskWrite ?? 0, mbps);
                    }
                }
            }
        }

        return new(cpuName, gpuName, ramName, diskName,
            cpuTemp, gpuTemp, ramTemp, diskTemp,
            cpuPower, gpuPower,
            cpuLoad, gpuLoad, ramLoad, diskLoad,
            cpuClock, gpuClock, gpuMemoryClock, cachedRamSpeed,
            diskRead, diskWrite);
    }

    static bool LooksLikeRamTemperature(string hardwareName, string sensorName)
    {
        string x = hardwareName + " " + sensorName;
        string[] keys = { "DIMM", "DRAM", "DDR", "RAM", "Memory", "TSOD", "SPD Hub" };
        return keys.Any(k => x.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    static double? ReadRamSpeedWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            double best = 0;
            foreach (ManagementObject o in searcher.Get())
            {
                double value = 0;
                if (o["ConfiguredClockSpeed"] != null) double.TryParse(o["ConfiguredClockSpeed"].ToString(), out value);
                if (value <= 0 && o["Speed"] != null) double.TryParse(o["Speed"].ToString(), out value);
                best = Math.Max(best, value);
            }
            return best > 0 ? best : null;
        }
        catch { return null; }
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
    readonly ConcurrentQueue<(double? cpu, double? gpu, double? ram, double? disk)> samples = new();
    public TempGraph() { DoubleBuffered = true; ResizeRedraw = true; }
    public void Add(double? cpu, double? gpu, double? ram, double? disk)
    {
        samples.Enqueue((cpu, gpu, ram, disk));
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
        using var ramPen = new Pen(Color.Gold, 1.5f);
        using var diskPen = new Pen(Color.MediumPurple, 1.5f);
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
        if (arr.Length >= 2)
        {
            float dx = Width / (float)Math.Max(1, arr.Length - 1);
            DrawSeries(g, arr.Select(x => x.cpu).ToArray(), cpuPen, dx);
            DrawSeries(g, arr.Select(x => x.gpu).ToArray(), gpuPen, dx);
            DrawSeries(g, arr.Select(x => x.ram).ToArray(), ramPen, dx);
            DrawSeries(g, arr.Select(x => x.disk).ToArray(), diskPen, dx);
        }

        g.DrawString("CPU", font, Brushes.Lime, Width - 170, 5);
        g.DrawString("GPU", font, Brushes.DeepSkyBlue, Width - 130, 5);
        g.DrawString("RAM", font, Brushes.Gold, Width - 90, 5);
        g.DrawString("SSD", font, Brushes.MediumPurple, Width - 48, 5);
    }

    void DrawSeries(Graphics g, double?[] values, Pen pen, float dx)
    {
        PointF? prev = null;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is not double v) { prev = null; continue; }
            var p = new PointF(i * dx, Height - (float)Math.Clamp(v, 0, 100) / 100f * Height);
            if (prev != null) g.DrawLine(pen, prev.Value, p);
            prev = p;
        }
    }
}
