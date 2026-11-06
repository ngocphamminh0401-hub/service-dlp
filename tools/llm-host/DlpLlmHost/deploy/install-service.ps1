# [Thay đổi] File mới (mục 14 "Đăng ký dịch vụ": sc create, sc sidtype ... unrestricted, recovery (restart khi
# lỗi), delayed-auto-start; mục 6.1: "chạy bằng tài khoản dịch vụ ảo quyền thấp NT SERVICE\LlmSvc"; mục 6.1/8:
# "chặn outbound mạng bằng Windows Firewall rule theo exe"): đăng ký DlpLlmHost.exe thành 1 Windows Service THẬT.
#
# BẮT BUỘC: publish trước (không dùng "dotnet run" cho service thật):
#   dotnet publish tools\llm-host\DlpLlmHost -c Release -o tools\llm-host\DlpLlmHost\publish --self-contained false
#
# Chạy script này với quyền Administrator:
#   .\install-service.ps1 -ExePath "D:\Service-DLP\tools\llm-host\DlpLlmHost\publish\DlpLlmHost.exe" -ModelDir "D:\Service-DLP\models\phi-4-mini-instruct-onnx\cpu_and_mobile\cpu-int4-rtn-block-32-acc-level-4"
#
# An toàn/khả năng đảo ngược: dùng uninstall-service.ps1 (cùng thư mục) để gỡ hoàn toàn (sc delete + xoá firewall
# rule) nếu muốn hoàn tác. Script KHÔNG tự chạy sc create nếu service đã tồn tại — báo lỗi rõ, không ghi đè âm thầm.

#Requires -RunAsAdministrator

param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [string]$ModelDir,

    [string]$ServiceName = "LlmSvc",
    [string]$PipeName = "dlp-llm-host",
    [int]$IdleUnloadMinutes = 10,
    # [Thay đổi] Buổi 11: 10240 -> 12288. Trần cũ làm lượt sinh đầu tiên sau khi nạp model báo "bad allocation" (private đỉnh ~9,8GB).
    [int]$MemoryLimitMb = 12288,
    [int]$CpuRatePercent = 50,

    # [Thay đổi] Vá lỗi (buổi 11): tài khoản ĐƯỢC PHÉP KẾT NỐI pipe phải là tài khoản của BÊN GỌI (DlpService),
    # KHÔNG PHẢI tài khoản của chính LlmSvc (xem PipeAcl.cs mục 5.5: allowedAccount = "tài khoản được phép kết nối").
    # Mặc định "NT AUTHORITY\SYSTEM" vì scripts/install-service.ps1 (DlpService) không khai obj= -> mặc định LocalSystem.
    # Nếu sau này DlpService được đổi sang chạy bằng tài khoản dịch vụ ảo riêng (NT SERVICE\RegexSvc, mục 6.1), phải
    # truyền lại -CallerAccount đúng tài khoản đó khi cài lại LlmSvc.
    [string]$CallerAccount = "NT AUTHORITY\SYSTEM"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    throw "Không tìm thấy exe: $ExePath — chạy 'dotnet publish' trước (xem comment đầu file)."
}
if (-not (Test-Path $ModelDir)) {
    throw "Không tìm thấy thư mục model: $ModelDir"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service '$ServiceName' đã tồn tại (trạng thái: $($existing.Status)). Chạy uninstall-service.ps1 trước nếu muốn cài lại."
}

$virtualAccount = "NT SERVICE\$ServiceName"

