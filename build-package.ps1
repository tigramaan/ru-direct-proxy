$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$taskRoot = (git rev-parse --show-toplevel).Replace('/', '\')
if ($LASTEXITCODE -ne 0 -or -not [string]::Equals($taskRoot,$PSScriptRoot,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build must run from the root of this Git checkout.'
}
& (Join-Path $PSScriptRoot 'fetch-dependencies.ps1')
$taskBundle = Join-Path $PSScriptRoot 'build\BrowserCountryProxy'
$taskBin = Join-Path $taskBundle 'bin'
New-Item -ItemType Directory -Path $taskBin -Force | Out-Null
foreach ($taskName in @('INSTALL.bat','ENABLE.bat','DISABLE.bat','CHECK.bat','README.txt','THIRD_PARTY.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination (Join-Path $taskBundle $taskName) -Force
}
foreach ($taskName in @('sing-box.exe','geoip-ru.srs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "vendor\$taskName") -Destination (Join-Path $taskBin $taskName) -Force
}
$taskNotice = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'vendor\sing-box-LICENSE.txt'))
$taskLicense = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'vendor\GPL-3.0.txt'))
[IO.File]::WriteAllText((Join-Path $taskBin 'sing-box-LICENSE.txt'), $taskNotice + "`r`n`r`n" + $taskLicense, (New-Object Text.UTF8Encoding($false)))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY.txt') -Destination (Join-Path $taskBin 'THIRD_PARTY.txt') -Force
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $taskCompiler /nologo /target:winexe /platform:x64 /codepage:65001 /optimize+ /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll "/out:$taskBin\CountryProxy.exe" (Join-Path $PSScriptRoot 'CountryProxy.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
$taskExpected = @('INSTALL.bat','ENABLE.bat','DISABLE.bat','CHECK.bat','README.txt','THIRD_PARTY.txt','bin\CountryProxy.exe','bin\sing-box.exe','bin\geoip-ru.srs','bin\sing-box-LICENSE.txt','bin\THIRD_PARTY.txt')
$taskActual = @(Get-ChildItem -LiteralPath $taskBundle -File -Recurse | ForEach-Object {$_.FullName.Substring($taskBundle.Length + 1)})
if (Compare-Object ($taskExpected | Sort-Object) ($taskActual | Sort-Object)) { throw 'Unexpected payload files.' }
$taskZip = Join-Path $PSScriptRoot 'build\ru-direct-proxy-v0.1.0-windows-x64.zip'
Compress-Archive -LiteralPath $taskBundle -DestinationPath $taskZip -CompressionLevel Optimal -Force
$taskDigest = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($taskZip + '.sha256.txt',$taskDigest + '  ' + [IO.Path]::GetFileName($taskZip) + "`r`n",(New-Object Text.UTF8Encoding($false)))
Get-Item -LiteralPath $taskZip | Select-Object FullName,Length
