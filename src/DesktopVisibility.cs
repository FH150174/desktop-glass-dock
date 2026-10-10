using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopGlassDock
{
    // 仅读取窗口身份与可见性，不发送键鼠输入，也不修改 Windows 开始菜单。
    internal static class DesktopVisibility
    {
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        private static readonly string WindowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static readonly int OwnProcess = Process.GetCurrentProcess().Id;
        private static readonly int OwnSession = Process.GetCurrentProcess().SessionId;

        internal sealed class Snapshot
        {
            public long ForegroundHandle;
            public uint ForegroundProcessId;
            public string ForegroundClass = "";
            public bool HasForeground, OwnWindow, ValidatedDesktop;
            public string Reason = "";
        }
        internal static bool Decide(bool hasForeground, bool ownWindow, bool validatedDesktop, bool lastExternalWasDesktop)
        {
            if (!hasForeground) return false;
            return ownWindow ? lastExternalWasDesktop : validatedDesktop;
        }
        private static string ClassOf(IntPtr window)
        {
            var name = new StringBuilder(256);
            return Native.GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
        }
        private static bool InCurrentSession(uint process)
        {
            try { using (var candidate = Process.GetProcessById((int)process)) return candidate.SessionId == OwnSession; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
        internal static Snapshot Capture()
        {
            var result = new Snapshot();
            try
            {
                IntPtr foreground = Native.GetForegroundWindow();
                result.ForegroundHandle = foreground.ToInt64();
                result.HasForeground = foreground != IntPtr.Zero;
                if (!result.HasForeground) { result.Reason = "没有可验证的前台窗口"; return result; }
                uint process;
                Native.GetWindowThreadProcessId(foreground, out process);
                result.ForegroundProcessId = process;
                result.ForegroundClass = ClassOf(foreground);
                result.OwnWindow = process == OwnProcess;
                if (result.OwnWindow) { result.Reason = "本工具窗口保持上一外部状态"; return result; }
                IntPtr shell = GetShellWindow();
                uint shellProcess = 0;
                if (shell != IntPtr.Zero) Native.GetWindowThreadProcessId(shell, out shellProcess);
                bool trustedShell = shellProcess != 0 && shellProcess == process && InCurrentSession(shellProcess) &&
                    String.Equals(Native.ProcessPath(shell), Path.Combine(WindowsPath, "explorer.exe"), StringComparison.OrdinalIgnoreCase);
                if (!trustedShell || !Native.IsWindowVisible(foreground) || GetAncestor(foreground, 2) != foreground)
                { result.Reason = "软件或未知前台窗口"; return result; }
                bool progman = result.ForegroundClass == "Progman" && foreground == shell;
                bool worker = result.ForegroundClass == "WorkerW" &&
                    FindWindowEx(foreground, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
                result.ValidatedDesktop = progman || worker;
                result.Reason = result.ValidatedDesktop ? "已验证 Windows 桌面前台" : "Explorer 非桌面窗口";
            }
            catch (Exception ex) { result.ValidatedDesktop = false; result.OwnWindow = false; result.Reason = "窗口身份读取失败：" + ex.GetType().Name; }
            return result;
        }
        internal sealed class StartMenuSnapshot
        {
            public bool IsOpen, IsChildWindow;
            public long WindowHandle, RootHandle;
            public string Source = "", Reason = "没有可见的原生开始菜单";
        }
        private static bool SupportedStartRoot(bool sameWindow, string rootClass) { return sameWindow || rootClass == "ImmersiveLauncher"; }
        private static bool StartCandidateAccepted(bool coreWindow, bool trustedExecutable, bool currentSession,
            bool visible, bool usableBounds, bool knownUncloaked, bool supportedRoot)
        {
            return coreWindow && trustedExecutable && currentSession && visible && usableBounds && knownUncloaked && supportedRoot;
        }
        private static bool TryStartCandidate(IntPtr window, string source, StartMenuSnapshot result)
        {
            if (ClassOf(window) != "Windows.UI.Core.CoreWindow" || !Native.IsWindowVisible(window)) return false;
            IntPtr root = GetAncestor(window, 2);
            string rootClass = ClassOf(root);
            bool supportedRoot = SupportedStartRoot(root == window, rootClass);
            Native.RECT rect;
            int cloaked, rootCloaked;
            bool usableBounds = Native.GetWindowRect(window, out rect) && rect.Right - rect.Left >= 200 && rect.Bottom - rect.Top >= 200;
            // DWM 只保证顶层窗口的 cloak 查询；子窗口由已验证宿主的 cloak 与祖先可见性共同约束。
            bool rootUncloaked = root != IntPtr.Zero && DwmGetWindowAttribute(root, 14, out rootCloaked, 4) >= 0 && rootCloaked == 0;
            int cloakResult = DwmGetWindowAttribute(window, 14, out cloaked, 4);
            bool uncloaked = rootUncloaked && (cloakResult >= 0 ? cloaked == 0 : root != window && rootClass == "ImmersiveLauncher");
            if (!usableBounds || !uncloaked || !supportedRoot) return false;
            uint process;
            Native.GetWindowThreadProcessId(window, out process);
            string expected = Path.Combine(WindowsPath, @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe");
            if (!StartCandidateAccepted(true, String.Equals(Native.ProcessPath(window), expected, StringComparison.OrdinalIgnoreCase),
                InCurrentSession(process), true, usableBounds, uncloaked, supportedRoot)) return false;
            result.IsOpen = true; result.WindowHandle = window.ToInt64(); result.RootHandle = root.ToInt64();
            result.IsChildWindow = root != window; result.Source = source; result.Reason = "已验证可见的原生开始菜单";
            return true;
        }
        private static bool SearchStartTree(IntPtr root, string source, HashSet<IntPtr> visited, StartMenuSnapshot result)
        {
            if (ClassOf(root) != "ImmersiveLauncher") return visited.Add(root) && TryStartCandidate(root, source, result);
            // 仅搜索候选宿主，最多 256 个节点、8 层，避免对整个桌面递归扫描。
            var queue = new Queue<KeyValuePair<IntPtr, int>>();
            queue.Enqueue(new KeyValuePair<IntPtr, int>(root, 0));
            int count = 0;
            while (queue.Count > 0 && count++ < 256)
            {
                KeyValuePair<IntPtr, int> item = queue.Dequeue();
                if (item.Key == IntPtr.Zero || !visited.Add(item.Key)) continue;
                if (TryStartCandidate(item.Key, source, result)) return true;
                if (item.Value >= 8) continue;
                IntPtr child = IntPtr.Zero;
                for (int index = 0; index < 128 && queue.Count < 256; index++)
                {
                    child = FindWindowEx(item.Key, child, null, null);
                    if (child == IntPtr.Zero) break;
                    queue.Enqueue(new KeyValuePair<IntPtr, int>(child, item.Value + 1));
                }
            }
            return false;
        }
        internal static StartMenuSnapshot CaptureStartMenu()
        {
            var result = new StartMenuSnapshot();
            var visited = new HashSet<IntPtr>();
            try
            {
                IntPtr foreground = Native.GetForegroundWindow();
                if (foreground != IntPtr.Zero)
                {
                    if (TryStartCandidate(foreground, "前台窗口", result)) return result;
                    IntPtr root = GetAncestor(foreground, 2);
                    if ((ClassOf(root) == "ImmersiveLauncher" || ClassOf(root) == "Windows.UI.Core.CoreWindow") &&
                        SearchStartTree(root, "前台宿主树", visited, result)) return result;
                }
                // EnumWindows 可能不返回 UWP 窗口；直接查询这两个原生类的顶层窗口。
                foreach (string className in new[] { "Windows.UI.Core.CoreWindow", "ImmersiveLauncher" })
                {
                    IntPtr window = IntPtr.Zero;
                    for (int index = 0; index < 128; index++)
                    {
                        window = FindWindowEx(IntPtr.Zero, window, className, null);
                        if (window == IntPtr.Zero) break;
                        if (SearchStartTree(window, "原生类宿主树", visited, result)) return result;
                    }
                }
            }
            catch (Exception ex) { result.Reason = "开始菜单身份读取失败：" + ex.GetType().Name; }
            return result;
        }
        internal static bool IsStartMenuOpen() { return CaptureStartMenu().IsOpen; }

        internal static object SelfTest()
        {
            var checks = new Dictionary<string, bool>();
            checks["真实 Progman 桌面显示"] = Decide(true, false, true, false);
            checks["真实 WorkerW 桌面显示"] = Decide(true, false, true, false);
            foreach (string name in new[] { "普通小窗", "最大化软件", "全屏软件", "副屏软件", "Explorer 文件夹", "Windows 开始菜单", "伪造桌面类", "身份查询失败" })
                checks[name + "前台隐藏"] = !Decide(true, false, false, true);
            checks["Dock 自身点击保持桌面显示"] = Decide(true, true, false, true);
            checks["自身菜单不把隐藏 Dock 带到软件上"] = !Decide(true, true, false, false);
            checks["显式启动台不把隐藏 Dock 带到软件上"] = !Decide(true, true, false, false);
            checks["无前台窗口保守隐藏"] = !Decide(false, false, false, true);
            checks["无前台时不保留旧的自身状态"] = !Decide(false, true, false, true);
            checks["拖动或底边不参与显示判定"] = !Decide(true, false, false, true);
            checks["原生开始菜单顶层窗口可识别"] = StartCandidateAccepted(true, true, true, true, true, true, SupportedStartRoot(true, "Windows.UI.Core.CoreWindow"));
            checks["原生开始菜单宿主内子窗口可识别"] = StartCandidateAccepted(true, true, true, true, true, true, SupportedStartRoot(false, "ImmersiveLauncher"));
            checks["仅进程存在而窗口关闭不显示任务栏"] = !StartCandidateAccepted(true, true, true, false, true, true, true);
            checks["被 cloak 的开始菜单不显示任务栏"] = !StartCandidateAccepted(true, true, true, true, true, false, true);
            checks["伪造窗口类或程序路径不能显示任务栏"] = !StartCandidateAccepted(true, false, true, true, true, true, true) && !StartCandidateAccepted(false, true, true, true, true, true, true);
            checks["其他会话或未知宿主不能显示任务栏"] = !StartCandidateAccepted(true, true, false, true, true, true, true) && !StartCandidateAccepted(true, true, true, true, true, true, SupportedStartRoot(false, "伪造宿主"));

            return new { Passed = checks.Values.All(x => x), Count = checks.Count, Checks = checks, LiveForeground = Capture(), LiveStartMenuOpen = IsStartMenuOpen(),
                Limitations = new[] { "纯逻辑测试未注入鼠标键盘；桌面显示、软件隐藏和开始菜单任务栏须在真实会话验收。" } };
        }

        internal sealed class ForegroundHook : IDisposable
        {
            private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
            [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWinEventHook(uint minimum, uint maximum, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
            [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
            private readonly WinEventProc callback;
            private readonly Dispatcher dispatcher;
            private readonly Action changed;
            private GCHandle callbackRoot;
            private IntPtr handle;
            private bool queued;
            internal ForegroundHook(Dispatcher ui, Action action)
            {
                dispatcher = ui; changed = action;
                callback = delegate
                {
                    if (handle == IntPtr.Zero || queued || dispatcher.HasShutdownStarted) return;
                    queued = true;
                    try { dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate { queued = false; if (handle != IntPtr.Zero) changed(); })); }
                    catch (InvalidOperationException) { queued = false; }
                };
                callbackRoot = GCHandle.Alloc(callback);
                handle = SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0);
                if (handle == IntPtr.Zero) { callbackRoot.Free(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "前台窗口监听失败"); }
            }
            public void Dispose()
            {
                if (handle == IntPtr.Zero) return;
                IntPtr previous = handle; handle = IntPtr.Zero;
                if (UnhookWinEvent(previous)) { if (callbackRoot.IsAllocated) callbackRoot.Free(); }
                else Files.Log("前台监听注销失败，已停用处理并保留回调引用。", null);
                GC.KeepAlive(callback);
            }
        }
    }
}
