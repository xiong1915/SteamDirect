param([string]$OutputDirectory, [string]$SourceDirectory, [switch]$Baseline)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=Join-Path $root 'src'
if($SourceDirectory){$source=$SourceDirectory}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'dist\render-acceptance'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output=Join-Path $OutputDirectory 'RenderAcceptance.exe'
$sources=@(Get-ChildItem $source -Filter '*.cs' -File | ForEach-Object FullName)
& $compiler /nologo /target:exe /main:SteamDirect.RenderAcceptance /platform:x64 /optimize+ /warnaserror+ "/win32manifest:$source\app.manifest" "/out:$output" "/resource:$source\mascot.png,SteamDirect.Mascot" "/resource:$source\app.config,SteamDirect.HighDpiConfig" "/resource:$source\wallpaper.jpg,SteamDirect.Wallpaper" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.ServiceProcess.dll /r:System.Security.dll @sources (Join-Path $PSScriptRoot 'RenderAcceptance.cs') (Join-Path $PSScriptRoot 'OffscreenGeometry.cs')
if($LASTEXITCODE -ne 0){throw 'Render acceptance compilation failed'}
Copy-Item "$source\app.config" "$output.config" -Force
& $output $OutputDirectory $(if($Baseline){"baseline"}else{"current"}) | Tee-Object -FilePath (Join-Path $OutputDirectory 'results.txt')
if($LASTEXITCODE -ne 0){throw 'Render acceptance failed'}
