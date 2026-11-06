# [Thay đổi] File mới (đo chi phí Windows Defender): Defender tốn bao nhiêu khi DlpService / 1 process mở file docx/pdf.
# Gộp 2 thí nghiệm ngày 27/10/2026:
#   Read    — copy mẫu file ra thư mục tạm rồi đọc 2 lần (lần 1 có bị quét không, lần 2 có dùng cache không)
#   Service — thả file cho DlpService ĐANG CHẠY: A copy thẳng vào watch-folder; B copy ra staging, chờ, rồi move vào
# Luôn đo CPU tích luỹ của MsMpEng (không cần admin). Với -Record (cần admin): ghi recording Defender song song và in
# bảng "mỗi file bị quét mấy lần, bởi process nào, lý do gì" từ Get-MpPerformanceReport -Raw.
# KHÔNG sửa gì trong DlpService/. File phải lưu UTF-8 CÓ BOM (PowerShell 5.1 đọc file không BOM theo mã ANSI).
<#
.SYNOPSIS
    Đo chi phí quét của Windows Defender khi mở file docx/pdf.
.EXAMPLE
    # Chỉ đo CPU Defender (PowerShell thường):
    .\tools\perf\Measure-DefenderOverhead.ps1
.EXAMPLE
    # Đầy đủ, có recording (PowerShell Administrator), tạm dừng LlmSvc khi đo:
    .\tools\perf\Measure-DefenderOverhead.ps1 -Record -StopLlmSvc
.EXAMPLE
    # Chỉ phân tích lại 1 file .etl đã ghi (Tag in ở đầu lượt đo):
    .\tools\perf\Measure-DefenderOverhead.ps1 -AnalyzeEtl tools\perf\results\defender-20261027-150000.etl -Tag _defender-20261027-150000
#>
param(
    [ValidateSet('All', 'Read', 'Service')] [string]$Mode = 'All',
    [string]$SourceDir = "D:\gendata",
    [int]$ReadCount = 1000,            # Read: số file (nửa pdf, nửa docx)
    [int]$ServicePerType = 50,         # Service: số file MỖI loại cho MỖI kịch bản A/B → mặc định 200 file
    [string]$WatchFolder = "D:\Service-DLP\watch-folder",
    [string]$LogFolder = "D:\Service-DLP\publish\logs",
    [string]$ServiceName = "DlpService",
    [int]$TimeoutSec = 300,            # chờ service xử lý xong mỗi kịch bản
    [switch]$Record,                   # ghi recording Defender (cần admin)
    [int]$RecordSeconds = 300,         # recording chạy CỐ ĐỊNH bấy nhiêu giây (New-MpPerformanceRecording -Seconds)
    [switch]$KeepEtl,                  # giữ file .etl (thường vài trăm MB); mặc định xoá sau khi đã xuất scans.json
    [switch]$StopLlmSvc,               # tạm dừng LlmSvc khi đo để không lẫn tải (cần admin), bật lại khi xong
    [string]$AnalyzeEtl,               # chỉ phân tích file .etl có sẵn, không chạy thí nghiệm
    [string]$Tag                       # dùng cùng -AnalyzeEtl: tag của lượt đo cần lọc
)

$ErrorActionPreference = 'Stop'
$RunId = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $Tag) { $Tag = "_defender-$RunId" }
$resultFolder = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $resultFolder -Force | Out-Null
$parentFolder = Split-Path $WatchFolder -Parent
$readFolder = Join-Path $parentFolder "$Tag-read"         # Read: NGOÀI watch-folder (service không xử lý)
$stagingFolder = Join-Path $parentFolder "$Tag-staging"   # Service B: staging NGOÀI watch-folder, cùng ổ
$testFolder = Join-Path $WatchFolder $Tag                 # Service A/B: trong watch-folder
$report = New-Object System.Collections.Generic.List[string]

# ============================================================================================================
# HÀM PHỤ TRỢ (AI sinh)
# ============================================================================================================

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Add-Report([string]$text) { $report.Add($text); Write-Host $text }

