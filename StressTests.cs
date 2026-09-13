using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using ILGPU;
using ILGPU.Runtime;

namespace CPUCKTest;

internal static class CpuStress
{
    public static Task RunAsync(CancellationToken token) => Task.Run(() =>
    {
        try
        {
            Parallel.For(
                0,
                Math.Max(1, Environment.ProcessorCount),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = token
                },
                _ =>
                {
                    double x = 0.123456789;
                    while (!token.IsCancellationRequested)
                    {
                        for (int i = 1; i < 200_000; i++)
                            x = Math.Sqrt(x * x + i) * 1.00000001 + Math.Sin(x);

                        if (double.IsNaN(x) || x > 1e100)
                            x = 0.123456789;
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
            if (device == null)
            {
                log("GPU stress unavailable: no CUDA GPU detected.");
                return;
            }

            using var accelerator = device.CreateAccelerator(context);
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
            long target = Math.Max(
                512L << 20,
                Math.Min(16L << 30, (long)Math.Max(0, free - (2L << 30)) * 60 / 100));

            if (total > 0)
                target = Math.Min(target, total * 60 / 100);

            const int chunkSize = 64 << 20;
            int wanted = (int)Math.Max(8, target / chunkSize);
            log($"RAM stress target: about {wanted * chunkSize / 1024.0 / 1024 / 1024:0.0} GB.");

            for (int i = 0; i < wanted && !token.IsCancellationRequested; i++)
            {
                try { chunks.Add(new byte[chunkSize]); }
                catch { break; }
            }

            long bytes = (long)chunks.Count * chunkSize;
            int workers = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
            int round = 1;

            while (!token.IsCancellationRequested)
            {
                var writeTimer = Stopwatch.StartNew();
                Parallel.For(
                    0,
                    chunks.Count,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = workers,
                        CancellationToken = token
                    },
                    i => chunks[i].AsSpan().Fill((byte)(round + i)));
                writeTimer.Stop();

                if (writeTimer.Elapsed.TotalSeconds > 0)
                    Volatile.Write(ref lastWriteGBps, bytes / writeTimer.Elapsed.TotalSeconds / 1e9);

                var readTimer = Stopwatch.StartNew();
                long roundSink = 0;
                Parallel.For<long>(
                    0,
                    chunks.Count,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = workers,
                        CancellationToken = token
                    },
                    () => 0,
                    (i, _, local) =>
                    {
                        ulong x = 0;
                        foreach (ulong value in MemoryMarshal.Cast<byte, ulong>(chunks[i]))
                            x ^= value;
                        return local ^ (long)x;
                    },
                    local => Interlocked.Add(ref roundSink, local));
                readTimer.Stop();

                Interlocked.Exchange(ref sink, roundSink);
                if (readTimer.Elapsed.TotalSeconds > 0)
                    Volatile.Write(ref lastReadGBps, bytes / readTimer.Elapsed.TotalSeconds / 1e9);

                round++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log("RAM stress error: " + ex.Message);
        }
        finally
        {
            chunks.Clear();
            GC.Collect();
            log("RAM stress stopped.");
        }
    }, token);

    static (long total, long free) ReadSystemMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject item in searcher.Get())
            {
                return (
                    Convert.ToInt64(item["TotalVisibleMemorySize"]) * 1024,
                    Convert.ToInt64(item["FreePhysicalMemory"]) * 1024);
            }
        }
        catch { }

        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (available, available / 2);
    }
}

internal static class DiskStress
{
    static double lastReadMBps;
    static double lastWriteMBps;
    static double activityPct;
    static double totalWrittenGB;
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
        modeName = "Idle";
    }

    public static Task RunAsync(
        CancellationToken token,
        Action<string> log,
        bool benchmark,
        string targetRoot) => Task.Run(() =>
    {
        string tempDir = Path.Combine(targetRoot, "CPUCKTestTemp");
        Directory.CreateDirectory(tempDir);

        string file = Path.Combine(
            tempDir,
            benchmark ? "CPUCKTest_DiskBenchmark.tmp" : "CPUCKTest_DiskStress.tmp");

        const long fileSize = 1024L * 1024 * 1024;
        long maxWritten = benchmark ? 4L * 1024 * 1024 * 1024 : 8L * 1024 * 1024 * 1024;
        const int bufferSize = 16 * 1024 * 1024;

        long totalWritten = 0;
        var buffer = new byte[bufferSize];
        Random.Shared.NextBytes(buffer);

        modeName = benchmark ? "Benchmark" : "Stress";
        log($"Disk {modeName.ToLowerInvariant()} target: {targetRoot}; 1 GB test file, max writes {maxWritten / 1024 / 1024 / 1024} GB/test.");

        try
        {
            while (!token.IsCancellationRequested)
            {
                Volatile.Write(ref activityPct, 100);

                if (totalWritten < maxWritten)
                {
                    var writeTimer = Stopwatch.StartNew();
                    long done = 0;
                    FileOptions options = benchmark
                        ? FileOptions.SequentialScan
                        : FileOptions.SequentialScan | FileOptions.WriteThrough;

                    using (var fs = new FileStream(
                        file,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read,
                        1024 * 1024,
                        options))
                    {
                        while (done < fileSize && !token.IsCancellationRequested)
                        {
                            int n = (int)Math.Min(buffer.Length, fileSize - done);
                            fs.Write(buffer, 0, n);
                            done += n;
                        }

                        if (benchmark)
                            fs.Flush();
                        else
                            fs.Flush(true);
                    }

                    writeTimer.Stop();
                    totalWritten += done;
                    Volatile.Write(ref totalWrittenGB, totalWritten / 1024.0 / 1024 / 1024);
                    if (writeTimer.Elapsed.TotalSeconds > 0)
                        Volatile.Write(
                            ref lastWriteMBps,
                            done / writeTimer.Elapsed.TotalSeconds / 1024.0 / 1024.0);
                }

                if (token.IsCancellationRequested || !File.Exists(file))
                    break;

                var readTimer = Stopwatch.StartNew();
                long read = 0;

                using (var fs = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    1024 * 1024,
                    FileOptions.SequentialScan))
                {
                    int n;
                    while (!token.IsCancellationRequested &&
                           (n = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        read += n;
                    }
                }

                readTimer.Stop();
                if (readTimer.Elapsed.TotalSeconds > 0)
                    Volatile.Write(
                        ref lastReadMBps,
                        read / readTimer.Elapsed.TotalSeconds / 1024.0 / 1024.0);

                if (benchmark && totalWritten >= maxWritten)
                    break;

                if (!benchmark && totalWritten >= maxWritten)
                    Thread.Sleep(150);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log("Disk test error: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref activityPct, 0);

            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch { }

            try
            {
                if (Directory.Exists(tempDir) &&
                    !Directory.EnumerateFileSystemEntries(tempDir).Any())
                {
                    Directory.Delete(tempDir, false);
                }
            }
            catch { }

            log($"Disk {modeName.ToLowerInvariant()} stopped. Temporary file removed. Total test writes: {totalWritten / 1024.0 / 1024 / 1024:0.0} GB.");
        }
    }, token);
}
