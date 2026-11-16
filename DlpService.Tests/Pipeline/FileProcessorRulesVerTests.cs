// [Thay đổi] File mới (H1 — cache bỏ qua thay đổi bộ rule): khoá hành vi "kết luận cũ chỉ dùng lại khi cùng rules_ver" ở cả 2 tầng cache
// của FileProcessor, và "quét lại cùng nội dung vì rule đổi thì merge lại verdict LLM cũ". Dùng ResultStore THẬT (SQLite trong thư mục
// tạm) + scanner không có rule nào (file nào cũng "sạch") để test không phụ thuộc bộ rule thật.
using DlpService.Classification;
using DlpService.Pipeline;
using DlpService.Platform;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Pipeline;

public sealed class FileProcessorRulesVerTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-rulesver-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _file;
    private readonly ResultStore _store;

    public FileProcessorRulesVerTests()
    {
        Directory.CreateDirectory(_work);
        _dbPath = Path.Combine(_work, "data", "dlp.db");
        _store = new ResultStore(_dbPath);
        _file = Path.Combine(_work, "ghi-chu.txt");
        File.WriteAllText(_file, "noi dung binh thuong khong co gi nhay cam");
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Mỗi lần gọi tạo processor MỚI (giống service restart với bộ rule khác) → cache hash trong RAM rỗng, chỉ còn cache DB
    private FileProcessor Processor(string rulesVer)
    {
        IPathPolicy policy = new CaseInsensitivePathPolicy();
        return new FileProcessor(new FileReaderService(policy), new RegexScanner([], rulesVer), new ClassificationEngine(NullLogger.Instance),
            NullLogger.Instance, new RouterOptions(), _store, new EscalationOptions(), fileConfidence: null, Path.GetFileName, policy);
    }

    // Test-only: đọc thẳng 1 cột của scan_result (ResultStore không có API công khai trả status)
    private string ReadScanColumn(string column)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT s.{column} FROM scan_result s JOIN file_state f ON f.file_id = s.file_id WHERE f.path = $path;";
        cmd.Parameters.AddWithValue("$path", _file);
        return Convert.ToString(cmd.ExecuteScalar())!;
    }

    [Fact]
    public void TryGetFileState_doc_lai_dung_rules_ver_da_luu()
    {
        Assert.Equal(ProcessOutcome.Scanned, Processor("rv1").ProcessFile(_file));

        var state = _store.TryGetFileState(_file)!;

        Assert.Equal("rv1", state.RulesVer);
        Assert.True(state.IsReusableFor("rv1"));
        Assert.False(state.IsReusableFor("rv2"));
    }

    [Fact]
    public void Chua_ket_luan_thi_khong_dung_lai_du_cung_rules_ver()
    {
        var state = new FileStateInfo(1, 1, "h", HasConclusion: false, RulesVer: "rv1");

        Assert.False(state.IsReusableFor("rv1"));
    }

    [Fact]
    public void Tang1_cung_rule_bo_qua_khac_rule_quet_lai()
    {
        Assert.Equal(ProcessOutcome.Scanned, Processor("rv1").ProcessFile(_file));
        Assert.Equal(ProcessOutcome.Unchanged, Processor("rv1").ProcessFile(_file));

        Assert.Equal(ProcessOutcome.Scanned, Processor("rv2").ProcessFile(_file));
        Assert.Equal("rv2", _store.TryGetFileState(_file)!.RulesVer);

        // Đã quét bằng rv2 → lần sau với rv2 lại bỏ qua được
        Assert.Equal(ProcessOutcome.Unchanged, Processor("rv2").ProcessFile(_file));
    }

    [Fact]
    public void Tang2_file_chi_bi_touch_sau_khi_doi_rule_van_quet_lai()
    {
        Assert.Equal(ProcessOutcome.Scanned, Processor("rv1").ProcessFile(_file));

        // Đổi mtime, giữ nguyên nội dung → tầng 1 trượt, tầng 2 (so hash) quyết định
        File.SetLastWriteTimeUtc(_file, DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(ProcessOutcome.Unchanged, Processor("rv1").ProcessFile(_file)); // cùng rule → tầng 2 bỏ qua (hành vi cũ giữ nguyên)

        File.SetLastWriteTimeUtc(_file, DateTime.UtcNow.AddMinutes(-3));
        Assert.Equal(ProcessOutcome.Scanned, Processor("rv2").ProcessFile(_file)); // khác rule → phải quét lại
        Assert.Equal("rv2", _store.TryGetFileState(_file)!.RulesVer);
    }

    // Tạo 1 job LLM ĐÃ DONE cho đúng nội dung hiện tại của file (giả lập LlmJobDispatcher đã chạy xong ở bộ rule rv1)
    private void CompleteLlmJobForCurrentContent(string jsonOut)
    {
        var state = _store.TryGetFileState(_file)!;
        var info = new FileInfo(_file);
        _store.SaveScan(new ScanRecord(_file, info.Length, info.LastWriteTimeUtc, state.FileHash!, 0, ResultStore.StatusProvisional,
            true, "[\"Public\"]", [], LlmPriority: LlmJobPriority.GreyZone, RulesVer: "rv1", MergePolicyVer: "mv1"));
        var job = _store.TryPickNextJob()!;
        Assert.True(_store.CompleteJob(job.JobId, new LlmResultRecord(jsonOut, 0.8, 0.8, "phi-4", "p1", 100),
            merge: new ScanMergeUpdate(0.8, true, "llm", "mv1"), fileId: job.FileId, fileHash: job.FileHash));
        Assert.Equal("llm", ReadScanColumn("source"));
    }

    [Fact]
    public void Doi_rule_cung_noi_dung_thi_merge_lai_verdict_LLM_cu_va_chot_final()
    {
        Processor("rv1").ProcessFile(_file);
        // LLM cũ: phát hiện thêm dữ liệu nhạy cảm mà regex bỏ sót (verdict không gắn rule_id) → Merger: source=llm, needs_review
        CompleteLlmJobForCurrentContent("{\"labels\":[\"PiiBasic\"],\"verdicts\":[{\"rule_id\":\"\",\"true_positive\":true,\"confidence\":0.9}]}");

        Assert.Equal(ProcessOutcome.Scanned, Processor("rv2").ProcessFile(_file));

        Assert.Equal("llm", ReadScanColumn("source"));   // không bị SaveScan ghi đè thành "regex" rồi mất
        Assert.Equal("1", ReadScanColumn("needs_review"));
        Assert.Equal(ResultStore.StatusFinal, ReadScanColumn("status")); // không kẹt provisional
        Assert.Equal("rv2", ReadScanColumn("rules_ver"));
    }

    [Fact]
    public void Verdict_cu_khong_con_tac_dung_thi_giu_ket_qua_regex()
    {
        Processor("rv1").ProcessFile(_file);
        CompleteLlmJobForCurrentContent("{\"labels\":[]}"); // không có "verdicts" → merge ra kết quả regex

        Assert.Equal(ProcessOutcome.Scanned, Processor("rv2").ProcessFile(_file));

        Assert.Equal("regex", ReadScanColumn("source"));
        Assert.Equal(ResultStore.StatusFinal, ReadScanColumn("status"));
    }
}
