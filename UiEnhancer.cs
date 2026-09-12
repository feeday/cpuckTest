using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CPUCKTest;

internal static class UiEnhancer
{
    const string RepoUrl = "https://github.com/feeday/cpuckTest";
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

        var uiTimer = Get<System.Windows.Forms.Timer>(main, "uiTimer");
        if (uiTimer != null)
            moveResizeGuard = new MoveResizeGuard(main, uiTimer);

        ApplyFixedWindow(main);
        RebuildLayout(main);
        EnableDoubleBuffering(main);

        var ramTempLabel = Get<Label>(main, "lblRamTemp");
        if (ramTempLabel != null && uiTimer != null)
        {
            void RefreshRamTempVisibility() =>
                ramTempLabel.Visible = !ramTempLabel.Text.Contains("N/A", StringComparison.OrdinalIgnoreCase)
                                       && !ramTempLabel.Text.Contains("--", StringComparison.OrdinalIgnoreCase);
            uiTimer.Tick += (_, _) => RefreshRamTempVisibility();
            RefreshRamTempVisibility();
        }

        main.FormClosing += (_, _) =>
        {
            try { moveResizeGuard?.Dispose(); } catch { }
            moveResizeGuard = null;
        };
    }

    static void ApplyFixedWindow(MainForm main)
    {
        main.SuspendLayout();
        main.FormBorderStyle = FormBorderStyle.FixedSingle;
        main.MaximizeBox = false;
        main.MinimizeBox = true;
        main.ClientSize = new Size(1480, 770);
        main.MinimumSize = main.Size;
        main.MaximumSize = main.Size;
        main.StartPosition = FormStartPosition.CenterScreen;
        main.Text = "CPUCK Test  ·  Stability & Hardware Monitor";
        main.ResumeLayout(false);
    }

    static void RebuildLayout(MainForm main)
    {
        var chkCpu = Get<CheckBox>(main, "chkCpu")!;
        var chkGpu = Get<CheckBox>(main, "chkGpu")!;
        var chkRam = Get<CheckBox>(main, "chkRam")!;
        var chkDisk = Get<CheckBox>(main, "chkDisk")!;
        var cmbMinutes = Get<ComboBox>(main, "cmbMinutes")!;
        var cmbDiskMode = Get<ComboBox>(main, "cmbDiskMode")!;
        var numCpuLimit = Get<NumericUpDown>(main, "numCpuLimit")!;
        var numGpuLimit = Get<NumericUpDown>(main, "numGpuLimit")!;
        var btnStart = Get<Button>(main, "btnStart")!;
        var btnStop = Get<Button>(main, "btnStop")!;
        var btnExport = Get<Button>(main, "btnExport")!;
        var btnLogs = Get<Button>(main, "btnLogs")!;

        var lblCpu = Get<Label>(main, "lblCpu")!;
        var lblGpu = Get<Label>(main, "lblGpu")!;
        var lblCpuPower = Get<Label>(main, "lblCpuPower")!;
        var lblGpuPower = Get<Label>(main, "lblGpuPower")!;
        var lblCpuClock = Get<Label>(main, "lblCpuClock")!;
        var lblGpuClock = Get<Label>(main, "lblGpuClock")!;
        var lblCpuLoad = Get<Label>(main, "lblCpuLoad")!;
        var lblGpuLoad = Get<Label>(main, "lblGpuLoad")!;
        var lblRam = Get<Label>(main, "lblRam")!;
        var lblRamClock = Get<Label>(main, "lblRamClock")!;
        var lblRamTemp = Get<Label>(main, "lblRamTemp")!;
        var lblRamRead = Get<Label>(main, "lblRamRead")!;
        var lblRamWrite = Get<Label>(main, "lblRamWrite")!;
        var lblDisk = Get<Label>(main, "lblDisk")!;
        var lblDiskRead = Get<Label>(main, "lblDiskRead")!;
        var lblDiskWrite = Get<Label>(main, "lblDiskWrite")!;
        var lblDiskLoad = Get<Label>(main, "lblDiskLoad")!;
        var lblDiskWritten = Get<Label>(main, "lblDiskWritten")!;
        var lblElapsed = Get<Label>(main, "lblElapsed")!;
        var lblStatus = Get<Label>(main, "lblStatus")!;
        var logBox = Get<ListBox>(main, "logBox")!;

        main.SuspendLayout();
        main.Controls.Clear();

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

        root.Controls.Add(BuildToolbar(chkCpu, chkGpu, chkRam, chkDisk, cmbMinutes, cmbDiskMode, numCpuLimit, numGpuLimit,
            btnStart, btnStop, btnExport, btnLogs, lblElapsed), 0, 0);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 5, 0, 8),
            Margin = Padding.Empty
        };
        for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cards.Controls.Add(BuildCard("CPU", lblCpu, new[] { lblCpuClock, lblCpuPower, lblCpuLoad }), 0, 0);
        cards.Controls.Add(BuildCard("GPU", lblGpu, new[] { lblGpuClock, lblGpuPower, lblGpuLoad }), 1, 0);
        cards.Controls.Add(BuildCard("MEMORY", lblRam, new[] { lblRamClock, lblRamTemp, lblRamRead, lblRamWrite }), 2, 0);
        cards.Controls.Add(BuildCard("STORAGE", lblDisk, new[] { lblDiskRead, lblDiskWrite, lblDiskLoad, lblDiskWritten }), 3, 0);
        root.Controls.Add(cards, 0, 1);

        var logGroup = MakeSection("TEST LOG");
        logBox.Dock = DockStyle.Fill;
        logBox.Margin = Padding.Empty;
        logBox.BackColor = Color.FromArgb(16, 17, 19);
        logBox.ForeColor = Color.FromArgb(218, 222, 228);
        logBox.Font = new Font("Consolas", 9.5f);
        logBox.BorderStyle = BorderStyle.None;
        logGroup.Controls.Add(logBox);
        root.Controls.Add(logGroup, 0, 2);

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

        lblStatus.Dock = DockStyle.Fill;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblStatus.ForeColor = Color.FromArgb(205, 210, 217);
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
        root.Controls.Add(footer, 0, 3);

        main.Controls.Add(root);
        main.ResumeLayout(true);
    }

    static Control BuildToolbar(
        CheckBox chkCpu, CheckBox chkGpu, CheckBox chkRam, CheckBox chkDisk,
        ComboBox cmbMinutes, ComboBox cmbDiskMode, NumericUpDown numCpuLimit, NumericUpDown numGpuLimit,
        Button btnStart, Button btnStop, Button btnExport, Button btnLogs, Label lblElapsed)
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
        cmbMinutes.Width = 86;
        cmbMinutes.Height = 31;
        cmbMinutes.Margin = new Padding(6, 5, 12, 0);
        row1.Controls.Add(cmbMinutes);

        row1.Controls.Add(ToolLabel("Disk mode", 0));
        cmbDiskMode.Width = 122;
        cmbDiskMode.Height = 31;
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
        numCpuLimit.Width = 62;
        numCpuLimit.Margin = new Padding(6, 1, 4, 0);
        row2.Controls.Add(numCpuLimit);
        row2.Controls.Add(ToolLabel("°C", 0));
        row2.Controls.Add(ToolLabel("GPU stop ≥", 22));
        numGpuLimit.Width = 62;
        numGpuLimit.Margin = new Padding(6, 1, 4, 0);
        row2.Controls.Add(numGpuLimit);
        row2.Controls.Add(ToolLabel("°C", 0));
        row2.Controls.Add(new Label
        {
            Text = "SSD: Stress = write-through ≤8 GB · Benchmark = cached peak ≤4 GB · temp file removed after stop",
            AutoSize = true,
            ForeColor = Color.FromArgb(255, 184, 64),
            Font = new Font("Segoe UI", 9f),
            Margin = new Padding(28, 5, 0, 0)
        });

        panel.Controls.Add(row1, 0, 0);
        panel.Controls.Add(row2, 0, 1);
        return panel;
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
        primary.AutoSize = false;
        primary.BackColor = Color.Transparent;
        primary.TextAlign = ContentAlignment.MiddleLeft;
        primary.ForeColor = Color.White;
        primary.Font = new Font("Segoe UI Semibold", 19f);
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
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.ForeColor = Color.FromArgb(194, 200, 209);
            label.Font = new Font("Segoe UI", 8.6f);
            label.Margin = Padding.Empty;
            label.BackColor = Color.Transparent;
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
        button.Height = 32;
        button.Margin = new Padding(0, 4, 8, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(65, 145, 255) : Color.FromArgb(102, 108, 116);
        button.BackColor = primary ? Color.FromArgb(36, 105, 210) : Color.FromArgb(34, 36, 39);
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 9f);
    }

    static T? Get<T>(MainForm main, string fieldName) where T : class
    {
        var field = typeof(MainForm).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(main) as T;
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
            foreach (var nested in DescendantsAndSelf(child)) yield return nested;
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
        else if (m.Msg == WM_EXITSIZEMOVE && resumeTimer)
        {
            resumeTimer = false;
            uiTimer.Start();
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
