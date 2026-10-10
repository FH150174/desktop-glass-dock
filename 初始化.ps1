# 首次初始化仅写本项目清单，不扫描个人目录，不修改 data 或系统设置。
$ErrorActionPreference='Stop'
$base=$PSScriptRoot
$appPath=Join-Path $base 'apps.json'
$dockPath=Join-Path $base 'dock.json'

function Read-List([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $text=[IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8)
    if ($text.Trim() -notmatch '^\[') { throw ('应用清单必须是 JSON 数组，未修改：'+$Path) }
    $parsed=ConvertFrom-Json -InputObject $text -ErrorAction Stop
    if ($text.Trim() -match '^\[\s*\]$') { return @() }
    if ($null -eq $parsed) { throw ('清单不能包含空条目，未修改：'+$Path) }
    foreach ($item in @($parsed)) { if ($null -eq $item) { throw ('清单不能包含空条目，未修改：'+$Path) } }
    return @($parsed)
}
function Write-NewList([string]$Path,[object[]]$Items) {
    $temporary=$Path+'.writing-'+[Guid]::NewGuid().ToString('N')
    # 通过 -InputObject 保留只有一个元素时的 JSON 数组结构。
    $json=ConvertTo-Json -InputObject @($Items) -Depth 6
    [IO.File]::WriteAllText($temporary,$json,[Text.Encoding]::UTF8)
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary,$Path,[NullString]::Value) }
    else { [IO.File]::Move($temporary,$Path) }
}
function New-SystemEntry([string]$Id,[string]$Name,[string]$File,[string]$Arguments='') {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { return }
    [pscustomobject]@{Id=$Id;Name=$Name;Target=$File;Arguments=$Arguments;WorkingDirectory=(Split-Path -Parent $File);ResolvedExe=$File;Kind='file';IconPath='';PreferredDock=$true}
}

$apps=@(Read-List $appPath)
if ($apps.Count -eq 0) {
    $windowsDirectory=[Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    if ([string]::IsNullOrWhiteSpace($windowsDirectory)) { throw '无法确定 Windows 目录，未生成清单。' }
    $apps=@(
        New-SystemEntry 'system-explorer' '文件资源管理器' (Join-Path $windowsDirectory 'explorer.exe')
        New-SystemEntry 'system-notepad' '记事本' (Join-Path $windowsDirectory 'System32\notepad.exe')
        New-SystemEntry 'system-powershell' 'Windows PowerShell' (Join-Path $windowsDirectory 'System32\WindowsPowerShell\v1.0\powershell.exe')
        New-SystemEntry 'system-command-prompt' '命令提示符' (Join-Path $windowsDirectory 'System32\cmd.exe')
    )
    if ($apps.Count -eq 0) { throw '未找到可用的 Windows 自带程序，未生成空清单。' }
    Write-NewList $appPath $apps
    Write-Host ('已生成 '+$apps.Count+' 个系统应用入口。')
} else {
    foreach ($app in $apps) {
        if ($null -eq $app -or [string]::IsNullOrWhiteSpace($app.Id) -or [string]::IsNullOrWhiteSpace($app.Name) -or [string]::IsNullOrWhiteSpace($app.Target)) {
            throw '已有 apps.json 存在无效条目，已保留文件，请先手动修复。'
        }
    }
    Write-Host '已有 apps.json，已保留。'
}
$dock=@(Read-List $dockPath)
if ($dock.Count -eq 0) {
    $ids=@($apps | Where-Object {$_.PreferredDock} | Select-Object -First 8 | ForEach-Object {$_.Id})
    if ($ids.Count -eq 0) { $ids=@($apps | Select-Object -First 8 | ForEach-Object {$_.Id}) }
    Write-NewList $dockPath $ids
    Write-Host ('已生成 '+$ids.Count+' 个 Dock 固定入口。')
} else { Write-Host '已有 dock.json，已保留。' }
Write-Host '初始化完成。未启动 Dock，未修改自启动、任务栏或用户 data。'
