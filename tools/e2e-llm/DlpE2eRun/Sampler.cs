// [Thay đổi] File mới: lấy mẫu CPU/RAM của các process (LLM host + chính harness — đại diện cho DlpService, vì harness
// chạy cùng code của DlpService) mỗi 1 giây, gắn nhãn "pha" đang chạy (pass1 / llm / replay) để tổng hợp theo pha.
// CPU% tính theo TOÀN MÁY (chia cho số nhân logic) — cùng thang đo với MachineIdleChecker của service thật.
using System.Diagnostics;

namespace DlpE2eRun;

internal sealed record Sample(double T, string Phase, string Proc, double CpuPct, double WsMb, double PrivMb, double MachineCpu);

internal sealed class Sampler : IDisposable
{
    private readonly Dictionary<string, Process> _procs;
    private readonly List<Sample> _samples = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public volatile string Phase = "setup";

    public Sampler(Dictionary<string, Process> procs)
    {
        _procs = procs;
        _loop = Task.Run(RunAsync);
    }

    public IReadOnlyList<Sample> Snapshot()
    {
        lock (_samples)
        {
            return _samples.ToList();
        }
    }

    private async Task RunAsync()
    {
        int cores = Environment.ProcessorCount;
        var prevCpu = new Dictionary<string, TimeSpan>();
        var prevAt = new Dictionary<string, double>();
        PerformanceCounter? machine = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                machine = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                machine.NextValue(); // lần đầu luôn trả 0
            }
        }
        catch (Exception)
        {
            machine = null; // không đọc được CPU toàn máy -> ghi -1, không chặn việc đo
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                double now = _clock.Elapsed.TotalSeconds;
                double machineCpu = -1;
                try
                {
                    machineCpu = machine?.NextValue() ?? -1;
                }
                catch (Exception)
                {
                    // bỏ qua
                }

                foreach (var (name, proc) in _procs)
                {
                    try
                    {
                        proc.Refresh();
                        if (proc.HasExited)
                        {
                            continue;
                        }
                        var cpu = proc.TotalProcessorTime;
                        if (prevCpu.TryGetValue(name, out var lastCpu))
                        {
                            double dt = now - prevAt[name];
                            double pct = dt > 0 ? (cpu - lastCpu).TotalSeconds / (dt * cores) * 100 : 0;
                            lock (_samples)
                            {
                                _samples.Add(new Sample(now, Phase, name, pct, proc.WorkingSet64 / 1048576.0,
                                    proc.PrivateMemorySize64 / 1048576.0, machineCpu));
                            }
                        }
                        prevCpu[name] = cpu;
                        prevAt[name] = now;
                    }
                    catch (Exception)
                    {
                        // process vừa thoát / không đọc được -> bỏ qua mẫu này
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // dừng bình thường
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // bỏ qua
        }
        _cts.Dispose();
    }
}
