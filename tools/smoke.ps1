[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Executable)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$directory = Join-Path (Get-Location) ('artifacts\smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$process = Start-Process -FilePath $executablePath -ArgumentList @('--isolated', '--smoke-test', '--data-dir', "`"$directory`"") -PassThru
if (-not $process.WaitForExit(45000)) {
    Stop-Process -Id $process.Id
    throw "Synthetic UI smoke timed out. Diagnostics: $directory"
}
$result = Join-Path $directory 'smoke-result.txt'
if (-not (Test-Path -LiteralPath $result)) { throw "No UI smoke result (exit $($process.ExitCode)); check the shared Windows App Runtime prerequisite." }
Get-Content -LiteralPath $result
if ($process.ExitCode -ne 0 -or (Get-Content -LiteralPath $result -Raw) -notlike 'PASS:*') { throw 'Synthetic UI smoke failed.' }
$logs = @(Get-ChildItem -LiteralPath (Join-Path $directory 'logs') -File -Filter 'camera-light-tray-*.log')
if (-not $logs.Count) { throw 'Native UI did not persist diagnostic logs.' }
$text = ($logs | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
foreach ($required in @('Application starting', 'Activity transition', 'Light write requested', 'Session stopped', 'Application stopped')) {
    if (-not $text.Contains($required)) { throw "Native diagnostic log missing: $required" }
}
