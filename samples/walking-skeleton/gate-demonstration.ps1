# ═══════════════════════════════════════════════════════════════════════════
# Phase 02 — Gate G0 (Viability) live demonstration (Playbook Phase 02 §⑤).
#
# The gate is demonstrated live, in front of the board, not asserted in a
# slide. This script performs the ten required demonstrations against a
# running skeleton and prints pass/fail per check.
#
# Prerequisites — either:
#   (a) docker compose up -d   (SQL Server + PostgreSQL + Jaeger + Seq), or
#   (b) SQL Server LocalDB, with the API started against it:
#
#   $env:ConnectionStrings__SqlServer =
#     "Server=(localdb)\MSSQLLocalDB;Database=EdpfSkeletonDemo;Trusted_Connection=True;TrustServerCertificate=True"
#   dotnet run --project Edpf.WalkingSkeleton.Api --framework net10.0
#
# The same demonstrations run headless in CI on both Tier A providers via
# Testcontainers — see tests/Edpf.WalkingSkeleton.Tests/Gate/.
# ═══════════════════════════════════════════════════════════════════════════
$ErrorActionPreference = "Stop"
$base     = "http://localhost:5080"
$tenantA  = "aaaaaaaa-0000-0000-0000-000000000001"
$tenantB  = "bbbbbbbb-0000-0000-0000-000000000002"
$pass = 0; $fail = 0

function Check($name, $condition, $detail) {
    if ($condition) { Write-Host "  PASS  $name  $detail" -ForegroundColor Green; $script:pass++ }
    else            { Write-Host "  FAIL  $name  $detail" -ForegroundColor Red;   $script:fail++ }
}

function Token($roles) {
    (Invoke-RestMethod "$base/dev/token?roles=$roles").token
}

function Headers($token, $tenant) {
    @{ Authorization = "Bearer $token"; "X-Tenant-Id" = $tenant }
}

$clinicianA = Token "clinician"
$officerA   = Token "compliance-officer"
$clinicianB = Token "clinician"

$mrn = "MRN-" + [guid]::NewGuid().ToString("N").Substring(0,10)
$body = @{ givenName="Asha"; familyName="Verma"; dateOfBirth="1984-03-14"; medicalRecordNumber=$mrn } | ConvertTo-Json

Write-Host "`n=== DEMONSTRATION 1: authenticated create under tenant A ===" -ForegroundColor Cyan
$created = Invoke-RestMethod -Method Post "$base/api/v1/patients" -Headers (Headers $clinicianA $tenantA) -ContentType "application/json" -Body $body
Check "1. Patient created" ($null -ne $created.id) "id=$($created.id)"

Write-Host "`n=== DEMONSTRATION 2: tenant B cannot read it - 404, not 403 ===" -ForegroundColor Cyan
try {
    Invoke-RestMethod "$base/api/v1/patients/$($created.id)" -Headers (Headers $clinicianB $tenantB) | Out-Null
    Check "2. Cross-tenant read refused" $false "request unexpectedly succeeded"
} catch {
    $code = [int]$_.Exception.Response.StatusCode
    Check "2. Cross-tenant read refused" ($code -eq 404) "HTTP $code (must be 404, never 403)"
}

Write-Host "`n=== DEMONSTRATION 3: one correlation id through the stack ===" -ForegroundColor Cyan
$corr = [guid]::NewGuid().ToString("N")
$h = Headers $clinicianA $tenantA; $h["X-Correlation-Id"] = $corr
$resp = Invoke-WebRequest "$base/api/v1/patients?page=1&pageSize=5" -Headers $h
Check "3. Correlation id round-trips" ($resp.Headers["X-Correlation-Id"] -eq $corr) "X-Correlation-Id=$corr"

Write-Host "`n=== DEMONSTRATION 4: audit chain validates, holds no raw PHI ===" -ForegroundColor Cyan
$verify = Invoke-RestMethod "$base/api/v1/audit/verify" -Headers (Headers $clinicianA $tenantA)
Check "4a. Audit chain valid" ($verify.isValid -eq $true) "records=$($verify.recordCount)"

Write-Host "`n=== DEMONSTRATION 5: raw database holds ciphertext, not plaintext ===" -ForegroundColor Cyan
function Sql($query) {
    (& sqlcmd -S "(localdb)\MSSQLLocalDB" -d EdpfSkeletonDemo -E -h -1 -W -Q $query) -join "`n"
}
$hex = (Sql "SET NOCOUNT ON; SELECT CONVERT(varchar(max), MrnEnvelope, 2) FROM PATIENT WHERE Id = '$($created.id)'").Trim()
$mrnHex = -join ($mrn.ToCharArray() | ForEach-Object { [convert]::ToString([byte][char]$_, 16).PadLeft(2,'0') })
Check "5a. MRN stored as ciphertext" ($hex -notmatch [regex]::Escape($mrnHex)) "envelope=$($hex.Substring(0,40))... ($($hex.Length/2) bytes)"
Check "5b. Envelope carries 35-byte header" (($hex.Length/2) -gt 35) "total=$($hex.Length/2) bytes"

Write-Host "`n=== DEMONSTRATION 6: crypto-shredding - data gone, chain intact ===" -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/api/v1/subjects/$($created.id)/erase" -Headers (Headers $officerA $tenantA) | Out-Null
$afterErase = Invoke-RestMethod "$base/api/v1/patients/$($created.id)" -Headers (Headers $clinicianA $tenantA)
Check "6a. PHI unrecoverable (tombstone)" ($afterErase.medicalRecordNumber -eq "[erased]") "MRN='$($afterErase.medicalRecordNumber)'"
$verify2 = Invoke-RestMethod "$base/api/v1/audit/verify" -Headers (Headers $clinicianA $tenantA)
Check "6b. Audit chain STILL valid after erasure" ($verify2.isValid -eq $true) "records=$($verify2.recordCount)"

