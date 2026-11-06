# [Thay đổi] File mới: script đo hiệu năng THỰC TẾ của DlpService đang chạy (Cách B - đo từ bên ngoài, "hộp đen").
# Script đóng vai người dùng: thả file vào watch-folder, bấm giờ tới khi service ghi dòng kết quả ra log, đồng
# thời lấy mẫu CPU/RAM của tiến trình service. KHÔNG sửa gì trong DlpService/.
# Lưu ý: file này phải lưu dạng UTF-8 CÓ BOM — PowerShell 5.1 đọc file không BOM theo mã ANSI, sẽ hỏng chữ Việt.
<#
.SYNOPSIS
    Đo độ trễ, thông lượng và CPU/RAM của DlpService đang chạy.
.EXAMPLE
    .\tools\perf\Measure-DlpPerformance.ps1 -SequentialCount 60 -BurstCount 750
#>
param(
    [string]$SampleFolder = "D:\crawl data\output\filled_forms\mau-don",
    [string]$LogFolder = "D:\Service-DLP\publish\logs",
    [string]$WatchFolder = "D:\Service-DLP\watch-folder",
    [int]$SequentialCount = 60,      # số file đo độ trễ (kịch bản 1), chia đều theo đuôi file
    [int]$BurstCount = 750,          # số file thả cùng lúc (kịch bản 2)
    [int]$TimeoutSec = 30,           # chờ tối đa cho 1 file ở kịch bản tuần tự
    [int]$BurstTimeoutSec = 900,     # chờ tối đa cho cả đợt burst
    [int]$PollIntervalMs = 50,       # bao lâu đọc log 1 lần -> cũng là sai số của phép đo
    [string]$ProcessName = "DlpService"
)

$ErrorActionPreference = 'Stop'

# Mã lượt chạy: gắn vào tên file test để không bao giờ trùng tên cũ (service bỏ qua file có nội dung không đổi)
$script:RunId = Get-Date -Format 'yyyyMMdd-HHmmss'
$testFolder = Join-Path $WatchFolder '_perf-test'
$resultFolder = Join-Path $PSScriptRoot 'results'

# ============================================================================================================
# HÀM PHỤ TRỢ (AI sinh)
# ============================================================================================================

