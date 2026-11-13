param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'src'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $root 'dist\VisualSmoke.exe'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist\visual-native' }
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | ForEach-Object FullName)
& $compiler /nologo /target:exe /main:SteamDirect.VisualSmoke /platform:x64 /optimize+ /warnaserror+ "/out:$output" "/win32manifest:$source\app.manifest" "/resource:$source\mascot.png,SteamDirect.Mascot" "/resource:$source\app.config,SteamDirect.HighDpiConfig" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll /r:System.Security.dll @sources (Join-Path $PSScriptRoot 'VisualSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Visual fixture compilation failed.' }
Copy-Item -LiteralPath (Join-Path $source 'app.config') -Destination ($output + '.config') -Force
& $output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Offline visual acceptance failed.' }
