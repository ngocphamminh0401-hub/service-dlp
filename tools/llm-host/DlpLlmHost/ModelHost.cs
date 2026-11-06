// [Thay đổi] File mới (mục 6.2/6.3/6.4 - Model và runtime, Vòng đời, Suy luận): quản lý nạp/giải phóng model ONNX
// Runtime GenAI THẬT. Nạp lười — chỉ khi có job đầu tiên gọi EnsureLoadedAsync (Classify), không tốn RAM lúc
// service mới khởi động. Tự giải phóng RAM sau N phút rảnh (RunIdleUnloadLoopAsync).
//
// PREFIX CACHE (mục 6.4 "Prefix cache cho system prompt và few-shot cố định — phần lặp lại nhiều nhất"): thay vì
// tạo Generator MỚI cho mỗi job (phải tính lại toàn bộ prefill của system prompt ~1300+ token mỗi lần — đã đo thật
// chiếm phần lớn thời gian, xem docs/buoi-09-tom-tat.md mục 4.c), giữ 1 Generator DÙNG CHUNG suốt vòng đời model:
// nạp prefix 1 lần lúc load (đây cũng chính là warm-up), rồi mỗi job chỉ AppendTokens phần đuôi (nội dung riêng của
// job) và RewindTo lại điểm "vừa xong prefix" sau khi xong — tái dùng KV-cache của prefix, không tính lại.
using System.Diagnostics;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace DlpLlmHost;

public enum ModelHostState { Unloaded, Loading, Ready, Failed }

