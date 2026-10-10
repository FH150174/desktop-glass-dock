using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopGlassDock
{
    // 仅绘制玻璃底面，子内容保持清晰，不对图标施加模糊。
    internal sealed class LiquidGlassSurface : Border, IDisposable
    {
        private readonly double radius;
        private readonly DispatcherTimer timer;
        private readonly Brush tint, sheen, rim, colorRim;
        private Window window;
        private HwndSource source;
        private GlassBackdrop backdrop;
        private bool disposed, loaded, tracking;
        private Point target = new Point(0.5, 0.15), light = new Point(0.5, 0.15);
        private double strength, targetStrength;

        internal static bool NativeBackdropEnabled = true;
        public string BackdropMode { get { return !NativeBackdropEnabled ? "透明高光（原生背景已禁用，用于对照）" : backdrop == null ? "透明高光（原生背景尚未创建）" : backdrop.Mode; } }
        public bool NativeBackdropAccepted { get { return NativeBackdropEnabled && backdrop != null && backdrop.Accepted; } }
        public IntPtr BackdropHandle { get { return backdrop == null ? IntPtr.Zero : backdrop.Handle; } }

        public LiquidGlassSurface(double cornerRadius)
        {
            radius = Math.Max(0, cornerRadius);
            CornerRadius = new CornerRadius(radius);
            // 绘制由本类统一完成，避免 Border 的透明底与内容层叠加产生额外合成矩形。
            Background = null;
            BorderThickness = new Thickness(0);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            // 提高玻璃底色透光率，图标与文字仍保持自身不透明度。
            tint = Gradient(new Point(0, 0), new Point(0.85, 1),
                new GradientStop(Color.FromArgb(24, 43, 51, 65), 0),
                new GradientStop(Color.FromArgb(36, 23, 29, 40), 0.55),
                new GradientStop(Color.FromArgb(28, 35, 42, 56), 1));
            sheen = Gradient(new Point(0, 0), new Point(0, 1),
                new GradientStop(Color.FromArgb(14, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(4, 246, 252, 255), 0.36),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.7));
            rim = Gradient(new Point(0.05, 0), new Point(0.9, 1),
                new GradientStop(Color.FromArgb(178, 250, 253, 255), 0),
                new GradientStop(Color.FromArgb(54, 221, 237, 255), 0.28),
                new GradientStop(Color.FromArgb(29, 204, 216, 245), 0.65),
                new GradientStop(Color.FromArgb(112, 249, 250, 255), 1));
            colorRim = Gradient(new Point(0, 1), new Point(1, 0),
                new GradientStop(Color.FromArgb(16, 83, 226, 255), 0),
                new GradientStop(Color.FromArgb(0, 175, 217, 255), 0.45),
                new GradientStop(Color.FromArgb(15, 212, 166, 255), 1));
            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += Update;
            Loaded += SurfaceLoaded;
            Unloaded += SurfaceUnloaded;
            IsVisibleChanged += VisibilityChanged;
            MouseMove += PointerMoved;
            MouseLeave += PointerLeft;
        }

        private static Brush Gradient(Point start, Point end, params GradientStop[] stops)
        {
            var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            foreach (GradientStop stop in stops) brush.GradientStops.Add(stop);
            brush.Freeze();
            return brush;
        }

        public void Attach(Window owner)
        {
            if (disposed) throw new ObjectDisposedException("LiquidGlassSurface");
            if (owner == null) throw new ArgumentNullException("owner");
            if (window == owner) return;
            if (window != null) throw new InvalidOperationException("玻璃面板不能重复绑定不同窗口。");
            window = owner;
            window.SourceInitialized += SourceReady;
            window.Closed += WindowClosed;
            window.LocationChanged += OwnerChanged;
            window.SizeChanged += OwnerSizeChanged;
            window.StateChanged += OwnerChanged;
            window.IsVisibleChanged += VisibilityChanged;
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) InitializeBackdrop(handle);
            RefreshTracking();
        }

        private void SourceReady(object sender, EventArgs e)
        {
            InitializeBackdrop(new WindowInteropHelper(window).Handle);
            RefreshTracking();
        }

        private void InitializeBackdrop(IntPtr handle)
        {
            if (disposed || backdrop != null || handle == IntPtr.Zero || !NativeBackdropEnabled) return;
            source = HwndSource.FromHwnd(handle);
            if (source != null) source.AddHook(OwnerMessage);
            backdrop = new GlassBackdrop(handle, window.Topmost);
            InvalidateVisual();
        }

        private IntPtr OwnerMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 只同步玻璃层，不消费主窗口输入消息。
            if (message == 0x0018 && wParam == IntPtr.Zero)
            {
                if (backdrop != null) backdrop.Hide();
            }
            else if (message == 0x031E || message == 0x001A)
            {
                if (backdrop != null) backdrop.RefreshComposition();
                InvalidateVisual();
            }
            else if (message == 0x0047 || message == 0x02E0) SyncBackdrop();
            return IntPtr.Zero;
        }

        private void SurfaceLoaded(object sender, RoutedEventArgs e) { loaded = true; RefreshTracking(); }
        private void SurfaceUnloaded(object sender, RoutedEventArgs e) { loaded = false; RefreshTracking(); }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { RefreshTracking(); }
        private void OwnerChanged(object sender, EventArgs e) { SyncBackdrop(); }
        private void OwnerSizeChanged(object sender, SizeChangedEventArgs e) { SyncBackdrop(); }
        private void WindowClosed(object sender, EventArgs e) { Dispose(); }

        private void RefreshTracking()
        {
            if (disposed) return;
            tracking = loaded && IsVisible && window != null && window.IsVisible;
            if (tracking) { timer.Start(); SyncBackdrop(); }
            else { timer.Stop(); if (backdrop != null) backdrop.Hide(); }
        }

        private void Update(object sender, EventArgs e)
        {
            if (disposed) return;
            SyncBackdrop();
            double dx = target.X - light.X, dy = target.Y - light.Y, ds = targetStrength - strength;
            if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(ds) < 0.003) return;
            light = new Point(light.X + dx * 0.24, light.Y + dy * 0.24);
            strength += ds * 0.22;
            InvalidateVisual();
        }

        private void PointerMoved(object sender, MouseEventArgs e)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            Point point = e.GetPosition(this);
            target = new Point(Math.Max(0, Math.Min(1, point.X / ActualWidth)), Math.Max(0, Math.Min(1, point.Y / ActualHeight)));
            targetStrength = 1;
        }

        private void PointerLeft(object sender, MouseEventArgs e) { targetStrength = 0; }

        private void SyncBackdrop()
        {
            if (backdrop == null) return;
            if (!NativeBackdropEnabled || !tracking || window == null || !window.IsVisible || window.WindowState == WindowState.Minimized ||
                !IsVisible || ActualWidth < 2 || ActualHeight < 2 || Opacity < 0.82 || window.Opacity < 0.82)
            { backdrop.Hide(); return; }
            try
            {
                Point start = PointToScreen(new Point(0, 0)), end = PointToScreen(new Point(ActualWidth, ActualHeight));
                double width = Math.Abs(end.X - start.X), height = Math.Abs(end.Y - start.Y);
                double scale = Math.Min(width / ActualWidth, height / ActualHeight);
                backdrop.Place((int)Math.Round(Math.Min(start.X, end.X)), (int)Math.Round(Math.Min(start.Y, end.Y)),
                    Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)),
                    Math.Max(0, (int)Math.Round(radius * scale)));
            }
            catch (InvalidOperationException)
            {
                // 卸载视觉树时可能暂时没有渲染源，等待下一次有效布局。
                backdrop.Hide();
            }
        }

        protected override void OnRender(DrawingContext drawing)
        {
            // 不调用 Border.OnRender：只绘一次连续圆角底面，子内容仍由 WPF 正常绘制。
            double width = ActualWidth, height = ActualHeight;
            if (width < 2 || height < 2) return;
            var bounds = new Rect(0.5, 0.5, width - 1, height - 1);
            double curve = Math.Min(radius, Math.Min(width, height) / 2);
            drawing.DrawRoundedRectangle(tint, null, bounds, curve, curve);
            if (!NativeBackdropAccepted || SystemParameters.HighContrast)
                drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(SystemParameters.HighContrast ? (byte)190 : (byte)18, 17, 23, 32)), null, bounds, curve, curve);
            drawing.DrawRoundedRectangle(sheen, null, bounds, curve, curve);
            drawing.DrawRoundedRectangle(colorRim, null, bounds, curve, curve);
            drawing.DrawRoundedRectangle(null, new Pen(rim, 1), bounds, curve, curve);
            if (width > 7 && height > 7)
                drawing.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), 0.75),
                    new Rect(2, 2, width - 4, height - 4), Math.Max(0, curve - 1.5), Math.Max(0, curve - 1.5));
            if (strength <= 0.005) return;
            drawing.PushClip(new RectangleGeometry(bounds, curve, curve));
            var glow = new RadialGradientBrush
            {
                Center = light, GradientOrigin = light,
                RadiusX = Math.Min(0.52, 170 / Math.Max(1, width)),
                RadiusY = Math.Min(1.8, 125 / Math.Max(1, height))
            };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(35 * strength), 235, 247, 255), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 235, 247, 255), 1));
            drawing.DrawRectangle(glow, null, bounds);
            var lightRim = new RadialGradientBrush
            {
                Center = light, GradientOrigin = light,
                RadiusX = Math.Min(0.6, 210 / Math.Max(1, width)), RadiusY = 1.3
            };
            lightRim.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(138 * strength), 255, 255, 255), 0));
            lightRim.GradientStops.Add(new GradientStop(Color.FromArgb(0, 220, 240, 255), 1));
            drawing.DrawRoundedRectangle(null, new Pen(lightRim, 1.4), bounds, curve, curve);
            drawing.Pop();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Tick -= Update;
            Loaded -= SurfaceLoaded;
            Unloaded -= SurfaceUnloaded;
            IsVisibleChanged -= VisibilityChanged;
            MouseMove -= PointerMoved;
            MouseLeave -= PointerLeft;
            if (source != null) source.RemoveHook(OwnerMessage);
            if (window != null)
            {
                window.SourceInitialized -= SourceReady;
                window.Closed -= WindowClosed;
                window.LocationChanged -= OwnerChanged;
                window.SizeChanged -= OwnerSizeChanged;
                window.StateChanged -= OwnerChanged;
                window.IsVisibleChanged -= VisibilityChanged;
            }
            if (backdrop != null) backdrop.Dispose();
        }
    }

    // Win10 documented DwmEnableBlurBehindWindow 不再提供模糊。
    // 将非推荐 Accent 兼容调用限制在无输入的独立原生 HWND，不改变 layered WPF。
    internal sealed class GlassBackdrop : IDisposable
    {
        private const int WcaAccentPolicy = 19;
        private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpNoOwnerZOrder = 0x0200;
        private static readonly string WindowClass = "DesktopGlassDock.Backdrop." + System.Diagnostics.Process.GetCurrentProcess().Id;
        private static readonly WndProcDelegate WindowProc = BackdropMessage;
        private static readonly object ClassLock = new object();
        private static bool classReady;
        private readonly IntPtr foreground;
        private SetCompositionDelegate setComposition;
        private IntPtr handle;
        private bool shown, disposed, failedLogged;
        private int lastX, lastY, lastWidth, lastHeight, lastRadius;
        public string Mode { get; private set; }
        public bool Accepted { get; private set; }
        public IntPtr Handle { get { return handle; } }

        public GlassBackdrop(IntPtr foregroundHandle, bool topmost)
        {
            foreground = foregroundHandle;
            Mode = "透明高光（原生兼容路径不可用）";
            try
            {
                IntPtr user32 = GetModuleHandle("user32.dll");
                IntPtr function = user32 == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(user32, "SetWindowCompositionAttribute");
                if (function == IntPtr.Zero) throw new NotSupportedException("缺少窗口合成兼容入口。");
                setComposition = (SetCompositionDelegate)Marshal.GetDelegateForFunctionPointer(function, typeof(SetCompositionDelegate));
                RegisterBackdropClass();
                // owned popup 强制处于 owner 之上，因此不设置 owner，显式维持在主 HWND 后。
                // 无激活、工具窗口、鼠标穿透；无任务栏按钮、无鼠标捕获。
                handle = CreateWindowEx(0x08000000 | 0x00000080 | 0x00000020 | (topmost ? 0x00000008u : 0u),
                    WindowClass, "DesktopGlassDock Glass Backdrop", 0x80000000, 0, 0, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建玻璃背景层。");
                RefreshComposition();
            }
            catch (Exception ex) { Fail("玻璃背景初始化失败，使用透明高光回退。", ex); }
        }

        private static void RegisterBackdropClass()
        {
            lock (ClassLock)
            {
                if (classReady) return;
                var wc = new WndClassEx();
                wc.Size = (uint)Marshal.SizeOf(typeof(WndClassEx));
                wc.Proc = WindowProc;
                wc.Instance = GetModuleHandle(null);
                wc.ClassName = WindowClass;
                if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册玻璃背景窗口。");
                classReady = true;
            }
        }

        public void RefreshComposition()
        {
            if (disposed || handle == IntPtr.Zero || setComposition == null) return;
            try
            {
                bool composed;
                if (SystemParameters.HighContrast || DwmIsCompositionEnabled(out composed) < 0 || !composed)
                {
                    Accepted = false;
                    Mode = "透明高光（高对比度或桌面合成不可用）";
                    Hide();
                    return;
                }
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                if (DwmExtendFrameIntoClientArea(handle, ref margins) < 0)
                    throw new InvalidOperationException("原生背景扩展失败。");
                // 优先原生 Blur，避免 Acrylic 自动浅色染色与 layered WPF 叠加。
                // 回退 Acrylic 颜色使用 ABGR；API 成功不能代替实屏模糊验收。
                bool accepted = ApplyAccent(3, 0);
                string mode = "原生 Blur 兼容请求已接受（视觉待验收）";
                if (!accepted) { accepted = ApplyAccent(4, unchecked((int)0x18251F1A)); mode = "原生 Acrylic 兼容请求已接受（视觉待验收）"; }
                if (!accepted) throw new Win32Exception(Marshal.GetLastWin32Error(), "系统拒绝背景合成请求。");
                bool changed = !Accepted || Mode != mode;
                Accepted = true;
                Mode = mode;
                failedLogged = false;
                if (changed) Files.Log("液态玻璃：" + Mode + "；仅作用本程序背景层。SetWindowCompositionAttribute 是非推荐兼容 API，更新或透明设置可能使其回退。", null);
            }
            catch (Exception ex) { Fail("原生背景合成不可用，隐藏背景层并使用透明高光回退。", ex); }
        }

        private bool ApplyAccent(int state, int color)
        {
            var policy = new AccentPolicy { State = state, Flags = state == 3 ? 2 : 0, GradientColor = color };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AccentPolicy)));
            try
            {
                Marshal.StructureToPtr(policy, memory, false);
                var data = new CompositionData { Attribute = WcaAccentPolicy, Data = memory, Size = new IntPtr(Marshal.SizeOf(typeof(AccentPolicy))) };
                return setComposition(handle, ref data);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        public void Place(int x, int y, int width, int height, int corner)
        {
            if (disposed || !Accepted || handle == IntPtr.Zero) { Hide(); return; }
            bool geometryChanged = width != lastWidth || height != lastHeight || corner != lastRadius;
            if (geometryChanged)
            {
                IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, corner * 2, corner * 2);
                if (region == IntPtr.Zero) { Fail("无法裁切玻璃圆角，隐藏背景层。", new Win32Exception(Marshal.GetLastWin32Error())); return; }
                if (SetWindowRgn(handle, region, true) == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    DeleteObject(region);
                    Fail("玻璃圆角裁切失败，隐藏背景层。", new Win32Exception(error));
                    return;
                }
                // 成功后 region 归 Windows 所有，不再释放。
            }
            bool changed = !shown || geometryChanged || x != lastX || y != lastY;
            if (changed || GetWindow(handle, 3) != foreground)
                if (!SetWindowPos(handle, foreground, x, y, width, height, SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder))
                { Fail("玻璃背景定位失败，隐藏背景层。", new Win32Exception(Marshal.GetLastWin32Error())); return; }
            shown = true;
            lastX = x; lastY = y; lastWidth = width; lastHeight = height; lastRadius = corner;
        }

        public void Hide()
        {
            if (handle != IntPtr.Zero && shown) ShowWindow(handle, 0);
            shown = false;
        }

        private void Fail(string message, Exception ex)
        {
            Accepted = false;
            Mode = "透明高光（原生背景回退）";
            Hide();
            if (!failedLogged) Files.Log(message, ex);
            failedLogged = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Hide();
            if (handle != IntPtr.Zero)
            {
                if (!DestroyWindow(handle)) Files.Log("释放玻璃背景窗口失败。", new Win32Exception(Marshal.GetLastWin32Error()));
                handle = IntPtr.Zero;
            }
            Accepted = false;
        }

        private static IntPtr BackdropMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == 0x0046) DesktopLayer.FilterPosition(hwnd, lParam);
            if (message == 0x0084) return new IntPtr(-1); // HTTRANSPARENT，同线程主窗口正常接收输入。
            if (message == 0x0021) return new IntPtr(3); // MA_NOACTIVATE。
            if (message == 0x0014) return new IntPtr(1); // DWM 合成背景，不填充实心黑色。
            if (message == 0x000F) { ValidateRect(hwnd, IntPtr.Zero); return IntPtr.Zero; }
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SetCompositionDelegate(IntPtr hwnd, ref CompositionData data);
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy { public int State, Flags, GradientColor, Animation; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CompositionData { public int Attribute; public IntPtr Data, Size; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WndClassEx
        {
            public uint Size, Style;
            public WndProcDelegate Proc;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            [MarshalAs(UnmanagedType.LPWStr)] public string MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
            public IntPtr SmallIcon;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WndClassEx windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);
        [DllImport("dwmapi.dll")]
        private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    }
}
