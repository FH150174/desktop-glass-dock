using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

namespace DesktopGlassDock
{
    // 仅规划本程序两个窗口相对于已验证桌面宿主的层级，不改变其他应用或系统窗口。
    internal sealed class DesktopLayer : IDisposable
    {
        internal sealed class LayerWindow
        {
            internal IntPtr Handle;
            internal bool TrustedDesktop, Topmost;
        }
        internal sealed class PlacementPlan
        {
            internal bool CanApply;
            internal IntPtr DesktopAnchor, InsertAfter;
            internal uint Flags;
        }

        private const uint PlacementFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
        private static readonly int OwnProcess = Process.GetCurrentProcess().Id;
        private static readonly int OwnSession = Process.GetCurrentProcess().SessionId;
        private static readonly Dictionary<IntPtr, DesktopLayer> Registered = new Dictionary<IntPtr, DesktopLayer>();
        private readonly IntPtr dock;
        private readonly Func<IntPtr> glass;
        private readonly Func<IList<LayerWindow>> capture;
        private readonly Func<IntPtr, bool> ownLive;
        private readonly Action<IntPtr, IntPtr, uint> place;
        private PlacementPlan lastPlan = new PlacementPlan();
        private IntPtr registeredGlass;
        private bool nativeMode, disposed, arranging, lastArrangeAccepted;
        private string lastError = "";

        internal DesktopLayer(IntPtr dock, Func<IntPtr> glass, Func<IList<LayerWindow>> capture,
            Func<IntPtr, bool> ownLive, Action<IntPtr, IntPtr, uint> place)
        {
            this.dock = dock; this.glass = glass; this.capture = capture; this.ownLive = ownLive; this.place = place;
        }
        internal DesktopLayer(IntPtr dock, Func<IntPtr> glass)
            : this(dock, glass, () => CaptureWindows(dock), IsOwnLive, PlaceNative)
        {
            nativeMode = true;
            RegisterPair();
        }

        internal static PlacementPlan PlanPlacement(IList<LayerWindow> windows, IntPtr dock, IntPtr glass)
        {
            var plan = new PlacementPlan { Flags = PlacementFlags };
            if (windows == null || dock == IntPtr.Zero) return plan;
            LayerWindow previous = null;
            foreach (LayerWindow window in windows)
            {
                if (window == null || window.Handle == IntPtr.Zero || window.Handle == dock || window.Handle == glass) continue;
                if (window.TrustedDesktop)
                {
                    // 最高可信宿主若已置顶，不存在安全的普通窗口带插入位置，不能降级使用更低宿主。
                    if (window.Topmost) return plan;
                    plan.CanApply = true;
                    plan.DesktopAnchor = window.Handle;
                    // HWND_TOP 是普通窗口带顶部；不把置顶前驱 HWND 当作插入目标，避免继承置顶。
                    plan.InsertAfter = previous == null || previous.Topmost ? IntPtr.Zero : previous.Handle;
                    return plan;
                }
                previous = window;
            }
            return plan;
        }
        internal bool Refresh()
        {
            lastArrangeAccepted = false;
            if (disposed || dock == IntPtr.Zero || !ownLive(dock)) return false;
            if (nativeMode) RegisterPair();
            IntPtr background = glass();
            lastPlan = PlanPlacement(capture(), dock, background);
            if (!lastPlan.CanApply) return false;
            arranging = true;
            try
            {
                // 每次原生调用还会复核自身 PID，句柄失效时绝不把复用的系统/应用窗口当目标。
                if (!ownLive(dock)) return false;
                place(dock, lastPlan.InsertAfter, lastPlan.Flags | 0x0040);
                if (background != IntPtr.Zero)
                {
                    if (!ownLive(background)) return false;
                    place(background, dock, lastPlan.Flags);
                }
                lastArrangeAccepted = true;
                lastError = "";
                return true;
            }
            finally { arranging = false; }
        }
        internal object Status()
        {
            return new
            {
                Mode = "DesktopLayer", HasTrustedDesktopAnchor = lastPlan.CanApply,
                DesktopAnchor = lastPlan.DesktopAnchor.ToInt64(), InsertAfter = lastPlan.InsertAfter.ToInt64(),
                OwnPlacementAccepted = lastArrangeAccepted, Disposed = disposed, Error = lastError,
                Reason = lastPlan.CanApply ? "常驻于可信桌面宿主上方，由普通应用窗口自然遮挡" : "暂未找到可信桌面宿主，保留当前位置并等待再次确认"
            };
        }

