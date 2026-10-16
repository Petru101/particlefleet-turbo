# Windows test of the release zip, kept away from Steam and Steam Cloud:
# - copies the Steam install of the game to C:\PFTest\game and starts it directly with steam_appid.txt = 480
#   (Valve's test app), so it never runs as Particle Fleet through Steam;
# - installs the release zip into the copy the way INSTALL.txt says;
# - redirects the game's data folder to C:\PFTest\data, and checks (and restores if needed) the real,
#   cloud-synced Documents\My Games\particlefleet afterwards;
# - auto-runs the self-test with the stress save and writes C:\PFTest\summary.txt.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$zip = Get-ChildItem "$here\ParticleFleetTurbo-*.zip" | Select-Object -First 1
$root = 'C:\PFTest'
$game = "$root\game"
$docs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\particlefleet'
$docsBackup = "$root\docs-backup"

$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath -replace '/', '\'
$libraries = @($steam) + (Select-String -Path "$steam\steamapps\libraryfolders.vdf" -Pattern '"path"\s+"(.+)"' |
    ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' })
$steamGame = $libraries | ForEach-Object { Join-Path $_ 'steamapps\common\Particle Fleet Emergence' } |
    Where-Object { Test-Path "$_\ParticleFleet.exe" } | Select-Object -First 1
if (-not $steamGame) { throw 'Particle Fleet Emergence not found in any Steam library' }

New-Item -ItemType Directory -Force $root | Out-Null
foreach ($dir in $game, "$root\data") { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
Write-Host "Copying $steamGame to $game ..."
robocopy $steamGame $game /E /NFL /NDL /NJH /NJS /NP | Out-Null
Set-Content "$game\steam_appid.txt" '480' -NoNewline -Encoding ascii

Expand-Archive -Force $zip.FullName $game
Copy-Item "$here\stress.pf1" "$root\stress.pf1" -Force
Set-Content "$game\BepInEx\config\petru.particlefleet.turbo.cfg" -Encoding ascii -Value (
    "[SelfTest]`r`nAutoRunSave = C:/PFTest/stress.pf1`r`nBrokenCacheRun = true`r`nDataDir = C:/PFTest/data`r`n")

function Fingerprint($dir) {
    if (-not (Test-Path $dir)) { return @() }
    Get-ChildItem $dir -Recurse -File | ForEach-Object {
        "$($_.FullName.Substring($dir.Length)) $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)" } | Sort-Object
}
if (Test-Path $docsBackup) { Remove-Item -Recurse -Force $docsBackup }
if (Test-Path $docs) { robocopy $docs $docsBackup /E /NFL /NDL /NJH /NJS /NP | Out-Null }
$before = Fingerprint $docs

Write-Host 'Starting the game; it runs the self-test and closes by itself.'
$process = Start-Process "$game\ParticleFleet.exe" -WorkingDirectory $game -PassThru `
    -ArgumentList '-screen-fullscreen', '0', '-screen-width', '1024', '-screen-height', '640'
if (-not $process.WaitForExit(900000)) { $process.Kill(); $timedOut = $true }

$after = Fingerprint $docs
$docsChanged = @(Compare-Object $before $after).Count -gt 0
if ($docsChanged -and (Test-Path $docsBackup)) {
    robocopy $docsBackup $docs /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
}

function SaveHash($path) {
    $file = [IO.File]::OpenRead($path)
    $file.Position = 4
    $gzip = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionMode]::Decompress)
    $memory = New-Object IO.MemoryStream
    $gzip.CopyTo($memory)
    $file.Close()
    $memory.Position = 0
    (Get-FileHash -InputStream $memory -Algorithm SHA256).Hash.Substring(0, 16)
}

$tests = "$game\BepInEx\turbo-selftest"
$unityLog = "$game\ParticleFleet_Data\output_log.txt"
$summary = & {
    "Timed out and killed: $([bool]$timedOut)"
    "Real Documents\My Games\particlefleet changed during the run: $docsChanged$(if ($docsChanged) { ' (restored from backup)' })"
    '== results.txt'
    if (Test-Path "$tests\results.txt") { Get-Content "$tests\results.txt" } else { 'no results' }
    '== save contents (SHA-256 of decompressed save; runs 1-3 should match, run 4 should differ)'
    if (Test-Path $tests) { Get-ChildItem "$tests\run*.pf1" | ForEach-Object { "$($_.Name)  $(SaveHash $_.FullName)" } }
    '== BepInEx log'
    if (Test-Path "$game\BepInEx\LogOutput.log") {
        Select-String "$game\BepInEx\LogOutput.log" -Pattern 'BepInEx \d|Unity v|Turbo|redirected|Error|Exception' | ForEach-Object { $_.Line }
    } else { 'no BepInEx log' }
    '== Unity log'
    if (Test-Path $unityLog) {
        "missing scripts: $(@(Select-String $unityLog -Pattern 'is missing').Count)"
        "crash: $(@(Select-String $unityLog -Pattern 'Crash').Count)"
        "exceptions: $(@(Select-String $unityLog -Pattern 'Exception').Count)"
        Select-String $unityLog -Pattern 'Direct3D|Renderer:|Version:' | ForEach-Object { $_.Line.Trim() }
    } else { 'no Unity log' }
}
$summary | Set-Content "$root\summary.txt"
Start-Process notepad "$root\summary.txt"