# Tìm file log service ĐANG ghi: dlpservice-<hôm nay>.log hoặc phần lớn nhất .1.log, .2.log... (khớp log
# rotation trong FileLoggerProvider.ResolveLogFilePath). Trả $null nếu hôm nay chưa có file nào.
function Get-CurrentLogFile {
    $prefix = 'dlpservice-' + (Get-Date -Format 'yyyy-MM-dd')
    $files = @(Get-ChildItem -LiteralPath $LogFolder -Filter "$prefix*.log" -File -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { return $null }

    $latest = $files | Sort-Object {
        # "dlpservice-2026-09-28" -> phần 0; "dlpservice-2026-09-28.3" -> phần 3
        $middle = [IO.Path]::GetFileNameWithoutExtension($_.Name).Substring($prefix.Length)
        if ($middle) { [int]$middle.TrimStart('.') } else { 0 }
    } | Select-Object -Last 1
    return $latest.FullName
}

# Đọc phần MỚI ghi thêm vào 1 file log kể từ vị trí $Offset (byte). Mở file ở chế độ chia sẻ để không chặn
# service đang ghi. Chỉ lấy tới ký tự xuống dòng cuối cùng — dòng đang ghi dở để dành cho lần đọc sau.
function Read-LogLinesFrom([string]$Path, [long]$Offset) {
    $empty = @{ Lines = @(); Offset = $Offset }
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    try {
        $length = $stream.Length
        if ($length -le $Offset) { return $empty }
        [void]$stream.Seek($Offset, [IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] ($length - $Offset)
        $total = 0
        while ($total -lt $buffer.Length) {
            $n = $stream.Read($buffer, $total, $buffer.Length - $total)
            if ($n -le 0) { break }
            $total += $n
        }
    }
    finally {
        $stream.Dispose()
    }

    if ($total -eq 0) { return $empty }
    $lastNewLine = [Array]::LastIndexOf($buffer, [byte]10, $total - 1)
    if ($lastNewLine -lt 0) { return $empty }

    $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $lastNewLine + 1)
    return @{
        Lines  = @($text -split "`r?`n" | Where-Object { $_ -ne '' })
        Offset = $Offset + $lastNewLine + 1
    }
}

# Tạo "đầu đọc log" bắt đầu từ CUỐI file log hiện tại -> chỉ thấy các dòng ghi SAU thời điểm tạo.
function New-LogReader {
    $path = Get-CurrentLogFile
    $offset = 0L
    if ($path) { $offset = (Get-Item -LiteralPath $path).Length }
    return @{ Path = $path; Offset = $offset; OverflowCount = 0 }
}

# Lấy mọi dòng log MỚI kể từ lần gọi trước. Tự xử lý khi service chuyển sang file log mới (quá 5MB -> .1.log,
# hoặc qua ngày mới): đọc nốt phần cuối file cũ rồi mới chuyển sang file mới. Đồng thời đếm dòng "Buffer tràn".
function Read-NewLogLines([hashtable]$Reader) {
    $lines = New-Object System.Collections.Generic.List[string]
    $newest = Get-CurrentLogFile

    if ($Reader.Path -and $newest -and $newest -ne $Reader.Path) {
        $r = Read-LogLinesFrom $Reader.Path $Reader.Offset
        $lines.AddRange([string[]]$r.Lines)
        $Reader.Path = $newest
        $Reader.Offset = 0L
    }
    elseif (-not $Reader.Path) {
        # Lúc tạo reader chưa có file log hôm nay -> file vừa được tạo, đọc từ đầu
        $Reader.Path = $newest
        $Reader.Offset = 0L
    }

    if ($Reader.Path) {
        $r = Read-LogLinesFrom $Reader.Path $Reader.Offset
        $lines.AddRange([string[]]$r.Lines)
        $Reader.Offset = $r.Offset
    }

    foreach ($line in $lines) {
        if ($line.Contains('[FileWatcher] Buffer tr')) { $Reader.OverflowCount++ }
    }
    return , $lines.ToArray()
}

# Nếu 1 dòng log là dòng KẾT QUẢ cuối cùng của 1 file test do script tạo, trả về @{ FileName; Kind }; ngược lại $null.
#   "[DLP] ...\seq-...-0001.pdf: sạch"                     -> Kind = 'sach'
#   "[DLP] ...\seq-...-0001.pdf: phát hiện 3 chỗ nhạy cảm" -> Kind = 'nhay-cam'
#   "[FileWatcher] Đọc thất bại ...\seq-...-0001.pdf: ..." -> Kind = 'loi'
# Các dòng phụ (chi tiết từng match, "mức ...", cảnh báo cắt nội dung) -> $null.
function Get-TestResult([string]$Line) {
    $m = [regex]::Match($Line, '(?:seq|burst)-\d{8}-\d{6}-\d{4}\.\w+')
    if (-not $m.Success) { return $null }

    $fileName = $m.Value
    # [Thay đổi] Log timeout: service log "CHƯA KẾT LUẬN ĐƯỢC" thay cho "sạch"/"phát hiện" khi có rule bị timeout
    if ($Line.Contains('Đọc thất bại')) { $kind = 'loi' }
    elseif ($Line.Contains("$($fileName): CHƯA KẾT LUẬN ĐƯỢC")) { $kind = 'chua-ket-luan' }
    elseif ($Line.Contains("$($fileName): sạch")) { $kind = 'sach' }
    elseif ($Line.Contains("$($fileName): phát hiện")) { $kind = 'nhay-cam' }
    else { return $null }

    return @{ FileName = $fileName; Kind = $kind }
}

# Tên file test duy nhất, ví dụ "seq-20260928-101500-0007.pdf"
function New-TestFileName([string]$Prefix, [int]$Index, [string]$Extension) {
    return '{0}-{1}-{2:D4}{3}' -f $Prefix, $script:RunId, $Index, $Extension
}

# 1 dòng kết quả đo cho 1 file. $LatencyMs = $null khi timeout. Chỉ lưu tên + số đo, KHÔNG lưu nội dung file.
function New-ResultRow([string]$Scenario, [string]$TestName, [IO.FileInfo]$Source, $LatencyMs, [string]$Kind) {
    $latency = $null
    if ($null -ne $LatencyMs) { $latency = [math]::Round([double]$LatencyMs, 1) }
    return [pscustomobject]@{
        Scenario  = $Scenario
        FileName  = $TestName
        Extension = $Source.Extension.ToLowerInvariant()
        SizeKB    = [math]::Round($Source.Length / 1KB, 1)
        LatencyMs = $latency
        Kind      = $Kind
    }
}

# Bắt đầu lấy mẫu CPU/RAM của service trong 1 background job (tiến trình PowerShell phụ chạy song song).
# Dùng performance counter vì Get-Process không đọc được CPU của service chạy bằng tài khoản hệ thống khi
# không có quyền admin. Mỗi lần Get-Counter mất ~1 giây -> ~1 mẫu/giây.
function Start-ResourceSampler {
    return Start-Job -ArgumentList $ProcessName -ScriptBlock {
        param($name)
        $cores = [Environment]::ProcessorCount
        $counters = "\Process($name)\% Processor Time", "\Process($name)\Working Set - Private"
        while ($true) {
            try {
                $s = (Get-Counter -Counter $counters -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop).CounterSamples
                [pscustomobject]@{
                    # "% Processor Time" cộng dồn mọi nhân (có thể > 100) -> chia số nhân để ra % của CẢ máy
                    CpuPct = [math]::Round($s[0].CookedValue / $cores, 1)
                    RamMB  = [math]::Round($s[1].CookedValue / 1MB, 1)
                }
            }
            catch {
                Start-Sleep -Milliseconds 500
            }
        }
    }
}

# Dừng job lấy mẫu và trả về toàn bộ mẫu đã thu được.
function Stop-ResourceSampler($Job) {
    Stop-Job -Job $Job
    $samples = @(Receive-Job -Job $Job)
    Remove-Job -Job $Job -Force
    return , $samples
}

# Phân vị kiểu "nearest-rank": p95 = giá trị mà 95% số mẫu nhỏ hơn hoặc bằng.
function Get-Percentile([double[]]$Values, [double]$P) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $rank = [math]::Ceiling($P / 100 * $sorted.Count) - 1
    return $sorted[[math]::Max(0, $rank)]
}

