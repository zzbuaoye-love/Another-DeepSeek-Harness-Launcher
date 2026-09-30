[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$WorkDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'AnotherDSHL.csproj') -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
$workRoot = if ([string]::IsNullOrWhiteSpace($WorkDirectory)) { Join-Path $repo 'installer\.work' } else {
    if (![IO.Path]::IsPathFullyQualified($WorkDirectory)) { throw 'WorkDirectory must be an absolute path.' }
    [IO.Path]::GetFullPath($WorkDirectory)
}
$work = Join-Path $workRoot ([Guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $repo "installer\artifacts\$version-win-x64"
$app = Join-Path $work 'application'
$ui = Join-Path $work 'ui'
$payload = Join-Path $work 'payload'
New-Item -ItemType Directory -Path $work,$artifacts,$payload -Force | Out-Null
function Publish-Project([string]$Project, [string]$Output) {
    & dotnet publish $Project -c $Configuration -r win-x64 -p:Platform=x64 --self-contained true -p:PublishReadyToRun=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $Output --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project" }
}
Publish-Project (Join-Path $repo 'AnotherDSHL.csproj') $app
Publish-Project (Join-Path $repo 'installer\src\Installer.WinUI\Installer.WinUI.csproj') $ui
Copy-Item -Path "$app\*" -Destination $payload -Recurse
foreach ($file in Get-ChildItem -LiteralPath $ui -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($ui, $file.FullName)
    $target = Join-Path $payload $relative
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Shared dependency mismatch: $relative" }
    } else {
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'THIRD_PARTY_NOTICES.md') -Destination $payload
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ Path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\','/'); Size = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
@{ Product = 'AnotherDSHL'; Version = $version; Files = $files } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'installation.json') -Encoding utf8
# LZX cabinet plus a tiny native entry point: one offline EXE, one copy of shared runtimes.
$cab = Join-Path $work 'payload.cab'
$ddf = @('.OPTION EXPLICIT','.Set Cabinet=ON','.Set Compress=ON','.Set CompressionType=LZX','.Set CompressionMemory=21','.Set MaxDiskSize=0','.Set MaxCabinetSize=0',('.Set DiskDirectoryTemplate="' + $work + '"'),'.Set CabinetNameTemplate=payload.cab',('.Set RptFileName="' + $work + '\payload.rpt"'),('.Set InfFileName="' + $work + '\payload.inf"'))
$ddf += Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object { '"' + $_.FullName + '" "' + [IO.Path]::GetRelativePath($payload, $_.FullName) + '"' }
$ddfPath = Join-Path $work 'payload.ddf'
$ddf | Set-Content -LiteralPath $ddfPath -Encoding utf8
& makecab.exe /F $ddfPath | Out-File (Join-Path $work 'makecab.log')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $cab)) { throw "Cabinet creation failed; inspect $work\makecab.log" }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Install Visual Studio C++ Build Tools to compile the native bootstrap.' }
$bootstrap = Join-Path $work 'Bootstrap.exe'
$resource = Join-Path $work 'Bootstrap.rc'
('1 ICON "' + (Join-Path $repo 'Assets\AppIcon.ico').Replace('\','\\') + '"') | Set-Content -LiteralPath $resource -Encoding ascii
$compile = Join-Path $work 'compile.cmd'
@('@echo off',('call "' + $vs + '\VC\Auxiliary\Build\vcvars64.bat" >nul'),
    ('rc /nologo /fo "' + $work + '\Bootstrap.res" "' + $resource + '"'), 'if errorlevel 1 exit /b 1',
    ('cl /nologo /std:c++17 /O1 /MT /EHsc /DUNICODE /D_UNICODE /utf-8 "' + $repo + '\installer\src\Bootstrap\Bootstrap.cpp" "' + $work + '\Bootstrap.res" /Fo"' + $work + '\Bootstrap.obj" /Fe"' + $bootstrap + '" /link /SUBSYSTEM:WINDOWS /MANIFEST:EMBED /MANIFESTINPUT:"' + $repo + '\app.manifest" setupapi.lib ole32.lib shell32.lib user32.lib')) | Set-Content -LiteralPath $compile -Encoding ascii
& $env:ComSpec /d /c $compile
if ($LASTEXITCODE -ne 0) { throw 'Native bootstrap compilation failed.' }
$setup = Join-Path $artifacts "AnotherDSHL-v$version-win-x64-Setup.exe"
Copy-Item -LiteralPath $bootstrap -Destination $setup -Force
$stream = [IO.File]::Open($setup, [IO.FileMode]::Append)
try {
    $input = [IO.File]::OpenRead($cab)
    try { $input.CopyTo($stream) } finally { $input.Dispose() }
    $writer = [IO.BinaryWriter]::new($stream)
    $writer.Write([uint64](Get-Item -LiteralPath $cab).Length)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('ADL_CAB_PAYLOAD1'))
    $writer.Flush()
} finally { $stream.Dispose() }
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii
[pscustomobject]@{ Setup = $setup; SetupMiB = [math]::Round((Get-Item $setup).Length / 1MB, 2); InstalledMiB = [math]::Round(($files | Measure-Object Size -Sum).Sum / 1MB, 2); Payload = $payload; Work = $work } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $artifacts 'build-info.json')