function Add-Table($rows) {
    $text = ($rows | Format-Table -AutoSize | Out-String -Width 250).TrimEnd()
    $report.Add('```'); $report.Add($text); $report.Add('```')
    Write-Host $text
}

# CPU tích luỹ (ms) của MsMpEng — RawValue của "% Processor Time" là thời gian CPU cộng dồn theo đơn vị 100 ns.
# Chính xác hơn lấy mẫu mỗi giây (không bỏ sót đột biến ngắn), không cần admin.
$mpCounter = New-Object Diagnostics.PerformanceCounter('Process', '% Processor Time', 'MsMpEng')
function Get-DefenderCpuMs { return $mpCounter.RawValue / 10000.0 }

# Chạy $Work, trả về thời gian + CPU Defender TĂNG THÊM so với mức nền (đã trừ $script:baseMsPerSec × thời gian)
function Measure-Defender([scriptblock]$Work, [int]$SettleSec = 3) {
    $a = Get-DefenderCpuMs
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $info = & $Work
    $workSec = $sw.Elapsed.TotalSeconds
    Start-Sleep -Seconds $SettleSec   # chờ Defender xử lý nốt
    $sec = $sw.Elapsed.TotalSeconds
    $extra = (Get-DefenderCpuMs) - $a - $script:baseMsPerSec * $sec
    return @{ Info = $info; WorkSec = $workSec; DefenderMs = [math]::Max(0, $extra) }
}

# Lấy $Count file đuôi $Ext trong $SourceDir, bỏ qua $Skip file đầu (để các kịch bản không dùng chung file)
function Get-Sample([string]$Ext, [int]$Skip, [int]$Count) {
    if ($Count -le 0) { return @() }
    return @(Get-ChildItem $SourceDir -Recurse -File -Filter "*$Ext" -ErrorAction SilentlyContinue |
        Select-Object -Skip $Skip -First $Count)
}

# Copy kèm số thứ tự ở đầu tên: file MỚI hoàn toàn (Defender chưa có kết quả cache cho file này)
function Copy-Sample($Files, [string]$Dest) {
    New-Item -ItemType Directory -Force $Dest | Out-Null
    $i = 0
    foreach ($f in $Files) { Copy-Item -LiteralPath $f.FullName (Join-Path $Dest ("{0:0000}_{1}" -f $i++, $f.Name)) }
}

# Chờ đến khi log service có dòng [Perf] cho đủ $Expected file chứa $Marker trong đường dẫn; trả về thời gian "đọc"/"tổng"
function Wait-ServicePerf([string]$Marker, [int]$Expected) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Seconds 2
        $lines = @(Select-String -Path (Join-Path $LogFolder 'dlpservice-*.log') -SimpleMatch '[Perf] ' -Encoding UTF8 |
            Where-Object { $_.Line.Contains($Marker) } | ForEach-Object { $_.Line })
    } while ($lines.Count -lt $Expected -and $sw.Elapsed.TotalSeconds -lt $TimeoutSec)
    $read = @($lines | ForEach-Object { if ($_ -match 'đọc ([\d.]+) ms') { [double]$Matches[1] } })
    $total = @($lines | ForEach-Object { if ($_ -match 'tổng ([\d.]+) ms') { [double]$Matches[1] } })
    return @{ Done = $lines.Count; Read = $read; Total = $total }
}

function Get-Avg($values) {
    if (-not $values -or @($values).Count -eq 0) { return $null }
    return [math]::Round((@($values) | Measure-Object -Average).Average, 1)
}

function Get-Median($values) {
    if (-not $values -or @($values).Count -eq 0) { return $null }
    $sorted = @($values | Sort-Object)
    return [math]::Round($sorted[[int][math]::Floor($sorted.Count / 2)], 1)
}

# Gắn nhãn kịch bản theo đường dẫn file bị quét
function Get-ScenarioLabel([string]$Path) {
    if ($Path -like "*\$Tag-read\*") { return 'Read' }
    if ($Path -like "*\$Tag-staging\*") { return 'B-staging' }
    if ($Path -like "*\$Tag\A\*") { return 'A-watch' }
    if ($Path -like "*\$Tag\B\*") { return 'B-watch' }
    return $null
}

