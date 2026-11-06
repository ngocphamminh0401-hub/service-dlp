# [Thay đổi] File mới (buổi 15): so sánh 2 bộ đọc trên SERVICE THẬT ở cùng giới hạn đồng thời (Watch:MaxConcurrency).
# Với từng bộ đọc: dừng service → đặt cấu hình (legacy = bộ đọc .NET có magic bytes; tika = Tika native Replace) → khởi động →
# chờ "Đang theo dõi" → chạy Measure-DlpServiceE2E.ps1 (thời gian từng file + cả pipeline, độ chính xác, CPU/RAM theo thời gian)
# → chạy Analyze-DlpServiceE2E.py. Cuối cùng KHÔI PHỤC cấu hình cũ (+ Watch:MaxConcurrency) và trạng thái LlmSvc.
# Lưu ý: file phải lưu UTF-8 CÓ BOM (PowerShell 5.1 đọc file không BOM theo mã ANSI, hỏng chữ Việt).
<#
.SYNOPSIS
    Deploy bản hiện tại và đo service thật lần lượt với bộ đọc .NET và Tika native.
.EXAMPLE
    # PowerShell QUYỀN ADMINISTRATOR, đứng ở D:\Service-DLP:
    .\tools\perf\Run-ReaderComparisonE2E.ps1
    # Chỉ đo lại 1 bộ đọc, không publish lại:
    .\tools\perf\Run-ReaderComparisonE2E.ps1 -Readers tika -SkipPublish
