using System.Management;
using System.Reflection;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;

namespace CPUCKTest;

internal static class DiskSelectorEnhancer
{
    static bool attached;
    static MainForm? main;
    static ComboBox? combo;
    static System.Windows.Forms.Timer? uiTimer;
    static DiskSensorReader? sensorReader;
    static string? originalTemp;
    static string? originalTmp;
    static string? selectedTempDir;
    static List<DiskChoice> disks = new();

    [ModuleInitializer]
    internal static void Initialize()
    {
        Application.Idle += TryAttach;
    }

    static void TryAttach(object? sender, EventArgs e)
    {
        if (attached) return;
        main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main == null) return;

        var mode = GetField<ComboBox>(main, "cmbDiskMode");
        var cpuLimit = GetField<NumericUpDown>(main, "numCpuLimit");
        uiTimer = GetField<System.Windows.Forms.Timer>(main, "uiTimer");
        if (mode?.Parent is not FlowLayoutPanel || cpuLimit?.Parent is not FlowLayoutPanel limits || uiTimer == null)
            return; // UiEnhancer has not rebuilt the layout yet; retry on the next idle pass.

        attached = true;
        Application.Idle -= TryAttach;
        originalTemp = Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.Process);
        originalTmp = Environment.GetEnvironmentVariable("TMP", EnvironmentVariableTarget.Process);

        disks = EnumerateFixedDisks();
        if (disks.Count == 0)
            disks.Add(new DiskChoice(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "System drive", ""));

        combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 250,
            Height = 29,
            Margin = new Padding(6, 1, 16, 0)
        };
        foreach (var d in disks) combo.Items.Add(d);

        string systemRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        int defaultIndex = disks.FindIndex(d => d.Root.TrimEnd('\\').Equals(systemRoot, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;

        var label = new Label
        {
            Text = "Disk",
            AutoSize = true,
            ForeColor = Color.FromArgb(218, 222, 228),
            Font = new Font("Segoe UI", 9f),
            Margin = new Padding(24, 5, 0, 0)
        };

        limits.Controls.Add(label);
        limits.Controls.Add(combo);
        // Put the selector after the CPU/GPU temperature limits and before the SSD hint.
        limits.Controls.SetChildIndex(label, Math.Min(6, limits.Controls.Count - 1));
        limits.Controls.SetChildIndex(combo, Math.Min(7, limits.Controls.Count - 1));

        combo.SelectedIndexChanged += (_, _) => ApplySelectedDisk();
        ApplySelectedDisk();

        try
        {
            sensorReader = new DiskSensorReader();
            sensorReader.Open();
        }
        catch { sensorReader = null; }

        uiTimer.Tick += UiTimer_Tick;
        main.FormClosing += (_, _) => Cleanup();
    }

    static void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (main == null || combo == null) return;

        var ctsField = typeof(MainForm).GetField("cts", BindingFlags.Instance | BindingFlags.NonPublic);
        bool running = ctsField?.GetValue(main) != null;
        combo.Enabled = !running;

        if (combo.SelectedItem is not DiskChoice selected) return;

        double? temp = null;
        try { temp = sensorReader?.ReadTemperature(selected.Model); } catch { }

        var lblDisk = GetField<Label>(main, "lblDisk");
        if (lblDisk != null)
        {
            string drive = selected.Root.TrimEnd('\\');
            lblDisk.Text = temp.HasValue ? $"{drive}  {temp.Value:0}°C" : $"{drive}  --°C";
            lblDisk.Tag = selected.Model;
        }
    }

    static void ApplySelectedDisk()
    {
        if (combo?.SelectedItem is not DiskChoice selected) return;
        try
        {
            string dir = Path.Combine(selected.Root, "CPUCKTestTemp");
            Directory.CreateDirectory(dir);
            selectedTempDir = dir;
            Environment.SetEnvironmentVariable("TEMP", dir, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TMP", dir, EnvironmentVariableTarget.Process);
        }
        catch
        {
            // If the selected root cannot host the temporary file, keep the previous TEMP.
        }
    }

    static void Cleanup()
    {
        try { uiTimer!.Tick -= UiTimer_Tick; } catch { }
        try { sensorReader?.Dispose(); } catch { }
        sensorReader = null;

        try { Environment.SetEnvironmentVariable("TEMP", originalTemp, EnvironmentVariableTarget.Process); } catch { }
        try { Environment.SetEnvironmentVariable("TMP", originalTmp, EnvironmentVariableTarget.Process); } catch { }

        try
        {
            if (!string.IsNullOrWhiteSpace(selectedTempDir) && Directory.Exists(selectedTempDir) && !Directory.EnumerateFileSystemEntries(selectedTempDir).Any())
                Directory.Delete(selectedTempDir, false);
        }
        catch { }
    }

    static List<DiskChoice> EnumerateFixedDisks()
    {
        var result = new List<DiskChoice>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive");
            foreach (ManagementObject disk in searcher.Get())
            {
                string device = disk["DeviceID"]?.ToString() ?? "";
                string model = disk["Model"]?.ToString()?.Trim() ?? device;
                foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
                {
                    foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                    {
                        string? id = logical["DeviceID"]?.ToString();
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        string root = id.EndsWith(":", StringComparison.Ordinal) ? id + "\\" : id;
                        if (!result.Any(x => x.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                            result.Add(new DiskChoice(root, model, device));
                    }
                }
            }
        }
        catch { }

        return result.OrderBy(x => x.Root, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static T? GetField<T>(MainForm form, string name) where T : class
    {
        return typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as T;
    }

    sealed record DiskChoice(string Root, string Model, string PhysicalDevice)
    {
        public override string ToString() => $"{Root.TrimEnd('\\')} · {Model}";
    }
}

internal sealed class DiskSensorReader : IVisitor, IDisposable
{
    readonly Computer computer = new() { IsStorageEnabled = true };

    public void Open()
    {
        computer.Open();
        computer.Accept(this);
    }

    public double? ReadTemperature(string model)
    {
        computer.Accept(this);
        var storage = computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();
        if (storage.Count == 0) return null;

        string wanted = Normalize(model);
        IHardware? best = null;
        int bestScore = -1;
        foreach (var hw in storage)
        {
            int score = MatchScore(wanted, Normalize(hw.Name));
            if (score > bestScore)
            {
                bestScore = score;
                best = hw;
            }
        }
        if (best == null) return null;

        var temps = best.Sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 120)
            .ToList();
        if (temps.Count == 0) return null;

        ISensor? sensor = temps.FirstOrDefault(s => s.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                       ?? temps.FirstOrDefault(s => s.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase))
                       ?? temps.FirstOrDefault(s => s.Name.Contains("Drive", StringComparison.OrdinalIgnoreCase))
                       ?? temps[0];
        return sensor.Value;
    }

    static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    static int MatchScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)) return 10000 + Math.Min(a.Length, b.Length);
        int score = 0;
        foreach (string token in SplitTokens(a)) if (b.Contains(token, StringComparison.OrdinalIgnoreCase)) score += token.Length;
        return score;
    }

    static IEnumerable<string> SplitTokens(string s)
    {
        for (int i = 0; i < s.Length; i += 4)
        {
            int len = Math.Min(4, s.Length - i);
            if (len >= 3) yield return s.Substring(i, len);
        }
    }

    public void Dispose() => computer.Close();
    public void VisitComputer(IComputer computer) { foreach (var h in computer.Hardware) h.Accept(this); }
    public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (var s in hardware.SubHardware) s.Accept(this); }
    public void VisitParameter(IParameter parameter) { }
    public void VisitSensor(ISensor sensor) { }
}
