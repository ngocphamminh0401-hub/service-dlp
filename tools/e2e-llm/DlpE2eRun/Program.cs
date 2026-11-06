// [Thay đổi] File mới: chạy thử END-TO-END luồng thật của DlpService trên ~20 file có nhãn thật, trong đó ~5 file rơi
// vào vùng xám và được gửi xuống LLM host THẬT. Dùng đúng các class của service (FileWatcherService = Pass 1 + Router +
// tạo job, ResultStore, LlmJobDispatcher, InferenceClient) với cấu hình THẬT đọc từ publish\appsettings.json
// (ngưỡng Score, Llm...). Mọi thứ ghi vào thư mục tạm — KHÔNG đụng DB / watch-folder thật.
//
// Cách chạy: dotnet run -c Release --project tools/e2e-llm/DlpE2eRun -- [--work <thư mục tạm>] [--total 20] [--grey 5]
//   Mặc định tự bật LLM host thật (publish\DlpLlmHost.exe) ở chế độ console với pipe riêng, tự tắt khi xong.
//   --host-exe attach  : không bật, dùng LLM host đang chạy sẵn (--pipe <tên>), đo process tên "DlpLlmHost".
// Quy tắc dự án: báo cáo KHÔNG chứa nội dung file / giá trị nhạy cảm — chỉ id mẫu, mức, điểm, thời gian.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dlp.Inference;
using DlpService.Classification;
using DlpService.Llm;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;
using DlpService.Watching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ClsLevel = DlpService.Classification.SensitivityLevel;
using LlmLevel = Dlp.Inference.SensitivityLevel;

namespace DlpE2eRun;

internal sealed record LabeledSample(string Id, string Domain, string Label, ClsLevel Truth, string DocPath);

internal sealed record Candidate(LabeledSample Sample, RouteZone Zone, ClsLevel RegexLevel, double Confidence,
    EscalationGroup? Group, int TextLength);

internal sealed record FileRow(Candidate Cand, long FileId, string Path, double S1, double SFinal, string Status, string Source,
    bool NeedsReview, string? JobStatus, int? JobAttempts, int? ServerLatencyMs, string? ModelVer, double? S2);

internal sealed record JobRun(int Index, string Outcome, double ClientSeconds);

internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static async Task<int> Main(string[] args)
    {
        string rawdata = Arg(args, "--rawdata") ?? @"D:\dlp-classifier\rawdata";
        string settingsPath = Arg(args, "--settings") ?? @"D:\Service-DLP\publish\appsettings.json";
        string rulesFolder = Arg(args, "--rules") ?? @"D:\Service-DLP\publish\rules";
        string hostExe = Arg(args, "--host-exe") ?? @"D:\Service-DLP\tools\llm-host\DlpLlmHost\publish\DlpLlmHost.exe";
        string pipe = Arg(args, "--pipe") ?? "dlp-llm-e2e";
        string outFolder = Arg(args, "--out") ?? @"D:\Service-DLP\tools\e2e-llm\DlpE2eRun\results";
        string workRoot = Arg(args, "--work") ?? Path.Combine(Path.GetTempPath(), "dlp-e2e-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        int total = IntArg(args, "--total", 20);
        int greyWanted = IntArg(args, "--grey", 5);
        int maxCandidates = IntArg(args, "--candidates", 600);
        int seed = IntArg(args, "--seed", 7);
        int maxMinutes = IntArg(args, "--max-minutes", 40);
        bool attach = string.Equals(hostExe, "attach", StringComparison.OrdinalIgnoreCase);
        bool replay = !args.Contains("--no-replay");

        foreach (var path in new[] { rawdata, settingsPath, rulesFolder })
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Console.Error.WriteLine($"Không tìm thấy: {path}");
                return 1;
            }
        }

        // ---- Cấu hình THẬT của service (cùng cách Worker.cs đọc) ----
        var config = new ConfigurationBuilder().AddJsonFile(settingsPath, optional: false).Build();
        var router = config.GetSection("Score").Get<RouterOptions>() ?? new RouterOptions();
        var escalation = config.GetSection("Llm").Get<EscalationOptions>() ?? new EscalationOptions();
        var quota = config.GetSection("Llm:Quota").Get<JobQuotaOptions>() ?? new JobQuotaOptions();
        var dispatcherOptions = new LlmJobDispatcherOptions
        {
            TokenBudget = Math.Max(200, config.GetValue("Llm:TokenBudget", 2000)),
            JobDeadline = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Llm:JobDeadlineSeconds", 60)))
        };
        var idleOptions = new MachineIdleOptions
        {
            MaxCpuPercent = config.GetValue("Llm:MaxCpuPercent", 50.0),
            MinBatteryPercent = config.GetValue("Llm:MinBatteryPercent", 20.0)
        };
        Console.WriteLine($"Cấu hình thật: T_high={router.THigh:0.00}, T_low={router.TLow:0.00}, deadline job={dispatcherOptions.JobDeadline.TotalSeconds}s, token={dispatcherOptions.TokenBudget}, CPU rảnh ≤ {idleOptions.MaxCpuPercent}%");

        var logger = NullLogger.Instance;
        var patterns = new RuleLoader(logger).LoadFromFolder(rulesFolder);
        var scanner = new RegexScanner(patterns, RuleLoader.ComputeRulesVersion(rulesFolder));
        var engine = new ClassificationEngine(logger);
        var reader = new FileReaderService();
        Console.WriteLine($"Đã nạp {patterns.Count} pattern từ {rulesFolder}");

        // ---- 1) Chọn 20 file: dò Pass 1 + Router offline (cùng công thức FileWatcherService) ----
        var all = LoadSamples(rawdata);
        var order = Interleave(all, seed).Take(maxCandidates).ToList();
        Console.WriteLine($"Nạp {all.Count} mẫu, dò tối đa {order.Count} ứng viên để tìm {greyWanted} file vùng xám...");
        var greyPool = new List<Candidate>();
        var otherPool = new List<Candidate>();
        int probed = 0;
        foreach (var sample in order)
        {
            var cand = Probe(sample, reader, scanner, engine, router, escalation);
            probed++;
            if (cand == null)
            {
                continue;
            }
            if (cand.Group == EscalationGroup.P1)
            {
                greyPool.Add(cand);
            }
            else if (cand.Group == null) // P2/P3 cũng xuống LLM -> loại để số file xuống LLM đúng ý đồ
            {
                otherPool.Add(cand);
            }
            if (greyPool.Count >= greyWanted * 3 && otherPool.Count >= 80)
            {
                break;
            }
        }
        var greySel = PickBalanced(greyPool, greyWanted, c => c.Sample.Label);
        var otherSel = PickOthers(otherPool, total - greySel.Count);
        var chosen = greySel.Concat(otherSel).ToList();
        Console.WriteLine($"Dò {probed} ứng viên: có {greyPool.Count} file P1 (vùng xám đủ điều kiện), {otherPool.Count} file không xuống LLM. Chọn {greySel.Count} xám + {otherSel.Count} khác.");
        if (greySel.Count == 0)
        {
            Console.Error.WriteLine("Không tìm thấy file vùng xám nào — tăng --candidates.");
            return 1;
        }

        // ---- 2) Dựng thư mục tạm + sao chép ----
        string watchDir = Path.Combine(workRoot, "watch");
        string dbPath = Path.Combine(workRoot, "e2e.db");
        Directory.CreateDirectory(watchDir);
        foreach (var c in chosen)
        {
            File.Copy(c.Sample.DocPath, Path.Combine(watchDir, Path.GetFileName(c.Sample.DocPath)), overwrite: true);
        }
        var byFullPath = chosen.ToDictionary(
            c => Path.GetFullPath(Path.Combine(watchDir, Path.GetFileName(c.Sample.DocPath))), c => c, StringComparer.OrdinalIgnoreCase);

        // ---- 3) Bật LLM host thật + bắt đầu lấy mẫu CPU/RAM ----
        Process? host = null;
        var sw = Stopwatch.StartNew();
        try
        {
            if (attach)
            {
                host = Process.GetProcessesByName("DlpLlmHost").FirstOrDefault();
            }
            else
            {
                // [Thay đổi] --host-args "<tham số>": thêm tham số cho LLM host (VD "--memory-limit-mb 14000") để thử nghiệm
                // các cấu hình khác mà không phải sửa code service.
                var psi = new ProcessStartInfo(hostExe, $"serve --pipe {pipe} {Arg(args, "--host-args")}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                host = Process.Start(psi);
                host!.OutputDataReceived += (_, _) => { };
                host.ErrorDataReceived += (_, _) => { };
                host.BeginOutputReadLine();
                host.BeginErrorReadLine();
            }
            if (host == null)
            {
                Console.Error.WriteLine("Không bật / không tìm thấy process DlpLlmHost.");
                return 1;
            }

            var procs = new Dictionary<string, Process> { ["DlpService(harness)"] = Process.GetCurrentProcess(), ["DlpLlmHost"] = host };
            using var sampler = new Sampler(procs);
            sampler.Phase = "host-start";

            using var inferenceClient = InferenceClient.ConnectNamedPipe(pipe);
            Info? info0 = null;
            var hostWait = Stopwatch.StartNew();
            while (hostWait.Elapsed < TimeSpan.FromMinutes(3) && info0 == null)
            {
                info0 = await inferenceClient.TryGetInfoAsync(TimeSpan.FromSeconds(3));
                if (info0 == null)
                {
                    await Task.Delay(1000);
                }
            }
            if (info0 == null)
            {
                Console.Error.WriteLine("LLM host không sẵn sàng sau 3 phút.");
                return 1;
            }
            double hostReadySec = hostWait.Elapsed.TotalSeconds;
            Console.WriteLine($"LLM host sẵn sàng sau {hostReadySec:0.0}s (ready_model={info0.Ready}, rss={info0.RssBytes / 1048576.0:0} MB)");
            bool modelWasLoaded = info0.Ready;

            // ---- 4) Pass 1 thật (FileWatcherService quét thư mục tạm đồng bộ) ----
            sampler.Phase = "pass1";
            using var store = new ResultStore(dbPath, logger, quota);
            var watcher = new FileWatcherService(scanner, engine, logger, router, store, escalation);
            var pass1 = Stopwatch.StartNew();
            watcher.StartWatching(watchDir);
            pass1.Stop();
            var jobsAfterPass1 = store.CountJobsByStatus();
            Console.WriteLine($"Pass 1 xong {pass1.Elapsed.TotalSeconds:0.00}s cho {chosen.Count} file. Hàng đợi job: {FormatCounts(jobsAfterPass1)}");

            // ---- 5) Vòng điều phối job THẬT (cùng nhịp 10 giây/lượt như Worker) ----
            sampler.Phase = "llm";
            var breaker = new CircuitBreaker();
            var guarded = new GuardedInferenceClient(inferenceClient, breaker);
            var dispatcher = new LlmJobDispatcher(store, new FileReaderService(), scanner, guarded, new MachineIdleChecker(idleOptions), dispatcherOptions, logger);
            var runs = new List<JobRun>();
            var skips = new Dictionary<string, int>();
            var llmWall = Stopwatch.StartNew();
            while (llmWall.Elapsed < TimeSpan.FromMinutes(maxMinutes))
            {
                var call = Stopwatch.StartNew();
                string outcome = await dispatcher.TryDispatchOnceAsync();
                call.Stop();
                string kind = outcome.StartsWith("not_idle", StringComparison.Ordinal) ? "not_idle" : outcome;
                if (outcome is "no_job" or "circuit_open" || kind == "not_idle")
                {
                    skips[kind] = skips.GetValueOrDefault(kind) + 1;
                }
                else
                {
                    runs.Add(new JobRun(runs.Count + 1, outcome, call.Elapsed.TotalSeconds));
                    Console.WriteLine($"  job #{runs.Count}: {outcome} ({call.Elapsed.TotalSeconds:0.0}s)");
                }

                var counts = store.CountJobsByStatus();
                if (counts.GetValueOrDefault("pending") + counts.GetValueOrDefault("running") == 0)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
            llmWall.Stop();
            var infoAfterJobs = await inferenceClient.TryGetInfoAsync(TimeSpan.FromSeconds(5));
            var jobsFinal = store.CountJobsByStatus();
            Console.WriteLine($"Pha LLM xong sau {llmWall.Elapsed.TotalSeconds:0.0}s. Hàng đợi: {FormatCounts(jobsFinal)}");

            // ---- 6) Đọc kết quả từ DB tạm ----
            var rows = ReadRows(dbPath, byFullPath);

            // ---- 7) Gọi lại LLM trực tiếp cho file xuống LLM để lấy mức 4 bậc (dispatcher KHÔNG lưu trường này) ----
            var llmLevels = new Dictionary<long, (ClsLevel Level, double ClientSec)>();
            if (replay)
            {
                sampler.Phase = "replay";
                foreach (var row in rows.Where(r => r.Cand.Group == EscalationGroup.P1 && r.JobStatus == "done"))
                {
                    var r = await ReplayAsync(row, store, scanner, inferenceClient, dispatcherOptions);
                    if (r != null)
                    {
                        llmLevels[row.FileId] = r.Value;
                        Console.WriteLine($"  replay file#{row.FileId}: LLM={r.Value.Level} ({r.Value.ClientSec:0.0}s)");
                    }
                }
            }
            sampler.Phase = "done";
            var infoEnd = await inferenceClient.TryGetInfoAsync(TimeSpan.FromSeconds(5));
            var samples = sampler.Snapshot();

            // ---- 8) Báo cáo ----
            Directory.CreateDirectory(outFolder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string mdPath = Path.Combine(outFolder, $"e2e-{stamp}.md");
            string csvPath = Path.Combine(outFolder, $"e2e-{stamp}.csv");
            File.WriteAllText(csvPath, BuildCsv(rows, llmLevels), new UTF8Encoding(true));
            File.WriteAllText(mdPath, BuildReport(new ReportInput(rows, llmLevels, runs, skips, samples, router, dispatcherOptions, idleOptions,
                pass1.Elapsed.TotalSeconds, hostReadySec, modelWasLoaded, llmWall.Elapsed.TotalSeconds, info0, infoAfterJobs, infoEnd,
                jobsAfterPass1, jobsFinal, probed, greyPool.Count)), new UTF8Encoding(true));
            Console.WriteLine($"Đã ghi: {mdPath}");
            Console.WriteLine($"Đã ghi: {csvPath}");
        }
        finally
        {
            if (!attach && host is { HasExited: false })
            {
                host.Kill(entireProcessTree: true);
            }
            try
            {
                if (!args.Contains("--keep"))
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(workRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"(Không xoá được thư mục tạm {workRoot}: {ex.Message})");
            }
        }
        return 0;
    }

    // ===== Chọn mẫu =====

    private static List<LabeledSample> LoadSamples(string rawdata)
    {
        var list = new List<LabeledSample>();
        foreach (var folder in Directory.EnumerateDirectories(rawdata).Order(StringComparer.OrdinalIgnoreCase))
        {
            string meta = Path.Combine(folder, "_metadata.jsonl");
            if (!File.Exists(meta))
            {
                continue;
            }
            foreach (var line in File.ReadLines(meta).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string? id = Str(root, "id"), label = Str(root, "label"), docx = Str(root, "docx_path");
                    if (id == null || label == null || docx == null || MapLabel(label) is not ClsLevel truth)
                    {
                        continue;
                    }
                    string full = Path.Combine(folder, Path.GetFileName(docx));
                    if (File.Exists(full))
                    {
                        list.Add(new LabeledSample(id, Str(root, "domain") ?? "?", label, truth, full));
                    }
                }
                catch (JsonException)
                {
                    // dòng hỏng -> bỏ qua
                }
            }
        }
        return list;
    }

    // Xáo từng nhóm (domain, nhãn) rồi lấy xoay vòng — mỗi nhóm đều có mặt, tái lập được nhờ seed.
    private static IEnumerable<LabeledSample> Interleave(List<LabeledSample> all, int seed)
    {
        var rng = new Random(seed);
        var groups = all.GroupBy(s => (s.Domain, s.Label)).Select(g => g.OrderBy(_ => rng.Next()).ToList()).ToList();
        for (int i = 0; groups.Any(g => i < g.Count); i++)
        {
            foreach (var g in groups.Where(g => i < g.Count))
            {
                yield return g[i];
            }
        }
    }

    // Cùng các bước Pass 1 + Router + Eligibility như FileWatcherService.ProcessFile (không ghi DB, không log nội dung).
    private static Candidate? Probe(LabeledSample sample, FileReaderService reader, RegexScanner scanner, ClassificationEngine engine,
        RouterOptions router, EscalationOptions escalation)
    {
        var read = reader.TryReadFile(sample.DocPath);
        if (!read.Success)
        {
            return null;
        }
        string content = read.Content ?? string.Empty;
        var scan = scanner.Scan(content);
        if (!scan.IsComplete)
        {
            return null; // quét chưa trọn — service thật cũng không gắn mức
        }
        string hash = ContentHash.Compute(content);
        var matches = scan.Matches;
        if (matches.Count == 0)
        {
            var d0 = EscalationPolicy.Decide(RouteZone.NoMatch, content.Length, sample.DocPath, Path.GetFileName(sample.DocPath), hash, escalation, read.NeedsOcr);
            return new Candidate(sample, RouteZone.NoMatch, ClsLevel.Public, 0, d0.Group, content.Length);
        }
        var level = engine.Classify(new ScanContext { FilePath = sample.DocPath, Content = content, Matches = matches }).Level;
        var s1 = FileScorer.Compute(matches, content.Length);
        var route = ScoreRouter.Route(level, s1, matches.Count, router);
        var d = EscalationPolicy.Decide(route.Zone, content.Length, sample.DocPath, Path.GetFileName(sample.DocPath), hash, escalation);
        return new Candidate(sample, route.Zone, level, route.Confidence, d.Group, content.Length);
    }

    // Lấy xoay vòng theo khoá (nhãn thật) để các nhãn đều có mặt.
    private static List<Candidate> PickBalanced(List<Candidate> pool, int count, Func<Candidate, string> key)
    {
        var groups = pool.GroupBy(key).Select(g => g.ToList()).ToList();
        var result = new List<Candidate>();
        for (int i = 0; result.Count < count && groups.Any(g => i < g.Count); i++)
        {
            foreach (var g in groups.Where(g => i < g.Count))
            {
                if (result.Count < count)
                {
                    result.Add(g[i]);
                }
            }
        }
        return result;
    }

    // 15 file còn lại: xoay vòng giữa các vùng (Certain / Low / NoMatch), trong mỗi vùng xoay vòng theo nhãn thật.
    private static List<Candidate> PickOthers(List<Candidate> pool, int count)
    {
        var perZone = pool.GroupBy(c => c.Zone).Select(g => PickBalanced(g.ToList(), g.Count(), c => c.Sample.Label)).ToList();
        var result = new List<Candidate>();
        for (int i = 0; result.Count < count && perZone.Any(z => i < z.Count); i++)
        {
            foreach (var z in perZone.Where(z => i < z.Count))
            {
                if (result.Count < count)
                {
                    result.Add(z[i]);
                }
            }
        }
        return result;
    }

    // ===== Đọc kết quả + replay =====

    private static List<FileRow> ReadRows(string dbPath, Dictionary<string, Candidate> byPath)
    {
        var rows = new List<FileRow>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.file_id, f.path, s.s1, s.s_final, s.status, s.source, s.needs_review,
                   j.status, j.attempts, r.latency_ms, r.model_ver, r.s2
            FROM file_state f JOIN scan_result s ON s.file_id = f.file_id
            LEFT JOIN llm_job j ON j.file_id = f.file_id AND j.file_hash = s.file_hash AND j.status <> 'stale'
            LEFT JOIN llm_result r ON r.job_id = j.job_id
            ORDER BY f.file_id;
            """;
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string path = Path.GetFullPath(rd.GetString(1));
            if (!byPath.TryGetValue(path, out var cand))
            {
                continue;
            }
            rows.Add(new FileRow(cand, rd.GetInt64(0), path, rd.GetDouble(2), rd.GetDouble(3), rd.GetString(4), rd.GetString(5),
                rd.GetInt64(6) == 1, rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? null : rd.GetInt32(8),
                rd.IsDBNull(9) ? null : rd.GetInt32(9), rd.IsDBNull(10) ? null : rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetDouble(11)));
        }
        return rows;
    }

    // Dựng request giống LlmJobDispatcher (cùng ChunkSelector + Masker + findings thật); thiếu Meta (BuildMeta là private).
    private static async Task<(ClsLevel Level, double ClientSec)?> ReplayAsync(FileRow row, ResultStore store, RegexScanner scanner,
        InferenceClient client, LlmJobDispatcherOptions options)
    {
        var reader = new FileReaderService();
        var extractor = reader.FindExtractor(row.Path);
        if (extractor == null)
        {
            return null;
        }
        var findings = store.GetFindings(row.FileId);
        var chunks = ChunkSelector.SelectChunks(extractor, row.Path, Path.GetFileName(row.Path), findings, options.TokenBudget);
        var masked = chunks.Select(c => Masker.Mask(c, scanner)).Where(c => c != null).Cast<ChunkResult>().ToList();
        var request = new ClassifyReq { JobId = "replay-" + row.FileId, ContractVer = ContractVersion.Current, DeadlineMs = (uint)options.JobDeadline.TotalMilliseconds };
        request.Chunks.AddRange(masked.Select(c => new Chunk { Kind = c.Kind, Rule = c.Rule ?? "", Text = c.Text }));
        request.Findings.AddRange(findings.Select(f => new Finding
        {
            RuleId = f.RuleId, Score = (float)f.Score, ValidatorPassed = f.ValidatorPassed ?? false,
            Count = (uint)f.Count, Location = f.Location, HardRule = f.HardRule
        }));
        var sw = Stopwatch.StartNew();
        var res = await client.TryClassifyAsync(request);
        sw.Stop();
        if (res is not { Status: Dlp.Inference.Status.Ok })
        {
            return null;
        }
        return (res.SensitivityLevel switch
        {
            LlmLevel.Internal => ClsLevel.Internal,
            LlmLevel.Confidential => ClsLevel.Confidential,
            LlmLevel.Restricted => ClsLevel.Restricted,
            _ => ClsLevel.Public
        }, sw.Elapsed.TotalSeconds);
    }

    // ===== Báo cáo =====

    private sealed record ReportInput(List<FileRow> Rows, Dictionary<long, (ClsLevel Level, double ClientSec)> LlmLevels, List<JobRun> Runs,
        Dictionary<string, int> Skips, IReadOnlyList<Sample> Samples, RouterOptions Router, LlmJobDispatcherOptions Dispatcher,
        MachineIdleOptions Idle, double Pass1Sec, double HostReadySec, bool ModelWasLoaded, double LlmWallSec, Info? InfoBefore,
        Info? InfoAfterJobs, Info? InfoEnd, IReadOnlyDictionary<string, int> JobsAfterPass1, IReadOnlyDictionary<string, int> JobsFinal,
        int Probed, int GreyPool);

    private static string BuildCsv(List<FileRow> rows, Dictionary<long, (ClsLevel Level, double ClientSec)> llm)
    {
        var sb = new StringBuilder("sample_id,domain,truth,zone,regex_level,s1,s_final,status,source,needs_review,group,job_status,server_latency_ms,llm_level_replay\n");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',', r.Cand.Sample.Id, r.Cand.Sample.Domain, r.Cand.Sample.Truth, r.Cand.Zone, r.Cand.RegexLevel,
                r.S1.ToString("0.000", Inv), r.SFinal.ToString("0.000", Inv), r.Status, r.Source, r.NeedsReview ? 1 : 0,
                r.Cand.Group?.ToString() ?? "-", r.JobStatus ?? "-", r.ServerLatencyMs?.ToString(Inv) ?? "",
                llm.TryGetValue(r.FileId, out var l) ? l.Level.ToString() : ""));
        }
        return sb.ToString();
    }

    private static string BuildReport(ReportInput x)
    {
        var sb = new StringBuilder();
        int cores = Environment.ProcessorCount;
        var rows = x.Rows;
        var greyRows = rows.Where(r => r.Cand.Group == EscalationGroup.P1).ToList();
        var done = greyRows.Where(r => r.JobStatus == "done").ToList();

        sb.AppendLine("# Chạy thử end-to-end luồng DLP → LLM (harness tách biệt)");
        sb.AppendLine();
        sb.AppendLine($"- Ngày chạy: {DateTime.Now:yyyy-MM-dd HH:mm:ss}; máy {cores} nhân logic");
        sb.AppendLine($"- Cấu hình thật: T_high={x.Router.THigh:0.00}, T_low={x.Router.TLow:0.00}, deadline/job={x.Dispatcher.JobDeadline.TotalSeconds:0}s, ngân sách {x.Dispatcher.TokenBudget} token, rảnh khi CPU ≤ {x.Idle.MaxCpuPercent:0}%");
        sb.AppendLine($"- Mẫu: {rows.Count} file ({greyRows.Count} vùng xám xuống LLM) chọn từ {x.Probed} ứng viên ({x.GreyPool} ứng viên P1); model_ver: `{done.FirstOrDefault()?.ModelVer ?? x.InfoBefore?.ModelVer}`");
        sb.AppendLine($"- Hàng đợi sau Pass 1: {FormatCounts(x.JobsAfterPass1)} → sau pha LLM: {FormatCounts(x.JobsFinal)}");
        sb.AppendLine();
        sb.AppendLine("> **Giới hạn của phép đo:** (1) \"DlpService\" ở phần CPU/RAM là process harness chạy cùng code, không phải `DlpService.exe` (không có overhead Windows Service). (2) Dispatcher hiện KHÔNG lưu mức 4 bậc LLM trả về, nên mức LLM lấy bằng 1 lượt gọi lại riêng (pha `replay`, thiếu `Meta`). (3) Cỡ mẫu nhỏ — chỉ để thấy luồng, không phải kết luận chất lượng model.");
        sb.AppendLine();

        // --- Độ chính xác ---
        sb.AppendLine("## 1. Độ chính xác");
        sb.AppendLine();
        int n = rows.Count;
        int regexOk = rows.Count(r => r.Cand.RegexLevel == r.Cand.Sample.Truth);
        int binOk = rows.Count(r => IsSens(r.Cand.RegexLevel) == IsSens(r.Cand.Sample.Truth));
        int changed = rows.Count(r => Math.Abs(r.SFinal - r.S1) > 1e-9);
        sb.AppendLine($"**Toàn bộ {n} file (kết quả cuối của pipeline).** Mức cuối = mức regex (Merger chỉ đổi điểm `S_final`, không đổi mức):");
        sb.AppendLine();
        sb.AppendLine($"- Đúng 4 mức: {regexOk}/{n} ({Pct(regexOk, n)}); đúng nhị phân nhạy cảm (Confidential+): {binOk}/{n} ({Pct(binOk, n)})");
        sb.AppendLine($"- File có `S_final` khác `S1` sau LLM: **{changed}**/{greyRows.Count} file xuống LLM (verdict theo rule chưa được LLM host điền → Merger không có gì để merge)");
        sb.AppendLine($"- Job: {greyRows.Count(r => r.JobStatus == "done")} done, {greyRows.Count(r => r.JobStatus != "done")} chưa done (trạng thái: {string.Join(", ", greyRows.Where(r => r.JobStatus != "done").Select(r => r.JobStatus ?? "không có job").Distinct())})");
        sb.AppendLine();
        sb.AppendLine("| Vùng | Số file | Regex đúng mức |");
        sb.AppendLine("|---|---|---|");
        foreach (var g in rows.GroupBy(r => r.Cand.Zone).OrderBy(g => g.Key))
        {
            int ok = g.Count(r => r.Cand.RegexLevel == r.Cand.Sample.Truth);
            sb.AppendLine($"| {g.Key} | {g.Count()} | {ok}/{g.Count()} ({Pct(ok, g.Count())}) |");
        }
        sb.AppendLine();
        sb.AppendLine("**Riêng các file vùng xám xuống LLM** (nhãn thật vs regex vs LLM):");
        sb.AppendLine();
        sb.AppendLine("| Mẫu | Nhãn thật | Regex | LLM | S1 | S_final | Job | Latency server (s) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in greyRows)
        {
            string llmCell = x.LlmLevels.TryGetValue(r.FileId, out var l) ? l.Level.ToString() : "—";
            sb.AppendLine($"| {r.Cand.Sample.Id} | {r.Cand.Sample.Truth} | {r.Cand.RegexLevel} | {llmCell} | {r.S1:0.00} | {r.SFinal:0.00} | {r.JobStatus ?? "không có"} | {(r.ServerLatencyMs.HasValue ? (r.ServerLatencyMs.Value / 1000.0).ToString("0.0", Inv) : "—")} |");
        }
        sb.AppendLine();
        int withLlm = greyRows.Count(r => x.LlmLevels.ContainsKey(r.FileId));
        int rxOkG = greyRows.Count(r => x.LlmLevels.ContainsKey(r.FileId) && r.Cand.RegexLevel == r.Cand.Sample.Truth);
        int llmOkG = greyRows.Count(r => x.LlmLevels.TryGetValue(r.FileId, out var l) && l.Level == r.Cand.Sample.Truth);
        int llmBinOk = greyRows.Count(r => x.LlmLevels.TryGetValue(r.FileId, out var l) && IsSens(l.Level) == IsSens(r.Cand.Sample.Truth));
        int rxBinOkG = greyRows.Count(r => x.LlmLevels.ContainsKey(r.FileId) && IsSens(r.Cand.RegexLevel) == IsSens(r.Cand.Sample.Truth));
        sb.AppendLine($"- Trên {withLlm} file vùng xám có mức LLM: regex đúng {rxOkG}/{withLlm}, **LLM đúng {llmOkG}/{withLlm}** (4 mức); nhị phân nhạy cảm: regex {rxBinOkG}/{withLlm}, LLM {llmBinOk}/{withLlm}");
        sb.AppendLine();

        // --- Thời gian ---
        sb.AppendLine("## 2. Thời gian");
        sb.AppendLine();
        var runSecs = x.Runs.Where(r => r.Outcome == "done:ok").Select(r => r.ClientSeconds).ToList();
        sb.AppendLine($"| Hạng mục | Giá trị |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| LLM host khởi động tới khi trả lời GetInfo (gồm kiểm SHA-256) | {x.HostReadySec:0.0}s |");
        sb.AppendLine($"| Pass 1 (trích text + regex + Router + ghi DB) cho {rows.Count} file | {x.Pass1Sec:0.00}s ({x.Pass1Sec / Math.Max(1, rows.Count) * 1000:0} ms/file) |");
        sb.AppendLine($"| Tổng pha LLM (gồm nhịp chờ 10s giữa các lượt như Worker thật) | {x.LlmWallSec:0.0}s |");
        sb.AppendLine($"| Model đã nạp sẵn trước job đầu? | {(x.ModelWasLoaded ? "có" : "không — job #1 gồm cả nạp model + dựng prefix cache")} |");
        if (runSecs.Count > 0)
        {
            sb.AppendLine($"| Job #1 (client round-trip) | {runSecs[0]:0.0}s |");
            var warm = x.ModelWasLoaded ? runSecs : runSecs.Skip(1).ToList();
            if (warm.Count > 0)
            {
                sb.AppendLine($"| Job ổn định ({warm.Count} job): min / p50 / max | {warm.Min():0.0}s / {Percentile(warm, 50):0.0}s / {warm.Max():0.0}s |");
            }
        }
        var lat = done.Where(r => r.ServerLatencyMs.HasValue).Select(r => r.ServerLatencyMs!.Value / 1000.0).ToList();
        if (lat.Count > 0)
        {
            sb.AppendLine($"| Latency do LLM tự đo (server): min / p50 / max | {lat.Min():0.0}s / {Percentile(lat, 50):0.0}s / {lat.Max():0.0}s |");
        }
        if (x.LlmLevels.Count > 0)
        {
            var rp = x.LlmLevels.Values.Select(v => v.ClientSec).ToList();
            sb.AppendLine($"| Lượt gọi lại (replay, prefix cache nóng): min / p50 / max | {rp.Min():0.0}s / {Percentile(rp, 50):0.0}s / {rp.Max():0.0}s |");
        }
        sb.AppendLine();
        sb.AppendLine($"Các lượt điều phối không chạy job: {(x.Skips.Count == 0 ? "không có" : string.Join(", ", x.Skips.Select(kv => $"{kv.Key}={kv.Value}")))}. Kết quả từng job: {string.Join("; ", x.Runs.Select(r => $"#{r.Index} {r.Outcome}"))}.");
        sb.AppendLine();

        // --- CPU / RAM ---
        sb.AppendLine("## 3. CPU / RAM (lấy mẫu mỗi 1s)");
        sb.AppendLine();
        sb.AppendLine($"CPU% tính theo TOÀN MÁY ({cores} nhân logic) — 100% = dùng hết mọi nhân; `nhân-tương-đương` = CPU% × {cores} / 100. LLM host bị Job Object giới hạn CPU tối đa 50% và RAM 10240 MB.");
        sb.AppendLine();
        sb.AppendLine("| Process | Pha | Số mẫu | CPU TB | CPU đỉnh | nhân-tđ TB | RAM (WS) TB | RAM (WS) đỉnh | Private đỉnh | CPU toàn máy TB |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var g in x.Samples.GroupBy(s => (s.Proc, s.Phase)).OrderBy(g => g.Key.Proc).ThenBy(g => PhaseOrder(g.Key.Phase)))
        {
            var list = g.ToList();
            double mach = list.Where(s => s.MachineCpu >= 0).Select(s => s.MachineCpu).DefaultIfEmpty(-1).Average();
            sb.AppendLine($"| {g.Key.Proc} | {g.Key.Phase} | {list.Count} | {list.Average(s => s.CpuPct):0.0}% | {list.Max(s => s.CpuPct):0.0}% | {list.Average(s => s.CpuPct) * cores / 100:0.00} | {list.Average(s => s.WsMb):0} MB | {list.Max(s => s.WsMb):0} MB | {list.Max(s => s.PrivMb):0} MB | {(mach >= 0 ? mach.ToString("0.0", Inv) + "%" : "n/a")} |");
        }
        sb.AppendLine();
        sb.AppendLine($"- RSS do LLM host tự báo: lúc sẵn sàng {FormatMb(x.InfoBefore?.RssBytes)}, sau các job {FormatMb(x.InfoAfterJobs?.RssBytes)}, cuối {FormatMb(x.InfoEnd?.RssBytes)}");
        return sb.ToString();
    }

    private static int PhaseOrder(string phase) => phase switch { "host-start" => 0, "pass1" => 1, "llm" => 2, "replay" => 3, _ => 4 };
    private static bool IsSens(ClsLevel l) => l >= ClsLevel.Confidential;
    private static string Pct(int a, int b) => b == 0 ? "n/a" : (100.0 * a / b).ToString("0.0", Inv) + "%";
    private static string FormatMb(ulong? bytes) => bytes.HasValue ? $"{bytes.Value / 1048576.0:0} MB" : "?";
    private static string FormatCounts(IReadOnlyDictionary<string, int> c) => c.Count == 0 ? "(trống)" : string.Join(", ", c.Select(kv => $"{kv.Key}={kv.Value}"));

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToList();
        double rank = p / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static ClsLevel? MapLabel(string label) => label.Trim().ToLowerInvariant() switch
    {
        "public" => ClsLevel.Public,
        "internal" => ClsLevel.Internal,
        "confidential" => ClsLevel.Confidential,
        "top_secret" or "secret" or "restricted" => ClsLevel.Restricted,
        _ => null
    };

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Arg(string[] args, string flag) => args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault();
    private static int IntArg(string[] args, string flag, int def) => int.TryParse(Arg(args, flag), out var v) ? v : def;
}
