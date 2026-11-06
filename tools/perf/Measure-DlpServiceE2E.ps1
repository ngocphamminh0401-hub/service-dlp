# [Thay đổi] File mới: đo DlpService THẬT (bản publish đang chạy dưới SCM) từ đầu đến cuối trên tập có nhãn rawdata —
# độ chính xác (đọc mức từ log), thời gian (độ trễ ngoài + dòng [Perf] trong service), thông lượng, tài nguyên theo thời
# gian và độ ổn định (lỗi/cảnh báo, tràn buffer, crash/restart, file mất/xử lý trùng, khởi động lại có quét lại không).
# Mở rộng từ Measure-DlpPerformance.ps1 (buổi 6). KHÔNG sửa gì trong DlpService/. Lớp regex: nên dừng LlmSvc trước khi đo.
# Lưu ý: file phải lưu UTF-8 CÓ BOM (PowerShell 5.1 đọc file không BOM theo mã ANSI, hỏng chữ Việt).
<#
.SYNOPSIS
    Đo end-to-end DlpService đang chạy trên toàn bộ rawdata có nhãn.
.EXAMPLE
    # PowerShell quyền Administrator (cần cho -RestartTest và đọc Event Log):
    .\tools\perf\Measure-DlpServiceE2E.ps1 -ManifestCsv tools\eval\results\baseline-20261021-110840.csv -RestartTest
#>
param(
    [Parameter(Mandatory)] [string]$ManifestCsv,         # CSV của DlpBaseline: danh sách file + nhãn (id, format, domain, label, truth, is_borderline)
    [string]$RawdataFolder = "D:\dlp-classifier\rawdata",
    [string]$WatchFolder = "D:\Service-DLP\watch-folder",
    [string]$LogFolder = "D:\Service-DLP\publish\logs",
    [string]$ServiceName = "DlpService",
    [string]$ProcessName = "DlpService",
    [int]$WarmupFiles = 10,           # [Thay đổi] làm nóng: số file thả trước, KHÔNG tính (service vừa khởi động: JIT, biên dịch regex)
    [int]$SequentialPerClass = 2,     # kịch bản tuần tự: số file mỗi lớp (16 lớp) cho MỖI định dạng → mặc định 64 file
    [int]$SequentialTimeoutSec = 60,
    [int]$BurstTimeoutSec = 7200,     # chờ tối đa cho cả đợt 25.486 file
    [int]$IdleAfterSec = 120,         # sau khi xong: theo dõi thêm khi service rảnh (RAM có trả lại không)
    [switch]$RestartTest,             # khởi động lại service sau đợt burst, đo thời gian khởi động + kiểm tra không quét lại (cần admin)
    [switch]$KeepFiles,               # giữ lại file test trong watch-folder (mặc định xoá khi xong)
    [switch]$FastDrop,                # [Thay đổi] đo thông lượng tối đa: copy trước ra thư mục tạm, thả bằng move (vài giây cho 25.000 file)
    [int]$PollIntervalMs = 100,
    [int[]]$RateSteps = @(),         # [Thay đổi] hạng mục 4: các mức tải đều (file/giây), VD -RateSteps 5,20,50; rỗng = bỏ qua
    [int]$StepSeconds = 120,          # [Thay đổi] thời lượng mỗi mức tải đều
    [switch]$NoBurst,                 # [Thay đổi] bỏ kịch bản burst (chỉ đo tải đều / rảnh dài)
    [int[]]$BatchSizes = @(),         # [Thay đổi] xử lý đồng thời: thả CÙNG LÚC từng lô N file (move nhanh), VD -BatchSizes 1,10,100,1000,5000
    [switch]$EditStorm,               # [Thay đổi] xử lý đồng thời: ghi đè 1 file 20 lần liên tiếp — kết quả cuối phải theo nội dung cuối
    [switch]$LockedFile,              # [Thay đổi] xử lý đồng thời: thả file rồi khoá độc quyền 5 giây — service có xử lý lại không
    [switch]$PostIdleProbe            # [Thay đổi] sau giai đoạn rảnh: thả 20 file — kiểm chứng sửa lỗi watcher bị GC thu hồi
)

$ErrorActionPreference = 'Stop'
$RunId = Get-Date -Format 'yyyyMMdd-HHmmss'
$RunStart = Get-Date
$testRoot = Join-Path $WatchFolder "_e2e-$RunId"
$seqFolder = Join-Path $testRoot 'seq'
$warmupFolder = Join-Path $testRoot 'warmup' # [Thay đổi] làm nóng
# [Thay đổi] -FastDrop: thư mục tạm cùng ổ với watch-folder nhưng NẰM NGOÀI (service không theo dõi)
$stagingFolder = Join-Path (Split-Path $WatchFolder -Parent) "_e2e-staging-$RunId"
$burstFolder = Join-Path $testRoot 'burst'
$resultFolder = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $resultFolder -Force | Out-Null

# ============================================================================================================
# HÀM PHỤ TRỢ (AI sinh) — đọc log tăng dần, giống Measure-DlpPerformance.ps1
# ============================================================================================================

# File log service ĐANG ghi: dlpservice-<hôm nay>.log hoặc phần lớn nhất .1.log, .2.log... (log rotation 5 MB)
function Get-CurrentLogFile {
    $prefix = 'dlpservice-' + (Get-Date -Format 'yyyy-MM-dd')
    $files = @(Get-ChildItem -LiteralPath $LogFolder -Filter "$prefix*.log" -File -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { return $null }
    $latest = $files | Sort-Object {
        $middle = [IO.Path]::GetFileNameWithoutExtension($_.Name).Substring($prefix.Length)
        if ($middle) { [int]$middle.TrimStart('.') } else { 0 }
    } | Select-Object -Last 1
    return $latest.FullName
}

# Đọc phần MỚI của 1 file log từ vị trí $Offset (byte), chế độ chia sẻ để không chặn service; bỏ dòng đang ghi dở
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
    finally { $stream.Dispose() }
    if ($total -eq 0) { return $empty }
    $lastNewLine = [Array]::LastIndexOf($buffer, [byte]10, $total - 1)
    if ($lastNewLine -lt 0) { return $empty }
    $text = [Text.Encoding]::UTF8.GetString($buffer, 0, $lastNewLine + 1)
    return @{ Lines = @($text -split "`r?`n" | Where-Object { $_ -ne '' }); Offset = $Offset + $lastNewLine + 1 }
}

