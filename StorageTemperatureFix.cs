using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using LibreHardwareMonitor.Hardware;

namespace CPUCKTest;

internal static class StorageTemperatureFix
{
    static bool attached;
    static Computer? storageComputer;
    static Label? diskLabel;
    static ListBox? logBox;
    static TempGraph? graph;
    static bool logged;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Application.Idle += Attach;
    }

    static void Attach(object? sender, EventArgs e)
    {
        if (attached) return;
        var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
        if (main == null) return;

        attached = true;
        Application.Idle -= Attach;

        diskLabel = Get<Label>(main, "lblDisk");
        logBox = Get<ListBox>(main, "logBox");
        graph = Get<TempGraph>(main, "graph");
        var timer = Get<System.Windows.Forms.Timer>(main, "uiTimer");
        if (timer == null) return;

        try
        {
            storageComputer = new Computer { IsStorageEnabled = true };
            storageComputer.Open();
        }
        catch
        {
            storageComputer = null;
        }

        // MainForm.UpdateUi is registered first. This handler runs afterwards and corrects
        // the displayed SSD temperature using one physical drive and its primary/composite
        // sensor instead of taking the maximum of every temperature sensor on every drive.
        timer.Tick += (_, _) => Refresh();
        main.FormClosing += (_, _) =>
        {
            try { storageComputer?.Close(); } catch { }
            storageComputer = null;
        };
    }

    static void Refresh()
    {
        if (storageComputer == null || diskLabel == null) return;

        try
        {
            foreach (var hw in storageComputer.Hardware) hw.Update();
            var drives = storageComputer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();
            if (drives.Count == 0) return;

            // Keep behavior deterministic: use the same first storage device that the main
            // monitor presents as DiskName instead of mixing temperatures from other drives.
            var drive = drives[0];
            var temps = drive.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue)
                .Select(s => new { s.Name, Value = (double)s.Value!.Value })
                .Where(x => x.Value > 0 && x.Value < 100)
                .ToList();

            if (temps.Count == 0) return;

            // NVMe drives commonly expose a normal/composite temperature plus Temperature 1/2
            // controller or NAND hotspot readings. The old code used Max(), which could turn a
            // normal 30-50°C SSD into an apparent 80+°C reading.
            var primary = temps.FirstOrDefault(x =>
                              x.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                          ?? temps.FirstOrDefault(x =>
                              x.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase))
                          ?? temps.FirstOrDefault(x =>
                              x.Name.Contains("Drive", StringComparison.OrdinalIgnoreCase))
                          ?? temps.OrderBy(x => x.Value).First();

            diskLabel.Text = $"SSD {primary.Value:0}°C";
            CorrectLastGraphSample(primary.Value);

            if (!logged && logBox != null)
            {
                logged = true;
                logBox.Items.Add($"[{DateTime.Now:HH:mm:ss}] SSD temp source: {drive.Name} / {primary.Name} ({primary.Value:0.0}°C)");
                logBox.TopIndex = Math.Max(0, logBox.Items.Count - 1);
            }
        }
        catch { }
    }

    static void CorrectLastGraphSample(double diskTemp)
    {
        if (graph == null) return;
        try
        {
            var field = typeof(TempGraph).GetField("samples", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(graph) is not ConcurrentQueue<(double? cpu, double? gpu, double? ram, double? disk)> q) return;
            var items = q.ToArray();
            if (items.Length == 0) return;
            while (q.TryDequeue(out _)) { }
            for (int i = 0; i < items.Length - 1; i++) q.Enqueue(items[i]);
            var last = items[^1];
            q.Enqueue((last.cpu, last.gpu, last.ram, diskTemp));
            graph.Invalidate();
        }
        catch { }
    }

    static T? Get<T>(MainForm main, string fieldName) where T : class
    {
        var field = typeof(MainForm).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(main) as T;
    }
}