public sealed class ModelHost(
    string modelDir,
    string modelVer,
    TimeSpan idleUnloadAfter,
    string promptPrefix,
    string? jsonSchema,
    int maxSuffixTokens,
    int maxNewTokens,
    Task integrityGate,
    string? fallbackJsonSchema = null,
    string? preferredJsonSchema = null) : IDisposable
{
    // [Thay đổi] preferredJsonSchema: schema thử ĐẦU TIÊN (chặt + cấm khoảng trắng tùy ý, xem ClassifyOutputSchema). Nghi vấn:
    // JSON schema cho phép khoảng trắng tùy ý giữa các thành phần nên model có thể "lạc" sinh khoảng trắng liên tục.
    // [Thay đổi] fallbackJsonSchema: schema dự phòng (bản cũ, không có biên) — chỉ dùng nếu runtime TỪ CHỐI jsonSchema chặt
    // (VD không hỗ trợ maxLength/maxItems). Tránh trường hợp schema chặt làm guidance tắt hẳn mà không ai biết.
    // [Thay đổi] "Cổng toàn vẹn": SHA-256 model được kiểm NỀN sau khi service đã báo RUNNING cho SCM (xem Program.cs),
    // nên model TUYỆT ĐỐI không được nạp trước khi cổng mở. Cổng hoàn tất bình thường = hash khớp; bị lỗi (faulted) =
    // hash sai/thiếu file; bị huỷ = service đang dừng.

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Model? _model;
    private Tokenizer? _tokenizer;
    private GeneratorParams? _sharedGeneratorParams;
    private Generator? _sharedGenerator;
    private int _prefixLength;
    private DateTime _lastUsedUtc = DateTime.MinValue;

    public ModelHostState State { get; private set; } = ModelHostState.Unloaded;
    public string ModelVer => modelVer;

    /// <summary>Nạp model nếu chưa nạp (hoặc vừa bị unload do rảnh); nếu đã sẵn sàng thì chỉ cập nhật "vừa dùng
    /// xong". Ném exception nếu nạp thất bại — bên gọi (gRPC service) tự bắt và trả MODEL_ERROR, KHÔNG để lộ ra
    /// ngoài như crash tiến trình.</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        // [Thay đổi] ĐỢI cổng hash TRƯỚC KHI lấy _lock (không giữ khoá trong lúc chờ). Chưa băm xong thì job đứng chờ
        // thay vì nhận lỗi — tránh job bị FailJob oan (mỗi lỗi tính 1 trong 3 lần thử). Hash sai: await ném đúng
        // exception đó -> InferenceHostService trả MODEL_ERROR. Job bị huỷ/quá hạn khi đang chờ -> OperationCanceledException.
        await integrityGate.WaitAsync(cancellationToken);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (State == ModelHostState.Ready)
            {
                _lastUsedUtc = DateTime.UtcNow;
                return;
            }

            State = ModelHostState.Loading;
            try
            {
                await Task.Run(() => LoadAndPreparePrefixCache(cancellationToken), cancellationToken);
                State = ModelHostState.Ready;
                _lastUsedUtc = DateTime.UtcNow;
            }
            catch
            {
                DisposeResourcesLocked();
                State = ModelHostState.Failed;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ONNX Runtime GenAI nạp/suy luận là API đồng bộ (chặn luồng) — chạy trong Task.Run để không chặn luồng gRPC.
    // Nạp prefix ở đây LUÔN ĐÓNG VAI TRÒ warm-up (mục 6.3: "warm-up bằng một request ngắn sau khi load") — trước
    // đây warm-up bằng chuỗi "Hello" không liên quan, giờ warm đúng con đường thật (prefix thật) và giữ luôn KV-cache.
    private void LoadAndPreparePrefixCache(CancellationToken cancellationToken)
    {
        var loadWatch = Stopwatch.StartNew(); // [Thay đổi] log chẩn đoán: thời gian nạp model + dựng prefix cache
        _model = new Model(modelDir);
        _tokenizer = new Tokenizer(_model);

        using var prefixSequences = _tokenizer.Encode(promptPrefix);
        _prefixLength = prefixSequences[0].Length;

        int maxLength = _prefixLength + maxSuffixTokens + maxNewTokens;
        GeneratorParams NewParams()
        {
            var p = new GeneratorParams(_model);
            p.SetSearchOption("do_sample", false); // tham lam — tương đương temperature 0 (mục 6.4)
            p.SetSearchOption("max_length", maxLength);
            return p;
        }
        _sharedGeneratorParams = NewParams();
        // [Thay đổi] Log chẩn đoán: trước đây SetGuidance thất bại bị nuốt IM LẶNG (catch rỗng) nên không ai biết ràng buộc
        // JSON schema có thật sự đang bật không. Giờ ghi rõ kết quả: nếu "KHONG BAT" thì model sinh tự do, không bị ép
        // đúng khuôn JSON — nghi vấn chính cho lỗi "JSON hỏng sau retry" và lượt sinh dài bất thường.
        // [Thay đổi] Thử lần lượt các schema theo thứ tự "chặt nhất → lỏng nhất": (1) chặt + cấm khoảng trắng tùy ý,
        // (2) chặt, (3) cũ không biên. Runtime từ chối schema nào thì tạo GeneratorParams MỚI (sau 1 lần SetGuidance lỗi
        // không tin trạng thái cũ) rồi thử schema kế; hết schema vẫn lỗi thì chạy không ép (như trước đây). Log ghi rõ
        // schema nào đang chạy và các schema nào đã bị từ chối (kèm lý do).
        var candidates = new List<(string Name, string Schema)>();
        if (preferredJsonSchema != null)
        {
            candidates.Add(("chat+compact(cam khoang trang tuy y)", preferredJsonSchema));
        }
        if (jsonSchema != null)
        {
            candidates.Add(("chat", jsonSchema));
        }
        if (fallbackJsonSchema != null)
        {
            candidates.Add(("cu khong bien", fallbackJsonSchema));
        }

        var rejected = new List<string>();
        string? appliedName = null;
        foreach (var (name, schema) in candidates)
        {
            try
            {
                _sharedGeneratorParams.SetGuidance("json_schema", schema);
                appliedName = name;
                break;
            }
            catch (Exception ex)
            {
                rejected.Add($"{name} -> {ex.GetType().Name}: {ex.Message}");
                _sharedGeneratorParams.Dispose();
                _sharedGeneratorParams = NewParams();
            }
        }
        string rejectedNote = rejected.Count > 0 ? $" [bi tu choi truoc do: {string.Join(" | ", rejected)}]" : "";
        string guidanceStatus = appliedName != null
            ? $"BAT (schema {appliedName}){rejectedNote}"
            : candidates.Count == 0 ? "KHONG BAT (khong co schema nao)" : $"KHONG BAT - moi schema deu bi tu choi{rejectedNote}";
        DiagLog.Write($"nap model: constrained decoding (json_schema) {guidanceStatus}; prefix={_prefixLength}tok, max_length={maxLength}, do_sample=false");

        _sharedGenerator = new Generator(_model, _sharedGeneratorParams);
        _sharedGenerator.AppendTokenSequences(prefixSequences);
        cancellationToken.ThrowIfCancellationRequested();
        // Bắt buộc chạy 1 bước để runtime thực sự tính KV-cache cho toàn bộ prefix (AppendTokens chỉ "đăng ký"
        // token, việc tính toán xảy ra ở lần GenerateNextToken tiếp theo). Token sinh ra ở bước này là "rác" — prefix
        // cố tình dừng giữa câu (ngay trước <|assistant|>, chưa có nội dung tài liệu) — bỏ ngay bằng RewindTo.
        _sharedGenerator.GenerateNextToken();
        _sharedGenerator.RewindTo((ulong)_prefixLength);
        DiagLog.Write($"nap model xong: {loadWatch.ElapsedMilliseconds}ms (gom nap model + tokenizer + dung prefix cache), ws={DiagLog.WorkingSetMb()}MB");
    }

    /// <summary>Sinh chữ thật cho phần "đuôi" (nội dung riêng của 1 job/batch) — PHẢI gọi EnsureLoadedAsync thành
    /// công trước. Tái dùng Generator dùng chung (prefix cache) — xem đầu file. Kiểm tra
    /// <paramref name="cancellationToken"/> SAU MỖI TOKEN — huỷ (deadline_ms hết hạn hoặc RPC Cancel) dừng vòng
    /// sinh gần như ngay lập tức so với lần kiểm tra kế tiếp. Trả kèm số token THẬT đã sinh (mục 13.3 - đo tokens/s).</summary>
    /// <param name="tokenCap">[Thay đổi] Trần số token MỚI cho riêng job này (0 = không đặt, chỉ dựa vào max_length cố định).
    /// max_length đặt lúc nạp model nên không đổi được theo từng job (Generator dùng chung cho prefix cache) — trần theo
    /// job được áp ngay trong vòng sinh, dừng sớm khi vượt (GenerateResult.HitTokenCap = true).</param>
    public async Task<GenerateResult> GenerateAsync(string suffix, CancellationToken cancellationToken = default, int tokenCap = 0)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (State != ModelHostState.Ready || _sharedGenerator == null || _tokenizer == null)
            {
                throw new InvalidOperationException("Model chưa sẵn sàng — phải gọi EnsureLoadedAsync trước khi GenerateAsync.");
            }
            _lastUsedUtc = DateTime.UtcNow;
            return await Task.Run(() => GenerateSync(suffix, cancellationToken, tokenCap), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private GenerateResult GenerateSync(string suffix, CancellationToken cancellationToken, int tokenCap)
    {
        var generator = _sharedGenerator!;
        // [Thay đổi] Log chẩn đoán timeout/model_error: các biến này khai ngoài try để nhánh huỷ/lỗi ghi được số token
        // đã sinh được tới lúc đó (phân biệt "sinh quá chậm" với "sinh mãi không dừng").
        var genWatch = Stopwatch.StartNew();
        int generated = 0, suffixTokens = 0, allowedNew = 0;
        try
        {
            using var suffixSequences = _tokenizer!.Encode(suffix);
            generator.AppendTokens(suffixSequences[0]);
            suffixTokens = suffixSequences[0].Length;
            int startLength = _prefixLength + suffixTokens;
            // max_length được đặt CỐ ĐỊNH lúc nạp = prefix + maxSuffixTokens + maxNewTokens (xem LoadAndPreparePrefixCache),
            // KHÔNG co theo độ dài đuôi thật — nên đuôi càng ngắn, số token model được phép sinh càng lớn hơn maxNewTokens.
            allowedNew = _prefixLength + maxSuffixTokens + maxNewTokens - startLength;
            // [Thay đổi] Trần theo job: lấy nhỏ hơn giữa trần job và trần cứng của max_length.
            if (tokenCap > 0 && tokenCap < allowedNew)
            {
                allowedNew = tokenCap;
            }

            bool hitCap = false;
            while (!generator.IsDone())
            {
                cancellationToken.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                generated++;
                if (tokenCap > 0 && generated >= allowedNew && !generator.IsDone())
                {
                    hitCap = true; // dừng sớm: model chưa tự kết thúc mà đã vượt trần cho phép
                    break;
                }
            }

            var outputTokens = generator.GetSequence(0);
            var newTokens = outputTokens.Slice(startLength).ToArray();
            return new GenerateResult(_tokenizer.Decode(newTokens), newTokens.Length, suffixTokens, allowedNew, genWatch.ElapsedMilliseconds, hitCap);
        }
        catch (OperationCanceledException)
        {
            DiagLog.Write($"gen bi huy giua chung: da sinh {generated} token sau {genWatch.ElapsedMilliseconds}ms (dau vao {suffixTokens} token, cho phep toi da {allowedNew} token moi, ws={DiagLog.WorkingSetMb()}MB)");
            throw;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"gen LOI sau {generated} token, {genWatch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message} (ws={DiagLog.WorkingSetMb()}MB)");
            throw;
        }
        finally
        {
            // [Thay đổi] mục 6.4: LUÔN đưa Generator dùng chung về đúng điểm "vừa xong prefix" sau mỗi job — kể cả
            // khi job này lỗi/bị huỷ giữa chừng — để job tiếp theo tái dùng prefix cache đúng, không dính phần đuôi
            // của job trước. RewindTo tự nó lỗi (generator hỏng) thì coi model hỏng luôn, bắt EnsureLoadedAsync lần
            // sau nạp lại từ đầu thay vì tiếp tục dùng 1 generator có thể đã sai trạng thái.
            try
            {
                generator.RewindTo((ulong)_prefixLength);
            }
            catch
            {
                DisposeResourcesLocked();
                State = ModelHostState.Failed;
            }
        }
    }

    /// <summary>Giải phóng model khỏi RAM (mục 6.3: "unload sau N phút rảnh để trả RAM"). An toàn gọi nhiều lần.</summary>
    public void Unload()
    {
        _lock.Wait();
        try
        {
            DisposeResourcesLocked();
            if (State != ModelHostState.Failed)
            {
                State = ModelHostState.Unloaded;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void DisposeResourcesLocked()
    {
        _sharedGenerator?.Dispose();
        _sharedGeneratorParams?.Dispose();
        _tokenizer?.Dispose();
        _model?.Dispose();
        _sharedGenerator = null;
        _sharedGeneratorParams = null;
        _tokenizer = null;
        _model = null;
    }

    /// <summary>Vòng lặp nền chạy suốt vòng đời process: mỗi 30s kiểm tra "rảnh quá N phút chưa" rồi unload.
    /// Program.cs khởi động vòng lặp này song song với server gRPC.</summary>
    public async Task RunIdleUnloadLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (State == ModelHostState.Ready && DateTime.UtcNow - _lastUsedUtc > idleUnloadAfter)
            {
                Unload();
            }
        }
    }

    public void Dispose()
    {
        DisposeResourcesLocked();
        _lock.Dispose();
    }
}

// [Thay đổi] Log chẩn đoán: thêm số token đầu vào (đuôi), số token mới được phép sinh, và thời gian sinh thuần (ms) —
// đều có giá trị mặc định nên chỗ khác tạo GenerateResult kiểu cũ vẫn biên dịch.
// HitTokenCap: vòng sinh bị DỪNG SỚM vì vượt trần token của job (khác với việc tự kết thúc hay chạm max_length cứng).
public sealed record GenerateResult(string Text, int TokenCount, int SuffixTokens = 0, int AllowedNewTokens = 0, long GenMs = 0, bool HitTokenCap = false);
