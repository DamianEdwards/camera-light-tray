#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch] $Remove)
$ErrorActionPreference = 'Stop'
$serviceName = 'CameraLightTrayMonitor'
$root = Join-Path $env:ProgramFiles 'CameraLightTray'
$destination = Join-Path $root 'Monitor'
$executable = Join-Path $destination 'CameraLightTray.Monitor.exe'
$source = Join-Path $PSScriptRoot 'monitor\CameraLightTray.Monitor.exe'

function Assert-NotReparse([string] $Path) {
    if ((Test-Path -LiteralPath $Path) -and
        ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing reparse-point installation path: $Path"
    }
}
foreach ($path in @($env:ProgramFiles, $root, $destination, $executable)) { Assert-NotReparse $path }
$existing = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if ($existing -and ($existing.PathName -ne "`"$executable`"" -or $existing.StartName -ne 'NT AUTHORITY\LocalService')) {
    throw 'Existing service has an unexpected executable/account. Inspect it manually; setup will not repurpose it.'
}
if ($existing) {
    $service = Get-Service -Name $serviceName
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
}
if ($Remove) {
    if ($existing) {
        & sc.exe delete $serviceName
        if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
    }
    if (Test-Path -LiteralPath $executable) { Remove-Item -LiteralPath $executable -Force }
    Write-Output 'Monitor removed. Per-user preferences and the UI have not been deleted.'
    exit 0
}
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Use the complete published ZIP; monitor payload is missing.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
# Replace inherited permissions; only administrators/SYSTEM can change executable code.
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
foreach ($rule in @(
    @('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'),
    @('S-1-5-19', 'ReadAndExecute'), @('S-1-5-32-545', 'ReadAndExecute'))) {
    $sid = New-Object Security.Principal.SecurityIdentifier($rule[0])
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        $sid, $rule[1], 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -LiteralPath $root -AclObject $acl
Set-Acl -LiteralPath $destination -AclObject $acl
Copy-Item -LiteralPath $source -Destination $executable -Force
$fileAcl = New-Object Security.AccessControl.FileSecurity
$fileAcl.SetAccessRuleProtection($false, $false)
$fileAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
Set-Acl -LiteralPath $executable -AclObject $fileAcl
if (-not $existing) {
    & sc.exe create $serviceName "binPath= `"$executable`"" 'start= auto' 'obj= NT AUTHORITY\LocalService' 'DisplayName= Camera Light Tray Monitor'
    if ($LASTEXITCODE -ne 0) { throw 'Service creation failed.' }
}
& sc.exe description $serviceName 'Read-only Media Foundation camera lifecycle monitor; no camera frames, preferences or light networking.'
if ($LASTEXITCODE -ne 0) { throw 'Service description failed.' }
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Output 'Monitor installed/running as LocalService. Verify provider access in the app; no light automation has been enabled.'
