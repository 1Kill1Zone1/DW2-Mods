param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [string[]]$Argument = @(),
    [string]$ArgumentFile,
    [Parameter(Mandatory=$true)][string]$WorkingDirectory,
    [Parameter(Mandatory=$true)][string]$Log,
    [string]$ResourceLog,
    [hashtable]$Environment = @{}
)
$ErrorActionPreference = 'Stop'
# Owner rule: a single build or test across all agents; do not queue a second.
$mutex = [Threading.Mutex]::new($false, 'Local\DW2Modding.SingleCheck')
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Another DW2 build/test is running. Wait for it to finish.' }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    if ($ArgumentFile) { $Argument = @(Get-Content -LiteralPath $ArgumentFile -Raw | ConvertFrom-Json) }
    foreach ($item in $Argument) { $info.ArgumentList.Add([string]$item) }
    $info.Environment['DOTNET_PROCESSOR_COUNT'] = '2'
    $info.Environment['DOTNET_gcServer'] = '0'
    $info.Environment['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'
    $info.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = [string]$Environment[$key] }
    $process = [Diagnostics.Process]::Start($info)
    $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if ($ResourceLog) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $logicalProcessors = [Environment]::ProcessorCount
        $lastCpu = 0.0; $lastSeconds = 0.0; $peakCpu = 0.0; $peakWorking = 0L; $peakPrivate = 0L
        while (-not $process.WaitForExit(250)) {
            try {
                $process.Refresh()
                $cpu = $process.TotalProcessorTime.TotalSeconds; $seconds = $watch.Elapsed.TotalSeconds
                $peakCpu = [Math]::Max($peakCpu, 100 * ($cpu - $lastCpu) / (($seconds - $lastSeconds) * $logicalProcessors))
                $peakWorking = [Math]::Max($peakWorking, $process.PeakWorkingSet64)
                $peakPrivate = [Math]::Max($peakPrivate, $process.PrivateMemorySize64)
                $lastCpu = $cpu; $lastSeconds = $seconds
            } catch [InvalidOperationException] { }
        }
        [ordered]@{ Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2); LogicalProcessors = $logicalProcessors;
            AverageCpuPercentMachine = [Math]::Round(100 * $lastCpu / ([Math]::Max(.001, $lastSeconds) * $logicalProcessors), 1);
            PeakSampleCpuPercentMachine = [Math]::Round($peakCpu, 1); PeakWorkingSetMiB = [Math]::Round($peakWorking / 1MB);
            PeakPrivateMiB = [Math]::Round($peakPrivate / 1MB); SamplingMs = 250; Scope = 'Direct child process; CPU/RAM sampled without a second test' } |
            ConvertTo-Json | Set-Content -LiteralPath $ResourceLog
    } else { $process.WaitForExit() }
    [IO.File]::WriteAllText($Log, $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
    Get-Content -LiteralPath $Log -Tail 12
    if ($process.ExitCode -ne 0) { throw "Build/test failed with exit $($process.ExitCode); see $Log" }
} finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