        private void RegisterPair()
        {
            if (disposed) return;
            if (IsOwnLive(dock)) Registered[dock] = this;
            IntPtr currentGlass = glass();
            if (registeredGlass != currentGlass)
            {
                if (registeredGlass != IntPtr.Zero) Registered.Remove(registeredGlass);
                registeredGlass = IntPtr.Zero;
                if (currentGlass != IntPtr.Zero && IsOwnLive(currentGlass))
                {
                    Registered[currentGlass] = this;
                    registeredGlass = currentGlass;
                }
            }
        }
        // 只改自己窗口的插入位置与 NOZORDER；几何和其他 flags 仍由原请求决定。
        internal static void FilterPosition(IntPtr window, IntPtr memory)
        {
            DesktopLayer layer;
            if (memory == IntPtr.Zero || !Registered.TryGetValue(window, out layer) || layer.disposed || layer.arranging || !IsOwnLive(window)) return;
            var position = (WindowPosition)Marshal.PtrToStructure(memory, typeof(WindowPosition));
            if (position.Window != window || (position.Flags & 0x0004) != 0 || position.InsertAfter == new IntPtr(-2)) return;
            try
            {
                PlacementPlan plan = PlanPlacement(layer.capture(), layer.dock, layer.glass());
                if (!plan.CanApply)
                    position.Flags |= 0x0004; // 无可信宿主时阻止请求意外提升，慢周期继续等待。
                else
                {
                    position.InsertAfter = window == layer.dock ? plan.InsertAfter : layer.dock;
                    position.Flags &= ~0x0004u;
                }
                Marshal.StructureToPtr(position, memory, false);
            }
            catch (Exception ex)
            {
                // 原生窗口过程不能抛出；保持几何请求，但禁止这次未知层级变化。
                position.Flags |= 0x0004;
                Marshal.StructureToPtr(position, memory, false);
                layer.RecordError("桌面层级请求确认失败，保留当前位置。", ex);
            }
        }
        internal void RecordError(string message, Exception ex)
        {
            string error = ex.GetType().Name + ":" + ex.Message;
            if (error != lastError) Files.Log(message, ex);
            lastError = error;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (nativeMode)
            {
                Registered.Remove(dock);
                if (registeredGlass != IntPtr.Zero) Registered.Remove(registeredGlass);
            }
        }