# [Thay đổi] Vá lỗi (buổi 11): PowerShell 5.1 âm thầm xoá dấu " lồng bên trong khi truyền 1 biến string cho tiến
# trình native qua cú pháp "sc.exe create ... binPath= $binPath ..." thông thường — làm binPath bị tách sai tại dấu
# cách trong "NT SERVICE\LlmSvc" (NT và SERVICE\LlmSvc thành 2 token riêng), khiến sc.exe nhận sai tham số và báo lỗi
# 1639. Đã xác nhận qua test tay (echoargs.exe): dựng binPath với dấu " lồng escape kiểu Win32 chuẩn (\"..\") rồi gọi
# qua System.Diagnostics.Process (bỏ qua lớp xử lý tham số lỗi của PowerShell với native command) mới ra đúng 1 token.
$exeArgs = "serve --pipe $PipeName --model-dir \`"$ModelDir\`" --idle-unload-minutes $IdleUnloadMinutes --memory-limit-mb $MemoryLimitMb --cpu-rate-percent $CpuRatePercent --allowed-account \`"$CallerAccount\`""
$binPath = "\`"$ExePath\`" $exeArgs"

# Dựng nguyên dòng tham số cho sc.exe: binPath BẮT BUỘC là ĐÚNG 1 token (exe + toàn bộ args), nên bọc thêm 1 cặp
# dấu " ngoài cùng; obj/DisplayName cũng có dấu cách nên cần bọc dấu " (không cần escape vì không lồng thêm nữa).
$createArgs = "create $ServiceName binPath= `"$binPath`" obj= `"$virtualAccount`" start= delayed-auto DisplayName= `"DLP LLM Host ($ServiceName)`""

# Hàm phụ: chạy sc.exe qua Process.Start (KHÔNG qua "&"/gọi native command thường của PowerShell) để tham số không bị
# PowerShell "sửa hộ" — .Arguments nhận thẳng 1 chuỗi, CreateProcess tự tách theo đúng chuẩn Win32 (CommandLineToArgvW).
function Invoke-ScExe([string]$argumentLine)
{
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "sc.exe"
    $psi.Arguments = $argumentLine
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($stdout) { Write-Host $stdout.TrimEnd() }
    if ($stderr) { Write-Host $stderr.TrimEnd() }
    return $process.ExitCode
}

Write-Host "== 1) sc create — tài khoản dịch vụ ảo, delayed-auto-start (mục 6.3, 14) =="
$exitCode = Invoke-ScExe $createArgs
if ($exitCode -ne 0) { throw "sc create thất bại (mã $exitCode)" }

Write-Host "== 2) sc sidtype unrestricted — cấp SID riêng cho tài khoản ảo (mục 5.5, 14), để Pipe ACL dùng được =="
sc.exe sidtype $ServiceName unrestricted
if ($LASTEXITCODE -ne 0) { throw "sc sidtype thất bại (mã $LASTEXITCODE)" }

Write-Host "== 3) sc failure — recovery: tự khởi động lại khi lỗi/treo (mục 6.3: 'Regex service có thể yêu cầu SCM khởi động lại LLM service khi treo') =="
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000
if ($LASTEXITCODE -ne 0) { throw "sc failure thất bại (mã $LASTEXITCODE)" }
sc.exe failureflag $ServiceName 1 | Out-Null # áp dụng recovery action cả khi service dừng vì lỗi CÓ mã lỗi khác 0, không chỉ crash

Write-Host "== 4) sc description =="
sc.exe description $ServiceName "DLP LLM Host — phân loại độ nhạy cảm bằng ONNX Runtime GenAI (Phi-4-mini-instruct INT4). Tài khoản ảo, quyền tối thiểu." | Out-Null

Write-Host "== 5) ACL chỉ đọc thư mục model cho tài khoản dịch vụ (mục 14: 'ACL chỉ đọc cho tài khoản LLM') =="
icacls $ModelDir /grant "${virtualAccount}:(OI)(CI)RX" /T | Out-Null

Write-Host "== 6) Firewall — chặn outbound theo exe (mục 6.1, 8) =="
$ruleName = "DLP LLM Host ($ServiceName) - Block Outbound"
if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
    Remove-NetFirewallRule -DisplayName $ruleName
}
New-NetFirewallRule -DisplayName $ruleName -Direction Outbound -Program $ExePath -Action Block -Profile Any | Out-Null

Write-Host ""
Write-Host "Đã đăng ký xong service '$ServiceName' (tài khoản chạy $virtualAccount, cho phép pipe kết nối từ $CallerAccount, delayed-auto-start, recovery, firewall block outbound)."
Write-Host "Service CHƯA được start. Chạy 'Start-Service $ServiceName' khi sẵn sàng, hoặc để SCM tự khởi động sau khi reboot (delayed-auto-start)."