# Đọc .etl → xuất scans.json + bảng: mỗi kịch bản × process × lý do quét → số lượt, số file, thời gian quét
function Add-EtlAnalysis([string]$EtlPath) {
    $scans = @((Get-MpPerformanceReport -Path $EtlPath -TopScans 100000 -Raw).TopScans)
    $jsonPath = [IO.Path]::ChangeExtension($EtlPath, '.scans.json')
    $scans | ConvertTo-Json -Depth 3 | Out-File $jsonPath -Encoding utf8
    Add-Report ""
    Add-Report "## Recording Defender (Get-MpPerformanceReport)"
    Add-Report ""
    Add-Report "Tổng lượt quét trong recording: $($scans.Count). Dữ liệu thô: ``$jsonPath``"

    $test = @($scans | ForEach-Object {
        $label = Get-ScenarioLabel $_.Path
        if ($label) {
            [pscustomobject]@{ Scenario = $label; Process = $_.Image; Reason = $_.Reason; Skip = $_.SkipReason
                Ms = $_.Duration / 10000.0; File = $_.Path; Ext = [IO.Path]::GetExtension($_.Path).ToLower() }
        }
    })
    if ($test.Count -eq 0) {
        Add-Report "Không có lượt quét nào trên file test (tag $Tag) — kiểm tra lại recording có bao trùm lúc đo không."
        return
    }

    Add-Report ""
    Add-Report "### Lượt quét trên file test theo kịch bản / process / lý do"
    Add-Table ($test | Group-Object Scenario, Process, Reason, Skip | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            'Kịch bản, process, lý do, bỏ qua?' = $_.Name; 'Lượt' = $_.Count
            'Số file' = @($_.Group.File | Sort-Object -Unique).Count
            'TB (ms)' = Get-Avg $_.Group.Ms; 'Trung vị (ms)' = Get-Median $_.Group.Ms
            'Max (ms)' = [math]::Round(($_.Group.Ms | Measure-Object -Maximum).Maximum, 1)
        }
    })

    # File có bị quét THẬT (không bị bỏ qua) nhiều hơn 1 lần không
    $real = @($test | Where-Object { $_.Skip -eq 'Not skipped' })
    Add-Report ""
    Add-Report "### Số lượt quét THẬT (không bị bỏ qua) trên mỗi file"
    Add-Table ($real | Group-Object Scenario | ForEach-Object {
        $perFile = $_.Group | Group-Object File
        [pscustomobject]@{
            'Kịch bản' = $_.Name; 'Số file' = $perFile.Count
            '1 lượt' = @($perFile | Where-Object { $_.Count -eq 1 }).Count
            '≥ 2 lượt' = @($perFile | Where-Object { $_.Count -ge 2 }).Count
        }
    })

    Add-Report ""
    Add-Report "### Thời gian quét thật theo định dạng"
    Add-Table ($real | Group-Object Scenario, Ext | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{ 'Kịch bản, đuôi' = $_.Name; 'Lượt' = $_.Count; 'TB (ms)' = Get-Avg $_.Group.Ms
            'Trung vị (ms)' = Get-Median $_.Group.Ms }
    })

    # File KHÔNG phải file test mà service khiến Defender quét (DLL Tika/JVM, font...): chi phí 1 lần khi khởi động
    $other = @($scans | Where-Object { $_.Image -like "$ServiceName*" -and -not (Get-ScenarioLabel $_.Path) } |
        Sort-Object Duration -Descending | Select-Object -First 10)
    if ($other.Count -gt 0) {
        Add-Report ""
        Add-Report "### File khác do $ServiceName mở khiến Defender quét (top 10)"
        Add-Table ($other | ForEach-Object { [pscustomobject]@{ 'ms' = [math]::Round($_.Duration / 10000.0, 1); 'Đường dẫn' = $_.Path } })
    }
}

function Save-Report {
    $path = Join-Path $resultFolder "defender-$RunId.md"
    $report | Out-File $path -Encoding utf8
    Write-Host "`nĐã ghi báo cáo: $path"
}

