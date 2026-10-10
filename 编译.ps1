$ErrorActionPreference='Stop'
$dockDirectory=$PSScriptRoot
$frameworkDirectory=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe=Join-Path $dockDirectory 'DesktopGlassDock.exe'
$running=@(Get-Process -Name DesktopGlassDock -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe})
if ($running.Count -gt 0) { throw '请先通过管理Dock.ps1 -Action Stop 或托盘退出 Dock，再编译。' }
$compiler=Join-Path $frameworkDirectory 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 Windows 自带的 .NET Framework x64 C# 编译器。' }
$compileArguments=@('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:'+$exe),('/win32manifest:'+(Join-Path $dockDirectory 'src\DesktopGlassDock.manifest')))
$references=@('WPF\PresentationFramework.dll','WPF\PresentationCore.dll','WPF\WindowsBase.dll','System.Xaml.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll')
foreach ($reference in $references) { $compileArguments+=('/r:'+(Join-Path $frameworkDirectory $reference)) }
$compileArguments+=@(Get-ChildItem -LiteralPath (Join-Path $dockDirectory 'src') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
& $compiler @compileArguments
if ($LASTEXITCODE -ne 0) { throw ('编译失败，退出码：'+$LASTEXITCODE) }
Write-Host ('编译成功：'+$exe)
