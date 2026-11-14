$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows x64 and .NET Framework 4.8 are required.' }
$output = Join-Path $root 'dist\SteamDirect.Tests.exe'
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath $source, $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName)
& $compiler /nologo /target:exe /main:SteamDirect.CoreTests /platform:x64 /optimize+ /warnaserror+ "/out:$output" "/resource:$source\mascot.png,SteamDirect.Mascot" "/resource:$source\app.config,SteamDirect.HighDpiConfig" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll /r:System.Security.dll @sources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
Copy-Item -LiteralPath (Join-Path $source 'app.config') -Destination ($output + '.config') -Force
& $output
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