# ============================================================================================================
# CHẾ ĐỘ CHỈ PHÂN TÍCH
# ============================================================================================================

if ($AnalyzeEtl) {
    Add-Report "# Chi phí Defender — phân tích $AnalyzeEtl (tag $Tag)"
    Add-EtlAnalysis (Resolve-Path $AnalyzeEtl).Path
    Save-Report
    return
}

# ============================================================================================================
# THÍ NGHIỆM
# ============================================================================================================

$isAdmin = Test-Admin
if (($Record -or $StopLlmSvc) -and -not $isAdmin) { throw "-Record / -StopLlmSvc cần chạy PowerShell bằng quyền Administrator." }
$runService = $Mode -in 'All', 'Service'
$runRead = $Mode -in 'All', 'Read'
if ($runService -and (Get-Service $ServiceName -ErrorAction SilentlyContinue).Status -ne 'Running') {
    Write-Warning "$ServiceName không chạy — bỏ qua kịch bản Service."
    $runService = $false
}

Add-Report "# Chi phí Windows Defender khi mở file docx/pdf — lượt $RunId"
Add-Report ""
Add-Report "- Tag file test: ``$Tag``; nguồn mẫu: ``$SourceDir``; số nhân CPU: $([Environment]::ProcessorCount)"
$rtp = Get-MpComputerStatus
Add-Report "- Defender: RealTimeProtection = $($rtp.RealTimeProtectionEnabled), OnAccessProtection = $($rtp.OnAccessProtectionEnabled)"

