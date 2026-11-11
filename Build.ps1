$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'src'
$output = Join-Path $PSScriptRoot 'dist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 和 .NET Framework 4.8。' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | ForEach-Object FullName)
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ "/win32manifest:$source\app.manifest" "/win32icon:$source\groundhog.ico" "/resource:$source\mascot.png,SteamDirect.Mascot" "/out:$output\SteamDirect.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll @sources
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Write-Output (Join-Path $output 'SteamDirect.exe')
