[CmdletBinding()]
param(
    [string] $PackageDirectory = "$PSScriptRoot\..\artifacts\package-0.1.0",
    [string] $ProbeExecutable = "$PSScriptRoot\..\artifacts\native-tests\CameraLightTray.Tests.exe",
    [switch] $CleanupOnly
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Machine-changing integration checks run only on disposable GitHub-hosted runners, never a developer machine.'
}
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$setup = Join-Path $package 'setup-monitor.ps1'
$output = Join-Path $env:GITHUB_WORKSPACE 'artifacts\monitor-integration'
$receipt = Join-Path $output 'owned.txt'
$userName = 'CltMonitorTest'
$serviceName = 'CameraLightTrayMonitor'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$process = $null

function Remove-TestResources {
    if (-not (Test-Path -LiteralPath $receipt)) { return }
    $failures = @()
    try {
        & $powershell -NoProfile -File $setup -Remove
        if ($LASTEXITCODE -ne 0) { throw 'Actual setup removal failed.' }
        if (Get-CimInstance Win32_Service -Filter "Name='$serviceName'") { throw 'Service remains after removal.' }
        if (Test-Path (Join-Path $env:ProgramFiles 'CameraLightTray\Monitor\CameraLightTray.Monitor.exe')) {
            throw 'Installed helper executable remains after removal.'
        }
    } catch { $failures += $_.Exception.Message }
    try {
        if (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $userName }
        if (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue) { throw 'Disposable standard-user account remains.' }
    } catch { $failures += $_.Exception.Message }
    if ($failures.Count) { throw ($failures -join ' ') }
    Remove-Item -LiteralPath $receipt
    Write-Output 'PASS: helper/service and disposable standard-user account removed.'
}
if ($CleanupOnly) { Remove-TestResources; return }
if ((Get-CimInstance Win32_Service -Filter "Name='$serviceName'") -or
    (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue) -or
    (Test-Path (Join-Path $env:ProgramFiles 'CameraLightTray'))) {
    throw 'Disposable integration runner unexpectedly contains existing test resources; refusing to replace them.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
Set-Content -LiteralPath $receipt -Value 'Disposable integration owns the fixed CameraLightTray service and CltMonitorTest account.'
try {
    & $powershell -NoProfile -File $setup
    if ($LASTEXITCODE -ne 0) { throw 'Actual published setup failed.' }
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($service.State -ne 'Running' -or $service.StartName -ne 'NT AUTHORITY\LocalService') {
        throw 'Published helper is not running as LocalService.'
    }
    $password = ConvertTo-SecureString ('aA1!' + [Guid]::NewGuid().ToString('N')) -AsPlainText -Force
    $user = New-LocalUser -Name $userName -Password $password -Description 'Camera Light Tray disposable integration probe'
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $user
    $acl = Get-Acl -LiteralPath $output
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $user.SID, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $output -AclObject $acl
    $result = Join-Path $output 'result.txt'
    $credential = New-Object PSCredential(".\$userName", $password)
    $probe = (Resolve-Path -LiteralPath $ProbeExecutable).Path
    $process = Start-Process -FilePath $probe -Credential $credential -LoadUserProfile `
        -ArgumentList @('--monitor-integration', "`"$result`"") -WorkingDirectory $output -PassThru
    # Windows PowerShell needs the credentialed child's handle cached before exit.
    $null = $process.Handle
    if (-not $process.WaitForExit(45000)) { throw 'Standard-user integration probe timed out.' }
    if (-not (Test-Path -LiteralPath $result)) { throw "Probe produced no result (exit $($process.ExitCode))." }
    $text = Get-Content -LiteralPath $result -Raw
    Write-Output $text
    if ($null -eq $process.ExitCode) { throw 'Standard-user probe exit code unavailable.' }
    if ($process.ExitCode -ne 0 -or $text -notlike 'PASS:*') {
        throw "Installed helper integration failed (probe exit $($process.ExitCode))."
    }
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id }
    Remove-TestResources
}
