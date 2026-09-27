using System.Diagnostics;
using System.Text.Json;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal static class ProbeBenchmark
{
    // Explicitly synthetic, offline, real disk writes. Higher size cap only for this benchmark.
    public static async Task<string> RunAsync(string parent, int seconds = 600, int rate = 10000)
    {
        if (seconds is < 1 or > 600 || rate is < 1 or > 10000) throw new ArgumentException("Benchmark bounds: 1–600 seconds, 1–10000 events/s.");
        var directory = Path.Combine(Path.GetFullPath(parent), "synthetic-stress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var drive = new DriveInfo(Path.GetPathRoot(directory)!);
        if (drive.AvailableFreeSpace < 12L * 1024 * 1024 * 1024) throw new IOException("Benchmark needs 12 GiB free disk space.");
        var run = Guid.NewGuid();
        await using var writer = new RawCaptureWriter(directory, run, maxBytes: 10L * 1024 * 1024 * 1024);
        var contract = new OptionContractDescriptor(12345, "QQQ", new DateOnly(2026, 9, 16), 100,
            OptionRight.Call, "QQQ", "SMART", 100, "20260916:0930-20260916:1600");
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes();
        var timer = Stopwatch.StartNew();
        long sent = 0, peakWorking = 0, peakManaged = 0;
        var memory = new List<object>();
        var nextMemory = 0;
        var total = (long)seconds * rate;
        while (sent < total)
        {
            var due = Math.Min(total, (long)(timer.Elapsed.TotalSeconds * rate));
            while (sent < due)
            {
                var n = ++sent;
                var utc = DateTime.UtcNow;
                var type = n % 4;
                writer.Publish(new(n, utc, Stopwatch.GetTimestamp(), type < 2 ? "tickPrice" : "tickString", 1, contract,
                    type == 0 ? 1 : type == 1 ? 2 : type == 2 ? 48 : 77,
                    type < 2 ? (type == 0 ? "1.00" : "1.05") : $"1.05;1;{new DateTimeOffset(utc).ToUnixTimeMilliseconds()};{n};1.02;true"));
            }
            if (writer.StopRequested.Task.IsCompleted) break;
            if (timer.Elapsed.TotalSeconds >= nextMemory)
            {
                process.Refresh(); var managed = GC.GetTotalMemory(false);
                peakWorking = Math.Max(peakWorking, process.WorkingSet64); peakManaged = Math.Max(peakManaged, managed);
                memory.Add(new { Seconds = timer.Elapsed.TotalSeconds, WorkingBytes = process.WorkingSet64, ManagedBytes = managed });
                Console.WriteLine($"synthetic {timer.Elapsed.TotalSeconds:F0}/{seconds}s records={sent} dropped={writer.Dropped} queue_peak={writer.QueuePeak}");
                nextMemory += 10;
            }
            await Task.Delay(5);
        }
        await writer.DisposeAsync();
        process.Refresh();
        var result = new
        {
            Synthetic = true, RequestedSeconds = seconds, Rate = rate, ElapsedSeconds = timer.Elapsed.TotalSeconds,
            Sent = sent, writer.Accepted, writer.Written, writer.Dropped, writer.QueuePeak, writer.Bytes, writer.Failure,
            Passed = sent == total && writer.Written == total && writer.Dropped == 0 && writer.Failure == null,
            PeakWorkingBytes = peakWorking, PeakManagedBytes = peakManaged,
            AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            CpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds,
            LogicalProcessors = Environment.ProcessorCount, OS = Environment.OSVersion.ToString(),
            Runtime = Environment.Version.ToString(), MemorySamples = memory,
            Note = "Synthetic writer stress; 10 GiB cap override. Not live IB data or evidence of direction accuracy."
        };
        await CaptureRunner.SaveNewAsync(Path.Combine(directory, "benchmark.json"), result);
        Console.WriteLine(directory);
        return directory;
    }
}
