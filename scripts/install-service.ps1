# [Thay đổi] File mới (checklist "chạy như Windows Service thật" - Bước A3): đăng ký DlpService.exe
# vào Service Control Manager (SCM) của Windows + cấu hình tự khởi động lại khi crash.
# Chạy file này bằng PowerShell với quyền Administrator (chuột phải -> "Run as Administrator",
# hoặc mở PowerShell as Admin rồi gõ: .\install-service.ps1).

$ServiceName = "DlpService"
$DisplayName = "DLP Data Classification Service"
$Description = "Theo dõi thư mục và phân loại mức độ nhạy cảm của dữ liệu (Data Loss Prevention)."

# $PSScriptRoot = thư mục chứa chính file script này (D:\Service-DLP\scripts) -> tính đường dẫn
# tương đối tới publish\, không hardcode ổ đĩa/đường dẫn tuyệt đối trong script.
$ExePath = Join-Path $PSScriptRoot "..\publish\DlpService.exe"
$ExePath = (Resolve-Path $ExePath -ErrorAction SilentlyContinue).Path

# --- Kiểm tra điều kiện trước khi cài ---

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Lỗi: cần chạy PowerShell với quyền Administrator." -ForegroundColor Red
    exit 1
}

if (-not $ExePath -or -not (Test-Path $ExePath)) {
    Write-Host "Lỗi: không tìm thấy DlpService.exe. Hãy chạy 'dotnet publish' trước (xem docs/buoi-03-tom-tat.md)." -ForegroundColor Red
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' đã tồn tại. Chạy uninstall-service.ps1 trước nếu muốn cài lại." -ForegroundColor Yellow
    exit 1
}

# --- Đăng ký service ---
# sc.exe cú pháp đặc biệt: "binPath=" và "start=" phải KHÔNG có khoảng trắng trước dấu "=",
# và PHẢI có 1 khoảng trắng sau dấu "=" (nằm ở việc truyền 2 tham số PowerShell riêng biệt bên dưới).
Write-Host "Đang đăng ký service '$ServiceName' -> $ExePath"
sc.exe create $ServiceName binPath= "`"$ExePath`"" start= auto DisplayName= "$DisplayName"
sc.exe description $ServiceName "$Description"

# --- Cấu hình tự khởi động lại khi crash ---
# reset=86400 (giây, 1 ngày): sau 1 ngày chạy ổn định thì bộ đếm số lần restart về 0
# actions=restart/5000 x3: crash lần 1/2/3 đều đợi 5000ms (5 giây) rồi tự khởi động lại
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000

Write-Host ""
Write-Host "Đã cài đặt xong. Service CHƯA chạy — khởi động bằng:" -ForegroundColor Green
Write-Host "  sc.exe start $ServiceName"
Write-Host "hoặc mở services.msc, tìm '$DisplayName', bấm Start."
