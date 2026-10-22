param(
    [Parameter(Mandatory = $true)][string]$Destination,
    [Parameter(Mandatory = $true)][string]$NodeExe,
    [Parameter(Mandatory = $true)][string]$ModuleRoot,
    [Parameter(Mandatory = $true)][string]$NodeLicensePath,
    [Parameter(Mandatory = $true)][string]$FridaLicensePath,
    [Parameter(Mandatory = $true)][string]$FridaLibraryLicensePath
)

$ErrorActionPreference = 'Stop'
# Copy only; no package installation, game access, deletion, or global changes.
$nativeDestination = [IO.Path]::GetFullPath($Destination)
$sourceModules = (Resolve-Path -LiteralPath $ModuleRoot).Path
$sourceNode = (Resolve-Path -LiteralPath $NodeExe).Path
$nodeLicense = (Resolve-Path -LiteralPath $NodeLicensePath).Path
$fridaLicense = (Resolve-Path -LiteralPath $FridaLicensePath).Path
$fridaLibraryLicense = (Resolve-Path -LiteralPath $FridaLibraryLicensePath).Path
$fridaManifest = Get-Content -LiteralPath (Join-Path $sourceModules 'frida\package.json') -Raw | ConvertFrom-Json
if ($fridaManifest.version -ne '17.23.2') { throw 'Expected the reviewed Frida 17.23.2 runtime.' }
$nodeVersion = & $sourceNode --version
if ($nodeVersion -ne 'v24.19.0') { throw "Expected reviewed Node v24.19.0; got $nodeVersion." }
if ((Get-Item -LiteralPath $nodeLicense).Length -lt 100 -or (Get-Item -LiteralPath $fridaLicense).Length -lt 100 -or (Get-Item -LiteralPath $fridaLibraryLicense).Length -lt 100) {
    throw 'Supply complete upstream Node and Frida license texts before packaging.'
}
$packages = @('frida', 'bindings', 'file-uri-to-path', 'minimatch', 'brace-expansion', 'balanced-match')
foreach ($name in $packages) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceModules "$name\package.json"))) { throw "Missing runtime dependency: $name" }
}

New-Item -ItemType Directory -Path $nativeDestination -Force | Out-Null
foreach ($name in @('agent.js', 'bridge.mjs')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $nativeDestination $name) -Force }
$runtime = Join-Path $nativeDestination 'runtime'
$modules = Join-Path $runtime 'node_modules'
$licenses = Join-Path $runtime 'licenses'
New-Item -ItemType Directory -Path $runtime, $modules, $licenses -Force | Out-Null
Copy-Item -LiteralPath $sourceNode -Destination (Join-Path $runtime 'node.exe') -Force
Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $licenses 'Node-LICENSE.txt') -Force
Copy-Item -LiteralPath $fridaLicense -Destination (Join-Path $licenses 'Frida-COPYING.txt') -Force
Copy-Item -LiteralPath $fridaLibraryLicense -Destination (Join-Path $licenses 'Frida-COPYING.LIB.txt') -Force
$manifestEntries = @()
foreach ($name in $packages) {
    $sourcePackage = Join-Path $sourceModules $name
    $targetPackage = Join-Path $modules $name
    New-Item -ItemType Directory -Path $targetPackage -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourcePackage 'package.json') -Destination $targetPackage -Force
    if ($name -eq 'frida') {
        $targetBuild = Join-Path $targetPackage 'build'
        $targetBuildSource = Join-Path $targetBuild 'src'
        New-Item -ItemType Directory -Path $targetBuildSource -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourcePackage 'build\frida_binding.node') -Destination $targetBuild -Force
        Copy-Item -LiteralPath (Join-Path $sourcePackage 'build\src\frida.js') -Destination $targetBuildSource -Force
        Copy-Item -LiteralPath $fridaLicense -Destination (Join-Path $targetPackage 'COPYING') -Force
        Copy-Item -LiteralPath $fridaLibraryLicense -Destination (Join-Path $targetPackage 'COPYING.LIB') -Force
    } elseif ($name -eq 'bindings') {
        Copy-Item -LiteralPath (Join-Path $sourcePackage 'bindings.js') -Destination $targetPackage -Force
    } elseif ($name -eq 'file-uri-to-path') {
        Copy-Item -LiteralPath (Join-Path $sourcePackage 'index.js') -Destination $targetPackage -Force
    } else {
        Copy-Item -LiteralPath (Join-Path $sourcePackage 'dist') -Destination $targetPackage -Recurse -Force
    }
    $packageLicenses = Get-ChildItem -LiteralPath $sourcePackage -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)' }
    if ($name -ne 'frida' -and $packageLicenses.Count -eq 0) { throw "No upstream license found for $name." }
    foreach ($file in $packageLicenses) { Copy-Item -LiteralPath $file.FullName -Destination $targetPackage -Force }
    $packageManifest = Get-Content -LiteralPath (Join-Path $sourcePackage 'package.json') -Raw | ConvertFrom-Json
    $manifestEntries += [pscustomobject]@{ name = $name; version = $packageManifest.version; license = $packageManifest.license }
}
$files = Get-ChildItem -LiteralPath $runtime -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path = [IO.Path]::GetRelativePath($runtime, $_.FullName); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[pscustomobject]@{ node = $nodeVersion; packages = $manifestEntries; files = @($files) } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runtime 'runtime-manifest.json') -Encoding utf8
Write-Output "Runtime copied to $runtime. No helper was launched or attached."
