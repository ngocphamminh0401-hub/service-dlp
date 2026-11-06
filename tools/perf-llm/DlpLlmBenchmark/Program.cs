// [Thay đổi] File mới (mục 13.3 - benchmark LLM thật): đo p50/p95 độ trễ, tokens/s, RAM, tỷ lệ lỗi parse JSON, và
// so khớp với nhãn thật trên bộ dữ liệu có sẵn của dự án (KHÔNG tạo bộ dữ liệu mới — dùng chung
// D:\dlp-classifier\rawdata với tools/eval/DlpBaseline, cùng enum SensitivityLevel).
//
// Cách chạy: 1) bật DlpLlmHost thật (dotnet run --project tools/llm-host/DlpLlmHost -- serve --pipe <tên>)
//            2) dotnet run --project tools/perf-llm/DlpLlmBenchmark -- --pipe <tên> [--limit N] [--out thư mục]
using System.Diagnostics;
using Dlp.Inference;
using DlpLlmBenchmark;
using DlpService.Llm;
using DlpService.Reading;
using DlpService.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using ClsLevel = DlpService.Classification.SensitivityLevel;

string rawdataFolder = ArgValue(args, "--rawdata") ?? @"D:\dlp-classifier\rawdata";
string pipeName = ArgValue(args, "--pipe") ?? "dlp-llm-host";
int limit = int.TryParse(ArgValue(args, "--limit"), out var l) ? l : 20;
string outFolder = ArgValue(args, "--out") ?? Path.Combine("tools", "perf-llm", "DlpLlmBenchmark", "results");
int deadlineMs = int.TryParse(ArgValue(args, "--deadline-ms"), out var d) ? d : 120_000;

if (!Directory.Exists(rawdataFolder))
{
    Console.Error.WriteLine($"Không tìm thấy thư mục dữ liệu nhãn: {rawdataFolder}");
    return 1;
}

var allSamples = SampleLoader.Load(rawdataFolder);
var samples = SampleLoader.TakeStratified(allSamples, limit);
Console.WriteLine($"Đã nạp {allSamples.Count} mẫu, lấy {samples.Count} mẫu (rải đều theo domain/nhãn, --limit {limit}).");

var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
var scanner = new RegexScanner(patterns);
var reader = new FileReaderService();

using var client = InferenceClient.ConnectNamedPipe(pipeName);

var infoBefore = await client.TryGetInfoAsync(TimeSpan.FromSeconds(10));
if (infoBefore == null)
{
    Console.Error.WriteLine($"Không kết nối được LLM host qua pipe '{pipeName}'. Đảm bảo DlpLlmHost đang chạy (dotnet run -- serve --pipe {pipeName}).");
    return 1;
}
Console.WriteLine($"Kết nối OK. ready={infoBefore.Ready}, model_ver={infoBefore.ModelVer}, rss_bytes trước khi chạy={infoBefore.RssBytes}");

// [Thay đổi] mục 13.3: KHÔNG tính lần nạp model + xây prefix cache (mục 6.4) đầu tiên vào số liệu đo — đó là chi
// phí 1 LẦN cho cả vòng đời service (đo thật ở buổi trước: ~137-141s trên máy thiếu RAM), không phải chi phí/job.
// Benchmark phải phản ánh hiệu năng ỔN ĐỊNH (steady-state) của từng job, không bị lệch bởi 1 job đầu tiên bất
// thường. Gửi 1 job "mồi" trước, deadline dài, KHÔNG tính vào kết quả.
if (!infoBefore.Ready)
{
    Console.WriteLine("Model chưa nạp — gửi 1 job \"mồi\" (warm-up, deadline 5 phút) để nạp model + xây prefix cache, KHÔNG tính vào số liệu benchmark...");
    var warmupReq = new ClassifyReq { JobId = "warmup", ContractVer = 1, DeadlineMs = 300_000 };
    warmupReq.Chunks.Add(new Chunk { Kind = "full", Rule = "", Text = "Thông báo lịch nghỉ lễ áp dụng cho toàn thể nhân viên." });
    var warmupSw = Stopwatch.StartNew();
    var warmupRes = await client.TryClassifyAsync(warmupReq);
    Console.WriteLine($"Job mồi xong sau {warmupSw.Elapsed.TotalSeconds:0.0}s, status={warmupRes?.Status.ToString() ?? "NO_CONNECT"}");
}

