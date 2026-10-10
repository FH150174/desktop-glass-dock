using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopGlassDock
{
    internal sealed class RunningWindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public uint ProcessId;
    }
    internal sealed class RunningAppGroup
    {
        public string Key;
        public string Name;
        public AppEntry IconEntry;
        public List<RunningWindowInfo> Windows = new List<RunningWindowInfo>();
        public bool IsActive;
    }
    internal static class RunningApps
    {
        private const int ExtendedAppWindow = 0x00040000;
        private const uint OwnerWindow = 4;
        private const uint RootOwner = 3;
        private static readonly uint CurrentProcessId = SelfProcessId();
        private static bool missingDwmReported;
        public static string LastActivationFailure { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Handle;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, Native.EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        private static uint SelfProcessId() { using (Process process = Process.GetCurrentProcess()) return (uint)process.Id; }
        public static List<RunningAppGroup> Capture(IEnumerable<AppEntry> catalog)
        {
            AppEntry[] known = (catalog ?? new AppEntry[0]).Where(x => x != null).ToArray();
            var groups = new Dictionary<string, RunningAppGroup>(StringComparer.OrdinalIgnoreCase);
            var paths = new Dictionary<uint, string>();
            IntPtr foreground = Native.GetForegroundWindow();
            bool complete = Native.EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                try
                {
                    uint processId;
                    Native.GetWindowThreadProcessId(window, out processId);
                    if (processId == 0 || processId == CurrentProcessId || !UsableWindow(window)) return true;
                    string title = ReadTitle(window);
                    if (String.IsNullOrWhiteSpace(title)) return true;
                    // 有正常可用的所属窗口时，不重复列其浮窗；禁用所属窗口的模态对话框仍有入口。
                    int style = Native.GetWindowLong(window, Native.GWL_EXSTYLE);
                    IntPtr owner = Native.GetWindow(window, OwnerWindow);
                    if (owner != IntPtr.Zero && (style & ExtendedAppWindow) == 0 &&
                        UsableWindow(owner) && IsWindowEnabled(owner) && !String.IsNullOrWhiteSpace(ReadTitle(owner))) return true;
                    string path = GetProcessPath(window, processId, paths);
                    bool hosted = String.Equals(Path.GetFileName(path), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                    if (hosted) path = HostedProcessPath(window, processId, paths);
                    string key = GroupKey(path, processId);
                    // 读不到宿主里的实际进程时逐窗口隔离，不能把所有商店应用合为同一图标。
                    if (hosted && String.IsNullOrWhiteSpace(path)) key += ":hwnd:" + window.ToInt64().ToString(CultureInfo.InvariantCulture);
                    RunningAppGroup group;
                    if (!groups.TryGetValue(key, out group))
                    {
                        AppEntry entry = FindCatalogMatch(path, known);
                        string name = entry == null ? (hosted && String.IsNullOrWhiteSpace(path) ? "应用窗口" : FallbackName(path, processId)) : entry.Name;
                        if (String.IsNullOrWhiteSpace(name)) name = FallbackName(path, processId);
                        group = new RunningAppGroup
                        {
                            Key = key, Name = name,
                            IconEntry = entry ?? new AppEntry { Id = "running-" + key, Name = name, Target = path, ResolvedExe = path, IconPath = "", Kind = "file" }
                        };
                        groups.Add(key, group);
                    }
                    group.Windows.Add(new RunningWindowInfo { Handle = window, Title = title, ProcessId = processId });
                    if (window == foreground || (foreground != IntPtr.Zero && GetAncestor(foreground, RootOwner) == GetAncestor(window, RootOwner))) group.IsActive = true;
                }
                catch (Exception ex) { Files.Log("枚举运行窗口时忽略一个无法读取的窗口。", ex); }
                return true;
            }, IntPtr.Zero);
            if (!complete) Files.Log("运行窗口枚举未完成，将保留本次已取得的软件。", null);
            foreach (RunningAppGroup group in groups.Values)
                group.Windows = group.Windows.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Handle.ToInt64()).ToList();
            return groups.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private static bool UsableWindow(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window) || !Native.IsWindowVisible(window) || IsCloaked(window)) return false;
            if ((Native.GetWindowLong(window, -16) & 0x40000000) != 0) return false;
            int style = Native.GetWindowLong(window, Native.GWL_EXSTYLE);
            if ((style & Native.WS_EX_TOOLWINDOW) != 0 || ((style & Native.WS_EX_NOACTIVATE) != 0 && (style & ExtendedAppWindow) == 0)) return false;
            var name = new StringBuilder(256);
            Native.GetClassName(window, name, name.Capacity);
            string className = name.ToString();
            return className != "Progman" && className != "WorkerW" && className != "Shell_TrayWnd" && className != "Shell_SecondaryTrayWnd";
        }
        private static bool IsCloaked(IntPtr window)
        {
            try { int cloaked; return DwmGetWindowAttribute(window, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0; }
            catch (DllNotFoundException ex) { ReportMissingDwm(ex); return false; }
            catch (EntryPointNotFoundException ex) { ReportMissingDwm(ex); return false; }
        }
        private static void ReportMissingDwm(Exception ex)
        {
            if (missingDwmReported) return;
            missingDwmReported = true;
            Files.Log("系统缺少窗口隐藏状态接口，将仅使用可见窗口筛选。", ex);
        }
        private static string ReadTitle(IntPtr window)
        {
            // 自身进程已排除；读取外部标题不会等待该窗口的消息循环，不读浏览器标签页内容。
            var title = new StringBuilder(4096);
            return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString().Trim() : "";
        }
        private static string GetProcessPath(IntPtr window, uint processId, IDictionary<uint, string> paths)
        {
            string path;
            if (!paths.TryGetValue(processId, out path)) { path = Native.ProcessPath(window) ?? ""; paths.Add(processId, path); }
            return path;
        }
        private static string HostedProcessPath(IntPtr frame, uint hostId, IDictionary<uint, string> paths)
        {
            string best = ""; bool preferred = false;
            EnumChildWindows(frame, delegate(IntPtr child, IntPtr unused)
            {
                uint childId;
                Native.GetWindowThreadProcessId(child, out childId);
                if (childId == 0 || childId == hostId || childId == CurrentProcessId) return true;
                string path = GetProcessPath(child, childId, paths);
                if (String.IsNullOrWhiteSpace(path)) return true;
                var className = new StringBuilder(256);
                Native.GetClassName(child, className, className.Capacity);
                bool core = className.ToString() == "Windows.UI.Core.CoreWindow";
                if (String.IsNullOrWhiteSpace(best) || (core && !preferred)) { best = path; preferred = core; }
                return true;
            }, IntPtr.Zero);
            return best;
        }
        internal static string GroupKey(string path, uint processId)
        {
            return String.IsNullOrWhiteSpace(path) ? "pid:" + processId.ToString(CultureInfo.InvariantCulture) : "exe:" + path.Trim().ToLowerInvariant();
        }
        internal static AppEntry FindCatalogMatch(string path, IEnumerable<AppEntry> known)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            AppEntry[] entries = known.Where(x => x != null).ToArray();
            AppEntry exact = entries.FirstOrDefault(x => String.Equals(path, x.ResolvedExe, StringComparison.OrdinalIgnoreCase) ||
                (String.Equals(Path.GetExtension(x.Target ?? ""), ".exe", StringComparison.OrdinalIgnoreCase) && String.Equals(path, x.Target, StringComparison.OrdinalIgnoreCase)));
            if (exact != null) return exact;
            // 商店应用按包名和发布者两端匹配，允许版本更新，不凭窗口标题猜应用。
            string[] parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!String.Equals(parts[i], "WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                string package = parts[i + 1];
                return entries.FirstOrDefault(x =>
                {
                    if (!String.Equals(x.Kind, "app", StringComparison.OrdinalIgnoreCase)) return false;
                    string family = (x.Target ?? "").Split('!')[0]; int split = family.LastIndexOf('_');
                    return split > 0 && package.StartsWith(family.Substring(0, split) + "_", StringComparison.OrdinalIgnoreCase) &&
                        package.EndsWith("_" + family.Substring(split + 1), StringComparison.OrdinalIgnoreCase);
                });
            }
            return null;
        }
        private static string FallbackName(string path, uint processId)
        {
            if (!String.IsNullOrWhiteSpace(path)) return Path.GetFileNameWithoutExtension(path);
            try { using (Process process = Process.GetProcessById((int)processId)) return process.ProcessName; }
            catch (ArgumentException) { return "软件 " + processId.ToString(CultureInfo.InvariantCulture); }
            catch (InvalidOperationException) { return "软件 " + processId.ToString(CultureInfo.InvariantCulture); }
            catch (System.ComponentModel.Win32Exception) { return "软件 " + processId.ToString(CultureInfo.InvariantCulture); }
        }
        internal static int NextWindowIndex(IList<RunningWindowInfo> windows, IntPtr foreground)
        {
            if (windows == null || windows.Count == 0) return -1;
            for (int i = 0; i < windows.Count; i++) if (windows[i].Handle == foreground) return (i + 1) % windows.Count;
            return 0;
        }
        private static bool StillMatches(RunningWindowInfo window)
        {
            if (window == null || !IsWindow(window.Handle) || !Native.IsWindowVisible(window.Handle) || IsCloaked(window.Handle)) return false;
            uint currentId; Native.GetWindowThreadProcessId(window.Handle, out currentId);
            return currentId != 0 && currentId == window.ProcessId && currentId != CurrentProcessId;
        }
        public static bool Activate(RunningAppGroup group, IntPtr specificWindow)
        {
            LastActivationFailure = null;
            if (group == null) return ActivationFailed("对应的软件已不可用，请稍后重试。", IntPtr.Zero);
            List<RunningWindowInfo> windows = group.Windows.Where(StillMatches).ToList();
            RunningWindowInfo chosen;
            if (specificWindow != IntPtr.Zero) chosen = windows.FirstOrDefault(x => x.Handle == specificWindow);
            else { int index = NextWindowIndex(windows, Native.GetForegroundWindow()); chosen = index < 0 ? null : windows[index]; }
            if (chosen == null) return ActivationFailed("该窗口已关闭，请从更新后的运行列表选择。", IntPtr.Zero);
            IntPtr target = chosen.Handle;
            // 模态对话框禁用了主窗口时，切到其最近的可用弹窗。
            for (int i = 0; i < 8 && !IsWindowEnabled(target); i++)
            {
                IntPtr popup = GetLastActivePopup(target);
                if (popup == target || popup == IntPtr.Zero) popup = Native.GetWindow(target, 6);
                if (popup == target || popup == IntPtr.Zero || !IsWindow(popup) || !Native.IsWindowVisible(popup) || IsCloaked(popup) ||
                    GetAncestor(popup, RootOwner) != GetAncestor(chosen.Handle, RootOwner)) break;
                target = popup;
            }
            if (!IsWindowEnabled(target)) return ActivationFailed("软件当前有不可用的对话框，暂时无法切换。", chosen.Handle);
            if (Native.IsIconic(target)) ShowWindowAsync(target, 9);
            if (Native.SetForegroundWindow(target) || Native.GetForegroundWindow() == target) return true;
            return ActivationFailed("Windows 暂未允许切换到“" + group.Name + "”，其任务栏图标已闪烁提示。", target);
        }
        private static bool ActivationFailed(string message, IntPtr window)
        {
            LastActivationFailure = message; Files.Log("运行窗口切换失败：" + message, null);
            if (window != IntPtr.Zero && IsWindow(window))
            {
                var info = new FlashInfo { Size = (uint)Marshal.SizeOf(typeof(FlashInfo)), Handle = window, Flags = 3, Count = 3, Timeout = 0 };
                FlashWindowEx(ref info);
            }
            return false;
        }
        public static Dictionary<string, bool> LogicSelfTest()
        {
            var edge = new AppEntry { Name = "Edge", Target = "edge.lnk", ResolvedExe = @"C:\Program Files\Edge\msedge.exe" };
            var calculator = new AppEntry { Name = "计算器", Kind = "app", Target = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" };
            AppEntry[] entries = { edge, calculator };
            var windows = new List<RunningWindowInfo> { new RunningWindowInfo { Handle = new IntPtr(100) }, new RunningWindowInfo { Handle = new IntPtr(200) }, new RunningWindowInfo { Handle = new IntPtr(300) } };
            return new Dictionary<string, bool>
            {
                { "同一可执行文件忽略大小写分组", GroupKey(@"C:\APP\Test.exe", 1) == GroupKey(@"c:\app\test.EXE", 2) },
                { "未知路径按进程隔离", GroupKey("", 1) != GroupKey("", 2) },
                { "未知路径空格安全回退", GroupKey(" ", 7) == "pid:7" },
                { "高清目录按完整路径匹配", FindCatalogMatch(@"c:\program files\edge\MSEDGE.exe", entries) == edge },
                { "同文件名不同路径不误匹配", FindCatalogMatch(@"D:\Other\msedge.exe", entries) == null },
                { "商店包升级仍匹配", FindCatalogMatch(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.99.0.0_x64__8wekyb3d8bbwe\CalculatorApp.exe", entries) == calculator },
                { "不同商店发布者不误匹配", FindCatalogMatch(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.99.0.0_x64__other\CalculatorApp.exe", entries) == null },
                { "相似商店包名不误匹配", FindCatalogMatch(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculatorOther_11.0_x64__8wekyb3d8bbwe\App.exe", entries) == null },
                { "多窗口点击选择下一个", NextWindowIndex(windows, new IntPtr(200)) == 2 },
                { "多窗口最后一个回到首个", NextWindowIndex(windows, new IntPtr(300)) == 0 },
                { "非当前软件点击选择首个", NextWindowIndex(windows, new IntPtr(400)) == 0 },
                { "窗口全部关闭不返回失效索引", NextWindowIndex(new List<RunningWindowInfo>(), IntPtr.Zero) == -1 }
            };
        }
    }
}
