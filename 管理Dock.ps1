param([ValidateSet('Start','Show','Stop','EnableStartup','DisableStartup','HideTaskbar','RestoreTaskbar','Restore','Status')][string]$Action='Show')
$ErrorActionPreference='Stop'
$base=$PSScriptRoot
$exe=Join-Path $base 'DesktopGlassDock.exe'
$startupBackup=Join-Path $base 'data\startup-backup.json'
$runPath='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName='DesktopGlassDock'
$expected='"'+$exe+'"'
# 只管理本工具的启动项、任务栏隐藏偏好和本次保存的任务栏状态。
function Read-StartupValue {
    $key=Get-Item -LiteralPath $runPath -ErrorAction SilentlyContinue
    if ($null -eq $key -or $key.GetValueNames() -notcontains $valueName) { return [pscustomobject]@{Exists=$false;Value=$null;Kind=$null} }
    return [pscustomobject]@{Exists=$true;Value=$key.GetValue($valueName,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);Kind=$key.GetValueKind($valueName).ToString()}
}
function Find-OurProcess { @(Get-Process -Name 'DesktopGlassDock' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) }
function Start-OurDock {
    Start-Process -FilePath $exe -WorkingDirectory $base -WindowStyle Hidden | Out-Null
}
function Stop-OurDock {
    if (@(Find-OurProcess).Count -eq 0) { Write-Host 'Dock 当前未运行。'; return }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '程序文件缺失但仍有 Dock 进程，请通过 Shift+右键 Dock 空白处正常退出。' }
    Start-Process -FilePath $exe -ArgumentList '--quit' -WorkingDirectory $base -WindowStyle Hidden | Out-Null
    $deadline=[DateTime]::UtcNow.AddSeconds(6)
    while (@(Find-OurProcess).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 150 }
    if (@(Find-OurProcess).Count -gt 0) { throw '正常退出尚未完成，请通过 Shift+右键 Dock 空白处的管理菜单退出。' }
    Write-Host 'Dock 已退出，已尝试恢复本次隐藏的系统任务栏。'
}
function Invoke-DockMaintenance([string]$Argument) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw ('缺少维护程序：'+$exe) }
    $maintenance=Start-Process -FilePath $exe -ArgumentList $Argument -WorkingDirectory $base -WindowStyle Hidden -Wait -PassThru
    if ($maintenance.ExitCode -ne 0) { throw ('Dock 维护操作未完成：'+$Argument+'，退出码 '+$maintenance.ExitCode+'。请查看 data 中的任务栏恢复记录和 dock.log。') }
}
function Set-TaskbarHidePreference([bool]$Enabled) {
    $settingsPath=Join-Path $base 'data\desktop-settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings=Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $settings -or $settings -isnot [pscustomobject]) { throw '桌面偏好格式无效，未覆盖。' }
    } else { $settings=[pscustomobject]@{} }
    $settings | Add-Member -NotePropertyName HideWindowsTaskbar -NotePropertyValue $Enabled -Force
    New-Item -ItemType Directory -Path (Split-Path -Parent $settingsPath) -Force | Out-Null
    $temporary=$settingsPath+'.writing-'+[Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($temporary,($settings | ConvertTo-Json -Depth 20),[Text.Encoding]::UTF8)
    if (Test-Path -LiteralPath $settingsPath) { [IO.File]::Replace($temporary,$settingsPath,[NullString]::Value) }
    else { [IO.File]::Move($temporary,$settingsPath) }
}
function Disable-OurStartup {
    $current=Read-StartupValue
    if (-not $current.Exists) { Write-Host 'Dock 开机启动已关闭。'; return }
    if ($current.Value -ne $expected) { throw '同名启动项已经变化，未修改它。' }
    if (-not (Test-Path -LiteralPath $startupBackup)) { throw '缺少本工具启动项备份，未删除启动项。' }
    $original=Get-Content -LiteralPath $startupBackup -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($original.Exists) { New-ItemProperty -Path $runPath -Name $valueName -PropertyType $original.Kind -Value $original.Value -Force | Out-Null }
    else { Remove-ItemProperty -LiteralPath $runPath -Name $valueName }
    Write-Host 'Dock 启动项已恢复到启用前的状态。'
}
if ((@('Start','Show','EnableStartup','HideTaskbar','RestoreTaskbar','Restore') -contains $Action) -and -not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw ('缺少程序：'+$exe) }
switch ($Action) {
    'Start' { Start-OurDock; Write-Host 'Dock 已启动。' }
    'Show' { Start-Process -FilePath $exe -ArgumentList '--show-launchpad' -WorkingDirectory $base -WindowStyle Hidden | Out-Null; Write-Host '已打开应用启动台。' }
    'Stop' { Stop-OurDock }
    'EnableStartup' {
        $current=Read-StartupValue
        if ($current.Exists -and $current.Value -ne $expected) { throw '同名启动项属于其他配置，未覆盖。' }
        New-Item -ItemType Directory -Path (Join-Path $base 'data') -Force | Out-Null
        if (-not (Test-Path -LiteralPath $startupBackup)) { $current | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $startupBackup -Encoding UTF8 }
        if (-not (Test-Path -LiteralPath $runPath)) { New-Item -Path $runPath -Force | Out-Null }
        New-ItemProperty -Path $runPath -Name $valueName -PropertyType String -Value $expected -Force | Out-Null
        Write-Host '已设置登录后启动 Dock。实际重启或重新登录仍需单独验证。'
    }
    'DisableStartup' { Disable-OurStartup }
    'HideTaskbar' {
        Stop-OurDock
        Set-TaskbarHidePreference $true
        Start-OurDock
        Write-Host '已启用由 Dock 隐藏 Windows 任务栏；正常退出 Dock 时恢复，下一次启动会再次隐藏。'
    }
    'RestoreTaskbar' {
        $wasRunning=@(Find-OurProcess).Count -gt 0
        Stop-OurDock
        Invoke-DockMaintenance '--restore-taskbar'
        if ($wasRunning) { Start-OurDock }
        Write-Host '系统任务栏已恢复，已关闭任务栏隐藏偏好。'
    }
    'Restore' {
        Disable-OurStartup
        Stop-OurDock
        Invoke-DockMaintenance '--restore-taskbar'
        Write-Host '原任务栏及本工具启动项已恢复，Dock 已退出。壁纸、桌面文件和现有应用均保留。'
    }
    'Status' {
        $taskbarStatus=$null
        if (Test-Path -LiteralPath $exe -PathType Leaf) {
            Invoke-DockMaintenance '--taskbar-status'
            $statusPath=Join-Path $base 'data\taskbar-query.json'
            if (Test-Path -LiteralPath $statusPath) {
                $taskbarStatus=Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($null -ne $taskbarStatus.State) { $taskbarStatus=$taskbarStatus.State }
            }
        }
        [pscustomobject]@{Executable=$exe;Running=(@(Find-OurProcess).Count -gt 0);Startup=(Read-StartupValue);ExpectedStartupValue=$expected;Taskbar=$taskbarStatus} | ConvertTo-Json -Depth 9
    }
}
