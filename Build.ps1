param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'src'
[xml]$manifest = Get-Content -Raw -LiteralPath (Join-Path $source 'app.manifest')
$version = [version]$manifest.assembly.assemblyIdentity.version
$display = if ($version.Revision -eq 0) { $version.ToString(3) } else { $version.ToString() }
function Sync-VersionText($path, $pattern, $replacement) {
    $content = [IO.File]::ReadAllText($path)
    if ([regex]::Matches($content, $pattern).Count -ne 1) { throw "无法同步版本号 $path" }
    $updated = [regex]::Replace($content, $pattern, $replacement)
    if ($updated -cne $content) { [IO.File]::WriteAllText($path, $updated) }
}
Sync-VersionText (Join-Path $source 'AssemblyInfo.cs') 'const string Number = "[\d.]+"' ('const string Number = "' + $version + '"')
Sync-VersionText (Join-Path $PSScriptRoot 'README.md') '\A# SteamDirect [\d.]+' ("# SteamDirect " + $display)
$output = Join-Path $PSScriptRoot 'dist'
$options = @('/optimize+')
if ($Configuration -eq 'Debug') {
    $output = Join-Path $output 'debug'
    $options = @('/optimize-', '/debug:full', '/define:DEBUG,TRACE')
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 和 .NET Framework 4.8。' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | ForEach-Object FullName)
& $compiler /nologo /target:winexe /platform:x64 @options /warnaserror+ "/win32manifest:$source\app.manifest" "/win32icon:$source\groundhog.ico" "/resource:$source\mascot.png,SteamDirect.Mascot" "/resource:$source\app.config,SteamDirect.HighDpiConfig" "/out:$output\SteamDirect.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll /r:System.Security.dll @sources
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Write-Output (Join-Path $output 'SteamDirect.exe')