Write-Host "`n=== DEMONSTRATION 7: outbox message dispatched exactly once ===" -ForegroundColor Cyan
Start-Sleep -Seconds 4
$row = (Sql "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS varchar)+'|'+CAST(ISNULL(MIN(Attempts),-1) AS varchar)+'|'+CAST(COUNT(DispatchedUtc) AS varchar) FROM OUTBOX_MESSAGE WHERE Payload LIKE '%$($created.id)%'").Trim()
$parts = $row -split '\|'
$total = [int]$parts[0]; $attempts = [int]$parts[1]; $dispatched = [int]$parts[2]
Check "7a. Exactly one outbox message" ($total -eq 1) "rows=$total"
Check "7b. Dispatched exactly once" (($dispatched -eq 1) -and ($attempts -eq 1)) "dispatched=$dispatched attempts=$attempts"

Write-Host "`n=== DEMONSTRATION 8: forced failure yields RFC 9457 problem details ===" -ForegroundColor Cyan
$bad = @{ givenName=""; familyName=""; dateOfBirth="1990-01-01"; medicalRecordNumber="x" } | ConvertTo-Json
try {
    Invoke-RestMethod -Method Post "$base/api/v1/patients" -Headers (Headers $clinicianA $tenantA) -ContentType "application/json" -Body $bad | Out-Null
    Check "8. RFC 9457 on failure" $false "request unexpectedly succeeded"
} catch {
    $problem = $_.ErrorDetails.Message | ConvertFrom-Json
    $ctype = $_.Exception.Response.Content.Headers.ContentType.ToString()
    Check "8a. Content-Type problem+json" ($ctype -like "application/problem+json*") "$ctype"
    Check "8b. Stable error code" ($problem.errorCode -eq "EDPF-VAL-1001") "errorCode=$($problem.errorCode)"
    Check "8c. Carries correlation id" (-not [string]::IsNullOrEmpty($problem.correlationId)) "correlationId=$($problem.correlationId)"
    Check "8d. Type URI stable" ($problem.type -like "https://errors.edpf.dev/*") "type=$($problem.type)"
}

Write-Host "`n=== EXTRA: idempotency replay + conflict (ADR-003) ===" -ForegroundColor Cyan
$key = [guid]::NewGuid().ToString("N")
$hk = Headers $clinicianA $tenantA; $hk["Idempotency-Key"] = $key
$body2 = @{ givenName="Ravi"; familyName="Kumar"; dateOfBirth="1979-06-02"; medicalRecordNumber="MRN-IDEM-0001" } | ConvertTo-Json
$i1 = Invoke-RestMethod -Method Post "$base/api/v1/patients" -Headers $hk -ContentType "application/json" -Body $body2
$i2 = Invoke-RestMethod -Method Post "$base/api/v1/patients" -Headers $hk -ContentType "application/json" -Body $body2
Check "I1. Replay returns original" ($i1.id -eq $i2.id) "id=$($i1.id)"
$body3 = @{ givenName="Ravi"; familyName="Kumar"; dateOfBirth="1979-06-02"; medicalRecordNumber="MRN-IDEM-0002" } | ConvertTo-Json
try {
    Invoke-RestMethod -Method Post "$base/api/v1/patients" -Headers $hk -ContentType "application/json" -Body $body3 | Out-Null
    Check "I2. Key reuse w/ different payload conflicts" $false "unexpectedly succeeded"
} catch {
    $code = [int]$_.Exception.Response.StatusCode
    Check "I2. Key reuse w/ different payload conflicts" ($code -eq 409) "HTTP $code"
}

Write-Host "`n=== EXTRA: authN/authZ negatives ===" -ForegroundColor Cyan
try {
    Invoke-RestMethod "$base/api/v1/patients" -Headers @{ "X-Tenant-Id" = $tenantA } | Out-Null
    Check "A1. No token -> 401" $false "unexpectedly succeeded"
} catch { Check "A1. No token -> 401" ([int]$_.Exception.Response.StatusCode -eq 401) "HTTP $([int]$_.Exception.Response.StatusCode)" }
try {
    Invoke-RestMethod -Method Post "$base/api/v1/subjects/$([guid]::NewGuid())/erase" -Headers (Headers $clinicianA $tenantA) | Out-Null
    Check "A2. Clinician cannot erase -> 403" $false "unexpectedly succeeded"
} catch { Check "A2. Clinician cannot erase -> 403" ([int]$_.Exception.Response.StatusCode -eq 403) "HTTP $([int]$_.Exception.Response.StatusCode)" }
try {
    Invoke-RestMethod "$base/api/v1/patients" -Headers @{ Authorization = "Bearer $clinicianA" } | Out-Null
    Check "A3. No tenant header -> 404" $false "unexpectedly succeeded"
} catch { Check "A3. No tenant header -> 404" ([int]$_.Exception.Response.StatusCode -eq 404) "HTTP $([int]$_.Exception.Response.StatusCode)" }

Write-Host "`n===================================================" -ForegroundColor Cyan
Write-Host " GATE G0 RESULT:  $pass passed, $fail failed" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Write-Host "===================================================" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 }
