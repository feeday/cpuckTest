using System.Diagnostics;
using System.Globalization;

namespace CPUCKTest;

public sealed class MainForm : Form
{
    private const string RepoUrl = "https://github.com/feeday/cpuckTest";
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    readonly CheckBox chkCpu = new() { Text = "Stress CPU", Checked = true, AutoSize = true };
    readonly CheckBox chkGpu = new() { Text = "Stress GPU", Checked = true, AutoSize = true };
    readonly CheckBox chkRam = new() { Text = "Stress RAM", Checked = false, AutoSize = true };
    readonly CheckBox chkDisk = new() { Text = "Stress Disk R/W", Checked = false, AutoSize = true };
    readonly ComboBox cmbMinutes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 86 };
    readonly ComboBox cmbDiskMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 122 };
    readonly ComboBox cmbDisk = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly NumericUpDown numCpuLimit = new() { Minimum = 80, Maximum = 105, Value = 100, Width = 62 };
    readonly NumericUpDown numGpuLimit = new() { Minimum = 70, Maximum = 100, Value = 90, Width = 62 };
    readonly Button btnStart = new() { Text = "Start", Width = 92, Height = 32 };
    readonly Button btnStop = new() { Text = "Stop", Width = 92, Height = 32, Enabled = false };
    readonly Button btnExport = new() { Text = "Export CSV", Width = 110, Height = 32 };
    readonly Button btnLogs = new() { Text = "Open Logs", Width = 105, Height = 32 };

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
    readonly Label lblDiskRead = MakeSmall("Read -- MB/s");
    readonly Label lblDiskWrite = MakeSmall("Write -- MB/s");
    readonly Label lblDiskLoad = MakeSmall("Activity -- %");
    readonly Label lblDiskWritten = MakeSmall("Written -- GB");
    readonly Label lblElapsed = MakeSmall("Elapsed 00:00");
    readonly Label lblStatus = new()
    {
        Text = "Ready",
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(205, 210, 217)
    };
    readonly ListBox logBox = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        BackColor = Color.FromArgb(16, 17, 19),
        ForeColor = Color.FromArgb(218, 222, 228),
        BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 9.5f)
    };
    readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 1000 };
    readonly Stopwatch sw = new();

    HardwareMonitor? monitor;
    CancellationTokenSource? cts;
    int testSeconds = 120;
    double maxCpuTemp, maxGpuTemp, maxRamTemp, maxDiskTemp;
    double maxCpuPower, maxGpuPower, maxCpuClock, maxGpuClock;
    double maxRamRead, maxRamWrite, maxDiskRead, maxDiskWrite;
    string? csvPath;
    StreamWriter? csv;
    bool stopping;
    bool resumeUiTimerAfterMove;

    public MainForm()
    {
        Text = "CPUCK Test  ·  Stability & Hardware Monitor";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(1480, 770);
        MinimumSize = Size;
        MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(26, 27, 29);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        cmbMinutes.Items.AddRange(new object[] { "1 min", "2 min", "5 min", "10 min", "15 min", "30 min" });
        cmbMinutes.SelectedIndex = 1;
        cmbDiskMode.Items.AddRange(new object[] { "Stress", "Benchmark" });
        cmbDiskMode.SelectedIndex = 0;

        var disks = DiskCatalog.EnumerateFixedDisks();
        if (disks.Count == 0)
            disks.Add(DiskCatalog.SystemDriveFallback());
        foreach (var disk in disks)
            cmbDisk.Items.Add(disk);
        cmbDisk.SelectedIndex = DiskCatalog.FindSystemDriveIndex(disks);

        BuildUi();

        btnStart.Click += (_, _) => StartTest();
        btnStop.Click += (_, _) => StopTest("Stopped by user");
        btnExport.Click += (_, _) => ExportCsv();
        btnLogs.Click += (_, _) => OpenLogs();
        cmbDisk.SelectedIndexChanged += (_, _) =>
        {
            if (cts == null)
                UpdateUi();
        };
        uiTimer.Tick += (_, _) => UpdateUi();
        Shown += (_, _) => InitMonitor();
        FormClosing += (_, _) => Shutdown();
        FormClosed += (_, _) => DriverCleanup.SchedulePostExitDelete();
    }

    DiskChoice SelectedDisk =>
        cmbDisk.SelectedItem as DiskChoice ?? DiskCatalog.SystemDriveFallback();

    static Label MakeMetric(string text) => new()
    {
        Text = text,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI Semibold", 19f),
        ForeColor = Color.White,
        BackColor = Color.Transparent
    };

    static Label MakeSmall(string text) => new()
    {
        Text = text,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(194, 200, 209),
        Font = new Font("Segoe UI", 8.6f),
        BackColor = Color.Transparent
    };

    void BuildUi()
    {
        SuspendLayout();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 14, 18, 12),
            BackColor = Color.FromArgb(26, 27, 29),
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        root.Controls.Add(BuildToolbar(), 0, 0);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 5, 0, 8),
            Margin = Padding.Empty
        };
        for (int i = 0; i < 4; i++)
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        cards.Controls.Add(BuildCard("CPU", lblCpu, new[] { lblCpuClock, lblCpuPower, lblCpuLoad }), 0, 0);
        cards.Controls.Add(BuildCard("GPU", lblGpu, new[] { lblGpuClock, lblGpuPower, lblGpuLoad }), 1, 0);
        cards.Controls.Add(BuildCard("MEMORY", lblRam, new[] { lblRamClock, lblRamTemp, lblRamRead, lblRamWrite }), 2, 0);
        cards.Controls.Add(BuildCard("STORAGE", lblDisk, new[] { lblDiskRead, lblDiskWrite, lblDiskLoad, lblDiskWritten }), 3, 0);
        root.Controls.Add(cards, 0, 1);

        var logGroup = MakeSection("TEST LOG");
        logGroup.Controls.Add(logBox);
        root.Controls.Add(logGroup, 0, 2);

        root.Controls.Add(BuildFooter(), 0, 3);

        Controls.Add(root);
        ResumeLayout(true);
    }

    Control BuildToolbar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 3, 0, 0),
            BackColor = Color.Transparent
        };

        foreach (var cb in new[] { chkCpu, chkGpu, chkRam, chkDisk })
        {
            cb.Margin = new Padding(0, 8, 16, 0);
            cb.ForeColor = Color.White;
            cb.Font = new Font("Segoe UI Semibold", 9.5f);
            row1.Controls.Add(cb);
        }

        row1.Controls.Add(ToolLabel("Duration", 8));
        cmbMinutes.Margin = new Padding(6, 5, 12, 0);
        row1.Controls.Add(cmbMinutes);

        row1.Controls.Add(ToolLabel("Disk mode", 0));
        cmbDiskMode.Margin = new Padding(6, 5, 12, 0);
        row1.Controls.Add(cmbDiskMode);

        StyleButton(btnStart, true);
        StyleButton(btnStop, false);
        StyleButton(btnExport, false);
        StyleButton(btnLogs, false);
        row1.Controls.Add(btnStart);
        row1.Controls.Add(btnStop);
        row1.Controls.Add(btnExport);
        row1.Controls.Add(btnLogs);

        lblElapsed.AutoSize = true;
        lblElapsed.ForeColor = Color.FromArgb(165, 173, 184);
        lblElapsed.Font = new Font("Segoe UI", 9f);
        lblElapsed.Margin = new Padding(12, 11, 0, 0);
        row1.Controls.Add(lblElapsed);

        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            BackColor = Color.FromArgb(31, 33, 36),
            Padding = new Padding(10, 5, 10, 0)
        };
        row2.Controls.Add(ToolLabel("CPU stop ≥", 0));
        numCpuLimit.Margin = new Padding(6, 1, 4, 0);
        row2.Controls.Add(numCpuLimit);
        row2.Controls.Add(ToolLabel("°C", 0));
        row2.Controls.Add(ToolLabel("GPU stop ≥", 22));
        numGpuLimit.Margin = new Padding(6, 1, 4, 0);
        row2.Controls.Add(numGpuLimit);
        row2.Controls.Add(ToolLabel("°C", 0));

        row2.Controls.Add(ToolLabel("Disk", 24));
        cmbDisk.Margin = new Padding(6, 1, 16, 0);
        row2.Controls.Add(cmbDisk);

        row2.Controls.Add(new Label
        {
            Text = "Stress: write-through ≤8 GB · Benchmark: cached ≤4 GB · temp file deleted after stop",
            AutoSize = true,
            ForeColor = Color.FromArgb(255, 184, 64),
            Font = new Font("Segoe UI", 9f),
            Margin = new Padding(14, 5, 0, 0)
        });

        panel.Controls.Add(row1, 0, 0);
        panel.Controls.Add(row2, 0, 1);
        return panel;
    }

    Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        lblStatus.Font = new Font("Segoe UI", 9.5f);
        footer.Controls.Add(lblStatus, 0, 0);

        footer.Controls.Add(new Label
        {
            Text = "Safety auto-stop enabled",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(255, 184, 64),
            Font = new Font("Segoe UI", 9f)
        }, 1, 0);

        var source = new LinkLabel
        {
            Text = "Source · github.com/feeday/cpuckTest",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            LinkColor = Color.FromArgb(90, 180, 255),
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(90, 180, 255),
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand
        };
        source.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); } catch { }
        };
        footer.Controls.Add(source, 2, 0);
        return footer;
    }

    static Panel BuildCard(string title, Label primary, IEnumerable<Label> details)
    {
        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(38, 40, 44),
            Margin = new Padding(0, 0, 10, 0),
            Padding = new Padding(14, 10, 14, 10)
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        table.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(138, 148, 162),
            Font = new Font("Segoe UI Semibold", 8.5f)
        }, 0, 0);

        primary.Dock = DockStyle.Fill;
        primary.Margin = Padding.Empty;
        table.Controls.Add(primary, 0, 1);

        var detailGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        detailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        detailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        detailGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        detailGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        int index = 0;
        foreach (var label in details.Take(4))
        {
            label.Dock = DockStyle.Fill;
            label.Margin = Padding.Empty;
            detailGrid.Controls.Add(label, index % 2, index / 2);
            index++;
        }

        table.Controls.Add(detailGrid, 0, 2);
        outer.Controls.Add(table);
        return outer;
    }

    static GroupBox MakeSection(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(190, 197, 207),
        BackColor = Color.FromArgb(22, 23, 25),
        Font = new Font("Segoe UI Semibold", 8.5f),
        Padding = new Padding(8, 10, 8, 8),
        Margin = new Padding(0, 4, 0, 6)
    };

    static Label ToolLabel(string text, int leftMargin) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.FromArgb(218, 222, 228),
        Font = new Font("Segoe UI", 9f),
        Margin = new Padding(leftMargin, 5, 0, 0)
    };

    static void StyleButton(Button button, bool primary)
    {
        button.Margin = new Padding(0, 4, 8, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(65, 145, 255) : Color.FromArgb(102, 108, 116);
        button.BackColor = primary ? Color.FromArgb(36, 105, 210) : Color.FromArgb(34, 36, 39);
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 9f);
    }

    void InitMonitor()
    {
        try
        {
            monitor = new HardwareMonitor();
            monitor.Open();
            AddLog("Hardware monitor ready.");
            var s = monitor.Read(SelectedDisk.Model);
            AddLog($"CPU: {s.CpuName ?? "Unknown"}");
            AddLog($"GPU: {s.GpuName ?? "Unknown"}");
            AddLog($"RAM: {s.RamName ?? "Generic Memory"} / {FormatRamSpeed(s.RamSpeed)}");
            if (!s.RamTemp.HasValue)
                AddLog("RAM temperature: sensor not exposed by this laptop/firmware (N/A is normal).");
            AddLog($"Disk target: {SelectedDisk.Root.TrimEnd('\\')} · {SelectedDisk.Model}");
        }
        catch (Exception ex)
        {
            AddLog("Hardware monitor error: " + ex.Message);
        }
        uiTimer.Start();
        UpdateUi();
    }

    void StartTest()
    {
        if (cts != null)
            return;

        if (!chkCpu.Checked && !chkGpu.Checked && !chkRam.Checked && !chkDisk.Checked)
        {
            MessageBox.Show("Select CPU, GPU, RAM and/or Disk first.");
            return;
        }

        bool benchmark = cmbDiskMode.SelectedIndex == 1;
        if (chkDisk.Checked)
        {
            var disk = SelectedDisk;
            string msg = benchmark
                ? $"SSD Benchmark target: {disk.Root.TrimEnd('\\')} · {disk.Model}\n\nCached I/O, writes at most 4 GB per test. Continue?"
                : $"SSD Stress target: {disk.Root.TrimEnd('\\')} · {disk.Model}\n\nWrite-through I/O, writes at most 8 GB per test. Continue?";
            if (MessageBox.Show(msg, "CPUCK Test - SSD", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
        }

        testSeconds = cmbMinutes.SelectedIndex switch
        {
            0 => 60,
            1 => 120,
            2 => 300,
            3 => 600,
            4 => 900,
            _ => 1800
        };

        ResetMaxima();
        MemoryStress.ResetSpeeds();
        DiskStress.Reset();
        logBox.Items.Clear();

        cts = new CancellationTokenSource();
        sw.Restart();
        stopping = false;
        SetRunningUi(true);

        var dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        Directory.CreateDirectory(dir);
        csvPath = Path.Combine(dir, $"CPUCK_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        csv = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(true));
        csv.WriteLine("Time,ElapsedSec,CPUTempC,CPUClockMHz,CPULoadPct,CPUPowerW,GPUTempC,GPUClockMHz,GPULoadPct,GPUPowerW,RAMLoadPct,RAMSpeedMTs,RAMTempC,RAMReadGBps,RAMWriteGBps,DiskName,DiskMode,DiskTempC,DiskReadMBps,DiskWriteMBps,DiskActivityPct,DiskWrittenGB");
        csv.Flush();

        AddLog($"Test started: CPU={chkCpu.Checked}, GPU={chkGpu.Checked}, RAM={chkRam.Checked}, Disk={chkDisk.Checked}, DiskMode={(benchmark ? "Benchmark" : "Stress")}, Duration={testSeconds}s");
        AddLog($"Safety limits: CPU {numCpuLimit.Value}°C / GPU {numGpuLimit.Value}°C / SSD 80°C");
        if (chkDisk.Checked)
            AddLog($"Disk target: {SelectedDisk.Root.TrimEnd('\\')} · {SelectedDisk.Model}");
        lblStatus.Text = "RUNNING - stability stress test";

        if (chkCpu.Checked)
            _ = CpuStress.RunAsync(cts.Token);
        if (chkGpu.Checked)
            _ = GpuStress.RunAsync(cts.Token, AddLogThreadSafe);
        if (chkRam.Checked)
            _ = MemoryStress.RunAsync(cts.Token, AddLogThreadSafe);
        if (chkDisk.Checked)
            _ = DiskStress.RunAsync(cts.Token, AddLogThreadSafe, benchmark, SelectedDisk.Root);
    }

    void UpdateUi()
    {
        HardwareSnapshot s = default;
        try
        {
            if (monitor != null)
                s = monitor.Read(SelectedDisk.Model);
        }
        catch { }

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
        lblRamTemp.Visible = s.RamTemp.HasValue;
        if (s.RamTemp.HasValue)
            lblRamTemp.Text = $"RAM Temp {s.RamTemp:0}°C";
        lblRamRead.Text = $"RAM R {(ramRead is > 0 ? ramRead.Value.ToString("0.0") : "--")} GB/s";
        lblRamWrite.Text = $"RAM W {(ramWrite is > 0 ? ramWrite.Value.ToString("0.0") : "--")} GB/s";

        string drive = SelectedDisk.Root.TrimEnd('\\');
        lblDisk.Text = $"{drive}  {(s.DiskTemp?.ToString("0") ?? "--")}°C";
        lblDiskRead.Text = $"Read {(diskRead?.ToString("0.0") ?? "--")} MB/s";
        lblDiskWrite.Text = $"Write {(diskWrite?.ToString("0.0") ?? "--")} MB/s";
        lblDiskLoad.Text = $"Activity {diskActivity:0}%";
        lblDiskWritten.Text = $"Written {DiskStress.TotalWrittenGB:0.0} GB";
        lblElapsed.Text = $"Elapsed {sw.Elapsed:mm\\:ss}";

        UpdateMaxima(s, ramRead, ramWrite, diskRead, diskWrite);

        if (cts == null || csv == null)
            return;

        csv.WriteLine(string.Join(',',
            DateTime.Now.ToString("HH:mm:ss"),
            ((int)sw.Elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            F(s.CpuTemp), F(s.CpuClock), F(s.CpuLoad), F(s.CpuPower),
            F(s.GpuTemp), F(s.GpuClock), F(s.GpuLoad), F(s.GpuPower),
            F(s.RamLoad), F(s.RamSpeed), F(s.RamTemp), F(ramRead), F(ramWrite),
            Csv(s.DiskName ?? SelectedDisk.Model), Csv(DiskStress.ModeName),
            F(s.DiskTemp), F(diskRead), F(diskWrite), F(diskActivity), F(DiskStress.TotalWrittenGB)));
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
            StopTest("Test completed");
    }

    void ResetMaxima()
    {
        maxCpuTemp = maxGpuTemp = maxRamTemp = maxDiskTemp = 0;
        maxCpuPower = maxGpuPower = maxCpuClock = maxGpuClock = 0;
        maxRamRead = maxRamWrite = maxDiskRead = maxDiskWrite = 0;
    }

    void UpdateMaxima(HardwareSnapshot s, double? ramRead, double? ramWrite, double? diskRead, double? diskWrite)
    {
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
    }

    void StopTest(string reason, bool closeOnly = false)
    {
        if (stopping)
            return;

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
            if (chkRam.Checked)
            {
                string tempPart = maxRamTemp > 0 ? $", temp max {maxRamTemp:0.0}°C" : "";
                AddLog($"RAM: read max {maxRamRead:0.0} GB/s, write max {maxRamWrite:0.0} GB/s{tempPart}");
            }
            if (chkDisk.Checked)
                AddLog($"Disk ({DiskStress.ModeName}): temp {maxDiskTemp:0.0}°C, read max {maxDiskRead:0.0} MB/s, write max {maxDiskWrite:0.0} MB/s, written {DiskStress.TotalWrittenGB:0.0} GB");
            if (csvPath != null)
                AddLog($"CSV saved: {csvPath}");

            lblStatus.Text = $"{reason} | CPU {maxCpuTemp:0.0}°C | GPU {maxGpuTemp:0.0}°C | SSD {maxDiskTemp:0.0}°C";
            SetRunningUi(false);
        }

        try { local?.Dispose(); } catch { }
        stopping = false;
    }

    void SetRunningUi(bool running)
    {
        btnStart.Enabled = !running;
        btnStop.Enabled = running;
        chkCpu.Enabled = !running;
        chkGpu.Enabled = !running;
        chkRam.Enabled = !running;
        chkDisk.Enabled = !running;
        cmbMinutes.Enabled = !running;
        cmbDiskMode.Enabled = !running;
        cmbDisk.Enabled = !running;
        numCpuLimit.Enabled = !running;
        numGpuLimit.Enabled = !running;
    }

    void ExportCsv()
    {
        try { csv?.Flush(); } catch { }

        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
        {
            MessageBox.Show("No test CSV exists yet. Start a test first.");
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Filter = "CSV table (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = Path.GetFileName(csvPath),
            Title = "Export test table"
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            File.Copy(csvPath, dlg.FileName, true);
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
        if (!IsDisposed)
            try { BeginInvoke(new Action(() => AddLog(text))); } catch { }
    }

    void Shutdown()
    {
        uiTimer.Stop();
        StopTest("Application closing", true);

        try
        {
            monitor?.Dispose();
            monitor = null;
        }
        catch { }

        DriverCleanup.ReleaseDriver();
    }

    static string FormatRamSpeed(double? speed) =>
        speed.HasValue && speed.Value > 0 ? $"{speed.Value:0} MT/s" : "-- MT/s";

    static string F(double? value) =>
        value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";

    static string Csv(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : "\"" + text.Replace("\"", "\"\"") + "\"";

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmEnterSizeMove)
        {
            resumeUiTimerAfterMove = uiTimer.Enabled;
            if (resumeUiTimerAfterMove)
                uiTimer.Stop();
        }
        else if (m.Msg == WmExitSizeMove && resumeUiTimerAfterMove)
        {
            resumeUiTimerAfterMove = false;
            uiTimer.Start();
        }

        base.WndProc(ref m);
    }
}
