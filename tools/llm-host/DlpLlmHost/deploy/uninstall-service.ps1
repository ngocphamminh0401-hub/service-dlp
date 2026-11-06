# [Thay đổi] File mới (đi kèm install-service.ps1, mục 14): gỡ hoàn toàn service + firewall rule đã tạo — để việc
# cài đặt ở trên có đường lùi rõ ràng (không để lại rác nếu người dùng đổi ý hoặc cài lại).
#Requires -RunAsAdministrator

param(
    [string]$ServiceName = "LlmSvc"
)

$ErrorActionPreference = "Stop"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne "Stopped") {
        Write-Host "Đang dừng service '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force
    }
    Write-Host "Đang xoá service '$ServiceName'..."
    sc.exe delete $ServiceName | Out-Null
} else {
    Write-Host "Service '$ServiceName' không tồn tại — bỏ qua."
}

$ruleName = "DLP LLM Host ($ServiceName) - Block Outbound"
if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
    Write-Host "Đang xoá firewall rule '$ruleName'..."
    Remove-NetFirewallRule -DisplayName $ruleName
} else {
    Write-Host "Firewall rule '$ruleName' không tồn tại — bỏ qua."
}

Write-Host "Đã gỡ xong. Thư mục model KHÔNG bị xoá (icacls đã cấp lúc cài đặt vẫn còn) — xoá thủ công nếu cần."
