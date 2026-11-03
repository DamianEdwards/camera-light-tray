$ErrorActionPreference = 'Stop'
$assetsPath = Join-Path $PSScriptRoot '..\src\CameraLightTray.App\obj\project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$library = @($assets.libraries.PSObject.Properties | Where-Object Name -eq 'Microsoft.WindowsAppSDK.Runtime/2.5.1')
if ($library.Count -ne 1) { throw 'Restore/build the app first; expected shared Runtime 2.5.1 dependency.' }
$package = $null
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $folder ($library[0].Value.path.Replace('/', '\') + '\tools\MSIX\win10-x64\Microsoft.WindowsAppRuntime.2.msix')
    if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
}
if (-not $package) { throw 'Resolved shared runtime framework MSIX is missing.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $identity = $manifest.Package.Identity
    if ($identity.Name -ne 'Microsoft.WindowsAppRuntime.2' -or
        $identity.Publisher -ne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
        $identity.ProcessorArchitecture -ne 'x64' -or $manifest.Package.Properties.Framework -ne 'true') {
        throw 'Unexpected shared runtime package identity.'
    }
    [pscustomobject]@{ Name = $identity.Name; Version = $identity.Version; PackagePath = $package }
}
finally { $archive.Dispose() }