# Tóm tắt độ trễ của 1 nhóm dòng kết quả (bỏ qua dòng timeout khi tính số liệu, nhưng đếm riêng).
function Get-LatencySummary([string]$Label, $Rows) {
    # Tên biến khác $Rows: biến PowerShell không phân biệt hoa thường, "$rows" chính là "$Rows".
    # Dùng foreach thay vì @($Rows): PowerShell 5.1 lỗi "Argument types do not match" khi @() bọc List[object].
    $all = @(foreach ($row in $Rows) { $row })
    $ok = @($all | Where-Object { $null -ne $_.LatencyMs })
    $values = [double[]]@($ok | ForEach-Object { $_.LatencyMs })
    $avg = $null
    if ($values.Count -gt 0) { $avg = [math]::Round(($values | Measure-Object -Average).Average, 0) }
    return [pscustomobject]@{
        Nhom     = $Label
        SoFile   = $all.Count
        Timeout  = $all.Count - $ok.Count
        TB_ms    = $avg
        P50_ms   = Get-Percentile $values 50
        P95_ms   = Get-Percentile $values 95
        Max_ms   = Get-Percentile $values 100
    }
}

# Tóm tắt CPU/RAM của 1 giai đoạn.
function Get-ResourceSummary([string]$Label, $Samples) {
    $s = @($Samples)
    $row = [pscustomobject]@{ GiaiDoan = $Label; SoMau = $s.Count; CPU_TB = $null; CPU_Max = $null; RAM_TB_MB = $null; RAM_Max_MB = $null }
    if ($s.Count -gt 0) {
        $cpu = $s | Measure-Object -Property CpuPct -Average -Maximum
        $ram = $s | Measure-Object -Property RamMB -Average -Maximum
        $row.CPU_TB = [math]::Round($cpu.Average, 1)
        $row.CPU_Max = $cpu.Maximum
        $row.RAM_TB_MB = [math]::Round($ram.Average, 1)
        $row.RAM_Max_MB = $ram.Maximum
    }
    return $row
}