function New-LogReader {
    $path = Get-CurrentLogFile
    $offset = 0L
    if ($path) { $offset = (Get-Item -LiteralPath $path).Length }
    return @{ Path = $path; Offset = $offset }
}

# Mọi dòng log MỚI kể từ lần gọi trước; tự chuyển sang file mới khi service xoay vòng log
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
        $Reader.Path = $newest
        $Reader.Offset = 0L
    }
    if ($Reader.Path) {
        $r = Read-LogLinesFrom $Reader.Path $Reader.Offset
        $lines.AddRange([string[]]$r.Lines)
        $Reader.Offset = $r.Offset
    }
    return , $lines.ToArray()
}

# ============================================================================================================
# PHÂN TÍCH DÒNG LOG (AI sinh) — trả về sự kiện của 1 file test, hoặc $null
# ============================================================================================================
$reTestFile = [regex]'_e2e-\d{8}-\d{6}\\(?<phase>seq|burst|warmup|rate\d+|batch\d+|postidle)\\(?<name>[^\\:]+\.(?:docx|pdf))'
$rePerf = [regex]'cache (?<cache>[\d.,]+) ms, đọc (?<read>[\d.,]+) ms, băm (?<hash>[\d.,]+) ms, quét (?<scan>[\d.,]+) ms, phân loại (?<cls>[\d.,]+) ms, ghi DB (?<save>[\d.,]+) ms, tổng (?<total>[\d.,]+) ms'
$reLevel = [regex]': mức (?<level>Public|Internal|Confidential|Restricted)\s*$'
$reConf = [regex]'nhánh (?<branch>\w+), conf=(?<conf>[\d.,]+) \(domain đoán: (?<domain>\w+)\)'

function Get-FileEvent([string]$Line) {
    $m = $reTestFile.Match($Line)
    if (-not $m.Success) { return $null }
    $e = @{ Phase = $m.Groups['phase'].Value; Name = $m.Groups['name'].Value }
    if ($Line.Contains('[Perf]')) {
        $p = $rePerf.Match($Line)
        if ($p.Success) {
            $e.Kind = 'perf'
            foreach ($k in 'cache', 'read', 'hash', 'scan', 'cls', 'save', 'total') {
                $e[$k] = [double]::Parse($p.Groups[$k].Value.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)
            }
            return $e
        }
    }
    if ($Line.Contains('[Conf]')) {
        $c = $reConf.Match($Line)
        if ($c.Success) { $e.Kind = 'conf'; $e.Branch = $c.Groups['branch'].Value; $e.Conf = $c.Groups['conf'].Value.Replace(',', '.'); $e.Domain = $c.Groups['domain'].Value; return $e }
    }
    if ($Line.Contains('Đọc thất bại')) { $e.Kind = 'level'; $e.Level = 'ReadError'; $e.Done = $true; return $e }
    if ($Line.Contains('CHƯA KẾT LUẬN')) { $e.Kind = 'level'; $e.Level = 'Unconcluded'; return $e }
    if ($Line -match '\[DLP\] .+: sạch\s*$') { $e.Kind = 'level'; $e.Level = 'Public'; return $e }
    $l = $reLevel.Match($Line)
    if ($l.Success -and $Line.Contains('[DLP]')) { $e.Kind = 'level'; $e.Level = $l.Groups['level'].Value; return $e }
    return $null
}

# Cập nhật bảng kết quả theo 1 sự kiện. Đếm số dòng [Perf] của mỗi file để phát hiện xử lý TRÙNG.
function Update-Result([hashtable]$Table, $Event, [double]$ElapsedMs) {
    $key = $Event.Phase + '|' + $Event.Name
    if (-not $Table.ContainsKey($key)) { return }
    $row = $Table[$key]
    switch ($Event.Kind) {
        'level' {
            if (-not $row.Level) { $row.Level = $Event.Level }
            if ($Event.Done -and -not $row.DoneMs) { $row.DoneMs = $ElapsedMs }
        }
        'conf' { if (-not $row.ConfBranch) { $row.ConfBranch = $Event.Branch; $row.Conf = $Event.Conf; $row.ConfDomain = $Event.Domain } }
        'perf' {
            $row.PerfCount++
            if (-not $row.DoneMs) {
                $row.DoneMs = $ElapsedMs
                foreach ($k in 'cache', 'read', 'hash', 'scan', 'cls', 'save', 'total') { $row["perf_$k"] = $Event[$k] }
            }
        }
    }
}

