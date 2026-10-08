$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$taskVendor = Join-Path $PSScriptRoot 'vendor'
$taskDownloads = Join-Path $PSScriptRoot 'work\downloads'
New-Item -ItemType Directory -Path $taskVendor,$taskDownloads -Force | Out-Null
$taskManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-VerifiedDependency($taskUrl, $taskHash, $taskDestination) {
    if (Test-Path -LiteralPath $taskDestination) {
        if ((Get-FileHash -LiteralPath $taskDestination -Algorithm SHA256).Hash -eq $taskHash) {return}
        throw "Existing dependency has an unexpected SHA-256: $taskDestination"
    }
    $taskPartial = $taskDestination + '.partial'
    $taskClient = New-Object Net.WebClient
    $taskClient.Proxy = $null
    try {$taskClient.DownloadFile($taskUrl,$taskPartial)} finally {$taskClient.Dispose()}
    if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash -ne $taskHash) {
        throw "Downloaded dependency has an unexpected SHA-256: $taskUrl"
    }
    Move-Item -LiteralPath $taskPartial -Destination $taskDestination
}

$taskArchive = Join-Path $taskDownloads 'sing-box-windows-amd64.zip'
Get-VerifiedDependency $taskManifest.archive.url $taskManifest.archive.sha256 $taskArchive
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = [IO.Compression.ZipFile]::OpenRead($taskArchive)
try {
    $taskEntry = $taskZip.GetEntry($taskManifest.archive.entry)
    if (!$taskEntry) {throw 'The upstream archive does not contain the expected executable.'}
    $taskExe = Join-Path $taskVendor 'sing-box.exe'
    if (Test-Path -LiteralPath $taskExe) {
        $taskSha = [Security.Cryptography.SHA256]::Create()
        $taskStream = $taskEntry.Open()
        try {$taskExpectedHash = [BitConverter]::ToString($taskSha.ComputeHash($taskStream)).Replace('-','')} finally {$taskStream.Dispose();$taskSha.Dispose()}
        if ((Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash -ne $taskExpectedHash) {throw 'The existing sing-box executable differs from the verified upstream archive.'}
    } else {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry,$taskExe,$false)
    }
} finally {$taskZip.Dispose()}
foreach($taskFile in $taskManifest.files) {
    Get-VerifiedDependency $taskFile.url $taskFile.sha256 (Join-Path $taskVendor $taskFile.name)
}
Write-Output 'Dependencies verified.'
