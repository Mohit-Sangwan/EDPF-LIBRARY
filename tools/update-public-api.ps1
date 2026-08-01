<#
.SYNOPSIS
    Regenerates a project's PublicAPI.Unshipped.txt baseline (Z.4 EDPF0013).

.DESCRIPTION
    Any public API change must be reflected in the baseline in the same
    commit, so accidental breaking changes cannot merge silently. This script
    produces the new baseline; the diff it creates is what a reviewer reads.

    The baseline is regenerated from EMPTY rather than appended to. Appending
    looks correct until a signature changes, at which point the old entry
    lingers and fails with RS0017 ("declared but not found") — the stale entry
    is invisible in a diff that only adds lines.

.PARAMETER Project
    Path to the project directory holding PublicAPI.Unshipped.txt.

.EXAMPLE
    ./tools/update-public-api.ps1 -Project src/Edpf.Abstractions
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Project
)

$ErrorActionPreference = 'Stop'

$baseline = Join-Path $Project 'PublicAPI.Unshipped.txt'
if (-not (Test-Path $baseline)) {
    throw "No PublicAPI.Unshipped.txt in '$Project'. Public API tracking is not enabled for that project."
}

Write-Host "Regenerating public API baseline for $Project..." -ForegroundColor Cyan

# Start from empty so every current symbol is reported as missing.
'#nullable enable' | Set-Content $baseline

$output = dotnet build $Project -c Release -t:Rebuild 2>&1 | Out-String -Stream
$symbols = $output |
    Select-String -Pattern "(?:warning|error) RS0016: Symbol '(.+?)' is not part" |
    ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Sort-Object -Unique

'#nullable enable' | Set-Content $baseline
$symbols | Add-Content $baseline

Write-Host "  $($symbols.Count) public symbols recorded." -ForegroundColor Green

# Verify: a clean build now proves the baseline matches the surface exactly.
$verify = dotnet build $Project -c Release -t:Rebuild 2>&1 | Out-String
if ($verify -match 'RS0016|RS0017') {
    throw 'Baseline regeneration did not converge; RS0016/RS0017 remain.'
}

Write-Host '  Verified: baseline matches the compiled surface.' -ForegroundColor Green
Write-Host 'Review the diff before committing — it IS the API change review.' -ForegroundColor Yellow