# ============================================================================================================
# BƯỚC 0: KIỂM TRA TRƯỚC KHI ĐO (AI sinh)
# ============================================================================================================
if (-not (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
    throw "Không thấy tiến trình '$ProcessName' — hãy khởi động service trước (sc start DlpService)."
}
if (-not (Test-Path -LiteralPath $LogFolder)) {
    throw "Không thấy thư mục log '$LogFolder'."
}

$sampleFiles = @(Get-ChildItem -LiteralPath $SampleFolder -File)
if ($sampleFiles.Count -eq 0) { throw "Thư mục mẫu '$SampleFolder' không có file nào." }

# Kịch bản 1: chia đều theo đuôi file (VD 30 pdf + 30 docx), chọn ngẫu nhiên, rồi xáo trộn thứ tự
$groups = @($sampleFiles | Group-Object Extension)
$perGroup = [math]::Ceiling($SequentialCount / $groups.Count)
$sequentialFiles = @(
    $groups |
        ForEach-Object { $_.Group | Get-Random -Count ([math]::Min($perGroup, $_.Count)) } |
        Select-Object -First $SequentialCount |
        Get-Random -Count $SequentialCount
)

# Kịch bản 2: lấy xoay vòng từ danh sách mẫu (BurstCount > số file mẫu thì quay lại từ đầu)
$burstFiles = @(for ($i = 0; $i -lt $BurstCount; $i++) { $sampleFiles[$i % $sampleFiles.Count] })

if (Test-Path -LiteralPath $testFolder) { Remove-Item -LiteralPath $testFolder -Recurse -Force }
New-Item -ItemType Directory -Path $testFolder | Out-Null
New-Item -ItemType Directory -Path $resultFolder -Force | Out-Null

Write-Host "Lượt đo $($script:RunId): $($sampleFiles.Count) file mẫu, tuần tự $($sequentialFiles.Count) file, burst $($burstFiles.Count) file"

$sequentialResults = New-Object System.Collections.Generic.List[object]
$burstResults = New-Object System.Collections.Generic.List[object]
$burstElapsedMs = $null
$burstOverflow = 0
$baselineSamples = @()
$burstSamples = @()
$sampler = $null

try {
    # ========================================================================================================
    # BƯỚC 1: ĐO MỨC NỀN khi service đang rảnh (AI sinh)
    # ========================================================================================================
    Write-Host "Bước 1: đo mức nền CPU/RAM (~6 giây)..."
    $sampler = Start-ResourceSampler
    Start-Sleep -Seconds 7   # ~1-2 giây đầu là thời gian job khởi động
    $baselineSamples = Stop-ResourceSampler $sampler
    $sampler = $null

    # ========================================================================================================
    # BƯỚC 2: KỊCH BẢN TUẦN TỰ — đo độ trễ từng file
    # ========================================================================================================
    Write-Host "Bước 2: kịch bản tuần tự..."
    $reader = New-LogReader
    $index = 0
    foreach ($file in $sequentialFiles) {
        $index++
        $newName = New-TestFileName 'seq' $index $file.Extension
        $destination = Join-Path $testFolder $newName

        # [Thay đổi] Bấm giờ ngay TRƯỚC khi copy -> độ trễ = thời gian copy + debounce 300ms + đọc/quét/phân loại + ghi log
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Copy-Item -LiteralPath $file.FullName -Destination $destination

        # [Thay đổi] Chờ tới khi log có dòng kết quả của ĐÚNG file này, hoặc hết giờ. Mặc định coi là timeout,
        # thấy kết quả thì ghi đè lại.
        $latencyMs = $null
        $kind = 'timeout'
        while ($sw.Elapsed.TotalSeconds -le $TimeoutSec) {
            foreach ($line in (Read-NewLogLines $reader)) {
                $r = Get-TestResult $line
                if ($r -and $r.FileName -eq $newName) {
                    $latencyMs = $sw.Elapsed.TotalMilliseconds
                    $kind = $r.Kind
                    break   # thoát foreach
                }
            }
            if ($null -ne $latencyMs) { break }   # thoát while
            Start-Sleep -Milliseconds $PollIntervalMs
        }

        $sequentialResults.Add((New-ResultRow 'tuan-tu' $newName $file $latencyMs $kind))

        # [Thay đổi] In tiến độ. Viết "$($newName)" chứ không "$newName:" — PowerShell hiểu "$newName:" là biến có phạm vi
        $shown = if ($null -ne $latencyMs) { '{0:N0} ms' -f $latencyMs } else { 'TIMEOUT' }
        Write-Host ("  [{0}/{1}] {2} ({3:N0} KB): {4} - {5}" -f $index, $sequentialFiles.Count, $newName, ($file.Length / 1KB), $shown, $kind)
    }

    # ========================================================================================================
    # BƯỚC 3: KỊCH BẢN DỒN DẬP — đo thông lượng + CPU/RAM khi tải cao
    # ========================================================================================================
    Write-Host "Bước 3: kịch bản burst..."
    $sampler = Start-ResourceSampler
    Start-Sleep -Seconds 2   # chờ job lấy mẫu khởi động xong mới bắt đầu thả file
    $reader = New-LogReader
    $pending = @{}           # tên file test -> FileInfo gốc, những file CHƯA thấy kết quả trong log

    # [Thay đổi] Thả toàn bộ file liên tiếp nhanh nhất có thể; đồng hồ chạy từ trước file đầu tiên
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $index = 0
    foreach ($f in $burstFiles) {
        $index++
        $newName = New-TestFileName 'burst' $index $f.Extension
        $pending[$newName] = $f
        Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $testFolder $newName)
    }
    $copyMs = $sw.Elapsed.TotalMilliseconds
    Write-Host ("  Đã thả {0} file trong {1:N0} ms, đang chờ service xử lý..." -f $burstFiles.Count, $copyMs)

    # [Thay đổi] Gạch dần từng file khỏi $pending khi thấy dòng kết quả của nó. Chỉ tính lần xuất hiện ĐẦU TIÊN
    # (buffer tràn -> service quét lại cả thư mục -> cùng 1 file có thể có 2 dòng kết quả).
    $lastProgress = 0
    while ($pending.Count -gt 0 -and $sw.Elapsed.TotalSeconds -le $BurstTimeoutSec) {
        foreach ($line in (Read-NewLogLines $reader)) {
            $r = Get-TestResult $line
            if ($r -and $pending.ContainsKey($r.FileName)) {
                $burstResults.Add((New-ResultRow 'burst' $r.FileName $pending[$r.FileName] $sw.Elapsed.TotalMilliseconds $r.Kind))
                $pending.Remove($r.FileName)
            }
        }

        # In tiến độ mỗi ~5 giây để biết script không bị treo
        if ($sw.Elapsed.TotalSeconds - $lastProgress -ge 5) {
            $lastProgress = $sw.Elapsed.TotalSeconds
            Write-Host ("  {0:N0}s: xong {1}/{2}" -f $lastProgress, $burstResults.Count, $burstFiles.Count)
        }
        Start-Sleep -Milliseconds $PollIntervalMs
    }
    $burstElapsedMs = $sw.Elapsed.TotalMilliseconds

    # [Thay đổi] File chưa thấy kết quả sau khi hết giờ -> ghi timeout
    foreach ($name in @($pending.Keys)) {
        $burstResults.Add((New-ResultRow 'burst' $name $pending[$name] $null 'timeout'))
    }

    $burstOverflow = $reader.OverflowCount
    $burstSamples = Stop-ResourceSampler $sampler
    $sampler = $null
}
finally {
    # Dọn dẹp dù script chạy xong hay bị lỗi / Ctrl+C giữa chừng
    if ($sampler) { Stop-Job -Job $sampler; Remove-Job -Job $sampler -Force }
    if (Test-Path -LiteralPath $testFolder) { Remove-Item -LiteralPath $testFolder -Recurse -Force -ErrorAction SilentlyContinue }
}