$llmStopped = $false
$recordJob = $null
$etlPath = Join-Path $resultFolder "defender-$RunId.etl"
try {
    if ($StopLlmSvc -and (Get-Service LlmSvc -ErrorAction SilentlyContinue).Status -eq 'Running') {
        Stop-Service LlmSvc
        $llmStopped = $true
        Add-Report "- Đã tạm dừng LlmSvc trong lúc đo"
    }

    # Chọn mẫu: Read lấy nhóm đầu, Service A nhóm tiếp, Service B nhóm sau — không trùng file giữa các kịch bản
    $readHalf = 0
    if ($runRead) { $readHalf = [int]($ReadCount / 2) }
    $samples = @{}
    foreach ($ext in '.pdf', '.docx') {
        $samples["read$ext"] = Get-Sample $ext 0 $readHalf
        if ($runService) {
            $samples["A$ext"] = Get-Sample $ext $readHalf $ServicePerType
            $samples["B$ext"] = Get-Sample $ext ($readHalf + $ServicePerType) $ServicePerType
        }
    }

    # Mức nền: CPU MsMpEng mỗi giây khi script chưa làm gì
    $a = Get-DefenderCpuMs; Start-Sleep -Seconds 10
    $script:baseMsPerSec = ((Get-DefenderCpuMs) - $a) / 10
    Add-Report "- Mức nền MsMpEng: $([math]::Round($script:baseMsPerSec, 1)) ms CPU/giây (đã trừ khỏi các số bên dưới)"

    if ($Record) {
        # Recording chạy trong job riêng (process con kế thừa quyền admin), dừng sau đúng $RecordSeconds giây
        $recordJob = Start-Job -ScriptBlock { param($e, $s) New-MpPerformanceRecording -RecordTo $e -Seconds $s } -ArgumentList $etlPath, $RecordSeconds
        Start-Sleep -Seconds 8   # chờ WPR khởi động xong trước khi bắt đầu đo
    }
    $workTimer = [Diagnostics.Stopwatch]::StartNew()
    $rows = New-Object System.Collections.Generic.List[object]

    if ($runRead) {
        $files = @($samples['read.pdf'] + $samples['read.docx'])
        Copy-Sample $files $readFolder
        Start-Sleep -Seconds 5
        $copied = @(Get-ChildItem $readFolder -File)
        foreach ($pass in 1, 2) {
            $m = Measure-Defender {
                foreach ($f in $copied) { [void][IO.File]::ReadAllBytes($f.FullName) }
                "$($copied.Count) file"
            }
            $rows.Add([pscustomobject]@{ 'Pha' = "Read: đọc lần $pass"; 'File' = $copied.Count
                'Thời gian (s)' = [math]::Round($m.WorkSec, 1); 'CPU Defender thêm (ms)' = [math]::Round($m.DefenderMs)
                'CPU Defender / file (ms)' = [math]::Round($m.DefenderMs / [math]::Max(1, $copied.Count), 1)
                'Service "đọc" TB / trung vị (ms)' = '' })
        }
    }

    if ($runService) {
        $setA = @($samples['A.pdf'] + $samples['A.docx'])
        $setB = @($samples['B.pdf'] + $samples['B.docx'])

        $mA = Measure-Defender {
            Copy-Sample $setA (Join-Path $testFolder 'A')
            $script:resA = Wait-ServicePerf "$Tag\A\" $setA.Count
        }
        $rows.Add([pscustomobject]@{ 'Pha' = 'Service A: copy thẳng vào watch-folder'; 'File' = "$($script:resA.Done)/$($setA.Count)"
            'Thời gian (s)' = [math]::Round($mA.WorkSec, 1); 'CPU Defender thêm (ms)' = [math]::Round($mA.DefenderMs)
            'CPU Defender / file (ms)' = [math]::Round($mA.DefenderMs / [math]::Max(1, $setA.Count), 1)
            'Service "đọc" TB / trung vị (ms)' = "$(Get-Avg $script:resA.Read) / $(Get-Median $script:resA.Read)" })

        $mB = Measure-Defender {
            Copy-Sample $setB $stagingFolder
            Start-Sleep -Seconds 15
            # Move TỪNG file vào thư mục đã tồn tại (giống -FastDrop của E2E) — move cả thư mục thì watcher có thể không thấy file con
            $dirB = Join-Path $testFolder 'B'
            New-Item -ItemType Directory -Force $dirB | Out-Null
            Get-ChildItem $stagingFolder -File | ForEach-Object { [IO.File]::Move($_.FullName, (Join-Path $dirB $_.Name)) }
            $script:resB = Wait-ServicePerf "$Tag\B\" $setB.Count
        }
        $rows.Add([pscustomobject]@{ 'Pha' = 'Service B: staging, chờ 15 s, move vào'; 'File' = "$($script:resB.Done)/$($setB.Count)"
            'Thời gian (s)' = [math]::Round($mB.WorkSec, 1); 'CPU Defender thêm (ms)' = [math]::Round($mB.DefenderMs)
            'CPU Defender / file (ms)' = [math]::Round($mB.DefenderMs / [math]::Max(1, $setB.Count), 1)
            'Service "đọc" TB / trung vị (ms)' = "$(Get-Avg $script:resB.Read) / $(Get-Median $script:resB.Read)" })
    }

    Add-Report ""
    Add-Report "## CPU Defender (performance counter, không cần admin)"
    Add-Table $rows
    Add-Report ""
    Add-Report "*Pha Service gồm cả thời gian copy và chờ service, nên CPU Defender có thể lẫn hoạt động khác trên máy. Bảng recording bên dưới (nếu có) đếm chính xác theo từng file.*"

    if ($recordJob) {
        $left = $RecordSeconds - 8 - $workTimer.Elapsed.TotalSeconds
        if ($left -lt 0) {
            Write-Warning "Thí nghiệm chạy lâu hơn -RecordSeconds $RecordSeconds — recording có thể thiếu phần cuối. Tăng -RecordSeconds."
        }
        else { Write-Host "Chờ recording kết thúc (còn ~$([math]::Ceiling($left)) s)..." }
        $recordJob | Wait-Job | Receive-Job
        Remove-Job $recordJob; $recordJob = $null
        Add-EtlAnalysis $etlPath
        if (-not $KeepEtl) { Remove-Item $etlPath -Force -ErrorAction SilentlyContinue }
    }
}
finally {
    if ($recordJob) { Stop-Job $recordJob -ErrorAction SilentlyContinue; Remove-Job $recordJob -Force -ErrorAction SilentlyContinue }
    foreach ($d in $readFolder, $stagingFolder, $testFolder) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if ($llmStopped) { Start-Service LlmSvc }
}

Save-Report