# ============================================================================================================
# LẤY MẪU TÀI NGUYÊN (AI sinh) — CPU, RAM, handle, thread mỗi ~1 giây, có mốc thời gian để vẽ theo thời gian
# ============================================================================================================
function Start-ResourceSampler {
    # [Thay đổi] hạng mục 4: thêm I/O đọc/ghi của service, CPU của Windows Defender (MsMpEng) và PowerShell (chính script đo +
    # job lấy mẫu), % thời gian bận của đĩa, RAM còn trống của máy. Counter nào không đọc được (VD thiếu quyền) → 0, không dừng đo.
    return Start-Job -ArgumentList $ProcessName -ScriptBlock {
        param($name)
        $cores = [Environment]::ProcessorCount
        $counters = "\Process($name)\% Processor Time", "\Process($name)\Working Set - Private", "\Process($name)\Handle Count",
                    "\Process($name)\Thread Count", "\Process($name)\ID Process", "\Processor(_Total)\% Processor Time",
                    "\Process($name)\IO Read Bytes/sec", "\Process($name)\IO Write Bytes/sec",
                    "\Process(MsMpEng*)\% Processor Time", "\Process(powershell*)\% Processor Time",
                    "\PhysicalDisk(_Total)\% Disk Time", "\Memory\Available MBytes"
        $svc = $name.ToLowerInvariant()
        while ($true) {
            try {
                $s = @((Get-Counter -Counter $counters -SampleInterval 1 -MaxSamples 1 -ErrorAction SilentlyContinue).CounterSamples)
                if ($s.Count -eq 0) { Start-Sleep -Milliseconds 500; continue }
                $v = { param($like) $x = ($s | Where-Object { $_.Path -like $like } | Measure-Object -Property CookedValue -Sum).Sum; if ($x) { $x } else { 0 } }
                [pscustomobject]@{
                    Time        = (Get-Date).ToString('o')
                    CpuPct      = [math]::Round((& $v "*\process($svc)\% processor time") / $cores, 2)
                    RamMB       = [math]::Round((& $v "*\process($svc)\working set - private") / 1MB, 1)
                    Handles     = [int](& $v "*\process($svc)\handle count")
                    Threads     = [int](& $v "*\process($svc)\thread count")
                    Pid         = [int](& $v "*\process($svc)\id process")
                    MachineCpu  = [math]::Round((& $v "*\processor(_total)\% processor time"), 1)
                    IoReadMBs   = [math]::Round((& $v "*\process($svc)\io read bytes/sec") / 1MB, 2)
                    IoWriteMBs  = [math]::Round((& $v "*\process($svc)\io write bytes/sec") / 1MB, 2)
                    DefenderCpu = [math]::Round((& $v "*\process(msmpeng*)\% processor time") / $cores, 2)
                    ScriptCpu   = [math]::Round((& $v "*\process(powershell*)\% processor time") / $cores, 2)
                    DiskPct     = [math]::Round((& $v "*\physicaldisk(_total)\% disk time"), 1)
                    AvailMB     = [int](& $v "*\memory\available mbytes")
                }
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
    }
}

function Stop-ResourceSampler($Job, [string]$Phase) {
    Stop-Job -Job $Job
    $samples = @(Receive-Job -Job $Job)
    Remove-Job -Job $Job -Force
    foreach ($s in $samples) { $s | Add-Member -NotePropertyName Phase -NotePropertyValue $Phase }
    return , $samples
}

# ============================================================================================================
# BƯỚC 0: KIỂM TRA TRƯỚC KHI ĐO
# ============================================================================================================
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc -or $svc.Status -ne 'Running') { throw "Service '$ServiceName' không chạy." }
$llm = Get-Service -Name 'LlmSvc' -ErrorAction SilentlyContinue
if ($llm -and $llm.Status -eq 'Running') { Write-Warning "LlmSvc ĐANG CHẠY — lớp LLM có thể chạy xen khi máy rảnh. Đo lớp regex nên dừng: sc.exe stop LlmSvc" }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($RestartTest -and -not $isAdmin) { throw "-RestartTest cần chạy PowerShell bằng quyền Administrator." }

$manifest = @(Import-Csv -LiteralPath $ManifestCsv -Encoding UTF8 | Where-Object { $_.read_ok -eq '1' })
if ($manifest.Count -eq 0) { throw "Manifest rỗng: $ManifestCsv" }
$startPid = (Get-Process -Name $ProcessName | Select-Object -First 1).Id
$startProcTime = (Get-Process -Id $startPid).StartTime

# Bảng kết quả: khoá "phase|tên file"
$results = @{}
function Add-Planned([string]$Phase, $Row) {
    $name = "$($Row.id).$($Row.format)"
    $src = Join-Path (Join-Path $RawdataFolder "$($Row.domain)__$($Row.label)") $name
    $results["$Phase|$name"] = @{
        Phase = $Phase; Name = $name; Source = $src; Id = $Row.id; Format = $Row.format; Domain = $Row.domain; Label = $Row.label
        Truth = $Row.truth; IsBorderline = $Row.is_borderline; SizeBytes = [long]$Row.size_bytes; BaselinePredicted = $Row.predicted
        Level = $null; DoneMs = $null; PerfCount = 0; CopyMs = $null; ConfBranch = $null; Conf = $null; ConfDomain = $null
    }
}

# Kịch bản tuần tự: N file mỗi lớp × mỗi định dạng (lấy ngẫu nhiên, cố định seed để lặp lại được)
$rand = New-Object Random 12345
$seqRows = @($manifest | Group-Object domain, label, format | ForEach-Object {
    $_.Group | Sort-Object { $rand.Next() } | Select-Object -First $SequentialPerClass
})
foreach ($r in $seqRows) { Add-Planned 'seq' $r }
if (-not $NoBurst) { foreach ($r in $manifest) { Add-Planned 'burst' $r } } # [Thay đổi] -NoBurst

# [Thay đổi] hạng mục 4: mỗi mức tải đều lấy (mức × StepSeconds) file KHÁC NHAU từ manifest đã xáo trộn
$rateList = @($RateSteps | Where-Object { $_ -gt 0 }) # [Sửa lỗi] -RateSteps 5,20,50 là MẢNG trong PowerShell, không phải chuỗi
$shuffled = @($manifest | Sort-Object { $rand.Next() })
$offset = 0
foreach ($rate in $rateList) {
    $need = $rate * $StepSeconds
    if ($offset + $need -gt $shuffled.Count) { throw "Không đủ file cho mức $rate file/giây ($need file)" }
    foreach ($r in $shuffled[$offset..($offset + $need - 1)]) { Add-Planned "rate$rate" $r }
    $offset += $need
}
# [Thay đổi] xử lý đồng thời: mỗi lô N file khác nhau; 20 file cho kiểm tra sau rảnh
$batchList = @($BatchSizes | Where-Object { $_ -gt 0 })
foreach ($n in $batchList) {
    if ($offset + $n -gt $shuffled.Count) { throw "Không đủ file cho lô $n" }
    foreach ($r in $shuffled[$offset..($offset + $n - 1)]) { Add-Planned "batch$n" $r }
    $offset += $n
}
if ($PostIdleProbe) { foreach ($r in $shuffled[$offset..($offset + 19)]) { Add-Planned 'postidle' $r }; $offset += 20 }
$stormInfo = $null; $lockedInfo = $null; $postIdleInfo = $null; $batchInfo = @()
$missingSrc = @($results.Values | Where-Object { -not (Test-Path -LiteralPath $_.Source) })
if ($missingSrc.Count -gt 0) { throw "Thiếu $($missingSrc.Count) file nguồn, VD $($missingSrc[0].Source)" }

New-Item -ItemType Directory -Path $seqFolder, $burstFolder, $warmupFolder -Force | Out-Null
Write-Host "Lượt $RunId — tuần tự $($seqRows.Count) file, burst $($manifest.Count) file. Service PID $startPid (khởi động $startProcTime)."

$allSamples = New-Object System.Collections.Generic.List[object]
$restartInfo = $null
$sampler = $null
$burstCopyMs = $null; $burstElapsedMs = $null
$logLevelCounts = @{}; $warnMessages = @{}; $overflowCount = 0; $logLinesTotal = 0

# Gom thống kê dòng log CỦA TOÀN LƯỢT ĐO (không chỉ file test): mức log, mẫu cảnh báo/lỗi, tràn buffer
function Collect-LogStats([string[]]$Lines) {
    foreach ($line in $Lines) {
        $script:logLinesTotal++
        if ($line -match '^\S+ \S+ \[(?<lvl>\w+)\]') {
            $lvl = $Matches['lvl']
            $script:logLevelCounts[$lvl] = 1 + [int]$script:logLevelCounts[$lvl]
            if ($lvl -in 'Warning', 'Error', 'Critical') {
                # Gom theo "mẫu" thông điệp: bỏ đường dẫn file và số để đếm được loại cảnh báo
                $msg = ($line -replace '^\S+ \S+ \[\w+\] [^:]+: ', '') -replace '[A-Za-z]:\\[^:]*', '<path>' -replace '\d+([.,]\d+)?', '#'
                if ($msg.Length -gt 140) { $msg = $msg.Substring(0, 140) }
                $key = "[$lvl] $msg"
                $script:warnMessages[$key] = 1 + [int]$script:warnMessages[$key]
            }
        }
        if ($line.Contains('[FileWatcher] Buffer tr')) { $script:overflowCount++ }
    }
}

# [Sửa lỗi] Bước 6 thành hàm, gọi trong finally → lỗi giữa chừng vẫn ghi được kết quả đã thu (lượt 20261021-144046
# mất hết số liệu vì lỗi ở cuối burst rồi finally xoá file test, phần ghi kết quả nằm SAU finally).
function Save-Results {
    # ============================================================================================================
    # BƯỚC 6: GHI KẾT QUẢ (CSV để phân tích bằng Analyze-DlpServiceE2E.py)
    # ============================================================================================================
    $prefix = Join-Path $resultFolder "e2e-$RunId"
    $results.Values | ForEach-Object {
        [pscustomobject]@{
            phase = $_.Phase; name = $_.Name; id = $_.Id; format = $_.Format; domain = $_.Domain; label = $_.Label
            truth = $_.Truth; is_borderline = $_.IsBorderline; size_bytes = $_.SizeBytes; baseline_predicted = $_.BaselinePredicted
            predicted = $_.Level; done_ms = $_.DoneMs; copy_ms = $_.CopyMs; perf_count = $_.PerfCount
            perf_cache = $_.perf_cache; perf_read = $_.perf_read; perf_hash = $_.perf_hash; perf_scan = $_.perf_scan
            perf_classify = $_.perf_cls; perf_save = $_.perf_save; perf_total = $_.perf_total
            conf_branch = $_.ConfBranch; conf = $_.Conf; conf_domain = $_.ConfDomain
        }
    } | Export-Csv -LiteralPath "$prefix-files.csv" -NoTypeInformation -Encoding UTF8
    $allSamples | Export-Csv -LiteralPath "$prefix-resources.csv" -NoTypeInformation -Encoding UTF8

    # Crash/restart ngoài ý muốn: PID đổi trong lúc đo (không tính lần restart chủ động) + Event Log SCM
    $endPid = (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    $pidsSeen = @($allSamples | Where-Object { $_.Phase -ne 'restart' } | Select-Object -ExpandProperty Pid -Unique | Where-Object { $_ -gt 0 })
    $scmEvents = @()
    try {
        $scmEvents = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; Id = 7031, 7034, 7023, 7024; StartTime = $RunStart } -ErrorAction Stop |
            Where-Object { $_.Message -match $ServiceName } | ForEach-Object { "$($_.TimeCreated) [$($_.Id)] $($_.Message)" })
    } catch { }   # không có sự kiện nào → Get-WinEvent ném lỗi "No events were found"
    $appErrors = @()
    try {
        $appErrors = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1, 2; StartTime = $RunStart } -ErrorAction Stop |
            Where-Object { $_.Message -match $ProcessName } | ForEach-Object { "$($_.TimeCreated) [$($_.ProviderName) $($_.Id)] $($_.Message.Split("`n")[0])" })
    } catch { }

    [pscustomobject]@{
        run_id = $RunId; run_start = $RunStart.ToString('o'); manifest = (Resolve-Path $ManifestCsv).Path
        burst_files = $manifest.Count; seq_files = $seqRows.Count
        burst_copy_ms = $burstCopyMs; burst_elapsed_ms = $burstElapsedMs
        start_pid = $startPid; end_pid = $endPid; pids_seen = ($pidsSeen -join ';')
        restart_running_ms = $restartInfo.RunningMs; restart_watching_ms = $restartInfo.WatchingMs; restart_rescanned_files = $restartInfo.RescannedFiles
        log_lines = $logLinesTotal; log_levels = (($logLevelCounts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';')
        buffer_overflow = $overflowCount; scm_events = ($scmEvents -join ' || '); app_errors = ($appErrors -join ' || ')
        llm_running = [bool]($llm -and $llm.Status -eq 'Running'); is_admin = $isAdmin
        fast_drop = [bool]$FastDrop # [Thay đổi] chế độ thả file
        batches = $batchInfo; storm = $stormInfo; locked = $lockedInfo; post_idle = $postIdleInfo # [Thay đổi] xử lý đồng thời
        rate_steps = ($RateSteps -join ','); step_seconds = $StepSeconds; no_burst = [bool]$NoBurst; idle_after_sec = $IdleAfterSec # [Thay đổi] hạng mục 4
    } | ConvertTo-Json | Set-Content -LiteralPath "$prefix-summary.json" -Encoding UTF8
    $warnMessages.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { [pscustomobject]@{ count = $_.Value; message = $_.Key } } |
        Export-Csv -LiteralPath "$prefix-warnings.csv" -NoTypeInformation -Encoding UTF8

    Write-Host ""
    Write-Host "Xong. Kết quả: $prefix-*.csv / -summary.json"
    Write-Host "Phân tích: python -I tools\perf\Analyze-DlpServiceE2E.py $prefix"
}

try {
    # ========================================================================================================
    # [Thay đổi] BƯỚC 0b: LÀM NÓNG — lượt 20261021-144046 cho thấy vài file tuần tự đầu chậm tới ~5 giây vì service vừa khởi
    # động (JIT + biên dịch regex lần đầu). Thả trước $WarmupFiles file (khác các file đo), chờ có dòng [Perf] rồi mới đo.
    # ========================================================================================================
    if ($WarmupFiles -gt 0) {
        Write-Host "Bước 0b: làm nóng $WarmupFiles file..."
        $warmRows = @($manifest | Sort-Object { $rand.Next() } | Select-Object -First $WarmupFiles)
        $warmNames = New-Object 'System.Collections.Generic.HashSet[string]'
        $reader = New-LogReader
        foreach ($r in $warmRows) {
            $name = "$($r.id).$($r.format)"
            Copy-Item -LiteralPath (Join-Path (Join-Path $RawdataFolder "$($r.domain)__$($r.label)") $name) -Destination (Join-Path $warmupFolder $name)
            [void]$warmNames.Add($name)
        }
        $warmSw = [Diagnostics.Stopwatch]::StartNew()
        while ($warmNames.Count -gt 0 -and $warmSw.Elapsed.TotalSeconds -le 120) {
            $lines = Read-NewLogLines $reader
            Collect-LogStats $lines
            foreach ($line in $lines) {
                $e = Get-FileEvent $line
                if ($e -and $e.Phase -eq 'warmup' -and $e.Kind -eq 'perf') { [void]$warmNames.Remove($e.Name) }
            }
            Start-Sleep -Milliseconds 200
        }
        Write-Host ("  Làm nóng xong sau {0:N1} giây (còn {1} file chưa thấy kết quả)" -f $warmSw.Elapsed.TotalSeconds, $warmNames.Count)
    }

    # ========================================================================================================
    # BƯỚC 1: MỨC NỀN (service rảnh)
    # ========================================================================================================
    Write-Host "Bước 1: mức nền 15 giây..."
    $sampler = Start-ResourceSampler
    Start-Sleep -Seconds 16
    $allSamples.AddRange([object[]](Stop-ResourceSampler $sampler 'nen'))
    $sampler = $null

    # ========================================================================================================
    # BƯỚC 2: TUẦN TỰ — độ trễ end-to-end từng file (copy → debounce → xử lý → dòng [Perf])
    # ========================================================================================================
    Write-Host "Bước 2: tuần tự $($seqRows.Count) file..."
    $sampler = Start-ResourceSampler
    $reader = New-LogReader
    $i = 0
    foreach ($row in @($results.Values | Where-Object { $_.Phase -eq 'seq' } | Sort-Object Name)) {
        $i++
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Copy-Item -LiteralPath $row.Source -Destination (Join-Path $seqFolder $row.Name)
        $row.CopyMs = $sw.Elapsed.TotalMilliseconds
        while (-not $row.DoneMs -and $sw.Elapsed.TotalSeconds -le $SequentialTimeoutSec) {
            $lines = Read-NewLogLines $reader
            Collect-LogStats $lines
            foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e $sw.Elapsed.TotalMilliseconds } }
            if (-not $row.DoneMs) { Start-Sleep -Milliseconds 20 }
        }
        if ($i % 16 -eq 0) { Write-Host ("  {0}/{1}" -f $i, $seqRows.Count) }
    }
    # Đọc nốt các dòng còn lại (dòng [Conf]/[DLP] có thể tới cùng lúc với [Perf])
    $lines = Read-NewLogLines $reader; Collect-LogStats $lines
    foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e 0 } }
    $allSamples.AddRange([object[]](Stop-ResourceSampler $sampler 'tuan-tu'))
    $sampler = $null

    # ========================================================================================================
    # [Thay đổi] BƯỚC 2b (hạng mục 4): TẢI ĐỀU — thả file với tốc độ cố định, đo độ trễ từng file + tài nguyên ở từng mức tải
    # ========================================================================================================
    foreach ($rate in $rateList) {
        $phase = "rate$rate"
        $phaseFolder = Join-Path $testRoot $phase
        New-Item -ItemType Directory -Path $phaseFolder -Force | Out-Null
        $rows = @($results.Values | Where-Object { $_.Phase -eq $phase })
        Write-Host ("Bước 2b: tải đều {0} file/giây × {1} giây ({2} file)..." -f $rate, $StepSeconds, $rows.Count)
        $sampler = Start-ResourceSampler
        Start-Sleep -Seconds 2
        $reader = New-LogReader
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $lastPoll = 0
        for ($k = 0; $k -lt $rows.Count; $k++) {
            $due = $k * 1000.0 / $rate
            # Chờ tới đúng thời điểm thả file kế tiếp; trong lúc chờ thì đọc log (~10 lần/giây) để bấm giờ kết quả chính xác
            while ($sw.Elapsed.TotalMilliseconds -lt $due) {
                if ($sw.Elapsed.TotalMilliseconds - $lastPoll -ge 100) {
                    $lastPoll = $sw.Elapsed.TotalMilliseconds
                    $lines = Read-NewLogLines $reader; Collect-LogStats $lines
                    foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e $sw.Elapsed.TotalMilliseconds } }
                }
                $left = $due - $sw.Elapsed.TotalMilliseconds
                if ($left -gt 1) { Start-Sleep -Milliseconds ([math]::Min(20, [int]$left)) }
            }
            Copy-Item -LiteralPath $rows[$k].Source -Destination (Join-Path $phaseFolder $rows[$k].Name)
            $rows[$k].CopyMs = $sw.Elapsed.TotalMilliseconds
        }
        $dropMs = $sw.Elapsed.TotalMilliseconds
        # Chờ service xử lý nốt (tối đa 120 giây sau khi thả xong)
        do {
            $lines = Read-NewLogLines $reader; Collect-LogStats $lines
            foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e $sw.Elapsed.TotalMilliseconds } }
            $done = @($rows | Where-Object { $_.DoneMs }).Count
            if ($done -lt $rows.Count) { Start-Sleep -Milliseconds 100 }
        } while ($done -lt $rows.Count -and $sw.Elapsed.TotalMilliseconds - $dropMs -le 120000)
        Write-Host ("  thả xong sau {0:N0} giây (tốc độ thực {1:N1} file/giây), xong {2}/{3}" -f ($dropMs / 1000), ($rows.Count * 1000.0 / [math]::Max(1, $dropMs)), $done, $rows.Count)
        $allSamples.AddRange([object[]](Stop-ResourceSampler $sampler $phase))
        $sampler = $null
    }

    # ========================================================================================================
    # [Thay đổi] BƯỚC 2c (xử lý đồng thời): LÔ đồng thời / ghi đè dồn dập / file bị khoá
    # ========================================================================================================
    function Poll-Into([hashtable]$Reader, $Stopwatch) {
        $lines = Read-NewLogLines $Reader; Collect-LogStats $lines
        foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e $Stopwatch.Elapsed.TotalMilliseconds } }
        return , $lines
    }

    # [Thay đổi] Poll-Into trả về CẢ danh sách như 1 phần tử (return , $lines) → nếu pipe thẳng vào Where-Object thì $_ là cả List và
    # $_.Contains(...) thành List.Contains (so khớp nguyên dòng) → không dòng nào khớp (lỗi lượt 143106: EditStorm/LockedFile báo 0).
    # Hàm này duyệt TỪNG dòng và chỉ giữ dòng chứa chuỗi cần tìm.
    function Poll-Matching([hashtable]$Reader, $Stopwatch, [string]$Needle) {
        $found = New-Object System.Collections.Generic.List[string]
        foreach ($l in (Poll-Into $Reader $Stopwatch)) { if ($l.Contains($Needle)) { $found.Add($l) } }
        return , $found
    }

    foreach ($n in $batchList) {
        $phase = "batch$n"
        $rows = @($results.Values | Where-Object { $_.Phase -eq $phase })
        $phaseFolder = Join-Path $testRoot $phase
        $stage = Join-Path $stagingFolder $phase
        New-Item -ItemType Directory -Path $phaseFolder, $stage -Force | Out-Null
        foreach ($row in $rows) { Copy-Item -LiteralPath $row.Source -Destination (Join-Path $stage $row.Name) } # chuẩn bị, không tính giờ
        Start-Sleep -Seconds 3
        Write-Host "Bước 2c: lô $n file thả cùng lúc..."
        $reader = New-LogReader
        $sw = [Diagnostics.Stopwatch]::StartNew()
        foreach ($row in $rows) {
            [IO.File]::Move((Join-Path $stage $row.Name), (Join-Path $phaseFolder $row.Name))
            $row.CopyMs = $sw.Elapsed.TotalMilliseconds
        }
        $dropMs = $sw.Elapsed.TotalMilliseconds
        $ovBefore = $overflowCount
        do {
            [void](Poll-Into $reader $sw)
            $done = @($rows | Where-Object { $_.DoneMs }).Count
            if ($done -lt $rows.Count) { Start-Sleep -Milliseconds 100 }
        } while ($done -lt $rows.Count -and $sw.Elapsed.TotalSeconds -le 600)
        $lastDone = (@($rows | ForEach-Object { $_.DoneMs } | Where-Object { $_ }) | Measure-Object -Maximum).Maximum
        $batchInfo += [pscustomobject]@{ n = $n; drop_ms = [math]::Round($dropMs); done = $done; last_done_ms = $lastDone; overflow = $overflowCount - $ovBefore }
        Write-Host ("  thả trong {0:N0} ms, xong {1}/{2} sau {3:N1} giây, tràn buffer {4}" -f $dropMs, $done, $rows.Count, ($lastDone / 1000), ($overflowCount - $ovBefore))
        Start-Sleep -Seconds 3
    }

    if ($EditStorm) {
        # Xen kẽ 2 file docx (Public / Restricted theo DlpBaseline), lần ghi thứ 20 (cuối) là bản Restricted → kết quả cuối phải Restricted
        $pub = $manifest | Where-Object { $_.format -eq 'docx' -and $_.truth -eq 'Public' -and $_.predicted -eq 'Public' } | Select-Object -First 1
        $sec = $manifest | Where-Object { $_.format -eq 'docx' -and $_.truth -eq 'Restricted' -and $_.predicted -eq 'Restricted' } | Select-Object -First 1
        $srcOf = { param($r) Join-Path (Join-Path $RawdataFolder "$($r.domain)__$($r.label)") "$($r.id).docx" }
        $stormFolder = Join-Path $testRoot 'storm'
        New-Item -ItemType Directory -Path $stormFolder -Force | Out-Null
        $dest = Join-Path $stormFolder 'storm.docx'
        Write-Host "Bước 2c: ghi đè 1 file 20 lần (100 ms/lần)..."
        $reader = New-LogReader
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $writeErr = 0
        for ($i = 1; $i -le 20; $i++) {
            $src = if ($i % 2 -eq 0) { & $srcOf $sec } else { & $srcOf $pub }
            try { Copy-Item -LiteralPath $src -Destination $dest -Force -ErrorAction Stop } catch { $writeErr++ } # service có thể đang đọc
            Start-Sleep -Milliseconds 100
        }
        $stormLines = New-Object System.Collections.Generic.List[string]
        # [Thay đổi] dùng Poll-Matching (lọc từng dòng) thay cho pipe cả List vào Where-Object
        while ($sw.Elapsed.TotalSeconds -le 20) { $stormLines.AddRange((Poll-Matching $reader $sw '\storm\storm.docx')); Start-Sleep -Milliseconds 200 }
        $levels = @($stormLines | ForEach-Object { if ($_ -match ': mức (\w+)\s*$') { $Matches[1] } elseif ($_ -match '\[DLP\] .+: sạch\s*$') { 'Public' } })
        $stormInfo = [pscustomobject]@{
            writes = 20; write_errors = $writeErr; processings = @($stormLines | Where-Object { $_.Contains('[Perf]') }).Count
            levels_seen = ($levels -join '>'); final_level = $(if ($levels.Count) { $levels[-1] } else { '' }); expected = $sec.predicted
        }
        Write-Host ("  {0} lần xử lý, mức lần lượt: {1} — mong đợi cuối: {2}" -f $stormInfo.processings, $stormInfo.levels_seen, $stormInfo.expected)
    }

    if ($LockedFile) {
        # Thả file rồi KHOÁ ĐỘC QUYỀN ngay (trước khi hết 300 ms debounce) trong 5 giây, mở khoá, chờ 30 giây xem service có xử lý lại
        $lockRows = @($manifest | Where-Object { $_.format -eq 'docx' -and $_.predicted -eq 'Restricted' } | Select-Object -Skip 1 -First 3)
        $lockFolder = Join-Path $testRoot 'locked'
        New-Item -ItemType Directory -Path $lockFolder -Force | Out-Null
        Write-Host "Bước 2c: 3 file bị khoá 5 giây ngay sau khi thả..."
        $reader = New-LogReader
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $handles = @()
        foreach ($r in $lockRows) {
            $dest = Join-Path $lockFolder "$($r.id).docx"
            Copy-Item -LiteralPath (Join-Path (Join-Path $RawdataFolder "$($r.domain)__$($r.label)") "$($r.id).docx") -Destination $dest
            $handles += [IO.File]::Open($dest, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        }
        $lockLines = New-Object System.Collections.Generic.List[string]
        # [Thay đổi] dùng Poll-Matching (lọc từng dòng) thay cho pipe cả List vào Where-Object
        while ($sw.Elapsed.TotalSeconds -le 5) { $lockLines.AddRange((Poll-Matching $reader $sw '\locked\')); Start-Sleep -Milliseconds 200 }
        $handles | ForEach-Object { $_.Dispose() }
        while ($sw.Elapsed.TotalSeconds -le 35) { $lockLines.AddRange((Poll-Matching $reader $sw '\locked\')); Start-Sleep -Milliseconds 200 }
        $processed = @($lockRows | Where-Object { $id = $_.id; @($lockLines | Where-Object { $_.Contains("\locked\$id.docx") -and $_.Contains('[Perf]') }).Count -gt 0 }).Count
        $lockedInfo = [pscustomobject]@{
            files = $lockRows.Count; read_failed = @($lockLines | Where-Object { $_.Contains('Đọc thất bại') }).Count; processed_after_unlock = $processed
            sample_error = (@($lockLines | Where-Object { $_.Contains('Đọc thất bại') } | Select-Object -First 1) -replace '^.*?: ', '' -replace '[A-Za-z]:\\\S+', '<path>')
        }
        Write-Host ("  đọc lỗi {0} lần, xử lý được sau khi mở khoá {1}/{2}" -f $lockedInfo.read_failed, $processed, $lockRows.Count)
    }

    # ========================================================================================================
    # BƯỚC 3: BURST — thả toàn bộ file có nhãn, đo thông lượng + độ chính xác + tài nguyên khi tải cao
    # ========================================================================================================
    Write-Host "Bước 3: burst $($manifest.Count) file..."
    $burstRows = @($results.Values | Where-Object { $_.Phase -eq 'burst' })

    # [Thay đổi] Đo thông lượng tối đa (-FastDrop): copy trước vào thư mục tạm CÙNG Ổ nhưng NGOÀI watch-folder (không tính giờ,
    # service không thấy), rồi chuyển từng file vào watch-folder — đổi tên trong cùng ổ chỉ sửa metadata nên thả 25.000 file
    # trong vài giây, service không còn bị giới hạn bởi tốc độ copy của script (lượt 20261021-150346: copy 416 giây).
    if ($FastDrop) {
        Write-Host "  -FastDrop: chuẩn bị file trong $stagingFolder (không tính giờ)..."
        New-Item -ItemType Directory -Path $stagingFolder -Force | Out-Null
        foreach ($row in $burstRows) { Copy-Item -LiteralPath $row.Source -Destination (Join-Path $stagingFolder $row.Name) }
    }

    $sampler = Start-ResourceSampler
    Start-Sleep -Seconds 2
    $reader = New-LogReader
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $pending = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($row in $burstRows) { [void]$pending.Add("burst|$($row.Name)") }
    $lastPoll = 0; $j = 0
    foreach ($row in $burstRows) {
        if ($FastDrop) {
            # [Thay đổi] File.Move nhanh hơn Move-Item (không qua provider PowerShell) — thả nhanh nhất có thể
            [IO.File]::Move((Join-Path $stagingFolder $row.Name), (Join-Path $burstFolder $row.Name))
        }
        else {
            Copy-Item -LiteralPath $row.Source -Destination (Join-Path $burstFolder $row.Name)
        }
        $row.CopyMs = $sw.Elapsed.TotalMilliseconds
        $j++
        # Vừa copy vừa đọc log (mỗi ~1 giây) để không dồn hàng chục MB log tới cuối
        if ($sw.Elapsed.TotalMilliseconds - $lastPoll -ge 1000) {
            $lastPoll = $sw.Elapsed.TotalMilliseconds
            $lines = Read-NewLogLines $reader; Collect-LogStats $lines
            foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e $sw.Elapsed.TotalMilliseconds } }
        }
    }
    $burstCopyMs = $sw.Elapsed.TotalMilliseconds
    Write-Host ("  Đã thả {0:N0} file trong {1:N0} giây, chờ service xử lý..." -f $burstRows.Count, ($burstCopyMs / 1000))

    $lastProgress = 0
    do {
        $lines = Read-NewLogLines $reader; Collect-LogStats $lines
        foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e $sw.Elapsed.TotalMilliseconds } }
        $done = @($burstRows | Where-Object { $_.DoneMs }).Count
        if ($sw.Elapsed.TotalSeconds - $lastProgress -ge 15) {
            $lastProgress = $sw.Elapsed.TotalSeconds
            Write-Host ("  {0:N0}s: xong {1:N0}/{2:N0}, tràn buffer {3}" -f $lastProgress, $done, $burstRows.Count, $overflowCount)
        }
        if ($done -lt $burstRows.Count) { Start-Sleep -Milliseconds 1000 }
    } while ($done -lt $burstRows.Count -and $sw.Elapsed.TotalSeconds -le $BurstTimeoutSec)
    # [Sửa lỗi] Measure-Object -Property không đọc được khoá của hashtable → lấy giá trị ra trước rồi mới tính max
    $burstElapsedMs = (@($burstRows | ForEach-Object { $_.DoneMs } | Where-Object { $_ }) | Measure-Object -Maximum).Maximum
    $allSamples.AddRange([object[]](Stop-ResourceSampler $sampler 'burst'))
    $sampler = $null

    # ========================================================================================================
    # BƯỚC 4: SAU TẢI — service rảnh lại, RAM/handle có trở về không
    # ========================================================================================================
    Write-Host "Bước 4: theo dõi khi rảnh $IdleAfterSec giây..."
    $sampler = Start-ResourceSampler
    $idleSw = [Diagnostics.Stopwatch]::StartNew()
    while ($idleSw.Elapsed.TotalSeconds -lt $IdleAfterSec) {
        $lines = Read-NewLogLines $reader; Collect-LogStats $lines
        foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e 0 } }
        Start-Sleep -Seconds 2
    }
    $allSamples.AddRange([object[]](Stop-ResourceSampler $sampler 'sau-tai'))
    $sampler = $null

    # [Thay đổi] Kiểm chứng sửa lỗi watcher bị GC thu hồi: sau khi service rảnh $IdleAfterSec giây, thả 20 file — phải được xử lý hết
    if ($PostIdleProbe) {
        Write-Host "Bước 4b: thả 20 file sau giai đoạn rảnh..."
        $pf = Join-Path $testRoot 'postidle'
        New-Item -ItemType Directory -Path $pf -Force | Out-Null
        $rows = @($results.Values | Where-Object { $_.Phase -eq 'postidle' })
        $reader = New-LogReader
        $sw = [Diagnostics.Stopwatch]::StartNew()
        foreach ($row in $rows) { Copy-Item -LiteralPath $row.Source -Destination (Join-Path $pf $row.Name); $row.CopyMs = $sw.Elapsed.TotalMilliseconds }
        do {
            $lines = Read-NewLogLines $reader; Collect-LogStats $lines
            foreach ($line in $lines) { $e = Get-FileEvent $line; if ($e) { Update-Result $results $e $sw.Elapsed.TotalMilliseconds } }
            $done = @($rows | Where-Object { $_.DoneMs }).Count
            if ($done -lt $rows.Count) { Start-Sleep -Milliseconds 200 }
        } while ($done -lt $rows.Count -and $sw.Elapsed.TotalSeconds -le 60)
        $postIdleInfo = [pscustomobject]@{ files = $rows.Count; processed = $done; idle_sec = $IdleAfterSec }
        Write-Host ("  xử lý {0}/{1} file sau {2} giây rảnh" -f $done, $rows.Count, $IdleAfterSec)
    }

    # ========================================================================================================
    # BƯỚC 5 (tuỳ chọn): KHỞI ĐỘNG LẠI — thời gian khởi động, có quét lại file đã xử lý không (phải 0 nhờ cache)
    # ========================================================================================================
    if ($RestartTest) {
        Write-Host "Bước 5: khởi động lại service..."
        $perfBefore = @{}; foreach ($row in $burstRows) { $perfBefore[$row.Name] = $row.PerfCount }
        $reader = New-LogReader
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Restart-Service -Name $ServiceName
        $runningMs = $sw.Elapsed.TotalMilliseconds
        $watchingMs = $null
        while ($sw.Elapsed.TotalSeconds -le 300) {
            $lines = Read-NewLogLines $reader; Collect-LogStats $lines
            foreach ($line in $lines) {
                if (-not $watchingMs -and $line.Contains('Đang theo dõi thư mục')) { $watchingMs = $sw.Elapsed.TotalMilliseconds }
                $e = Get-FileEvent $line; if ($e) { Update-Result $results $e 0 }
            }
            if ($watchingMs -and $sw.Elapsed.TotalMilliseconds - $watchingMs -ge 30000) { break }
            Start-Sleep -Milliseconds 500
        }
        $rescanned = @($burstRows | Where-Object { $_.PerfCount -gt $perfBefore[$_.Name] }).Count
        $restartInfo = [pscustomobject]@{ RunningMs = [math]::Round($runningMs); WatchingMs = $watchingMs; RescannedFiles = $rescanned }
    }
}
finally {
    # [Sửa lỗi] Giữ lại mẫu tài nguyên đang lấy dở + LUÔN ghi kết quả trước khi xoá file test
    if ($sampler) {
        try { $allSamples.AddRange([object[]](Stop-ResourceSampler $sampler 'dang-do-khi-loi')) } catch { }
        $sampler = $null
    }
    try { Save-Results } catch { Write-Warning "Không ghi được kết quả: $_" }
    if (-not $KeepFiles -and (Test-Path -LiteralPath $testRoot)) {
        Write-Host "Xoá file test..."
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    # [Thay đổi] -FastDrop: dọn thư mục tạm (nếu lỗi giữa chừng còn sót file chưa chuyển)
    if (Test-Path -LiteralPath $stagingFolder) {
        Remove-Item -LiteralPath $stagingFolder -Recurse -Force -ErrorAction SilentlyContinue
    }
}
