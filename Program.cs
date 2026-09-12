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
    readonly ComboBox cmbDiskMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
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
    readonly Label lblDiskLoad = MakeSmall("Disk Activity -- %");
    readonly Label lblDiskWritten = MakeSmall("Written -- GB");
    readonly Label lblElapsed = MakeSmall("Elapsed 00:00");
    readonly Label lblStatus = new() { Text = "Ready", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, ForeColor = Color.Gainsboro };
    readonly ListBox logBox = new() { Dock = DockStyle.Fill, IntegralHeight = false, BackColor = Color.FromArgb(20, 20, 20), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
    readonly TempGraph graph = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 1000 };

    HardwareMonitor? monitor;
    CancellationTokenSource? cts;
    Task? cpuTask, gpuTask, ramTask, diskTask;
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
        cmbDiskMode.Items.AddRange(new object[] { "Stress", "Benchmark" });
        cmbDiskMode.SelectedIndex = 0;

        BuildUi();
        btnStart.Click += (_, _) => StartTest();
        btnStop.Click += (_, _) => StopTest("Stopped by user");
        btnExport.Click += (_, _) => ExportCsv();
        btnLogs.Click += (_, _) => OpenLogs();
        uiTimer.Tick += (_, _) => UpdateUi();
        FormClosing += (_, _) => StopTest("Application closing", true);
        Shown += (_, _) => InitMonitor();
    }

    static Label MakeMetric(string text) => new()
    {
        Text = text, AutoSize = false, Width = 210, Height = 54,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI Semibold", 20f), BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White
    };

    static Label MakeSmall(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(9, 8, 9, 8), ForeColor = Color.Gainsboro
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
        controls.Controls.AddRange(new Control[] { chkCpu, chkGpu, chkRam, chkDisk, cmbMinutes, cmbDiskMode, btnStart, btnStop, btnExport, btnLogs });
        var limits = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        limits.Controls.AddRange(new Control[] { new Label { Text = "CPU stop ≥", AutoSize = true }, numCpuLimit, new Label { Text = "°C", AutoSize = true }, new Label { Text = "GPU stop ≥", AutoSize = true }, numGpuLimit, new Label { Text = "°C", AutoSize = true } });
        var metrics = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        metrics.Controls.AddRange(new Control[] { lblCpu, lblGpu, lblCpuClock, lblGpuClock, lblCpuPower, lblGpuPower, lblCpuLoad, lblGpuLoad });
        var secondary = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        secondary.Controls.AddRange(new Control[] { lblRam, lblRamClock, lblRamTemp, lblRamRead, lblRamWrite, lblDisk, lblDiskRead, lblDiskWrite, lblDiskLoad, lblDiskWritten, lblElapsed });
        var graphGroup = new GroupBox { Text = "Temperature History", Dock = DockStyle.Fill, ForeColor = Color.White, Padding = new Padding(8) };
        graphGroup.Controls.Add(graph);
        var logGroup = new GroupBox { Text = "Test Log", Dock = DockStyle.Fill, ForeColor = Color.White, Padding = new Padding(8) };
        logGroup.Controls.Add(logBox);
        root.Controls.Add(controls, 0, 0);
        root.Controls.Add(limits, 0, 1);
        root.Controls.Add(metrics, 0, 2);
        root.Controls.Add(secondary, 0, 3);
        root.Controls.Add(graphGroup, 0, 4);
        root.Controls.Add(logGroup, 0, 5);
        root.Controls.Add(lblStatus, 0, 6);
        Controls.Add(root);
    }

    void InitMonitor()
    {
        try
        {
            monitor = new HardwareMonitor();
            monitor.Open();
            AddLog("Hardware monitor ready.");
            var s = monitor.Read();
            AddLog($"CPU: {s.CpuName ?? "Unknown"}");
            AddLog($"GPU: {s.GpuName ?? "Unknown"}");
            AddLog($"RAM: {s.RamName ?? "Generic Memory"} / {FormatRamSpeed(s.RamSpeed)}");
            if (!s.RamTemp.HasValue) AddLog("RAM temperature: sensor not exposed by this laptop/firmware (N/A is normal).");
            AddLog($"Disk: {s.DiskName ?? "Unknown"}");
        }
        catch (Exception ex) { AddLog("Hardware monitor error: " + ex.Message); }
        uiTimer.Start();
    }

    void StartTest()
    {
        if (cts != null) return;
        if (!chkCpu.Checked && !chkGpu.Checked && !chkRam.Checked && !chkDisk.Checked)
        {
            MessageBox.Show("Select CPU, GPU, RAM and/or Disk first.");
            return;
        }

        bool benchmark = cmbDiskMode.SelectedIndex == 1;
        if (chkDisk.Checked)
        {
            string msg = benchmark
                ? "SSD Benchmark writes a temporary file to the Windows TEMP drive.\n\nBenchmark uses cached I/O and writes at most 4 GB per test. Continue?"
                : "SSD Stress writes a temporary file to the Windows TEMP drive.\n\nStress uses write-through I/O and writes at most 8 GB per test. Continue?";
            if (MessageBox.Show(msg, "CPUCK Test - SSD", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }

        testSeconds = cmbMinutes.SelectedIndex switch { 0 => 60, 1 => 120, 2 => 300, 3 => 600, 4 => 900, _ => 1800 };
        maxCpuTemp = maxGpuTemp = maxRamTemp = maxDiskTemp = 0;
        maxCpuPower = maxGpuPower = maxCpuClock = maxGpuClock = 0;
        maxRamRead = maxRamWrite = maxDiskRead = maxDiskWrite = 0;
        MemoryStress.ResetSpeeds();
        DiskStress.Reset();
        graph.Clear();
        logBox.Items.Clear();
        cts = new CancellationTokenSource();
        sw.Restart();
        stopping = false;
        btnStart.Enabled = false;
        btnStop.Enabled = true;
        chkCpu.Enabled = chkGpu.Enabled = chkRam.Enabled = chkDisk.Enabled = cmbMinutes.Enabled = cmbDiskMode.Enabled = false;
        numCpuLimit.Enabled = numGpuLimit.Enabled = false;

        var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(dir);
        csvPath = Path.Combine(dir, $"CPUCK_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        csv = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(true));
        csv.WriteLine("Time,ElapsedSec,CPUTempC,CPUClockMHz,CPULoadPct,CPUPowerW,GPUTempC,GPUClockMHz,GPULoadPct,GPUPowerW,RAMLoadPct,RAMSpeedMTs,RAMTempC,RAMReadGBps,RAMWriteGBps,DiskName,DiskMode,DiskTempC,DiskReadMBps,DiskWriteMBps,DiskActivityPct,DiskWrittenGB");
        csv.Flush();

        AddLog($"Test started: CPU={chkCpu.Checked}, GPU={chkGpu.Checked}, RAM={chkRam.Checked}, Disk={chkDisk.Checked}, DiskMode={(benchmark ? "Benchmark" : "Stress")}, Duration={testSeconds}s");
        AddLog($"Safety limits: CPU {numCpuLimit.Value}°C / GPU {numGpuLimit.Value}°C / SSD 80°C");
        lblStatus.Text = "RUNNING - stability stress test";

        if (chkCpu.Checked) cpuTask = CpuStress.RunAsync(cts.Token);
        if (chkGpu.Checked) gpuTask = GpuStress.RunAsync(cts.Token, AddLogThreadSafe);
        if (chkRam.Checked) ramTask = MemoryStress.RunAsync(cts.Token, AddLogThreadSafe);
        if (chkDisk.Checked) diskTask = DiskStress.RunAsync(cts.Token, AddLogThreadSafe, benchmark);
    }

    void UpdateUi()
    {
        HardwareSnapshot s = default;
        try { if (monitor != null) s = monitor.Read(); } catch { }

        double? ramRead = chkRam.Checked && cts != null ? MemoryStress.LastReadGBps : null;
        double? ramWrite = chkRam.Checked && cts != null ? MemoryStress.LastWriteGBps : null;
        double? diskRead = chkDisk.Checked && cts != null && DiskStress.LastReadMBps > 0 ? DiskStress.LastReadMBps : s.DiskReadMBps;
        double? diskWrite = chkDisk.Checked && cts != null && DiskStress.LastWriteMBps > 0 ? DiskStress.LastWriteMBps : s.DiskWriteMBps;
        double diskActivity = chkDisk.Checked && cts != null ? DiskStress.ActivityPct : (s.DiskLoad ?? 0);

        lblCpu.Text = $"CPU  {(s.CpuTemp?.ToString("0") ?? "--")}°C";
        lblGpu.Text = $"GPU  {(s.GpuTemp?.ToString("0") ?? "--")}°C";
        lblCpuClock.Text = $"CPU Clock {(s.CpuClock?.ToString("0") ?? "--")} MHz";
        lblGpuClock.Text = $"GPU Clock {(s.GpuClock?.ToString("0") ?? "--")} MHz";
        lblCpuPower.Text = $"CPU Power {(s.CpuPower?.ToString("0.0") ?? "--")} W";
        lblGpuPower.Text = $"GPU Power {(s.GpuPower?.ToString("0.0") ?? "--")} W";
        lblCpuLoad.Text = $"CPU Load {(s.CpuLoad?.ToString("0") ?? "--")} %";
        lblGpuLoad.Text = $"GPU Load {(s.GpuLoad?.ToString("0") ?? "--")} %";
        lblRam.Text = $"RAM {(s.RamLoad?.ToString("0") ?? "--")} %";
        lblRamClock.Text = $"RAM Speed {FormatRamSpeed(s.RamSpeed)}";
        lblRamTemp.Text = s.RamTemp.HasValue ? $"RAM Temp {s.RamTemp:0}°C" : "RAM Temp N/A";
        lblRamRead.Text = $"RAM R {(ramRead is > 0 ? ramRead.Value.ToString("0.0") : "--")} GB/s";
        lblRamWrite.Text = $"RAM W {(ramWrite is > 0 ? ramWrite.Value.ToString("0.0") : "--")} GB/s";
        lblDisk.Text = $"SSD {(s.DiskTemp?.ToString("0") ?? "--")}°C";
        lblDiskRead.Text = $"Read {(diskRead?.ToString("0.0") ?? "--")} MB/s";
        lblDiskWrite.Text = $"Write {(diskWrite?.ToString("0.0") ?? "--")} MB/s";
        lblDiskLoad.Text = $"Activity {diskActivity:0}%";
        lblDiskWritten.Text = $"Written {DiskStress.TotalWrittenGB:0.0} GB";
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
        if (diskRead is double dr) maxDiskRead = Math.Max(maxDiskRead, dr);
        if (diskWrite is double dw) maxDiskWrite = Math.Max(maxDiskWrite, dw);

        graph.Add(s.CpuTemp, s.GpuTemp, s.RamTemp, s.DiskTemp);

        if (cts != null && csv != null)
        {
            csv.WriteLine(string.Join(',', DateTime.Now.ToString("HH:mm:ss"), ((int)sw.Elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                F(s.CpuTemp), F(s.CpuClock), F(s.CpuLoad), F(s.CpuPower), F(s.GpuTemp), F(s.GpuClock), F(s.GpuLoad), F(s.GpuPower),
                F(s.RamLoad), F(s.RamSpeed), F(s.RamTemp), F(ramRead), F(ramWrite), Csv(s.DiskName), Csv(DiskStress.ModeName), F(s.DiskTemp), F(diskRead), F(diskWrite), F(diskActivity), F(DiskStress.TotalWrittenGB)));
            csv.Flush();

            if (s.CpuTemp >= (double)numCpuLimit.Value) { StopTest($"AUTO STOP: CPU reached {s.CpuTemp:0.0}°C"); return; }
            if (s.GpuTemp >= (double)numGpuLimit.Value) { StopTest($"AUTO STOP: GPU reached {s.GpuTemp:0.0}°C"); return; }
            if (chkDisk.Checked && s.DiskTemp >= 80) { StopTest($"AUTO STOP: SSD reached {s.DiskTemp:0.0}°C"); return; }
            if (sw.Elapsed.TotalSeconds >= testSeconds) { StopTest("Test completed"); return; }
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
            if (chkRam.Checked) AddLog($"RAM: read max {maxRamRead:0.0} GB/s, write max {maxRamWrite:0.0} GB/s");
            if (chkDisk.Checked) AddLog($"Disk ({DiskStress.ModeName}): temp {maxDiskTemp:0.0}°C, read max {maxDiskRead:0.0} MB/s, write max {maxDiskWrite:0.0} MB/s, written {DiskStress.TotalWrittenGB:0.0} GB");
            if (csvPath != null) AddLog($"CSV saved: {csvPath}");
            lblStatus.Text = $"{reason} | CPU {maxCpuTemp:0.0}°C | GPU {maxGpuTemp:0.0}°C | SSD {maxDiskTemp:0.0}°C";
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            chkCpu.Enabled = chkGpu.Enabled = chkRam.Enabled = chkDisk.Enabled = cmbMinutes.Enabled = cmbDiskMode.Enabled = true;
            numCpuLimit.Enabled = numGpuLimit.Enabled = true;
        }
        try { local?.Dispose(); } catch { }
        stopping = false;
    }

    void ExportCsv()
    {
        try { csv?.Flush(); } catch { }
        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath)) { MessageBox.Show("No test CSV exists yet. Start a test first."); return; }
        using var dlg = new SaveFileDialog { Filter = "CSV table (*.csv)|*.csv|All files (*.*)|*.*", FileName = Path.GetFileName(csvPath), Title = "Export test table" };
        if (dlg.ShowDialog() == DialogResult.OK) File.Copy(csvPath, dlg.FileName, true);
    }

    void OpenLogs()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    void AddLog(string text)
    {
        logBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
        logBox.TopIndex = Math.Max(0, logBox.Items.Count - 1);
    }

    void AddLogThreadSafe(string text)
    {
        if (!IsDisposed) try { BeginInvoke(() => AddLog(text)); } catch { }
    }
}

internal static class CpuStress
{
    public static Task RunAsync(CancellationToken token) => Task.Run(() =>
    {
        try
        {
            Parallel.For(0, Math.Max(1, Environment.ProcessorCount), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token }, _ =>
            {
                double x = 0.123456789;
                while (!token.IsCancellationRequested)
                {
                    for (int i = 1; i < 200_000; i++) x = Math.Sqrt(x * x + i) * 1.00000001 + Math.Sin(x);
                    if (double.IsNaN(x) || x > 1e100) x = 0.123456789;
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
            var device = context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda);
            if (device == null) { log("GPU stress unavailable: no CUDA GPU detected."); return; }
            using var accelerator = device.CreateAccelerator(context);
            log($"GPU stress device: {accelerator.Name}");
            const int count = 16 * 1024 * 1024;
            using var buffer = accelerator.Allocate1D<float>(count);
            var kernel = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>>(Kernel);
            while (!token.IsCancellationRequested) { kernel(count, buffer.View); accelerator.Synchronize(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log("GPU stress error: " + ex.Message); }
    }, token);

    static void Kernel(Index1D index, ArrayView<float> data)
    {
        float x = data[index] + index * 0.000001f + 0.1234f;
        for (int i = 0; i < 512; i++) x = XMath.Sin(x) * XMath.Cos(x + 0.1f) + XMath.Sqrt(XMath.Abs(x) + 1.0f);
        data[index] = x;
    }
}

internal static class MemoryStress
{
    static double lastReadGBps, lastWriteGBps;
    static long sink;
    public static double LastReadGBps => Volatile.Read(ref lastReadGBps);
    public static double LastWriteGBps => Volatile.Read(ref lastWriteGBps);
    public static void ResetSpeeds() { Volatile.Write(ref lastReadGBps, 0); Volatile.Write(ref lastWriteGBps, 0); }

    public static Task RunAsync(CancellationToken token, Action<string> log) => Task.Run(() =>
    {
        var chunks = new List<byte[]>();
        try
        {
            var (total, free) = ReadSystemMemory();
            long target = Math.Max(512L << 20, Math.Min(16L << 30, (long)Math.Max(0, free - (2L << 30)) * 60 / 100));
            if (total > 0) target = Math.Min(target, total * 60 / 100);
            const int chunkSize = 64 << 20;
            int wanted = (int)Math.Max(8, target / chunkSize);
            log($"RAM stress target: about {wanted * chunkSize / 1024.0 / 1024 / 1024:0.0} GB.");
            for (int i = 0; i < wanted && !token.IsCancellationRequested; i++) try { chunks.Add(new byte[chunkSize]); } catch { break; }
            long bytes = (long)chunks.Count * chunkSize;
            int workers = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
            int round = 1;
            while (!token.IsCancellationRequested)
            {
                var w = Stopwatch.StartNew();
                Parallel.For(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, i => chunks[i].AsSpan().Fill((byte)(round + i)));
                w.Stop();
                if (w.Elapsed.TotalSeconds > 0) Volatile.Write(ref lastWriteGBps, bytes / w.Elapsed.TotalSeconds / 1e9);
                var r = Stopwatch.StartNew();
                long roundSink = 0;
                Parallel.For<long>(0, chunks.Count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token }, () => 0,
                    (i, _, local) => { ulong x = 0; foreach (ulong v in MemoryMarshal.Cast<byte, ulong>(chunks[i])) x ^= v; return local ^ (long)x; },
                    local => Interlocked.Add(ref roundSink, local));
                r.Stop();
                Interlocked.Exchange(ref sink, roundSink);
                if (r.Elapsed.TotalSeconds > 0) Volatile.Write(ref lastReadGBps, bytes / r.Elapsed.TotalSeconds / 1e9);
                round++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log("RAM stress error: " + ex.Message); }
        finally { chunks.Clear(); GC.Collect(); log("RAM stress stopped."); }
    }, token);

    static (long total, long free) ReadSystemMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject o in searcher.Get()) return (Convert.ToInt64(o["TotalVisibleMemorySize"]) * 1024, Convert.ToInt64(o["FreePhysicalMemory"]) * 1024);
        }
        catch { }
        long f = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (f, f / 2);
    }
}

internal static class DiskStress
{
    static double lastReadMBps, lastWriteMBps, activityPct, totalWrittenGB;
    static int running;
    static string modeName = "Idle";
    public static double LastReadMBps => Volatile.Read(ref lastReadMBps);
    public static double LastWriteMBps => Volatile.Read(ref lastWriteMBps);
    public static double ActivityPct => Volatile.Read(ref activityPct);
    public static double TotalWrittenGB => Volatile.Read(ref totalWrittenGB);
    public static string ModeName => modeName;

    public static void Reset()
    {
        Volatile.Write(ref lastReadMBps, 0);
        Volatile.Write(ref lastWriteMBps, 0);
        Volatile.Write(ref activityPct, 0);
        Volatile.Write(ref totalWrittenGB, 0);
        Interlocked.Exchange(ref running, 0);
        modeName = "Idle";
    }

    public static Task RunAsync(CancellationToken token, Action<string> log, bool benchmark) => Task.Run(() =>
    {
        string file = Path.Combine(Path.GetTempPath(), benchmark ? "CPUCKTest_DiskBenchmark.tmp" : "CPUCKTest_DiskStress.tmp");
        const long fileSize = 1024L * 1024 * 1024;
        long maxWritten = benchmark ? 4L * 1024 * 1024 * 1024 : 8L * 1024 * 1024 * 1024;
        const int bufferSize = 16 * 1024 * 1024;
        long totalWritten = 0;
        var buffer = new byte[bufferSize];
        Random.Shared.NextBytes(buffer);
        modeName = benchmark ? "Benchmark" : "Stress";
        Interlocked.Exchange(ref running, 1);
        log($"Disk {modeName.ToLowerInvariant()} target: {Path.GetPathRoot(file)} TEMP drive; 1 GB test file, max writes {maxWritten / 1024 / 1024 / 1024} GB/test.");

        try
        {
            while (!token.IsCancellationRequested)
            {
                Volatile.Write(ref activityPct, 100);
                if (totalWritten < maxWritten)
                {
                    var w = Stopwatch.StartNew();
                    long done = 0;
                    FileOptions options = benchmark ? FileOptions.SequentialScan : FileOptions.SequentialScan | FileOptions.WriteThrough;
                    using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, options))
                    {
                        while (done < fileSize && !token.IsCancellationRequested)
                        {
                            int n = (int)Math.Min(buffer.Length, fileSize - done);
                            fs.Write(buffer, 0, n);
                            done += n;
                        }
                        if (!benchmark) fs.Flush(true); else fs.Flush();
                    }
                    w.Stop();
                    totalWritten += done;
                    Volatile.Write(ref totalWrittenGB, totalWritten / 1024.0 / 1024 / 1024);
                    if (w.Elapsed.TotalSeconds > 0) Volatile.Write(ref lastWriteMBps, done / w.Elapsed.TotalSeconds / 1024.0 / 1024.0);
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
                if (r.Elapsed.TotalSeconds > 0) Volatile.Write(ref lastReadMBps, read / r.Elapsed.TotalSeconds / 1024.0 / 1024.0);

                if (benchmark && totalWritten >= maxWritten) break;
                if (!benchmark && totalWritten >= maxWritten) Thread.Sleep(150);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log("Disk test error: " + ex.Message); }
        finally
        {
            Interlocked.Exchange(ref running, 0);
            Volatile.Write(ref activityPct, 0);
            try { if (File.Exists(file)) File.Delete(file); } catch { }
            log($"Disk {modeName.ToLowerInvariant()} stopped. Temporary file removed. Total test writes: {totalWritten / 1024.0 / 1024 / 1024:0.0} GB.");
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
        IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
        IsStorageEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true
    };
    double? cachedRamSpeed;

    public void Open() { computer.Open(); computer.Accept(this); cachedRamSpeed = ReadRamSpeedWmi(); }
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
        double? cpuClock = null, gpuClock = null, gpuMemClock = null, diskRead = null, diskWrite = null;

        foreach (var hw in Flatten(computer.Hardware))
        {
            bool isCpu = hw.HardwareType == HardwareType.Cpu;
            bool isGpu = hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd;
            bool isMemory = hw.HardwareType == HardwareType.Memory;
            bool isStorage = hw.HardwareType == HardwareType.Storage;
            if (isCpu) cpuName ??= hw.Name;
            if (isGpu) gpuName ??= hw.Name;
            if (isMemory) ramName ??= hw.Name;
            if (isStorage) diskName ??= hw.Name;

            foreach (var s in hw.Sensors)
            {
                if (!s.Value.HasValue) continue;
                double v = s.Value.Value;
                string n = s.Name;
                if (isCpu)
                {
                    if (s.SensorType == SensorType.Temperature && (n.Contains("Package", StringComparison.OrdinalIgnoreCase) || cpuTemp == null)) cpuTemp = Math.Max(cpuTemp ?? 0, v);
                    if (s.SensorType == SensorType.Power && (n.Contains("Package", StringComparison.OrdinalIgnoreCase) || cpuPower == null)) cpuPower = Math.Max(cpuPower ?? 0, v);
                    if (s.SensorType == SensorType.Load && n.Contains("Total", StringComparison.OrdinalIgnoreCase)) cpuLoad = v;
                    if (s.SensorType == SensorType.Clock && !n.Contains("Bus", StringComparison.OrdinalIgnoreCase)) cpuClock = Math.Max(cpuClock ?? 0, v);
                }
                if (isGpu)
                {
                    if (s.SensorType == SensorType.Temperature) gpuTemp = Math.Max(gpuTemp ?? 0, v);
                    if (s.SensorType == SensorType.Power) gpuPower = Math.Max(gpuPower ?? 0, v);
                    if (s.SensorType == SensorType.Load && (n.Contains("Core", StringComparison.OrdinalIgnoreCase) || n.Contains("GPU", StringComparison.OrdinalIgnoreCase))) gpuLoad = Math.Max(gpuLoad ?? 0, v);
                    if (s.SensorType == SensorType.Clock)
                    {
                        if (n.Contains("Memory", StringComparison.OrdinalIgnoreCase)) gpuMemClock = Math.Max(gpuMemClock ?? 0, v);
                        else gpuClock = Math.Max(gpuClock ?? 0, v);
                    }
                }
                if (isMemory)
                {
                    if (s.SensorType == SensorType.Load && !n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) ramLoad = Math.Max(ramLoad ?? 0, v);
                    if (s.SensorType == SensorType.Temperature) ramTemp = Math.Max(ramTemp ?? 0, v);
                    if (s.SensorType == SensorType.Clock && cachedRamSpeed == null) cachedRamSpeed = v * 2;
                }
                if (isStorage)
                {
                    if (s.SensorType == SensorType.Temperature) diskTemp = diskTemp == null ? v : Math.Max(diskTemp.Value, v);
                    if (s.SensorType == SensorType.Load && n.Contains("Activity", StringComparison.OrdinalIgnoreCase)) diskLoad = Math.Max(diskLoad ?? 0, v);
                    if (s.SensorType == SensorType.Throughput)
                    {
                        double mb = v / 1024 / 1024;
                        if (n.Contains("Read", StringComparison.OrdinalIgnoreCase)) diskRead = Math.Max(diskRead ?? 0, mb);
                        if (n.Contains("Write", StringComparison.OrdinalIgnoreCase)) diskWrite = Math.Max(diskWrite ?? 0, mb);
                    }
                }
            }
        }

        return new(cpuName, gpuName, ramName, diskName, cpuTemp, gpuTemp, ramTemp, diskTemp, cpuPower, gpuPower,
            cpuLoad, gpuLoad, ramLoad, diskLoad, cpuClock, gpuClock, gpuMemClock, cachedRamSpeed, diskRead, diskWrite);
    }

    static double? ReadRamSpeedWmi()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            double best = 0;
            foreach (ManagementObject o in s.Get())
            {
                double v = 0;
                if (o["ConfiguredClockSpeed"] != null) double.TryParse(o["ConfiguredClockSpeed"].ToString(), out v);
                if (v <= 0 && o["Speed"] != null) double.TryParse(o["Speed"].ToString(), out v);
                best = Math.Max(best, v);
            }
            return best > 0 ? best : null;
        }
        catch { return null; }
    }

    static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> source)
    {
        foreach (var h in source) { yield return h; foreach (var s in Flatten(h.SubHardware)) yield return s; }
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
        e.Graphics.Clear(Color.Black);
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
            e.Graphics.DrawLine(grid, 0, y, Width, y);
            if (i < 10) e.Graphics.DrawString((100 - i * 10).ToString(), font, Brushes.Gray, 2, y + 1);
        }
        for (int i = 0; i <= 20; i++) e.Graphics.DrawLine(grid, i * Width / 20f, 0, i * Width / 20f, Height);
        e.Graphics.DrawLine(warnPen, 0, Height * .1f, Width, Height * .1f);
        var a = samples.ToArray();
        if (a.Length >= 2)
        {
            float dx = Width / (float)Math.Max(1, a.Length - 1);
            DrawSeries(e.Graphics, a.Select(x => x.cpu).ToArray(), cpuPen, dx);
            DrawSeries(e.Graphics, a.Select(x => x.gpu).ToArray(), gpuPen, dx);
            DrawSeries(e.Graphics, a.Select(x => x.ram).ToArray(), ramPen, dx);
            DrawSeries(e.Graphics, a.Select(x => x.disk).ToArray(), diskPen, dx);
        }
        e.Graphics.DrawString("CPU", font, Brushes.Lime, Width - 170, 5);
        e.Graphics.DrawString("GPU", font, Brushes.DeepSkyBlue, Width - 130, 5);
        e.Graphics.DrawString("RAM", font, Brushes.Gold, Width - 90, 5);
        e.Graphics.DrawString("SSD", font, Brushes.MediumPurple, Width - 48, 5);
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
