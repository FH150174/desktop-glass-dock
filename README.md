# Desktop Glass Dock

Windows 原生 WPF 桌面 Dock 与应用启动台。界面是真实桌面窗口，应用入口可启动本机程序、切换已有窗口；不依赖浏览器、网页或全屏图片。

## 功能

- 高透明圆角玻璃、高光边缘、指针光泽与弹性动画。通过 Windows 背景合成近似液态玻璃，不包含 Apple 原生材质或真实光学折射。
- Dock 与启动台内部拖拽排序，启动台搜索、分页和拖拽跨页；排序保存在本机。
- 最多固定 8 个应用，另保留“显示桌面”入口。运行区按真实顶层窗口和可执行文件分组，点击切换、多窗口选择、溢出菜单；不读取浏览器标签页。
- Dock 仅在 Windows 桌面处于前台时显示；软件、文件夹、开始菜单在前台时隐藏，并取消置顶。可通过快捷键主动打开应用启动台。
- 可选隐藏 Windows 任务栏；原生开始菜单打开期间显示任务栏，关闭后重新隐藏。可信的显示与 cloak 变化事件会立即请求刷新，并在过渡期间进行有界短确认；保留低频兜底检查。正常退出 Dock 恢复原任务栏；保留隐藏偏好供下次启动使用。
- 图标在运行时从本机程序、快捷方式或应用资源提取。仓库不含第三方软件图标包、壁纸、个人应用清单或桌面截图。

## 环境与限制

已在 Windows 10 22H2 x64、.NET Framework 4.8、100% 缩放的双屏环境运行与编译。需要可写的本地目录及 Windows PowerShell 5.1；构建使用系统自带 C# 编译器，不需要管理员权限、NuGet、额外 SDK 或付费软件。其他 Windows 版本、缩放比例与显示器组合尚未验证。

Windows 背景模糊依赖兼容性合成接口，系统或显卡不支持时退回透明高光。开始菜单身份识别针对 Windows 10 的原生宿主；Windows 11 或其他开始菜单替代工具尚未验证。桌面前台检测保守处理未知窗口。

已验证原环境中的应用点击、翻页、启动、拖拽排序、回弹、空白右键、Shift 管理菜单及应用右键；软件前台隐藏、退出恢复与重开隐藏已实测。开始菜单与任务栏使用原生窗口类直接查询，补足桌面窗口枚举可能遗漏的系统界面；显示时校正任务栏的屏外偏移，托盘和时钟由原生父任务栏正常显示。原环境反馈开始菜单任务栏已经能显示，但出现较迟；已增加事件响应优化，并通过任务栏、桌面判定及既有交互自检。最新响应手感、返回桌面重新显示和托盘点击仍待实际交互验收。实际重启或重新登录保持效果尚未验证；自启动指用户登录后运行，不是登录前系统服务。

## 构建和首次启动

将仓库放到自己希望长期使用的可写目录。在该目录打开 Windows PowerShell：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\初始化.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\编译.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\管理Dock.ps1 -Action Start
```

初始化脚本仅定位 Windows 自带的资源管理器、记事本、PowerShell 和命令提示符，并生成本机 `apps.json` 和 `dock.json`；不会自动扫描用户软件或个人目录。仅在清单缺失或空数组时生成，保留已有非空清单和 `data` 中的自定义设置。两个清单属于本机生成文件，已加入忽略规则；仓库中的 `apps.example.json`、`dock.example.json` 是空数组格式示例。

首次启动默认**不隐藏 Windows 任务栏、不启用自启动**。需要隐藏任务栏时执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\管理Dock.ps1 -Action HideTaskbar
```

程序以单实例运行。同一用户会话请只运行一份副本；若已有其他目录中的 Dock，先退出旧副本。编译脚本也要求先正常退出当前目录的 Dock。

## 日常操作

