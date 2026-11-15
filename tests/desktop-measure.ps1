param(
    [ValidateSet('empty', 'garden', 'covered')][string]$Scenario = 'garden',
    [int]$Seconds = 30,
    [string]$Output = '.\desktop-measurement.json'
)
$ErrorActionPreference = 'Stop'
$diagnosticPath = Join-Path $env:LOCALAPPDATA 'WildflowerDesktopData\diagnostics.json'
if (!(Test-Path $diagnosticPath)) { throw 'Start WildflowerDesktop.exe --diagnostics (add --empty-host for the empty baseline) first.' }
$initial = Get-Content -Raw $diagnosticPath | ConvertFrom-Json
if ($Scenario -eq 'empty' -and !$initial.emptyHost) { throw 'Empty baseline requires a fresh --diagnostics --empty-host launch.' }
if ($Scenario -ne 'empty' -and $initial.emptyHost) { throw 'Garden/covered measurement requires the real theme, not --empty-host.' }
$samples = @()
for ($sample = 0; $sample -lt $Seconds; $sample++) {
    $hostState = Get-Content -Raw $diagnosticPath | ConvertFrom-Json
    if (((Get-Date).ToUniversalTime() - [datetime]$hostState.timestampUtc).TotalSeconds -gt 8) { throw 'Host diagnostics are stale.' }
    # Include WebView2 crashpad and any descendants as well as the environment process list.
    $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($entry in $hostState.processes) { [void]$ids.Add([int]$entry.processId) }
    do {
        $added = $false
        foreach ($entry in $all) { if ($ids.Contains([int]$entry.ParentProcessId)) { $added = $ids.Add([int]$entry.ProcessId) -or $added } }
    } while ($added)
    $privateSet = @{}
    foreach ($entry in (Get-CimInstance Win32_PerfRawData_PerfProc_Process)) { $privateSet[[int]$entry.IDProcess] = [long]$entry.WorkingSetPrivate }
    $processes = @()
    foreach ($processId in $ids) {
        try {
            $process = Get-Process -Id $processId -ErrorAction Stop
            $processes += [pscustomobject]@{ processId = $processId; privateWorkingSetBytes = $privateSet[$processId]; privateBytes = $process.PrivateMemorySize64; cpuSeconds = $process.TotalProcessorTime.TotalSeconds }
        } catch { } # Processes can exit between snapshots.
    }
    $gpu = @()
    try {
        foreach ($entry in (Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -ErrorAction Stop)) {
            if ($entry.Name -match '^pid_(\d+)_' -and $ids.Contains([int]$Matches[1])) {
                $gpu += [pscustomobject]@{ processId = [int]$Matches[1]; dedicatedBytes = $entry.DedicatedUsage; sharedBytes = $entry.SharedUsage }
            }
        }
    } catch { } # VM graphics drivers may not expose GPU counters; absence is explicit in output.
    $samples += [pscustomobject]@{
        timestampUtc = (Get-Date).ToUniversalTime().ToString('o'); paused = $hostState.paused; screen = $hostState.screen
        flowerCount = $hostState.page.flowerCount; frameEvents = $hostState.frameEvents; reportedFrameCount = $hostState.reportedFrameCount
        lastFrameUtc = $hostState.lastFrameUtc; privateWorkingSetBytes = ($processes | Measure-Object privateWorkingSetBytes -Sum).Sum
        processes = $processes; gpu = $gpu; gpuCountersAvailable = ($gpu.Count -gt 0)
    }
    Start-Sleep -Seconds 1
}
$result = [pscustomobject]@{ scenario = $Scenario; architecture = $env:PROCESSOR_ARCHITECTURE; samples = $samples }
$result | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $Output
if ($Scenario -eq 'covered') {
    $stable = @($samples | Select-Object -Skip 3)
    if ($stable.Count -lt 3 -or ($stable | Where-Object { !$_.paused }).Count -gt 0) { throw 'Full coverage did not remain paused; see measurement.' }
    if (($stable.frameEvents | Select-Object -Unique).Count -ne 1) { throw 'Renderer emitted new frame events while covered; see measurement.' }
}
if ($Scenario -eq 'garden' -and $samples[-1].frameEvents -le $samples[0].frameEvents) {
    throw 'No new frame events in visible garden interval. Enable animated effects before testing pause/resume.'
}
Write-Host "Saved local process/memory/frame evidence to $Output. No desktop screenshots or click records captured."