var results = new List<BenchResult>();
int skippedNoExtractor = 0, skippedMaskedAway = 0;

foreach (var sample in samples)
{
    var extractor = reader.FindExtractor(sample.DocPath);
    if (extractor == null)
    {
        skippedNoExtractor++;
        continue;
    }

    IReadOnlyList<ChunkResult> chunks;
    try
    {
        // Không có Finding thật (không chạy Pass 1 đầy đủ ở benchmark này) -> ChunkSelector coi như "không có vùng
        // nghi vấn cụ thể", chỉ còn metadata/mẫu đại diện/thống kê nếu file vượt ngân sách, hoặc gửi nguyên văn nếu
        // nhỏ hơn ngân sách (giống hệt luồng thật cho file nhỏ).
        chunks = ChunkSelector.SelectChunks(extractor, sample.DocPath, Path.GetFileName(sample.DocPath), [], tokenBudget: 2000);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{sample.Id}] Lỗi trích xuất: {ex.Message}");
        continue;
    }

    var masked = chunks.Select(c => Masker.Mask(c, scanner)).Where(c => c != null).Cast<ChunkResult>().ToList();
    if (masked.Count == 0)
    {
        // mục 4.11: chunk còn dính rule cứng sau khi mask -> huỷ cả chunk, không gửi. Coi như "không đo được" cho
        // mẫu này (không phải lỗi LLM).
        skippedMaskedAway++;
        continue;
    }

    var request = new ClassifyReq { JobId = sample.Id, ContractVer = 1, DeadlineMs = (uint)deadlineMs };
    request.Chunks.AddRange(masked.Select(c => new Chunk { Kind = c.Kind, Rule = c.Rule ?? "", Text = c.Text }));

    var sw = Stopwatch.StartNew();
    var response = await client.TryClassifyAsync(request);
    sw.Stop();

    results.Add(new BenchResult(sample, response, sw.Elapsed.TotalMilliseconds));
    Console.WriteLine($"[{results.Count}/{samples.Count}] {sample.Id} ({sample.Label}) -> status={response?.Status.ToString() ?? "NO_CONNECT"} predicted={(response != null ? ToClsLevel(response.SensitivityLevel).ToString() : "-")} client_ms={sw.Elapsed.TotalMilliseconds:0}");
}

var infoAfter = await client.TryGetInfoAsync(TimeSpan.FromSeconds(10));
Console.WriteLine($"rss_bytes sau khi chạy={infoAfter?.RssBytes.ToString() ?? "?"}");

Directory.CreateDirectory(outFolder);
string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
string csvPath = Path.Combine(outFolder, $"llm-benchmark-{timestamp}.csv");
string mdPath = Path.Combine(outFolder, $"llm-benchmark-{timestamp}.md");
BenchReport.WriteCsv(csvPath, results);
BenchReport.WriteMarkdown(mdPath, results, infoBefore, infoAfter, skippedNoExtractor, skippedMaskedAway, pipeName, rawdataFolder);

Console.WriteLine();
Console.WriteLine($"Đã ghi: {csvPath}");
Console.WriteLine($"Đã ghi: {mdPath}");
return 0;

static string? ArgValue(string[] args, string flag) => args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault();

static ClsLevel ToClsLevel(SensitivityLevel level) => LevelMapper.ToClsLevel(level);

internal sealed record BenchResult(BenchSample Sample, ClassifyRes? Response, double ClientMs);