# ============================================================================================================
# BƯỚC 4: BÁO CÁO (AI sinh)
# ============================================================================================================
Write-Host ""
Write-Host "===== ĐỘ TRỄ (từ lúc copy file tới lúc service ghi kết quả; đã gồm 300ms debounce) =====" -ForegroundColor Cyan
$latencyTable = @(Get-LatencySummary 'Tuần tự - tất cả' $sequentialResults)
foreach ($g in ($sequentialResults | Group-Object Extension)) {
    $latencyTable += Get-LatencySummary "Tuần tự - $($g.Name)" $g.Group
}
$latencyTable += Get-LatencySummary 'Burst - lúc hoàn thành' $burstResults
$latencyTable | Format-Table -AutoSize | Out-Host

Write-Host "===== THÔNG LƯỢNG (burst) =====" -ForegroundColor Cyan
$burstDone = @($burstResults | Where-Object { $null -ne $_.LatencyMs })
$burstMB = ($burstFiles | Measure-Object -Property Length -Sum).Sum / 1MB
if ($burstElapsedMs -and $burstElapsedMs -gt 0) {
    $seconds = $burstElapsedMs / 1000
    Write-Host ("Hoàn thành     : {0}/{1} file trong {2:N1} giây" -f $burstDone.Count, $burstFiles.Count, $seconds)
    Write-Host ("Thông lượng    : {0:N1} file/giây, {1:N2} MB/giây" -f ($burstDone.Count / $seconds), ($burstMB / $seconds))
}
else {
    Write-Host "Chưa có số liệu burst (Bước 3 chưa được code?)"
}
Write-Host ("Buffer tràn    : {0} lần" -f $burstOverflow)

Write-Host ""
Write-Host "===== CPU (% toàn máy) / RAM (Working Set - Private) =====" -ForegroundColor Cyan
@(
    (Get-ResourceSummary 'Nền (rảnh)' $baselineSamples),
    (Get-ResourceSummary 'Burst' $burstSamples)
) | Format-Table -AutoSize | Out-Host

$csvPath = Join-Path $resultFolder "perf-$($script:RunId).csv"
($sequentialResults.ToArray() + $burstResults.ToArray()) | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
Write-Host "Chi tiết từng file: $csvPath"
