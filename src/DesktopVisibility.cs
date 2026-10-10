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
        internal static bool IsStartMenuOpen()
        {
            bool found = false;
            string expected = Path.Combine(WindowsPath, @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe");
            Native.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!Native.IsWindowVisible(window) || ClassOf(window) != "Windows.UI.Core.CoreWindow" || GetAncestor(window, 2) != window) return true;
                Native.RECT rect;
                int cloaked;
                if (!Native.GetWindowRect(window, out rect) || rect.Right - rect.Left < 200 || rect.Bottom - rect.Top < 200 ||
                    DwmGetWindowAttribute(window, 14, out cloaked, 4) < 0 || cloaked != 0) return true;
                uint process;
                Native.GetWindowThreadProcessId(window, out process);
                if (!String.Equals(Native.ProcessPath(window), expected, StringComparison.OrdinalIgnoreCase) || !InCurrentSession(process)) return true;
                found = true;
                return false;
            }, IntPtr.Zero);
            // EnumWindows 回调主动结束也可能返回 false，已验证窗口优先。
            return found;
        }
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
