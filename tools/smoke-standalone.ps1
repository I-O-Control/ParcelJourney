param([string]$Executable = (Join-Path $PSScriptRoot '../standalone/ParcelJourney.exe'))
$ErrorActionPreference = 'Stop'
$appPath = $Executable
$process = Start-Process -FilePath $appPath -ArgumentList '--no-browser' -WindowStyle Hidden -PassThru
$sessionPath = Join-Path ([IO.Path]::GetTempPath()) "parceljourney-$($process.Id).url"
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (!(Test-Path -LiteralPath $sessionPath)) {
        if ($process.HasExited) { throw 'Application exited before becoming ready' }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Application startup timed out' }
        Start-Sleep -Milliseconds 200
    }
    $address = Get-Content -LiteralPath $sessionPath -Raw
    $health = Invoke-RestMethod "$address/api/health"
    if (!$health.ready) { throw 'Health check failed' }
    if ($health.mode -ne 'real-logs') { throw 'Release is not using real logs' }
    $model = Invoke-RestMethod "$address/3d/model.json"
    if ($model.parcels.Count -ne 10 -or $model.validation.mode -ne 'real-logs') { throw '3D model is not the real 10-case dataset' }
    if ($model.parcels[0].events[0].Evidence.Count -lt 1) { throw 'Source evidence missing' }
    $page = Invoke-WebRequest $address -UseBasicParsing
    if ($page.StatusCode -ne 200 -or !$page.Content.Contains('const ReplayEngine') -or !$page.Content.Contains('0015698027')) { throw 'Razor page missing bundled replay' }
    if (!$page.Content.Contains('function setRotation') -or !$page.Content.Contains('SC_START')) { throw 'Rotation update not bundled' }
    if ($page.Content.Contains('class="mission"') -and !$health.runtime.StartsWith('8.')) { throw 'Corporate release must align with IocOrchestrator .NET 8' }
    if (!$page.Headers['Content-Security-Policy']) { throw 'Missing security headers' }
    $blocked = $false
    try { Invoke-RestMethod "$address/api/exit" -Method Post | Out-Null } catch { $blocked = $_.Exception.Response.StatusCode.value__ -eq 403 }
    if (!$blocked) { throw 'Exit endpoint accepted an unauthenticated request' }
    $session = Invoke-RestMethod "$address/api/session"
    Invoke-RestMethod "$address/api/exit" -Method Post -Headers @{'X-ParcelJourney-Session'=$session.token} | Out-Null
    if (!$process.WaitForExit(10000)) { throw 'Application failed to shut down' }
    Write-Output "PASS: self-contained EXE starts; .NET $($health.runtime); Razor page HTTP 200; bundled 10 real parcels; protected exit; clean shutdown."
} finally {
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