#>
#Requires -RunAsAdministrator
param(
    [string]$ManifestCsv = "tools\eval\results\baseline-20261021-110840.csv",
    [string[]]$Readers = @('legacy', 'tika'),
    [int]$MaxConcurrency = 6,
    [int]$TikaIsolates = 2,
    [int[]]$RateSteps = @(5, 20, 50),  # tải đều (file/giây) — đo CPU/RAM ở mức tải thường gặp
    [int]$StepSeconds = 120,
    [int]$IdleAfterSec = 300,          # theo dõi khi rảnh sau tải (RAM có trả lại không)
    [int]$ReadyTimeoutSec = 300,       # chờ service quét xong thư mục lúc khởi động
    [switch]$SkipPublish,              # dùng bản đang có trong publish\ (không build lại)
    [switch]$KeepLlmRunning            # mặc định dừng LlmSvc khi đo (đo riêng lớp regex, như các lượt trước)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$publish = Join-Path $root 'publish'
$cfgPath = Join-Path $publish 'appsettings.json'
$logFolder = Join-Path $publish 'logs'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$cfgPath.bak-$stamp"
$summaryPath = Join-Path $PSScriptRoot "results\readercmp-$stamp.txt"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# ---------- Hàm phụ trợ (AI sinh) ----------

# Gán thuộc tính cho object đọc từ JSON (ConvertFrom-Json trả PSCustomObject: thuộc tính chưa có phải Add-Member)
function Set-JsonProp($Object, [string]$Name, $Value) {
    if ($Object.PSObject.Properties[$Name]) { $Object.$Name = $Value }
    else { $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

function Write-Config($Config) {
    [IO.File]::WriteAllText($cfgPath, ($Config | ConvertTo-Json -Depth 10), $utf8NoBom)
}

function Stop-Dlp {
    $svc = Get-Service DlpService
    if ($svc.Status -ne 'Stopped') { Stop-Service DlpService; $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
}

# Khởi động service rồi chờ dòng "Đang theo dõi thư mục" MỚI (ghi sau lúc khởi động) — service quét toàn thư mục trước khi in dòng này
function Start-DlpAndWaitReady {
    $since = Get-Date
    Start-Service DlpService
    $deadline = $since.AddSeconds($ReadyTimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $log = Get-ChildItem -LiteralPath $logFolder -Filter 'dlpservice-*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log -and $log.LastWriteTime -gt $since) {
            # Mở chia sẻ: service đang ghi file log
            $fs = New-Object IO.FileStream($log.FullName, 'Open', 'Read', 'ReadWrite, Delete')
            try { $text = (New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
            $lines = $text -split "`n" | Where-Object { $_ -match 'Đang theo dõi thư mục|Xử lý tối đa' }
            $ready = $lines | Where-Object { $_ -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})' -and [datetime]$Matches[1] -ge $since.AddSeconds(-1) -and $_ -match 'Đang theo dõi' }
            $limit = $lines | Where-Object { $_ -match 'Xử lý tối đa (\d+) file' } | Select-Object -Last 1
            if ($ready) {
                Write-Host "  Service sẵn sàng sau $([int]((Get-Date) - $since).TotalSeconds) giây. $limit"
                return
            }
        }
    }
    throw "Service không báo 'Đang theo dõi' sau $ReadyTimeoutSec giây — xem $logFolder"
}

# ---------- Chạy ----------

Copy-Item -LiteralPath $cfgPath -Destination $backup
$original = Get-Content -LiteralPath $backup -Raw -Encoding UTF8 | ConvertFrom-Json
$llmWasRunning = (Get-Service LlmSvc -ErrorAction SilentlyContinue).Status -eq 'Running'
Write-Host "Sao lưu cấu hình: $backup. LlmSvc đang chạy: $llmWasRunning"
"So sánh bộ đọc trên service thật — $stamp, MaxConcurrency=$MaxConcurrency, Tika isolates=$TikaIsolates" | Out-File $summaryPath -Encoding utf8

try {
    if ($llmWasRunning -and -not $KeepLlmRunning) { Write-Host 'Dừng LlmSvc trong lúc đo'; Stop-Service LlmSvc }
    Stop-Dlp

    if (-not $SkipPublish) {
        Write-Host 'Publish bản hiện tại vào publish\ ...'
        dotnet publish (Join-Path $root 'DlpService\DlpService.csproj') -c Release -r win-x64 --self-contained false -o $publish -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish thất bại' }
    }

    foreach ($reader in $Readers) {
        Write-Host "`n===== Bộ đọc: $reader ====="
        Stop-Dlp

        # Cấu hình: lấy bản gốc (giữ DllPath, Sha256, Llm, Score...) rồi chỉ đổi phần bộ đọc + giới hạn đồng thời
        $cfg = Get-Content -LiteralPath $backup -Raw -Encoding UTF8 | ConvertFrom-Json
        Set-JsonProp $cfg 'Watch' ([pscustomobject]@{ MaxConcurrency = $MaxConcurrency })
        Set-JsonProp $cfg.TikaNative 'Enabled' ($reader -eq 'tika')
        Set-JsonProp $cfg.TikaNative 'Mode' 'Replace'
        Set-JsonProp $cfg.TikaNative 'Isolates' $TikaIsolates
        Write-Config $cfg

        Start-DlpAndWaitReady
        $before = @(Get-ChildItem (Join-Path $PSScriptRoot 'results') -Filter 'e2e-*-summary.json' | ForEach-Object Name)
        & (Join-Path $PSScriptRoot 'Measure-DlpServiceE2E.ps1') -ManifestCsv $ManifestCsv -FastDrop `
            -RateSteps $RateSteps -StepSeconds $StepSeconds -IdleAfterSec $IdleAfterSec
        $new = Get-ChildItem (Join-Path $PSScriptRoot 'results') -Filter 'e2e-*-summary.json' | Where-Object { $before -notcontains $_.Name } |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $new) { throw "Không thấy kết quả E2E mới cho bộ đọc $reader" }
        $prefix = $new.FullName -replace '-summary\.json$', ''
        "$reader`t$(Split-Path $prefix -Leaf)" | Out-File $summaryPath -Append -Encoding utf8

        # Phân tích (độ chính xác, độ trễ, [Perf], tài nguyên) → file .md cạnh kết quả
        python -I (Join-Path $PSScriptRoot 'Analyze-DlpServiceE2E.py') $prefix | Out-File "$prefix-analysis-$reader.md" -Encoding utf8
        Write-Host "  Kết quả: $prefix-*  (phân tích: $prefix-analysis-$reader.md)"
    }
}
finally {
    # Khôi phục cấu hình gốc + giới hạn đồng thời (bản mới đã deploy), chạy lại service và LlmSvc như trước khi đo
    Write-Host "`nKhôi phục cấu hình gốc (+ Watch:MaxConcurrency=$MaxConcurrency) và khởi động lại service"
    Stop-Dlp
    Set-JsonProp $original 'Watch' ([pscustomobject]@{ MaxConcurrency = $MaxConcurrency })
    Write-Config $original
    Start-Service DlpService
    if ($llmWasRunning -and -not $KeepLlmRunning) { Start-Service LlmSvc }
    Write-Host "Tóm tắt lượt đo: $summaryPath"
}