| 操作 | 功能 |
| --- | --- |
| 右键 Dock 玻璃边缘空白处 | 打开应用启动台 |
| Shift + 右键 Dock 空白处 | 打开管理菜单，可恢复任务栏或退出 |
| Ctrl + Alt + Space | 打开应用启动台，软件前台也可使用 |
| Dock 第一个固定系统图标 | 显示桌面 |
| 点击 Dock 应用 | 启动应用或切换已有窗口 |
| 右键应用图标 | 固定、移除等入口管理 |
| 在 Dock 或启动台内部拖动应用 | 调整该区域顺序 |
| 拖着启动台图标停在底部翻页箭头约半秒 | 拖拽跨页 |
| 启动台中的添加入口 | 选择自己的程序、文件或快捷方式 |

隐藏任务栏模式下，退出 Dock 会恢复系统任务栏，下次启动 Dock 会再次隐藏。原生开始菜单打开期间应显示系统任务栏。此开合行为在最新版本仍需人工验收。

启动台移除入口只修改本工具清单，不删除原程序、文件或快捷方式。`data` 保存本机应用路径、排序、运行窗口标题、日志以及恢复备份，请勿提交或公开。手动编辑应用清单时填写真实绝对路径；当前程序不会展开 `%WINDIR%` 等变量。

## 登录自启动、状态和恢复

设置当前用户登录后启动，注册项指向当前目录的可执行文件：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\管理Dock.ps1 -Action EnableStartup
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\管理Dock.ps1 -Action Status
```

启用前先备份同名注册项。已有同名项指向其他目录时脚本拒绝覆盖。设置后请保持程序目录不变；移动目录前先禁用该副本自启动，移动后重新启用。取消自启动：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\管理Dock.ps1 -Action DisableStartup
```

恢复 Windows 任务栏并关闭隐藏偏好，继续保留当前 Dock：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\管理Dock.ps1 -Action RestoreTaskbar
```

完整恢复：还原本工具启用前的自启动项，正常退出 Dock，恢复任务栏。个人应用、壁纸、桌面文件与本工具数据均保留：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\管理Dock.ps1 -Action Restore
```

任务栏被隐藏后需要命令行恢复时，可用 Win + R 输入 `powershell`，进入项目目录运行以上命令。请保留 `data\taskbar-backup.json` 和 `data\startup-backup.json`，直到完成恢复。异常退出时也可使用同一目录的程序执行 `--restore-taskbar`；该命令恢复已保存的任务栏状态并关闭隐藏偏好。

## 自检

以下诊断在本目录 `data` 写入结果，不启动应用、不改变自启动或任务栏设置；前三项只验证隔离数据与判定规则，后两项会只读枚举当前窗口或本机资源。结构化自检不能代替实际鼠标操作、视觉截图或重新登录验收：

```powershell
Start-Process .\DesktopGlassDock.exe -ArgumentList '--interaction-self-test' -Wait
Start-Process .\DesktopGlassDock.exe -ArgumentList '--desktop-visibility-self-test' -Wait
Start-Process .\DesktopGlassDock.exe -ArgumentList '--taskbar-self-test' -Wait
Start-Process .\DesktopGlassDock.exe -ArgumentList '--running-self-test' -Wait
Start-Process .\DesktopGlassDock.exe -ArgumentList '--self-test' -Wait
```

运行自检前先完成初始化和编译。`running-self-test` 使用非空应用清单验证窗口组布局。结果中的 `Passed` 字段和进程退出码用于判断是否通过。

## 源码

`src/DesktopGlassDock.cs` 为 Dock、启动台、图标、目录与启动逻辑；`DesktopVisibility.cs` 判断真实桌面与原生开始菜单；`TaskbarManager.cs` 管理可逆的任务栏隐藏与恢复；`RunningApps.cs` 枚举运行窗口；`LiquidGlass.cs` 绘制玻璃与原生合成背景。程序使用随 Windows 提供的 .NET Framework 和 Win32 API。

发布副本由白名单源码重新整理，未包含原机器的配置、历史 Git、日志、图标素材、编译产物或桌面文件。仓库尚未指定许可证；公开可见不等于授予开源使用授权。
