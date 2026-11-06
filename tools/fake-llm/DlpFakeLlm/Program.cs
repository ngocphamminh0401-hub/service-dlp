// [Thay đổi] File mới (mục 5 - LLM giả chạy process riêng): 2 chế độ.
//   dotnet run --project tools/fake-llm/DlpFakeLlm -- serve [--pipe TÊN]   → chạy LLM giả THẬT, đứng chờ mãi
//   dotnet run --project tools/fake-llm/DlpFakeLlm                        → tự khởi động LLM giả làm process CON,
//     kết nối bằng gRPC client thật qua Named Pipe, gọi thử GetInfo + Classify (OK/BUSY/TIMEOUT/ERROR/NOT_READY/
//     BAD_CONTRACT), in PASS/FAIL, rồi kill process con — xác nhận hợp đồng contracts/inference.proto hoạt động
//     đúng qua ĐÚNG kênh thật (Named Pipe, mục 5.1), không phải gọi hàm C# trực tiếp trong cùng 1 tiến trình.
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using Dlp.Inference;
using Grpc.Net.Client;

const string DefaultPipeName = "dlp-fake-llm-test";

if (args.Length > 0 && args[0] == "serve")
{
    string pipeName = args.SkipWhile(a => a != "--pipe").Skip(1).FirstOrDefault() ?? DefaultPipeName;
    // [Thay đổi] Test contract_ver lệch (mục 5.3): --contract-ver N cho LLM giả TỰ NHẬN mình đang chạy phiên bản N
    uint contractVer = uint.TryParse(args.SkipWhile(a => a != "--contract-ver").Skip(1).FirstOrDefault(), out var v) ? v : 1;
    await RunServer(pipeName, contractVer);
    return 0;
}

return await RunSmokeTest();

// ================= Chế độ "serve": chạy LLM giả thật, đứng chờ mãi =================
static async Task RunServer(string pipeName, uint contractVer)
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning); // bớt log Kestrel/gRPC mặc định, dễ đọc report của bên gọi
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenNamedPipe(pipeName, listenOptions =>
        {
            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
        });
    });
    builder.Services.AddGrpc();
    builder.Services.AddSingleton(new DlpFakeLlm.FakeLlmOptions(contractVer));

    var app = builder.Build();
    app.MapGrpcService<DlpFakeLlm.FakeInferenceService>();

    Console.WriteLine($"[fake-llm] Đang chạy, Named Pipe: {pipeName}, contract_ver={contractVer}");
    await app.RunAsync();
}

