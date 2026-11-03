[CmdletBinding()]
param([string] $Version = '0.1.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Version must be numeric major.minor.patch.' }
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $output = Join-Path (Get-Location) "artifacts\package-$Version"
    if (Test-Path -LiteralPath $output) { throw "Output already exists: $output. Use a fresh version or remove only that generated directory explicitly." }
    dotnet publish src\CameraLightTray.App -c Release -r win-x64 "-p:Version=$Version" -o $output --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT UI publish failed.' }
    dotnet publish src\CameraLightTray.Monitor -c Release -r win-x64 "-p:Version=$Version" -o "$output\monitor" --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT monitor publish failed.' }
    Copy-Item tools\setup-monitor.ps1 $output
    Copy-Item README.md $output
    Copy-Item LICENSE $output
    $bundled = @(Get-ChildItem $output -Recurse -File | Where-Object {
        $_.Name -match '^(Microsoft\.UI\.Xaml\.dll|Microsoft\.WindowsAppRuntime\.dll|coreclr\.dll|hostfxr\.dll)$'
    })
    if ($bundled.Count) { throw "Shared-runtime package unexpectedly bundles runtime files: $($bundled.Name)" }
    if (-not (Test-Path "$output\CameraLightTray.exe") -or
        -not (Test-Path "$output\monitor\CameraLightTray.Monitor.exe")) { throw 'Native executable missing.' }
    $symbols = Join-Path (Get-Location) "artifacts\symbols-$Version"
    if (Test-Path -LiteralPath $symbols) { throw "Symbols output already exists: $symbols." }
    New-Item -ItemType Directory -Path $symbols -Force | Out-Null
    Get-ChildItem $output -Recurse -Filter '*.pdb' | ForEach-Object {
        $relative = $_.FullName.Substring($output.Length).TrimStart('\')
        $target = Join-Path $symbols $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Move-Item -LiteralPath $_.FullName -Destination $target
    }
    $zip = "artifacts\CameraLightTray-$Version-win-x64.zip"
    Compress-Archive -Path "$output\*" -DestinationPath $zip
    Compress-Archive -Path "$symbols\*" -DestinationPath "artifacts\CameraLightTray-$Version-win-x64-symbols.zip"
    Get-ChildItem artifacts -File -Filter "CameraLightTray-$Version-*.zip" | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    } | Set-Content "artifacts\CameraLightTray-$Version-SHA256SUMS.txt" -Encoding ascii
    Write-Output "Unsigned development spike: $zip. Requires shared Windows App Runtime 2.5+ (compatible 2.x)."
}
finally { Pop-Location }