        private static bool IsOwnLive(IntPtr window)
        {
            uint owner;
            return window != IntPtr.Zero && IsWindow(window) && Native.GetWindowThreadProcessId(window, out owner) != 0 && owner == OwnProcess;
        }
        private static void PlaceNative(IntPtr window, IntPtr after, uint flags)
        {
            if (!IsOwnLive(window)) throw new InvalidOperationException("本程序窗口句柄已失效，未调整其他窗口。");
            if ((flags & 0x0040) != 0 && Native.IsIconic(window)) Native.ShowWindow(window, 4);
            // 调用方与 Hook 均保持不激活；若外部曾把自己窗口置顶，先仅对自己显式取消置顶。
            if ((Native.GetWindowLong(window, Native.GWL_EXSTYLE) & 8) != 0)
                if (!SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, flags))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取消本程序窗口置顶。");
            if (after != IntPtr.Zero && (!IsWindow(after) || (Native.GetWindowLong(after, Native.GWL_EXSTYLE) & 8) != 0))
                throw new InvalidOperationException("桌面层前驱已变化，等待下一次确认。");
            bool alreadyAfter = after == IntPtr.Zero ? GetWindow(window, 3) == IntPtr.Zero : GetWindow(window, 3) == after;
            if (alreadyAfter && ((flags & 0x0040) == 0 || Native.IsWindowVisible(window))) return;
            if (!SetWindowPos(window, after, 0, 0, 0, 0, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将本程序窗口排列到桌面层。");
        }

        private static IList<LayerWindow> CaptureWindows(IntPtr dock)
        {
            var windows = new List<LayerWindow>();
            Native.RECT dockBounds;
            if (!IsOwnLive(dock) || !Native.GetWindowRect(dock, out dockBounds)) return windows;
            IntPtr shell = GetShellWindow();
            uint explorer;
            bool trustedShell = shell != IntPtr.Zero && Native.GetWindowThreadProcessId(shell, out explorer) != 0 && explorer != 0 &&
                String.Equals(Native.ProcessPath(shell), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);
            if (!trustedShell) return windows;
            Native.GetWindowThreadProcessId(shell, out explorer);
            using (Process process = Process.GetProcessById((int)explorer))
                if (process.SessionId != OwnSession) return windows;
            var visited = new HashSet<IntPtr>();
            for (IntPtr window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero && visited.Count < 2048; window = GetWindow(window, 2))
            {
                if (!visited.Add(window)) break;
                var candidate = new LayerWindow { Handle = window, Topmost = (Native.GetWindowLong(window, Native.GWL_EXSTYLE) & 8) != 0 };
                var name = new StringBuilder(128);
                Native.GetClassName(window, name, name.Capacity);
                string className = name.ToString();
                if ((className == "WorkerW" || className == "Progman" && window == shell) && Native.IsWindowVisible(window) && !Native.IsIconic(window) && GetAncestor(window, 2) == window)
                {
                    uint owner;
                    int cloaked;
                    Native.RECT bounds;
                    candidate.TrustedDesktop = Native.GetWindowThreadProcessId(window, out owner) != 0 && owner == explorer &&
                        DwmGetWindowAttribute(window, 14, out cloaked, 4) >= 0 && cloaked == 0 &&
                        Native.GetWindowRect(window, out bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top &&
                        bounds.Left <= dockBounds.Left && bounds.Top <= dockBounds.Top && bounds.Right >= dockBounds.Right && bounds.Bottom >= dockBounds.Bottom;
                }
                windows.Add(candidate);
            }
            return windows;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPosition
        {
            public IntPtr Window, InsertAfter;
            public int X, Y, Width, Height;
            public uint Flags;
        }
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        internal static IDictionary<string, bool> RegressionChecks()
        {
            IntPtr app = new IntPtr(11), dock = new IntPtr(12), glass = new IntPtr(13), desktop = new IntPtr(14), lowerDesktop = new IntPtr(15);
            var windows = new List<LayerWindow>
            {
                new LayerWindow { Handle = app },
                new LayerWindow { Handle = dock, TrustedDesktop = true },
                new LayerWindow { Handle = glass, TrustedDesktop = true },
                new LayerWindow { Handle = desktop, TrustedDesktop = true },
                new LayerWindow { Handle = lowerDesktop, TrustedDesktop = true }
            };
            var checks = new Dictionary<string, bool>();
            PlacementPlan plan = PlanPlacement(windows, dock, glass);
            checks["桌面层采用最高可信宿主"] = plan.CanApply && plan.DesktopAnchor == desktop;
            checks["普通应用之下桌面宿主之上规划"] = plan.CanApply && plan.InsertAfter == app;
            checks["自身Dock与玻璃不会冒充桌面锚点"] = plan.DesktopAnchor != dock && plan.DesktopAnchor != glass && plan.DesktopAnchor == desktop;
            var noAnchor = new List<LayerWindow> { new LayerWindow { Handle = app }, new LayerWindow { Handle = dock }, new LayerWindow { Handle = glass } };
            checks["缺少可信宿主不提交猜测定位"] = !PlanPlacement(noAnchor, dock, glass).CanApply;
            var topmostDesktop = new List<LayerWindow>
            {
                new LayerWindow { Handle = app },
                new LayerWindow { Handle = desktop, TrustedDesktop = true, Topmost = true },
                new LayerWindow { Handle = lowerDesktop, TrustedDesktop = true }
            };
            checks["最高可信宿主置顶时拒绝且不降级较低宿主"] = !PlanPlacement(topmostDesktop, dock, glass).CanApply;
            var topmost = new List<LayerWindow> { new LayerWindow { Handle = app, Topmost = true }, new LayerWindow { Handle = desktop, TrustedDesktop = true } };
            PlacementPlan topmostPlan = PlanPlacement(topmost, dock, glass);
            checks["最高桌面前驱置顶时不继承置顶"] = topmostPlan.CanApply && topmostPlan.InsertAfter == IntPtr.Zero;
            var desktopFirst = new List<LayerWindow> { new LayerWindow { Handle = desktop, TrustedDesktop = true } };
            checks["桌面已最高时在普通窗口层顶部放置"] = PlanPlacement(desktopFirst, dock, glass).CanApply && PlanPlacement(desktopFirst, dock, glass).InsertAfter == IntPtr.Zero;

            var calls = new List<Tuple<IntPtr, IntPtr, uint>>();
            var live = new HashSet<IntPtr> { dock, glass };
            var layer = new DesktopLayer(dock, () => glass, () => windows, live.Contains, (target, after, flags) => calls.Add(Tuple.Create(target, after, flags)));
            bool arranged = layer.Refresh();
            checks["实际定位只操作自身Dock和玻璃"] = arranged && calls.Count == 2 && calls[0].Item1 == dock && calls[1].Item1 == glass;
            checks["玻璃严格保持在自己Dock之后"] = calls.Count == 2 && calls[0].Item2 == app && calls[1].Item2 == dock;
            checks["层级更新保留几何不激活且不改变owner层级"] = calls.Count == 2 && calls.All(x => (x.Item3 & 3u) == 3u && (x.Item3 & 0x210u) == 0x210u);
            layer.Dispose();
            int afterDispose = calls.Count;
            layer.Refresh();
            checks["Dispose后不再安排定位"] = calls.Count == afterDispose && afterDispose == 2;

            calls.Clear();
            live.Remove(dock);
            var stale = new DesktopLayer(dock, () => glass, () => windows, live.Contains, (target, after, flags) => calls.Add(Tuple.Create(target, after, flags)));
            checks["自身句柄失效时不操作任何窗口"] = !stale.Refresh() && calls.Count == 0;
            stale.Dispose();
            live.Add(dock);
            var noDesktop = new DesktopLayer(dock, () => glass, () => noAnchor, live.Contains, (target, after, flags) => calls.Add(Tuple.Create(target, after, flags)));
            checks["无锚点保守保留当前位置不碰外部窗口"] = !noDesktop.Refresh() && calls.Count == 0;
            noDesktop.Dispose();
            return checks;
        }
    }
}