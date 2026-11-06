# [Thay đổi] File mới (checklist "chạy như Windows Service thật" - Bước A3): dừng và gỡ DlpService
# khỏi Service Control Manager (SCM). Chạy bằng PowerShell với quyền Administrator.

$ServiceName = "DlpService"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Lỗi: cần chạy PowerShell với quyền Administrator." -ForegroundColor Red
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' không tồn tại, không có gì để gỡ." -ForegroundColor Yellow
    exit 0
}

if ($existing.Status -ne "Stopped") {
    Write-Host "Đang dừng service '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus("Stopped", (New-TimeSpan -Seconds 15))
}

Write-Host "Đang gỡ service '$ServiceName'..."
sc.exe delete $ServiceName

Write-Host "Đã gỡ xong." -ForegroundColor Green
