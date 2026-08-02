<#
.SYNOPSIS
    Captures or checks the benchmark baseline (Z.9, EDPF-BNC-001).

.DESCRIPTION
    Phase 31 built BenchmarkBaseline — the logic that compares a run against
    recorded measurements and fails over a 5% regression. It had nothing to
    compare against: no baseline had ever been captured, and no tooling existed
    to capture one. A gate with no baseline is not a gate.

    This script closes that. `-Capture` records the current run as the
    baseline; without it, the script compares and exits non-zero on a
    regression.

    ── READ THIS BEFORE TRUSTING A TIME REGRESSION ──────────────────────────

    A benchmark reports two very different things, and confusing them is the
    trap this script exists to avoid.

    WITHIN-RUN PRECISION is the confidence margin: how tightly one run's
    samples cluster. On a full job here it is 0.7%-1.9%, which looks excellent.

    BETWEEN-RUN REPRODUCIBILITY is what a gate actually needs, because it
    compares today's run against a baseline captured days ago. Two consecutive
    full jobs on this machine, no code change:

        EncryptField[32 B]            898.8 ns -> 1,250.1 ns   +39.1%
        DeserializeEnvelope[1 KB]     107.0 ns ->   150.5 ns   +40.6%
        SerializeRoundTrip[64 KB]   9,969.5 ns -> 14,811.5 ns  +48.6%
        SerializeRoundTrip[32 B]      140.8 ns ->   109.0 ns   -22.6%

    Every benchmark moved by more than 22% while each run called itself
    precise to under 2%. The two statistics differ by a factor of about thirty,
    so a 5% timing gate on shared hardware fails the build on the next run with
    no code change at all.

    ALLOCATION IS DIFFERENT IN KIND. BenchmarkDotNet COUNTS allocated bytes
    rather than sampling them. Across those same two runs every figure was
    byte-identical: 136, 240, 392, 1128, 1232, 2376, 65642, 65744, 131400.
    Allocation is always enforced, and it is the dimension that catches the
    defects worth catching anyway.

    Timing is therefore advisory unless -EnforceTiming is passed, which is
    appropriate only on a dedicated runner whose between-run drift someone has
    actually measured.

    Never capture a baseline with -Short: margins there run 29%-273%.

.PARAMETER Capture
    Record this run as the new baseline instead of comparing against it.

.PARAMETER EnforceTiming
    Treat timing regressions as failures. Only meaningful on a dedicated
    benchmark machine; see above.

.PARAMETER Short
    Use BenchmarkDotNet's short job. Faster, far noisier, and unsuitable for
    capturing a baseline — offered for smoke-testing the plumbing only.

.EXAMPLE
    ./tools/benchmark-baseline.ps1 -Capture

.EXAMPLE
    ./tools/benchmark-baseline.ps1 -EnforceTiming
