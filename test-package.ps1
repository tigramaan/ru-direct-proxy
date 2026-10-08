$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$taskBuiltBin = Join-Path $PSScriptRoot 'build\BrowserCountryProxy\bin'
$taskTest = Join-Path $PSScriptRoot ('work\package test ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskTest -Force | Out-Null
foreach($taskName in @('CountryProxy.exe','sing-box.exe','geoip-ru.srs')) {
    Copy-Item -LiteralPath (Join-Path $taskBuiltBin $taskName) -Destination (Join-Path $taskTest $taskName)
}
$taskReport = Join-Path $taskTest 'self-test-result.json'
$taskArgs = '--self-test --quiet --result "' + $taskReport + '"'
$taskProcess = Start-Process -FilePath (Join-Path $taskTest 'CountryProxy.exe') -ArgumentList $taskArgs -WindowStyle Hidden -PassThru -Wait
if($taskProcess.ExitCode -ne 0) {
    if(Test-Path -LiteralPath $taskReport) {Get-Content -LiteralPath $taskReport -Raw | Write-Output}
    throw 'Self-test failed.'
}
$taskResult = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
foreach($taskField in @('github_tls','beget_imap_tls','beget_smtp_tls','routes_unchanged','windows_proxy_unchanged')) {
    if($taskResult.$taskField -ne $true) {throw "Self-test failed: $taskField"}
}
Write-Output 'PASS: TLS connections, unchanged routes and unchanged Windows proxy.'
Write-Output ('Local report: ' + $taskReport)
