using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace DesktopGlassDock
{
    // 只管理当前 Explorer 所属的系统任务栏；配置和恢复信息全部留在程序的 data 目录。
    internal sealed class TaskbarManager : IDisposable
    {
        private const uint AutoHide = 1;
        private const string MainClass = "Shell_TrayWnd";
        private const string SecondaryClass = "Shell_SecondaryTrayWnd";
        private static readonly string SettingsPath = Path.Combine(Files.Data, "desktop-settings.json");
        private static readonly string BackupPath = Path.Combine(Files.Data, "taskbar-backup.json");
        private static readonly string StatusPath = Path.Combine(Files.Data, "taskbar-status.json");
        private DispatcherTimer timer;
        private ShellTaskbars.RevealHook revealHook;
        private DesktopVisibility.StartMenuHook startMenuHook;
        private DispatcherTimer transitionTimer;
        private readonly StartTransitionWindow transition = new StartTransitionWindow();
        private StartTransitionStatus transitionStatus = new StartTransitionStatus();
        private bool refreshing;
        private TaskbarBackup backup;
        private bool started;
        private bool enabled;
        private bool configured;
        private bool restorePending;
        private bool temporarilyShownForStartMenu;
        private DesktopVisibility.StartMenuSnapshot startMenu = new DesktopVisibility.StartMenuSnapshot();
        private bool disposed;
        private string lastError = "";
        private string lastStatus = "";
        private string lastStartIdentityError = "";

        public bool IsHiding { get { return enabled && restorePending && !temporarilyShownForStartMenu; } }

        public void Start()
        {
            if (disposed) throw new ObjectDisposedException("TaskbarManager");
            if (started) return;
            Files.Prepare();
            enabled = Requested();
            configured = enabled;
            started = true;
            if (!enabled) { PersistStatus(); return; }
            try
            {
                // 已有备份即为本次工具最初状态，重开或 Explorer 重建时不能覆盖。
                backup = ReadBackup();
                ApplyHidden();
                timer = new DispatcherTimer(DispatcherPriority.Background);
                timer.Interval = TimeSpan.FromMilliseconds(250);
                timer.Tick += OnTick;
                timer.Start();
                revealHook = new ShellTaskbars.RevealHook(OnTaskbarRevealed);
                try { startMenuHook = new DesktopVisibility.StartMenuHook(Dispatcher.CurrentDispatcher, BeginShellTransition); }
                catch (Exception ex) { Files.Log("开始菜单快速监听未启用，保留周期确认。", ex); }
                PersistStatus();
            }
            catch (Exception ex)
            {
                enabled = false;
                StopTimer();
                lastError = ex.Message;
                if (restorePending)
                {
                    TaskbarRestoreResult rollback = RestoreCore(backup);
                    restorePending = !rollback.Success;
                    if (!rollback.Success) lastError += "；回滚失败：" + rollback.Error;
                }
                Files.Log("任务栏隐藏未启用：" + lastError, ex);
                PersistStatus();
                throw new InvalidOperationException("任务栏隐藏失败，已尝试恢复原状态。", ex);
            }
        }

        public void DisableAndRestore()
        {
            if (disposed) throw new ObjectDisposedException("TaskbarManager");
            // 先保存关闭选项。保存失败时保持当前模式，避免重开又悄悄隐藏。
            Dictionary<string, object> settings = ReadSettings();
            settings["HideWindowsTaskbar"] = false;
            Files.Write(SettingsPath, settings);
            configured = false;
            enabled = false;
            StopTimer();
            TaskbarRestoreResult result = RestoreCore(backup ?? ReadBackup());
            restorePending = !result.Success;
            lastError = result.Error ?? "";
            PersistStatus();
            if (!result.Success) throw new InvalidOperationException("恢复系统任务栏失败：" + result.Error);
            Files.Log("已关闭任务栏隐藏并恢复原状态。", null);
        }

        public object Status()
        {
            List<TaskbarWindow> windows = ShellTaskbars.Enumerate();
            TaskbarWindow main = windows.FirstOrDefault(x => x.ClassName == MainClass);
            uint? state = main == null ? (uint?)null : ShellTaskbars.GetState();
            return new
            {
                Enabled = enabled,
                IsHiding = IsHiding,
                ConfiguredHideWindowsTaskbar = started ? configured : Requested(),
                Started = started,
                RestorePending = restorePending,
                BackupExists = File.Exists(BackupPath),
                AllTaskbarsHidden = windows.Count > 0 && windows.All(x => !x.Visible),
                RevealHookActive = revealHook != null && revealHook.Active,
                StartMenuHookActive = startMenuHook != null && startMenuHook.Active,
                StartMenuTransition = transitionStatus,
                TemporarilyShownForStartMenu = temporarilyShownForStartMenu,
                StartMenu = startMenu,
                AppBarState = state,
                Error = lastError,
                Taskbars = windows.Select(x => x.Snapshot()).ToArray()
            };
        }


        // 独立诊断只读取配置和系统窗口；不代表驻留实例，也不记录/修改任何状态。
        public static object QuerySystemStatus()
        {
            bool? requested = null;
            uint? state = null;
            var errors = new List<string>();
            var windows = new List<TaskbarWindow>();
            try
            {
                var settings = File.Exists(SettingsPath)
                    ? Files.Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(SettingsPath, Encoding.UTF8))
                    : new Dictionary<string, object>();
                object value;
                if (settings == null) throw new InvalidDataException("桌面设置不是有效的 JSON 对象。");
                if (!settings.TryGetValue("HideWindowsTaskbar", out value)) requested = false;
                else if (value is bool) requested = (bool)value;
                else throw new InvalidDataException("HideWindowsTaskbar 必须是布尔值。");
            }
            catch (Exception ex) { errors.Add("读取任务栏隐藏选项失败：" + ex.Message); }
            try
            {
                windows = ShellTaskbars.Enumerate(true);
                if (windows.Any(x => x.ClassName == MainClass)) state = ShellTaskbars.GetState();
                else errors.Add("当前未检测到可验证的 Explorer 主任务栏。");
            }
            catch (Exception ex) { errors.Add("查询系统任务栏失败：" + ex.Message); }
            return new
            {
                ConfiguredHideWindowsTaskbar = requested,
                BackupExists = File.Exists(BackupPath),
                AllTaskbarsHidden = windows.Count > 0 && windows.All(x => !x.Visible),
                AppBarState = state,
                Taskbars = windows.Select(x => x.Snapshot()).ToArray(),
                StartMenu = DesktopVisibility.CaptureStartMenu(),
                Error = String.Join("；", errors)
            };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            StopTimer();
            // 普通退出恢复桌面；不修改用户选项，下次随 Dock 启动时仍可重新隐藏。
            if (restorePending)
            {
                TaskbarRestoreResult result = RestoreCore(backup);
                restorePending = !result.Success;
                lastError = result.Error ?? "";
                if (!result.Success) Files.Log("退出 Dock 时恢复任务栏失败：" + result.Error, null);
            }
            if (started) PersistStatus();
        }

        // 供程序未运行时的独立恢复入口使用；调用方需先停止另一实例的隐藏计时器。
        public static object RestoreSavedState()
        {
            Files.Prepare();
            TaskbarRestoreResult result;
            try
            {
                Dictionary<string, object> settings = ReadSettings();
                settings["HideWindowsTaskbar"] = false;
                Files.Write(SettingsPath, settings);
                result = RestoreCore(ReadBackup());
            }
            catch (Exception ex)
            {
                result = new TaskbarRestoreResult { Success = false, Error = ex.Message };
                Files.Log("读取任务栏恢复备份失败。", ex);
            }
            Files.Write(Path.Combine(Files.Data, "taskbar-restore-result.json"), result);
            if (!result.Success) throw new InvalidOperationException("恢复系统任务栏失败：" + result.Error);
            return result;
        }

        internal static long MonotonicMilliseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        }

        public void RefreshForShellUi()
        {
            if (!enabled || disposed) return;
            long received = MonotonicMilliseconds();
            bool candidate;
            try
            {
                candidate = DesktopVisibility.IsTrustedStartEventWindow(Native.GetForegroundWindow());
                lastStartIdentityError = "";
            }
            catch (Exception ex)
            {
                string error = ex.GetType().FullName + ":" + ex.Message;
                if (lastStartIdentityError != error)
                    Files.Log("开始菜单前台身份查询失败，继续周期确认。", ex);
                lastStartIdentityError = error;
                RefreshState();
                return;
            }
            if (candidate || temporarilyShownForStartMenu)
                BeginShellTransition(candidate ? "开始菜单前台过渡" : "开始菜单离开前台", received, candidate);
            else RefreshState();
        }

        private void BeginShellTransition(string trigger, long received, bool expectedOpen)
        {
            if (!enabled || disposed) return;
            bool fresh = !transition.Active || transition.ExpectedOpen != expectedOpen;
            transition.Begin(received, expectedOpen);
            if (fresh)
                transitionStatus = new StartTransitionStatus { Trigger = trigger, ExpectedOpen = expectedOpen, Active = true, Outcome = "短时确认中" };
            // 第一轮由 Send 事件直接执行；开始动画还没完成时才用有界短计时器继续确认。
            RefreshState();
            if (!transition.Active || !enabled || disposed) return;
            if (transitionTimer == null)
            {
                transitionTimer = new DispatcherTimer(DispatcherPriority.Input);
                transitionTimer.Interval = TimeSpan.FromMilliseconds(20);
                transitionTimer.Tick += OnTransitionTick;
            }
            transitionTimer.Start();
        }

        private void OnTransitionTick(object sender, EventArgs args)
        {
            if (!enabled || disposed) { StopTransitionTimer(); return; }
            if (transitionTimer == null) return;
            RefreshState(!transition.TakeTick(MonotonicMilliseconds()));
        }

        private void OnTick(object sender, EventArgs args) { RefreshState(); }

        private void RefreshState(bool transitionExpired = false)
        {
            if (!enabled || disposed || refreshing) return;
            refreshing = true;
            try
            {
                ApplyHidden();
                if (transitionExpired)
                    FinishTransition(false, "短时确认结束，保留慢周期校验", ShellTaskbars.Enumerate());
                else if (transition.Active)
                {
                    List<TaskbarWindow> windows = ShellTaskbars.Enumerate();
                    bool hasMain = windows.Any(x => x.ClassName == MainClass);
                    bool confirmed = hasMain && (transition.ExpectedOpen
                        ? windows.All(x => x.Visible && InsideMonitor(x.Rect, x.MonitorRect))
                        : windows.All(x => !x.Visible));
                    bool expectedState = startMenu.IsOpen == transition.ExpectedOpen;
                    if (expectedState && confirmed)
                    {
                        transition.Observe(startMenu.IsOpen, true);
                        FinishTransition(true, "原生窗口状态已确认", windows);
                    }
                    else if (MonotonicMilliseconds() - transition.StartedAt >= 200)
                    {
                        transition.Cancel();
                        FinishTransition(false, "短时确认结束，保留慢周期校验", windows);
                    }
                }
                if (lastError.Length > 0) lastError = "";
                PersistStatus();
            }
            catch (Exception ex)
            {
                // 系统调用失败时停止继续隐藏并恢复；不重试写满日志或让系统保持半修改状态。
                enabled = false;
                StopTimer();
                TaskbarRestoreResult result = RestoreCore(backup);
                restorePending = !result.Success;
                lastError = ex.Message + (result.Success ? "" : "；恢复失败：" + result.Error);
                Files.Log("任务栏隐藏已停止：" + lastError, ex);
                PersistStatus();
            }
            finally { refreshing = false; }
        }

        private void FinishTransition(bool confirmed, string outcome, List<TaskbarWindow> windows)
        {
            transitionStatus.Active = false;
            transitionStatus.Outcome = outcome;
            transitionStatus.ConfirmationWindowMilliseconds = Math.Max(0, MonotonicMilliseconds() - transition.StartedAt);
            // 这是事件回调到原生可见位/屏内边界的确认耗时，不是屏幕像素绘制完成时间。
            transitionStatus.HookCallbackToNativeConfirmationMilliseconds = confirmed ? transitionStatus.ConfirmationWindowMilliseconds : (long?)null;
            transitionStatus.NativeTaskbarsVisible = windows.Count > 0 && windows.All(x => x.Visible);
            transitionStatus.NativeTaskbarsFullyOnScreen = windows.Count > 0 && windows.All(x => InsideMonitor(x.Rect, x.MonitorRect));
            StopTransitionTimer();
        }

        private void StopTransitionTimer()
        {
            transition.Cancel();
            if (transitionTimer == null) return;
            transitionTimer.Stop();
            transitionTimer.Tick -= OnTransitionTick;
            transitionTimer = null;
        }

        private sealed class StartTransitionStatus
        {
            public string Trigger = "", Outcome = "尚无开始菜单过渡";
            public bool ExpectedOpen, Active, NativeTaskbarsVisible, NativeTaskbarsFullyOnScreen;
            public long? HookCallbackToNativeConfirmationMilliseconds;
            public long ConfirmationWindowMilliseconds;
        }

        private void ApplyHidden()
        {
            // 即使 Explorer 临时没有任务栏，也更新开始菜单诊断，不能沿用上次隐藏状态。
            startMenu = DesktopVisibility.CaptureStartMenu();
            temporarilyShownForStartMenu = startMenu.IsOpen;
            List<TaskbarWindow> windows = ShellTaskbars.Enumerate();
            TaskbarWindow main = windows.FirstOrDefault(x => x.ClassName == MainClass);
            // Explorer 登录/重启间隙没有主任务栏时只等待，不对其他窗口或系统设置操作。
            if (main == null) return;
            if (backup == null)
            {
                TaskbarBackup initial = new TaskbarBackup
                {
                    Version = 1,
                    CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                    AppBarState = ShellTaskbars.GetState(),
                    Windows = windows.Select(x => x.Snapshot()).ToList()
                };
                ValidateBackup(initial);
                // 必须先持久化成功，才允许第一次系统改动。
                Files.Write(BackupPath, initial);
                backup = initial;
            }
            uint state = ShellTaskbars.GetState();
            restorePending = true;
            if (temporarilyShownForStartMenu)
            {
                // 仅在真实开始菜单打开期间显示原生任务栏，不改用户隐藏偏好。
                uint visibleState = state & ~AutoHide;
                if (state != visibleState) ShellTaskbars.SetState(main, visibleState);
                // Explorer 在清除自动隐藏后可能重新布局，必须读取新边界再校正。
                foreach (TaskbarWindow window in ShellTaskbars.Enumerate())
                {
                    TaskbarWindowState saved = backup.Windows.FirstOrDefault(x => x.ClassName == window.ClassName &&
                        String.Equals(x.MonitorDevice, window.MonitorDevice, StringComparison.OrdinalIgnoreCase));
                    ShellTaskbars.EnsureFullyOnScreen(window, saved);
                    ShellTaskbars.SetVisible(window, true);
                }
                return;
            }
            if ((state & AutoHide) == 0)
                ShellTaskbars.SetState(main, state | AutoHide);
            foreach (TaskbarWindow window in windows)
                if (window.Visible) ShellTaskbars.SetVisible(window, false);
        }

        private void OnTaskbarRevealed(IntPtr window)
        {
            if (!enabled || disposed) return;
            try
            {
                if (!DesktopVisibility.IsStartMenuOpen()) ShellTaskbars.HideRevealed(window);
            }
            catch (Exception ex)
            {
                // 原生回调不能向外抛异常；周期校验会继续确认或执行回滚。
                if (lastError != ex.Message) Files.Log("重新隐藏系统任务栏失败。", ex);
                lastError = ex.Message;
            }
        }

        private void StopTimer()
        {
            // 必须先注销显示事件，再恢复任务栏，避免恢复请求被本工具再次隐藏。
            if (startMenuHook != null) { startMenuHook.Dispose(); startMenuHook = null; }
            StopTransitionTimer();
            transitionStatus.Active = false;
            if (revealHook != null) { revealHook.Dispose(); revealHook = null; }
            if (timer == null) return;
            timer.Stop();
            timer.Tick -= OnTick;
            timer = null;
        }

        private void PersistStatus()
        {
            try
            {
                object state = Status();
                string serialized = Files.Json.Serialize(state);
                if (serialized == lastStatus) return;
                var record = new { UpdatedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture), State = state }; Files.Write(StatusPath, record); File.AppendAllText(Path.Combine(Files.Data, "taskbar-state-history.jsonl"), Files.Json.Serialize(record) + Environment.NewLine, Encoding.UTF8);
                lastStatus = serialized;
            }
            catch (Exception ex) { Files.Log("任务栏状态记录失败。", ex); }
        }

        private static Dictionary<string, object> ReadSettings()
        {
            return Files.Read(SettingsPath, new Dictionary<string, object>()) ?? new Dictionary<string, object>();
        }

        private static bool Requested()
        {
            object value;
            return ReadSettings().TryGetValue("HideWindowsTaskbar", out value) && value is bool && (bool)value;
        }

        private static TaskbarBackup ReadBackup()
        {
            if (!File.Exists(BackupPath)) return null;
            TaskbarBackup saved = Files.Read<TaskbarBackup>(BackupPath, null);
            ValidateBackup(saved);
            return saved;
        }

        private static void ValidateBackup(TaskbarBackup saved)
        {
            if (saved == null || saved.Version != 1 || saved.Windows == null ||
                saved.Windows.Count == 0 || !saved.Windows.Any(x => x != null && x.ClassName == MainClass))
                throw new InvalidDataException("任务栏恢复备份缺失或版本不支持，未修改系统。");
            if (saved.Windows.Any(x => x == null || !ShellTaskbars.IsTaskbarClass(x.ClassName) ||
                String.IsNullOrWhiteSpace(x.MonitorDevice) || x.Bounds == null))
                throw new InvalidDataException("任务栏恢复备份包含无效窗口或显示器信息，未修改系统。");
        }

        private static bool OriginalVisibility(TaskbarBackup saved, TaskbarWindow window)
        {
            TaskbarWindowState exact = saved.Windows.FirstOrDefault(x => x.ClassName == window.ClassName &&
                String.Equals(x.MonitorDevice, window.MonitorDevice, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact.Visible;
            // 主任务栏可能换了显示器；新接入显示器的副任务栏按可见恢复。
            if (window.ClassName == MainClass)
            {
                TaskbarWindowState oldMain = saved.Windows.FirstOrDefault(x => x.ClassName == MainClass);
                if (oldMain != null) return oldMain.Visible;
            }
            return true;
        }

        private static TaskbarRestoreResult RestoreCore(TaskbarBackup saved)
        {
            var result = new TaskbarRestoreResult { Success = false, BackupExists = saved != null, RestoredAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture), Error = "" };
            if (saved == null) { result.Success = true; return result; }
            try
            {
                ValidateBackup(saved);
                List<TaskbarWindow> windows = ShellTaskbars.Enumerate();
                TaskbarWindow main = windows.FirstOrDefault(x => x.ClassName == MainClass);
                if (main == null) throw new InvalidOperationException("当前没有可验证的 Explorer 主任务栏，请在 Explorer 恢复后再次执行恢复。");
                ShellTaskbars.SetState(main, saved.AppBarState);
                foreach (TaskbarWindow window in ShellTaskbars.Enumerate())
                {
                    if ((saved.AppBarState & AutoHide) == 0 && OriginalVisibility(saved, window))
                    {
                        TaskbarWindowState original = saved.Windows.FirstOrDefault(x => x.ClassName == window.ClassName &&
                            String.Equals(x.MonitorDevice, window.MonitorDevice, StringComparison.OrdinalIgnoreCase));
                        ShellTaskbars.EnsureFullyOnScreen(window, original);
                    }
                    ShellTaskbars.SetVisible(window, OriginalVisibility(saved, window));
                }
                // ShowWindowAsync 返回的是已提交请求，需另外确认实际可见位而非把返回值当最终结果。
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    windows = ShellTaskbars.Enumerate();
                    main = windows.FirstOrDefault(x => x.ClassName == MainClass);
                    if (main != null && ShellTaskbars.GetState() == saved.AppBarState &&
                        windows.All(x => x.Visible == OriginalVisibility(saved, x) &&
                            ((saved.AppBarState & AutoHide) != 0 || !x.Visible || InsideMonitor(x.Rect, x.MonitorRect))))
                    {
                        result.Success = true;
                        result.AppBarState = ShellTaskbars.GetState();
                        result.Taskbars = windows.Select(x => x.Snapshot()).ToArray();
                        return result;
                    }
                    Thread.Sleep(25);
                }
                throw new InvalidOperationException("恢复请求已提交，但在 300 毫秒内未确认任务栏状态一致。请保留备份并重试恢复。");
            }
            catch (Exception ex) { result.Error = ex.Message; Files.Log("恢复系统任务栏失败。", ex); }
            return result;
        }

        // 自动隐藏会把整个父任务栏推到屏外；只修正溢出的父窗口，不单独移动托盘或时钟。
        private static bool InsideMonitor(WinRect rect, WinRect monitor)
        {
            return rect.Right > rect.Left && rect.Bottom > rect.Top && rect.Left >= monitor.Left &&
                rect.Top >= monitor.Top && rect.Right <= monitor.Right && rect.Bottom <= monitor.Bottom;
        }
        private static WinRect FullTaskbarBounds(WinRect current, WinRect monitor, WinRect? saved)
        {
            if (InsideMonitor(current, monitor)) return current;
            int width = current.Right - current.Left, height = current.Bottom - current.Top;
            int monitorWidth = monitor.Right - monitor.Left, monitorHeight = monitor.Bottom - monitor.Top;
            if (monitorWidth <= 0 || monitorHeight <= 0) throw new InvalidDataException("显示器边界无效，未移动任务栏。");
            if (saved.HasValue && (width <= 0 || height <= 0 || width > monitorWidth || height > monitorHeight))
            {
                WinRect previous = saved.Value;
                int previousWidth = previous.Right - previous.Left, previousHeight = previous.Bottom - previous.Top;
                if (previousWidth > 0 && previousHeight > 0 && previousWidth <= monitorWidth && previousHeight <= monitorHeight)
                { width = previousWidth; height = previousHeight; }
            }
            if (width <= 0 || height <= 0) throw new InvalidDataException("任务栏尺寸无效，未移动其他窗口。");
            width = Math.Min(width, monitorWidth); height = Math.Min(height, monitorHeight);
            int left = Math.Max(monitor.Left, Math.Min(current.Left, monitor.Right - width));
            int top = Math.Max(monitor.Top, Math.Min(current.Top, monitor.Bottom - height));
            // 横栏只沿上/下边修正，竖栏只沿左/右边修正；负坐标和不同 DPI 使用实际物理边界。
            if (width >= height)
                top = Math.Abs((long)current.Top - monitor.Top) <= Math.Abs((long)current.Bottom - monitor.Bottom) ? monitor.Top : monitor.Bottom - height;
            else
                left = Math.Abs((long)current.Left - monitor.Left) <= Math.Abs((long)current.Right - monitor.Right) ? monitor.Left : monitor.Right - width;
            return new WinRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        }
        private static TaskbarBounds BoundsOf(WinRect rect)
        {
            return new TaskbarBounds { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        }
        private static bool SameBounds(WinRect first, WinRect second)
        {
            return first.Left == second.Left && first.Top == second.Top && first.Right == second.Right && first.Bottom == second.Bottom;
        }


        // 原生事件与 UI 调度之间的边界，回归测试可用队列替换消息循环。
        internal sealed class StartEventRouter : IDisposable
        {
            private readonly Func<IntPtr, bool> trusted;
            private readonly Action<Action> schedule;
            private readonly Action<string, long, bool> refresh;
            private readonly Func<long> clock;
            private bool queued, dispatching, disposed, expectedOpen;
            private long receivedAt;
            private string trigger;
            internal StartEventRouter(Func<IntPtr, bool> trusted, Action<Action> schedule,
                Action<string, long, bool> refresh, Func<long> clock)
            {
                this.trusted = trusted; this.schedule = schedule; this.refresh = refresh; this.clock = clock;
            }
            internal void Notify(uint eventType, IntPtr window, int objectId, int childId)
            {
                if (disposed || dispatching || window == IntPtr.Zero || objectId != 0 || childId != 0) return;
                string nextTrigger;
                switch (eventType)
                {
                    case 0x8002: nextTrigger = "开始菜单显示事件"; break;
                    case 0x8003: nextTrigger = "开始菜单隐藏事件"; break;
                    case 0x8017: nextTrigger = "开始菜单cloak事件"; break;
                    case 0x8018: nextTrigger = "开始菜单解除cloak事件"; break;
                    default: return;
                }
                long received = clock();
                // 这里验证身份而不要求已经可见；是否真正展开仍交给严格的 CaptureStartMenu。
                if (!trusted(window)) return;
                expectedOpen = eventType == 0x8002 || eventType == 0x8018;
                trigger = nextTrigger;
                if (queued) return;
                receivedAt = received;
                queued = true;
                try
                {
                    schedule(delegate
                    {
                        queued = false;
                        if (disposed || dispatching) return;
                        dispatching = true;
                        try { refresh(trigger, receivedAt, expectedOpen); }
                        finally { dispatching = false; }
                    });
                }
                catch { queued = false; throw; }
            }
            public void Dispose() { disposed = true; queued = false; }
        }

        internal sealed class StartTransitionWindow
        {
            internal bool Active { get; private set; }
            internal bool ExpectedOpen { get; private set; }
            internal long StartedAt { get; private set; }
            private int ticks;
            internal void Begin(long now, bool expectedOpen)
            {
                // 同方向事件不刷新起点，避免事件风暴让短周期永久运行。
                if (Active && ExpectedOpen == expectedOpen) return;
                Active = true; ExpectedOpen = expectedOpen; StartedAt = now; ticks = 0;
            }
            internal bool TakeTick(long now)
            {
                if (!Active) return false;
                if (now - StartedAt >= 200 || ticks >= 10) { Active = false; return false; }
                ticks++;
                return true;
            }
            internal void Observe(bool actualOpen, bool confirmed)
            {
                if (Active && actualOpen == ExpectedOpen && confirmed) Active = false;
            }
            internal void Cancel() { Active = false; }
        }

        // 不读取或改变系统的纯逻辑检查，可并入主程序的隔离自检。
        public static object SelfTest()
        {
            var original = new TaskbarBackup
            {
                Version = 1, AppBarState = 0, CapturedAt = "测试",
                Windows = new List<TaskbarWindowState>
                {
                    new TaskbarWindowState { ClassName = MainClass, MonitorDevice = "主屏", Visible = true, Bounds = new TaskbarBounds() },
                    new TaskbarWindowState { ClassName = SecondaryClass, MonitorDevice = "副屏", Visible = false, Bounds = new TaskbarBounds() }
                }
            };
            var checks = new Dictionary<string, bool>();
            ValidateBackup(original);
            checks["保留原自动隐藏以外的标志"] = ((2u | AutoHide) & 2u) != 0;
            checks["主屏按显示器恢复可见"] = OriginalVisibility(original, new TaskbarWindow { ClassName = MainClass, MonitorDevice = "主屏" });
            checks["副屏按显示器恢复隐藏"] = !OriginalVisibility(original, new TaskbarWindow { ClassName = SecondaryClass, MonitorDevice = "副屏" });
            checks["主任务栏换屏仍恢复原状态"] = OriginalVisibility(original, new TaskbarWindow { ClassName = MainClass, MonitorDevice = "新主屏" });
            checks["新副屏任务栏默认可见"] = OriginalVisibility(original, new TaskbarWindow { ClassName = SecondaryClass, MonitorDevice = "新副屏" });
            checks["只接受两个系统任务栏类"] = !ShellTaskbars.IsTaskbarClass("Notepad") && ShellTaskbars.IsTaskbarClass(MainClass) && ShellTaskbars.IsTaskbarClass(SecondaryClass);
            bool invalidRejected = false;
            try { ValidateBackup(new TaskbarBackup { Version = 99, Windows = original.Windows }); }
            catch (InvalidDataException) { invalidRejected = true; }
            checks["无效备份拒绝变更"] = invalidRejected;
            var config = new Dictionary<string, object> { { "HideWindowsTaskbar", true }, { "OtherSetting", "保留" } };
            config["HideWindowsTaskbar"] = false;
            checks["关闭隐藏保留其他设置"] = !(bool)config["HideWindowsTaskbar"] && (string)config["OtherSetting"] == "保留";
            var monitor = new WinRect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var bottom = FullTaskbarBounds(new WinRect { Left = 0, Top = 1078, Right = 1920, Bottom = 1108 }, monitor, null);
            checks["底部自动隐藏偏移恢复完整高度"] = bottom.Top == 1050 && bottom.Bottom == 1080 && bottom.Right - bottom.Left == 1920;
            var top = FullTaskbarBounds(new WinRect { Left = 0, Top = -28, Right = 1920, Bottom = 2 }, monitor, null);
            checks["顶部任务栏保持原边与厚度"] = top.Top == 0 && top.Bottom == 30;
            var left = FullTaskbarBounds(new WinRect { Left = -38, Top = 0, Right = 2, Bottom = 1080 }, monitor, null);
            checks["左侧竖栏恢复屏内"] = left.Left == 0 && left.Right == 40 && left.Bottom == 1080;
            var right = FullTaskbarBounds(new WinRect { Left = 1918, Top = 0, Right = 1958, Bottom = 1080 }, monitor, null);
            checks["右侧竖栏恢复屏内"] = right.Left == 1880 && right.Right == 1920;
            var negativeMonitor = new WinRect { Left = -1080, Top = -601, Right = 0, Bottom = 1319 };
            var secondary = FullTaskbarBounds(new WinRect { Left = -1080, Top = 1317, Right = 0, Bottom = 1367 }, negativeMonitor, null);
            checks["负坐标副屏按实际像素恢复"] = secondary.Left == -1080 && secondary.Top == 1269 && secondary.Bottom == 1319;
            var inside = new WinRect { Left = 0, Top = 1030, Right = 1920, Bottom = 1080 };
            checks["已屏内任务栏不重复移动"] = SameBounds(FullTaskbarBounds(inside, monitor, null), inside);
            var fallback = FullTaskbarBounds(new WinRect { Left = 0, Top = 1078, Right = 3000, Bottom = 1108 }, monitor, inside);
            checks["异常尺寸仅按同屏备份兜底"] = fallback.Top == 1030 && fallback.Right == 1920 && fallback.Bottom == 1080;
            var smaller = FullTaskbarBounds(new WinRect { Left = 0, Top = 718, Right = 1920, Bottom = 748 },
                new WinRect { Left = 0, Top = 0, Right = 1280, Bottom = 720 }, inside);
            checks["分辨率变小按当前屏幕调整"] = smaller.Right == 1280 && smaller.Top == 690 && smaller.Bottom == 720;

            // 模拟 fullscreen Start：桌面应用枚举为空，但原生类查询仍返回真实系统任务栏和一个冒名窗口。
            var desktopOnly = new IntPtr[0];
            IntPtr realTaskbar = new IntPtr(1), spoofedTaskbar = new IntPtr(2), unrelatedWindow = new IntPtr(3);
            var fixture = new Dictionary<IntPtr, TaskbarWindow>
            {
                { realTaskbar, new TaskbarWindow { Handle = realTaskbar, ClassName = MainClass, ProcessId = 42 } },
                { spoofedTaskbar, new TaskbarWindow { Handle = spoofedTaskbar, ClassName = MainClass, ProcessId = 99 } },
                { unrelatedWindow, new TaskbarWindow { Handle = unrelatedWindow, ClassName = "Notepad", ProcessId = 42 } }
            };
            Func<string, IEnumerable<IntPtr>> directClasses = name => name == MainClass
                ? new[] { realTaskbar, spoofedTaskbar, unrelatedWindow, realTaskbar } : new IntPtr[0];
            List<TaskbarWindow> directResult = ShellTaskbars.CollectTaskbars(directClasses, 42, handle => fixture[handle]);
            checks["桌面应用枚举为空仍找到原生任务栏"] = desktopOnly.Length == 0 && directResult.Count == 1 && directResult[0].Handle == realTaskbar;
            checks["原生类同名但非当前Explorer拒绝"] = !directResult.Any(x => x.Handle == spoofedTaskbar);
            checks["当前Explorer其他窗口不当任务栏操作"] = !directResult.Any(x => x.Handle == unrelatedWindow);
            checks["没有已验证Explorer不操作任何任务栏"] = ShellTaskbars.CollectTaskbars(directClasses, 0, handle => fixture[handle]).Count == 0;

            var pending = new Queue<Action>();
            int refreshed = 0;
            long eventClock = 0, firstObserved = -1;
            bool requestedOpen = false;
            var router = new StartEventRouter(handle => handle == realTaskbar, pending.Enqueue,
                delegate(string trigger, long received, bool expectedOpen) { refreshed++; firstObserved = received; requestedOpen = expectedOpen; }, () => eventClock);
            router.Notify(0x8018, realTaskbar, 0, 0);
            if (pending.Count > 0) pending.Dequeue()();
            checks["可信开始菜单解除cloak无需等慢周期"] = refreshed == 1 && firstObserved == 0 && requestedOpen;
            router.Notify(0x8018, spoofedTaskbar, 0, 0);
            router.Notify(0x8002, realTaskbar, -4, 0);
            router.Notify(0x8005, realTaskbar, 0, 0);
            checks["无关身份子对象及其他事件不触发刷新"] = pending.Count == 0 && refreshed == 1;
            eventClock = 10;
            router.Notify(0x8002, realTaskbar, 0, 0);
            eventClock = 15;
            router.Notify(0x8018, realTaskbar, 0, 0);
            checks["开始菜单事件风暴只排队一次"] = pending.Count == 1;
            if (pending.Count > 0) pending.Dequeue()();
            checks["合并事件保留最早计时起点"] = refreshed == 2 && firstObserved == 10;
            router.Notify(0x8017, realTaskbar, 0, 0);
            if (pending.Count > 0) pending.Dequeue()();
            checks["开始菜单cloak及时请求隐藏"] = refreshed == 3 && !requestedOpen;
            router.Notify(0x8002, realTaskbar, 0, 0);
            router.Dispose();
            while (pending.Count > 0) pending.Dequeue()();
            router.Notify(0x8018, realTaskbar, 0, 0);
            checks["退出后已排队及新事件都不操作任务栏"] = refreshed == 3 && pending.Count == 0;

            var transition = new StartTransitionWindow();
            transition.Begin(0, true);
            int fastTicks = 0;
            for (int index = 1; index <= 20; index++) if (transition.TakeTick(index * 10)) fastTicks++;
            checks["无法确认时短周期最多十次"] = fastTicks == 10 && !transition.Active;
            transition.Begin(1000, true);
            transition.Begin(1150, true);
            checks["事件风暴不延长二百毫秒确认窗口"] = !transition.TakeTick(1200) && !transition.Active;
            transition.Begin(2000, true);
            transition.Observe(false, true);
            checks["开始动画尚未展开时继续短确认"] = transition.Active;
            transition.Observe(true, true);
            checks["真实可见且屏内后立即收敛回慢周期"] = !transition.Active;
            transition.Begin(3000, false);
            transition.Observe(false, false);
            checks["异步显隐尚未确认时不提前停止"] = transition.Active;
            transition.Cancel();
            checks["停止时清理过渡确认"] = !transition.TakeTick(3020) && !transition.Active;

            return new { Passed = checks.Values.All(x => x), Count = checks.Count, Checks = checks };
        }

        public sealed class TaskbarBackup
        {
            public int Version { get; set; }
            public string CapturedAt { get; set; }
            public uint AppBarState { get; set; }
            public List<TaskbarWindowState> Windows { get; set; }
        }
        public sealed class TaskbarBounds
        {
            public int Left { get; set; }
            public int Top { get; set; }
            public int Right { get; set; }
            public int Bottom { get; set; }
        }
        public sealed class TaskbarWindowState
        {
            public string ClassName { get; set; }
            public string MonitorDevice { get; set; }
            public bool Visible { get; set; }
            public TaskbarBounds Bounds { get; set; }
            public TaskbarBounds MonitorBounds { get; set; }
            public bool FullyOnScreen { get; set; }
            public TaskbarComponentState[] TrayComponents { get; set; }
        }
        public sealed class TaskbarComponentState
        {
            public string ClassName { get; set; }
            public bool Visible { get; set; }
            public bool Enabled { get; set; }
            public bool FullyOnScreen { get; set; }
            public bool InsideTaskbar { get; set; }
            public TaskbarBounds Bounds { get; set; }
        }

        public sealed class TaskbarRestoreResult
        {
            public bool Success { get; set; }
            public bool BackupExists { get; set; }
            public string RestoredAt { get; set; }
            public string Error { get; set; }
            public uint AppBarState { get; set; }
            public TaskbarWindowState[] Taskbars { get; set; }
        }
        private sealed class TaskbarWindow
        {
            public IntPtr Handle;
            public uint ProcessId;
            public string ClassName;
            public string MonitorDevice;
            public bool Visible;
            public WinRect Rect, MonitorRect;
            public TaskbarWindowState Snapshot()
            {
                return new TaskbarWindowState
                {
                    ClassName = ClassName, MonitorDevice = MonitorDevice, Visible = Visible,
                    Bounds = BoundsOf(Rect), MonitorBounds = BoundsOf(MonitorRect),
                    FullyOnScreen = InsideMonitor(Rect, MonitorRect),
                    TrayComponents = ShellTaskbars.TrayComponents(this)
                };
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint callbackMessage;
            public uint edge;
            public WinRect rect;
            public IntPtr lParam;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public uint cbSize;
            public WinRect monitor;
            public WinRect work;
            public uint flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string device;
        }

        private static class ShellTaskbars
        {
            private static uint trustedProcess;
            private static readonly int Session = Process.GetCurrentProcess().SessionId;
            [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder buffer, int count);
            [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out WinRect rect);
            [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window, int command);
            [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

            public static bool IsTaskbarClass(string name) { return name == MainClass || name == SecondaryClass; }
            public sealed class RevealHook : IDisposable
            {
                private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);
                [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
                [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
                private readonly WinEventProc callback;
                private GCHandle callbackRoot;
                private IntPtr handle;
                private bool dispatching;
                public bool Active { get { return handle != IntPtr.Zero; } }
                public RevealHook(Action<IntPtr> revealed)
                {
                    callback = delegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
                    {
                        if (handle == IntPtr.Zero || window == IntPtr.Zero || objectId != 0 || childId != 0 || dispatching) return;
                        dispatching = true;
                        try { revealed(window); }
                        catch (Exception ex) { Files.Log("系统任务栏显示事件处理失败。", ex); }
                        finally { dispatching = false; }
                    };
                    // WINEVENT_OUTOFCONTEXT：回调在本线程消息循环执行，不注入 Explorer。
                    callbackRoot = GCHandle.Alloc(callback);
                    handle = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, callback, 0, 0, 0);
                    if (handle == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        callbackRoot.Free();
                        throw new Win32Exception(error, "系统任务栏显示事件监听注册失败。");
                    }
                }
                public void Dispose()
                {
                    if (handle == IntPtr.Zero) return;
                    IntPtr old = handle;
                    handle = IntPtr.Zero;
                    if (UnhookWinEvent(old))
                    {
                        if (callbackRoot.IsAllocated) callbackRoot.Free();
                    }
                    else
                    {
                        // 注销异常时保留委托的强引用，失效回调已被 handle=0 禁用，避免原生悬空指针。
                        Files.Log("系统任务栏显示事件监听注销失败，已停用处理并保留回调引用。", null);
                    }
                    GC.KeepAlive(callback);
                }
            }

            public static void HideRevealed(IntPtr handle)
            {
                string name = ClassOf(handle);
                if (!IsTaskbarClass(name)) return;
                uint process;
                if (GetWindowThreadProcessId(handle, out process) == 0 || process == 0 || process != ExplorerProcess()) return;
                SetVisible(new TaskbarWindow { Handle = handle, ClassName = name, ProcessId = process }, false);
            }
            private static uint ExplorerProcess(bool queryOnly = false)
            {
                IntPtr desktop = GetShellWindow();
                uint process;
                if (desktop == IntPtr.Zero || GetWindowThreadProcessId(desktop, out process) == 0 || process == 0) return 0;
                if (process == trustedProcess) return process;
                try
                {
                    using (Process shell = Process.GetProcessById((int)process))
                    {
                        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                        if (shell.SessionId != Session || !String.Equals(shell.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) ||
                            !String.Equals(shell.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase))
                            return 0;
                    }
                    trustedProcess = process;
                    return process;
                }
                catch (Exception ex)
                {
                    if (queryOnly) throw new InvalidOperationException("无法确认当前 Explorer 进程。", ex);
                    Files.Log("无法确认当前 Explorer 进程，未对任务栏操作。", ex);
                    return 0;
                }
            }

            private static string ClassOf(IntPtr window)
            {
                var name = new StringBuilder(128);
                return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
            }
            private static bool StillOwned(TaskbarWindow window)
            {
                uint actual;
                uint explorer = ExplorerProcess();
                return explorer != 0 && explorer == window.ProcessId &&
                    GetWindowThreadProcessId(window.Handle, out actual) != 0 && actual == explorer &&
                    ClassOf(window.Handle) == window.ClassName && IsTaskbarClass(window.ClassName);
            }

            private static IEnumerable<IntPtr> FindClassWindows(string className)
            {
                IntPtr previous = IntPtr.Zero;
                for (int index = 0; index < 128; index++)
                {
                    IntPtr current = FindWindowEx(IntPtr.Zero, previous, className, null);
                    if (current == IntPtr.Zero || current == previous) yield break;
                    yield return current;
                    previous = current;
                }
            }
            // 查询策略可替换为纯夹具；UWP 开始菜单打开时不能依赖只枚举桌面应用的 EnumWindows。
            public static List<TaskbarWindow> CollectTaskbars(Func<string, IEnumerable<IntPtr>> query, uint explorer, Func<IntPtr, TaskbarWindow> inspect)
            {
                var windows = new List<TaskbarWindow>();
                if (explorer == 0) return windows;
                var seen = new HashSet<IntPtr>();
                foreach (string expectedClass in new[] { MainClass, SecondaryClass })
                {
                    int count = 0;
                    foreach (IntPtr handle in query(expectedClass))
                    {
                        if (count++ >= 128) break;
                        if (handle == IntPtr.Zero || !seen.Add(handle)) continue;
                        TaskbarWindow candidate = inspect(handle);
                        if (candidate != null && candidate.ClassName == expectedClass && candidate.ProcessId == explorer)
                            windows.Add(candidate);
                    }
                }
                return windows;
            }
            public static List<TaskbarWindow> Enumerate(bool queryOnly = false)
            {
                uint explorer = ExplorerProcess(queryOnly);
                return CollectTaskbars(FindClassWindows, explorer, delegate(IntPtr handle)
                {
                    string name = ClassOf(handle);
                    uint owner;
                    if (!IsTaskbarClass(name) || GetWindowThreadProcessId(handle, out owner) == 0 || owner != explorer) return null;
                    WinRect rect;
                    if (!GetWindowRect(handle, out rect)) return null;
                    var monitor = new MonitorInfo { cbSize = (uint)Marshal.SizeOf(typeof(MonitorInfo)) };
                    if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor) || String.IsNullOrWhiteSpace(monitor.device)) return null;
                    return new TaskbarWindow { Handle = handle, ProcessId = owner, ClassName = name, MonitorDevice = monitor.device, Visible = IsWindowVisible(handle), Rect = rect, MonitorRect = monitor.monitor };
                });
            }

            public static TaskbarComponentState[] TrayComponents(TaskbarWindow taskbar)
            {
                var result = new List<TaskbarComponentState>();
                var queue = new Queue<IntPtr>();
                queue.Enqueue(taskbar.Handle);
                // 仅读取 Explorer 托盘/时钟的实际状态，绝不操作子窗口；兼容主栏和副栏的不同层级。
                int count = 0;
                while (queue.Count > 0 && count++ < 256)
                {
                    IntPtr parent = queue.Dequeue(), child = IntPtr.Zero;
                    for (int index = 0; index < 128 && queue.Count < 256; index++)
                    {
                        child = FindWindowEx(parent, child, null, null);
                        if (child == IntPtr.Zero) break;
                        uint process;
                        if (GetWindowThreadProcessId(child, out process) == 0 || process != taskbar.ProcessId) continue;
                        queue.Enqueue(child);
                        string name = ClassOf(child);
                        if (name != "TrayNotifyWnd" && name != "TrayClockWClass" && name != "ClockButton") continue;
                        WinRect rect;
                        if (!GetWindowRect(child, out rect)) continue;
                        result.Add(new TaskbarComponentState
                        {
                            ClassName = name, Visible = IsWindowVisible(child), Enabled = IsWindowEnabled(child),
                            FullyOnScreen = InsideMonitor(rect, taskbar.MonitorRect), InsideTaskbar = InsideMonitor(rect, taskbar.Rect),
                            Bounds = BoundsOf(rect)
                        });
                    }
                }
                return result.ToArray();
            }
            public static void EnsureFullyOnScreen(TaskbarWindow window, TaskbarWindowState saved)
            {
                if (!StillOwned(window)) throw new InvalidOperationException("Explorer 任务栏已变化，未移动其他窗口。");
                WinRect? previous = saved == null || saved.Bounds == null ? (WinRect?)null :
                    new WinRect { Left = saved.Bounds.Left, Top = saved.Bounds.Top, Right = saved.Bounds.Right, Bottom = saved.Bounds.Bottom };
                WinRect target = FullTaskbarBounds(window.Rect, window.MonitorRect, previous);
                if (SameBounds(target, window.Rect)) return;
                // 保持系统原有 Z 序，不抢开始菜单焦点。只有几何溢出时才异步移动一次。
                const uint flags = 0x0004 | 0x0010 | 0x0200 | 0x4000; // NOZORDER | NOACTIVATE | NOOWNERZORDER | ASYNCWINDOWPOS
                if (!SetWindowPos(window.Handle, IntPtr.Zero, target.Left, target.Top, target.Right - target.Left, target.Bottom - target.Top, flags))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "系统拒绝任务栏屏内布局请求。");
            }

            public static uint GetState()
            {
                var data = new AppBarData { cbSize = (uint)Marshal.SizeOf(typeof(AppBarData)) };
                return checked((uint)SHAppBarMessage(4, ref data).ToUInt64());
            }
            public static void SetState(TaskbarWindow main, uint state)
            {
                if (main.ClassName != MainClass || !StillOwned(main)) throw new InvalidOperationException("Explorer 主任务栏已变化，未修改状态。");
                var data = new AppBarData { cbSize = (uint)Marshal.SizeOf(typeof(AppBarData)), hWnd = main.Handle, lParam = new IntPtr(state) };
                if (SHAppBarMessage(10, ref data) == UIntPtr.Zero)
                    throw new InvalidOperationException("系统拒绝任务栏状态请求。");
                if (GetState() != state)
                    throw new InvalidOperationException("任务栏状态请求未得到系统确认。");
            }
            public static void SetVisible(TaskbarWindow window, bool visible)
            {
                if (!StillOwned(window)) throw new InvalidOperationException("Explorer 任务栏已变化，未修改其他窗口。");
                if (IsWindowVisible(window.Handle) == visible) return;
                // SW_SHOWNOACTIVATE 不抢走用户当前应用焦点；返回值只表示异步请求已提交。
                if (!ShowWindowAsync(window.Handle, visible ? 4 : 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "系统拒绝任务栏显隐请求。");
            }
        }
    }
}
