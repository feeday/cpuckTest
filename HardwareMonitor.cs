using System.Management;
using LibreHardwareMonitor.Hardware;

namespace CPUCKTest;

internal sealed record DiskChoice(string Root, string Model, string PhysicalDevice)
{
    public override string ToString() => $"{Root.TrimEnd('\\')} · {Model}";
}

internal static class DiskCatalog
{
    public static List<DiskChoice> EnumerateFixedDisks()
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
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        string root = id.EndsWith(":", StringComparison.Ordinal) ? id + "\\" : id;
                        if (!result.Any(x => x.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
                            result.Add(new DiskChoice(root, model, device));
                    }
                }
            }
        }
        catch { }

        return result
            .OrderBy(x => x.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static DiskChoice SystemDriveFallback()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return new DiskChoice(root, "System drive", "");
    }

    public static int FindSystemDriveIndex(IReadOnlyList<DiskChoice> disks)
    {
        string systemRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        int index = -1;
        for (int i = 0; i < disks.Count; i++)
        {
            if (disks[i].Root.TrimEnd('\\').Equals(systemRoot, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        return index >= 0 ? index : 0;
    }
}

internal readonly record struct HardwareSnapshot(
    string? CpuName,
    string? GpuName,
    string? RamName,
    string? DiskName,
    double? CpuTemp,
    double? GpuTemp,
    double? RamTemp,
    double? DiskTemp,
    double? CpuPower,
    double? GpuPower,
    double? CpuLoad,
    double? GpuLoad,
    double? RamLoad,
    double? DiskLoad,
    double? CpuClock,
    double? GpuClock,
    double? RamSpeed,
    double? DiskReadMBps,
    double? DiskWriteMBps);

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

    public void VisitComputer(IComputer computer)
    {
        foreach (var hardware in computer.Hardware)
            hardware.Accept(this);
    }

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            sub.Accept(this);
    }

    public void VisitParameter(IParameter parameter) { }
    public void VisitSensor(ISensor sensor) { }

    public HardwareSnapshot Read(string? selectedDiskModel = null)
    {
        computer.Accept(this);

        var allHardware = Flatten(computer.Hardware).ToList();
        var selectedStorage = SelectStorage(
            allHardware.Where(h => h.HardwareType == HardwareType.Storage),
            selectedDiskModel);

        string? cpuName = null;
        string? gpuName = null;
        string? ramName = null;
        string? diskName = selectedStorage?.Name;

        double? cpuTemp = null;
        double? gpuTemp = null;
        double? ramTemp = null;
        double? diskTemp = null;
        double? cpuPower = null;
        double? gpuPower = null;
        double? cpuLoad = null;
        double? gpuLoad = null;
        double? ramLoad = null;
        double? diskLoad = null;
        double? cpuClock = null;
        double? gpuClock = null;
        double? diskRead = null;
        double? diskWrite = null;

        foreach (var hardware in allHardware)
        {
            bool isCpu = hardware.HardwareType == HardwareType.Cpu;
            bool isGpu = hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd;
            bool isMemory = hardware.HardwareType == HardwareType.Memory;
            bool isSelectedStorage = ReferenceEquals(hardware, selectedStorage);

            if (isCpu)
                cpuName ??= hardware.Name;
            if (isGpu)
                gpuName ??= hardware.Name;
            if (isMemory)
                ramName ??= hardware.Name;

            if (isSelectedStorage)
                diskTemp = SelectPrimaryDiskTemperature(hardware);

            foreach (var sensor in hardware.Sensors)
            {
                if (!sensor.Value.HasValue)
                    continue;

                double value = sensor.Value.Value;
                string name = sensor.Name;

                if (isCpu)
                {
                    if (sensor.SensorType == SensorType.Temperature &&
                        (name.Contains("Package", StringComparison.OrdinalIgnoreCase) || cpuTemp == null))
                    {
                        cpuTemp = Math.Max(cpuTemp ?? 0, value);
                    }

                    if (sensor.SensorType == SensorType.Power &&
                        (name.Contains("Package", StringComparison.OrdinalIgnoreCase) || cpuPower == null))
                    {
                        cpuPower = Math.Max(cpuPower ?? 0, value);
                    }

                    if (sensor.SensorType == SensorType.Load &&
                        name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        cpuLoad = value;
                    }

                    if (sensor.SensorType == SensorType.Clock &&
                        !name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                    {
                        cpuClock = Math.Max(cpuClock ?? 0, value);
                    }
                }

                if (isGpu)
                {
                    if (sensor.SensorType == SensorType.Temperature)
                        gpuTemp = Math.Max(gpuTemp ?? 0, value);

                    if (sensor.SensorType == SensorType.Power)
                        gpuPower = Math.Max(gpuPower ?? 0, value);

                    if (sensor.SensorType == SensorType.Load &&
                        (name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("GPU", StringComparison.OrdinalIgnoreCase)))
                    {
                        gpuLoad = Math.Max(gpuLoad ?? 0, value);
                    }

                    if (sensor.SensorType == SensorType.Clock &&
                        !name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                    {
                        gpuClock = Math.Max(gpuClock ?? 0, value);
                    }
                }

                if (isMemory)
                {
                    if (sensor.SensorType == SensorType.Load &&
                        !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    {
                        ramLoad = Math.Max(ramLoad ?? 0, value);
                    }

                    if (sensor.SensorType == SensorType.Temperature)
                        ramTemp = Math.Max(ramTemp ?? 0, value);

                    if (sensor.SensorType == SensorType.Clock && cachedRamSpeed == null)
                        cachedRamSpeed = value * 2;
                }

                if (isSelectedStorage)
                {
                    if (sensor.SensorType == SensorType.Load &&
                        name.Contains("Activity", StringComparison.OrdinalIgnoreCase))
                    {
                        diskLoad = Math.Max(diskLoad ?? 0, value);
                    }

                    if (sensor.SensorType == SensorType.Throughput)
                    {
                        double mb = value / 1024 / 1024;
                        if (name.Contains("Read", StringComparison.OrdinalIgnoreCase))
                            diskRead = Math.Max(diskRead ?? 0, mb);
                        if (name.Contains("Write", StringComparison.OrdinalIgnoreCase))
                            diskWrite = Math.Max(diskWrite ?? 0, mb);
                    }
                }
            }
        }

        return new HardwareSnapshot(
            cpuName,
            gpuName,
            ramName,
            diskName,
            cpuTemp,
            gpuTemp,
            ramTemp,
            diskTemp,
            cpuPower,
            gpuPower,
            cpuLoad,
            gpuLoad,
            ramLoad,
            diskLoad,
            cpuClock,
            gpuClock,
            cachedRamSpeed,
            diskRead,
            diskWrite);
    }

    static IHardware? SelectStorage(IEnumerable<IHardware> drives, string? model)
    {
        var list = drives.ToList();
        if (list.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(model) ||
            model.Equals("System drive", StringComparison.OrdinalIgnoreCase))
        {
            return list[0];
        }

        string wanted = Normalize(model);
        IHardware? best = null;
        int bestScore = -1;

        foreach (var drive in list)
        {
            int score = MatchScore(wanted, Normalize(drive.Name));
            if (score > bestScore)
            {
                bestScore = score;
                best = drive;
            }
        }

        return best ?? list[0];
    }

    static double? SelectPrimaryDiskTemperature(IHardware drive)
    {
        var temps = drive.Sensors
            .Where(s =>
                s.SensorType == SensorType.Temperature &&
                s.Value.HasValue &&
                s.Value.Value > 0 &&
                s.Value.Value < 120)
            .ToList();

        if (temps.Count == 0)
            return null;

        var primary =
            temps.FirstOrDefault(s => s.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
            ?? temps.FirstOrDefault(s => s.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase))
            ?? temps.FirstOrDefault(s => s.Name.Contains("Drive", StringComparison.OrdinalIgnoreCase))
            ?? temps.OrderBy(s => s.Value!.Value).First();

        return primary.Value;
    }

    static double? ReadRamSpeedWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");

            double best = 0;
            foreach (ManagementObject item in searcher.Get())
            {
                double value = 0;

                if (item["ConfiguredClockSpeed"] != null)
                    double.TryParse(item["ConfiguredClockSpeed"]!.ToString(), out value);

                if (value <= 0 && item["Speed"] != null)
                    double.TryParse(item["Speed"]!.ToString(), out value);

                best = Math.Max(best, value);
            }

            return best > 0 ? best : null;
        }
        catch
        {
            return null;
        }
    }

    static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    static int MatchScore(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;

        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
            b.Contains(a, StringComparison.OrdinalIgnoreCase))
        {
            return 10_000 + Math.Min(a.Length, b.Length);
        }

        int score = 0;
        foreach (string token in SplitTokens(a))
        {
            if (b.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += token.Length;
        }
        return score;
    }

    static IEnumerable<string> SplitTokens(string value)
    {
        for (int i = 0; i < value.Length; i += 4)
        {
            int len = Math.Min(4, value.Length - i);
            if (len >= 3)
                yield return value.Substring(i, len);
        }
    }

    static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> source)
    {
        foreach (var hardware in source)
        {
            yield return hardware;
            foreach (var sub in Flatten(hardware.SubHardware))
                yield return sub;
        }
    }
}
