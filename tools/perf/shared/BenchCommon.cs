// [Thay đổi] File mới (Benchmark T04/T05): phần dùng chung giữa DlpRegexBench và DlpExtractorBench — chọn file đo (manifest của
// DlpBenchData và/hoặc mẫu rawdata), thống kê median/p95/CV, đo 1 lần chạy (thời gian, cấp phát, số lần GC), mô tả môi trường.
// Mỗi csproj thêm <Compile Include="..\shared\BenchCommon.cs" />.
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace DlpTools.Perf;

/// <summary>1 file cần đo: bộ dữ liệu (D-*), id hiển thị (đường dẫn tương đối), đường dẫn đầy đủ, định dạng.</summary>
internal sealed record BenchFile(string Set, string Id, string Path, string Format);

/// <summary>Kết quả 1 lần đo: thời gian thực, thời gian CPU của tiến trình, byte cấp phát (luồng hiện tại), số lần GC từng thế hệ.</summary>
internal readonly record struct Sample(double WallMs, double CpuMs, long AllocBytes, int Gen0, int Gen1, int Gen2);

internal static class BenchCommon
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Đọc danh sách file: manifest.csv của DlpBenchData (lọc theo --sets) + tuỳ chọn N file lấy mẫu từ rawdata (bộ D-raw).</summary>
    public static List<BenchFile> SelectFiles(string? datasetDir, string? sets, string? rawdata, int rawSample, int seed = 20261118)
    {
        var files = new List<BenchFile>();
        var wanted = sets?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (datasetDir != null)
        {
            string manifest = System.IO.Path.Combine(datasetDir, "manifest.csv");
            foreach (var line in File.ReadLines(manifest).Skip(1))
            {
                var c = line.Split(',');
                if (c.Length < 3 || (wanted != null && !wanted.Contains(c[0]))) continue;
                files.Add(new BenchFile(c[0], c[1], System.IO.Path.Combine(datasetDir, c[1]), c[2]));
            }
        }
        if (rawdata != null && rawSample > 0)
        {
            var all = Directory.EnumerateFiles(rawdata, "*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                .Select(f => System.IO.Path.GetRelativePath(rawdata, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
            var rand = new Random(seed);
            foreach (var rel in all.OrderBy(_ => rand.Next()).Take(rawSample).OrderBy(f => f, StringComparer.Ordinal))
            {
                files.Add(new BenchFile("D-raw", rel, System.IO.Path.Combine(rawdata, rel), System.IO.Path.GetExtension(rel).TrimStart('.')));
            }
        }
        return files;
    }

    /// <summary>Đo 1 lần chạy <paramref name="action"/>. Cấp phát đo trên LUỒNG hiện tại (công cụ chạy 1 luồng nên chính xác);
    /// CPU là của cả tiến trình (gồm luồng GC nền) — xấp xỉ tốt khi chỉ có 1 luồng làm việc.</summary>
    public static Sample Measure(Action action)
    {
        var proc = Process.GetCurrentProcess();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        action();
        double wall = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
        proc.Refresh();
        double cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        return new Sample(wall, cpu, alloc, GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2);
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return double.NaN;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    public static double P95(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? double.NaN : v[Math.Min(v.Count - 1, (int)Math.Ceiling(v.Count * 0.95) - 1)];
    }

    /// <summary>Hệ số biến thiên (độ lệch chuẩn / trung bình) — &gt; 10 % coi là nhiễu (kế hoạch §4).</summary>
    public static double Cv(IEnumerable<double> values)
    {
        var v = values.ToList();
        if (v.Count < 2) return 0;
        double mean = v.Average();
        if (mean == 0) return 0;
        double sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
        return sd / mean;
    }

    /// <summary>Mô tả môi trường đo (đưa vào đầu báo cáo / environment.md).</summary>
    public static string Environment()
    {
        string cpuModel = "";
        try
        {
            cpuModel = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var sb = new StringBuilder();
        sb.AppendLine($"| Máy | {System.Environment.MachineName} |");
        sb.AppendLine($"| CPU | {cpuModel} × {System.Environment.ProcessorCount} luồng logic |");
        sb.AppendLine($"| RAM | {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0 / 1024:0.0} GB khả dụng cho GC |");
        sb.AppendLine($"| OS | {RuntimeInformation.OSDescription} |");
        sb.AppendLine($"| .NET | {RuntimeInformation.FrameworkDescription} |");
        sb.AppendLine($"| GC | {(GCSettings.IsServerGC ? "Server" : "Workstation")}, latency {GCSettings.LatencyMode} |");
        sb.AppendLine($"| Build | {(IsDebug() ? "**Debug — KHÔNG dùng số đo này**" : "Release")} |");
        return sb.ToString();
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    public static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static string F(double v, string fmt = "0.000") => double.IsNaN(v) ? "" : v.ToString(fmt, Inv);
}