// ================= Chế độ mặc định: tự khởi động server con + gọi thử qua gRPC thật =================
static async Task<int> RunSmokeTest()
{
    string pipeName = DefaultPipeName + "-" + Guid.NewGuid().ToString("N")[..8]; // tránh đụng pipe của lượt chạy khác
    var report = new SmokeReport();

    using var server = FakeLlmProcess.Start(pipeName);
    using var channel = ConnectNamedPipeChannel(pipeName);
    var client = new Inference.InferenceClient(channel);

    if (!await WaitUntilReady(client, TimeSpan.FromSeconds(10)))
    {
        report.Check("Kết nối được LLM giả qua Named Pipe", false, "GetInfo không trả lời sau 10s");
        Console.WriteLine(report.Summary());
        return 1;
    }
    report.Check("Kết nối được LLM giả qua Named Pipe", true, "GetInfo trả lời OK");

    var info = await client.GetInfoAsync(new Empty());
    report.Check("GetInfo trả đúng contract_ver=1", info.ContractVer == 1, $"contract_ver={info.ContractVer}");
    report.Check("GetInfo báo ready=true", info.Ready, $"ready={info.Ready}, model_ver={info.ModelVer}");

    async Task<ClassifyRes> Classify(string jobId, uint contractVer = 1)
    {
        var req = new ClassifyReq { JobId = jobId, ContractVer = contractVer, DeadlineMs = 500 };
        req.Findings.Add(new Finding { RuleId = "vn_id_001", Category = "pii", Score = 0.9f, Count = 1, HardRule = true });
        return await client.ClassifyAsync(req);
    }

    var ok = await Classify("job-binhthuong-001");
    report.Check("Classify bình thường -> OK, có nhãn + verdict xác nhận finding", ok.Status == Status.Ok && ok.Labels.Count > 0 && ok.Verdicts.Count == 1,
        $"status={ok.Status}, labels={string.Join(",", ok.Labels)}, verdict={ok.Verdicts.FirstOrDefault()?.RuleId}={ok.Verdicts.FirstOrDefault()?.TruePositive}");

    var okAgain = await Classify("job-binhthuong-001");
    report.Check("Cùng job_id -> cùng nhãn (ngẫu nhiên có kiểm soát, không phải ngẫu nhiên thật)", ok.Labels.SequenceEqual(okAgain.Labels),
        $"lần 1={string.Join(",", ok.Labels)}, lần 2={string.Join(",", okAgain.Labels)}");

    var busy = await Classify("BUSY-job-002");
    report.Check("Classify BUSY -> trả ngay, không xếp hàng ngầm (mục 5.4)", busy.Status == Status.Busy, $"status={busy.Status}");

    var sw = Stopwatch.StartNew();
    var timeout = await Classify("TIMEOUT-job-003");
    report.Check("Classify TIMEOUT -> trả sau khi quá deadline_ms", timeout.Status == Status.Timeout && sw.ElapsedMilliseconds >= 500,
        $"status={timeout.Status}, mất {sw.ElapsedMilliseconds}ms (deadline 500ms)");

    var modelError = await Classify("ERROR-job-004");
    report.Check("Classify MODEL_ERROR", modelError.Status == Status.ModelError, $"status={modelError.Status}");

    var notReady = await Classify("NOTREADY-job-005");
    report.Check("Classify NOT_READY", notReady.Status == Status.NotReady, $"status={notReady.Status}");

    var badContract = await Classify("job-006", contractVer: 99);
    report.Check("contract_ver lệch -> BAD_CONTRACT (mục 5.3), không xử lý như bình thường", badContract.Status == Status.BadContract,
        $"status={badContract.Status}");

    await client.CancelAsync(new CancelReq { JobId = "job-binhthuong-001" });
    report.Check("Cancel không ném lỗi", true, "gọi xong, không exception");

    Console.WriteLine(report.Summary());
    return report.Failures == 0 ? 0 : 1;
}

// Kết nối gRPC qua Named Pipe: .NET chưa có sẵn transport này trong GrpcChannel, phải tự nối SocketsHttpHandler
// với NamedPipeClientStream (đúng mẫu tài liệu chính thức của Microsoft cho "gRPC qua Windows named pipe").
static GrpcChannel ConnectNamedPipeChannel(string pipeName)
{
    var socketsHttpHandler = new SocketsHttpHandler
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.WriteThrough | PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            try
            {
                await clientStream.ConnectAsync(cancellationToken);
                return clientStream;
            }
            catch
            {
                clientStream.Dispose();
                throw;
            }
        }
    };

    return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = socketsHttpHandler });
}

// Named Pipe của server có thể chưa sẵn sàng ngay sau khi Process.Start — thử GetInfo vài lần thay vì 1 lần
static async Task<bool> WaitUntilReady(Inference.InferenceClient client, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        try
        {
            await client.GetInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddMilliseconds(500));
            return true;
        }
        catch (Grpc.Core.RpcException)
        {
            await Task.Delay(200);
        }
    }
    return false;
}

/// <summary>Chạy chính file .exe này với tham số "serve" làm process con — LLM giả THẬT, kill được như 1 tiến trình.</summary>
internal sealed class FakeLlmProcess : IDisposable
{
    private readonly Process _process;

    private FakeLlmProcess(Process process) => _process = process;

    public static FakeLlmProcess Start(string pipeName)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[] { "serve", "--pipe", pipeName })
        {
            info.ArgumentList.Add(a);
        }
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine("  [fake-llm] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new FakeLlmProcess(process);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
        _process.WaitForExit();
        _process.Dispose();
    }
}

/// <summary>In PASS/FAIL ra console.</summary>
internal sealed class SmokeReport
{
    private readonly StringBuilder _sb = new();
    public int Failures { get; private set; }

    public void Check(string name, bool ok, string detail)
    {
        if (!ok)
        {
            Failures++;
        }
        string line = $"{(ok ? "PASS" : "FAIL")}  {name} — {detail}";
        Console.WriteLine(line);
        _sb.AppendLine(line);
    }

    public string Summary() => "\n" + (Failures == 0 ? "== TẤT CẢ PASS ==" : $"== {Failures} FAIL ==");
}