#>
[CmdletBinding()]
param(
    [switch]$Capture,
    [switch]$EnforceTiming,
    [switch]$Short
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$benchmarkProject = Join-Path $repoRoot 'tests/Edpf.Benchmarks'
$baselinePath = Join-Path $repoRoot 'tests/Edpf.Benchmarks/baseline.json'
$artifacts = Join-Path $benchmarkProject 'BenchmarkDotNet.Artifacts/results'

$jobArgs = @('--filter', '*', '--exporters', 'json')
if ($Short) { $jobArgs += @('--job', 'short') }

Write-Host "Running benchmarks$(if ($Short) { ' (short job — noisy)' })..."
Push-Location $benchmarkProject
try {
    & dotnet run -c Release --framework net10.0 -- @jobArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Benchmark run failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$report = Get-ChildItem $artifacts -Filter '*-report-full-compressed.json' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $report) { throw "No benchmark report was produced in $artifacts." }

$measurements = (Get-Content $report.FullName -Raw | ConvertFrom-Json).Benchmarks | ForEach-Object {
    $margin = $_.Statistics.ConfidenceInterval.Margin
    $mean = $_.Statistics.Mean
    [pscustomobject]@{
        # Parameters are part of the identity: EncryptField at 32 bytes and at
        # 64 KB are different benchmarks that happen to share a method name.
        Name             = "$($_.Method)[$($_.Parameters)]"
        MeanNanoseconds  = [math]::Round($mean, 2)
        AllocatedBytes   = $_.Memory.BytesAllocatedPerOperation
        MarginFraction   = [math]::Round($margin / $mean, 4)
    }
}

if ($Capture) {
    $noisiest = ($measurements | Measure-Object -Property MarginFraction -Maximum).Maximum

    $payload = [pscustomobject]@{
        capturedUtc     = (Get-Date).ToUniversalTime().ToString('o')
        machine         = $env:COMPUTERNAME
        processorCount  = [Environment]::ProcessorCount
        job             = if ($Short) { 'short' } else { 'default' }
        # Recorded so a reader can see how much of the 5% tolerance is noise
        # before deciding whether to believe a timing verdict.
        worstMarginFraction = $noisiest
        measurements    = $measurements
    }

    $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $baselinePath -Encoding utf8

    Write-Host "Baseline captured: $($measurements.Count) measurement(s) -> $baselinePath"
    Write-Host ("Worst confidence margin: {0:P1} of mean." -f $noisiest)
    if ($noisiest -gt 0.05) {
        Write-Warning ("Measurement noise ({0:P1}) exceeds the 5% regression tolerance. " -f $noisiest +
            'Timing verdicts from this baseline are advisory only; capture on quiet, dedicated hardware ' +
            'before enforcing them.')
    }
    exit 0
}

if (-not (Test-Path $baselinePath)) {
    throw "No baseline at $baselinePath. Run with -Capture first."
}

$baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
$recorded = @{}
foreach ($m in $baseline.measurements) { $recorded[$m.Name] = $m }

$tolerance = 0.05
$failures = @()
$advisories = @()

foreach ($current in $measurements) {
    if (-not $recorded.ContainsKey($current.Name)) {
        # A renamed benchmark loses its history silently, so this is reported
        # rather than treated as a pass — matching BenchmarkBaseline's own
        # NoBaseline finding.
        $failures += "$($current.Name): no baseline entry (new or renamed benchmark)."
        continue
    }

    $before = $recorded[$current.Name]

    $allocChange = ($current.AllocatedBytes - $before.AllocatedBytes) / [double]$before.AllocatedBytes
    if ($allocChange -gt $tolerance) {
        $failures += ("{0}: allocation {1} B -> {2} B ({3:P1})." -f
            $current.Name, $before.AllocatedBytes, $current.AllocatedBytes, $allocChange)
    }

    $timeChange = ($current.MeanNanoseconds - $before.MeanNanoseconds) / $before.MeanNanoseconds
    if ($timeChange -gt $tolerance) {
        $line = "{0}: mean {1} ns -> {2} ns ({3:P1})." -f
            $current.Name, $before.MeanNanoseconds, $current.MeanNanoseconds, $timeChange

        # Advisory unless someone has declared the hardware quiet.
        #
        # The recorded MarginFraction is NOT used to decide this, and that is
        # a deliberate correction. The margin is WITHIN-RUN precision — how
        # tightly one run's samples cluster. What a gate needs is BETWEEN-RUN
        # reproducibility, and on this machine the two differ by a factor of
        # about thirty: two consecutive full jobs moved every benchmark by
        # between -22.6% and +48.6% while each reported itself precise to
        # under 2%.
        #
        # Calibrating enforcement on the margin would therefore have enforced
        # every benchmark and failed the build on the very next run with no
        # code change at all.
        if ($EnforceTiming) { $failures += $line } else { $advisories += $line }
    }
}

foreach ($a in $advisories) { Write-Host "advisory  $a" -ForegroundColor Yellow }

if ($failures.Count -gt 0) {
    Write-Host ''
    foreach ($f in $failures) { Write-Host "REGRESSION  $f" -ForegroundColor Red }
    exit 1
}

Write-Host "No regression beyond $($tolerance * 100)%$(if (-not $EnforceTiming) { ' (allocation enforced, timing advisory)' })."
exit 0
