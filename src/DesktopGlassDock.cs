using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]
namespace DesktopGlassDock
{
    public sealed class AppEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Target { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string ResolvedExe { get; set; }
        public string Kind { get; set; }
        public string IconPath { get; set; }
        public bool PreferredDock { get; set; }
    }

    internal static class Files
    {
        public static readonly string Base = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Data = Path.Combine(Base, "data");
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private static readonly object LogLock = new object();
        public static void Prepare() { Directory.CreateDirectory(Data); }
        public static T Read<T>(string path, T fallback)
        {
            if (!File.Exists(path)) return fallback;
            try { return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex) { Log("读取配置失败：" + path, ex); throw new InvalidDataException("无法读取配置：" + path + "。请查看日志后修复该文件。", ex); }
        }
        public static void Write(string path, object value)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, Json.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        public static void Log(string message, Exception ex)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Data);
                    File.AppendAllText(Path.Combine(Data, "dock.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + (ex == null ? "" : Environment.NewLine + ex) + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { Trace.WriteLine(message); }
        }
        public static void Alert(string message, Exception ex)
        {
            Log(message, ex);
            MessageBox.Show(message + (ex == null ? "" : "\n\n" + ex.Message) + "\n\n日志：" + Path.Combine(Data, "dock.log"), "桌面 Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal sealed class Catalog
    {
        public List<AppEntry> Items = new List<AppEntry>();
        private List<AppEntry> userItems;
        private HashSet<string> hidden;
        private List<string> dockIds;
        private readonly string basePath;
        private readonly string dataPath;
        public Catalog() : this(Files.Base, Files.Data) { }
        internal Catalog(string sourcePath, string settingsPath) { basePath = sourcePath; dataPath = settingsPath; Directory.CreateDirectory(dataPath); }
        private static bool SameId(string left, string right) { return String.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        public void Reload()
        {
            List<AppEntry> source = Files.Read(Path.Combine(basePath, "apps.json"), new List<AppEntry>()) ?? new List<AppEntry>();
            userItems = Files.Read(Path.Combine(dataPath, "user-apps.json"), new List<AppEntry>()) ?? new List<AppEntry>();
            hidden = new HashSet<string>(Files.Read(Path.Combine(dataPath, "hidden-ids.json"), new List<string>()) ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            string dockPath = File.Exists(Path.Combine(dataPath, "dock.json")) ? Path.Combine(dataPath, "dock.json") : Path.Combine(basePath, "dock.json");
            dockIds = File.Exists(dockPath) ? Files.Read(dockPath, new List<string>()) : null;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Items = source.Concat(userItems).Where(delegate(AppEntry item)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.Id) || String.IsNullOrWhiteSpace(item.Name) || String.IsNullOrWhiteSpace(item.Target))
                { Files.Log("忽略缺少 Id、名称或目标的应用条目。", null); return false; }
                return !hidden.Contains(item.Id) && ids.Add(item.Id);
            }).ToList();
            List<string> savedOrder = Files.Read(Path.Combine(dataPath, "app-order.json"), new List<string>()) ?? new List<string>();
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in savedOrder) if (!String.IsNullOrWhiteSpace(id) && !rank.ContainsKey(id)) rank.Add(id, rank.Count);
            Items = Items.OrderBy(x => rank.ContainsKey(x.Id) ? rank[x.Id] : Int32.MaxValue).ToList();
            if (dockIds == null)
            {
                dockIds = source.Concat(userItems).Where(x => x != null && x.PreferredDock && Items.Any(y => String.Equals(y.Id, x.Id, StringComparison.OrdinalIgnoreCase))).Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
                if (dockIds.Count == 0) dockIds = Items.Take(8).Select(x => x.Id).ToList();
            }
            dockIds = dockIds.Where(id => !String.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Where(id => Items.Any(x => String.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))).Take(8).ToList();
        }
        public List<AppEntry> DockItems { get { return dockIds.Select(id => Items.FirstOrDefault(x => String.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).ToList(); } }
        public bool IsPinned(AppEntry item) { return dockIds.Any(id => String.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)); }
        // 搜索排序只改匹配应用占用的槽位，其余应用始终保持原来的相对顺序。
        public bool MoveApp(string movingId, string beforeId, IEnumerable<string> scopeIds)
        {
            if (String.IsNullOrWhiteSpace(movingId)) return false;
            var scope = new HashSet<string>((scopeIds ?? Items.Select(x => x.Id)).Where(x => !String.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            List<AppEntry> selected = Items.Where(x => scope.Contains(x.Id)).ToList();
            AppEntry moving = selected.FirstOrDefault(x => SameId(x.Id, movingId));
            if (moving == null || (beforeId != null && !selected.Any(x => SameId(x.Id, beforeId)))) return false;
            if (SameId(movingId, beforeId)) return false;
            selected.Remove(moving);
            int destination = beforeId == null ? selected.Count : selected.FindIndex(x => SameId(x.Id, beforeId));
            if (destination < 0) return false;
            selected.Insert(destination, moving);
            var replacement = new List<AppEntry>(); int slot = 0;
            foreach (AppEntry item in Items) replacement.Add(scope.Contains(item.Id) ? selected[slot++] : item);
            if (Items.Select(x => x.Id).SequenceEqual(replacement.Select(x => x.Id), StringComparer.OrdinalIgnoreCase)) return false;
            Files.Write(Path.Combine(dataPath, "app-order.json"), replacement.Select(x => x.Id).ToArray());
            Items = replacement; return true;
        }
        public bool MoveDock(string movingId, string beforeId)
        {
            if (String.IsNullOrWhiteSpace(movingId) || !dockIds.Any(x => SameId(x, movingId))) return false;
            if (beforeId != null && !dockIds.Any(x => SameId(x, beforeId))) return false;
            if (SameId(movingId, beforeId)) return false;
            List<string> replacement = dockIds.Where(x => !SameId(x, movingId)).ToList();
            int destination = beforeId == null ? replacement.Count : replacement.FindIndex(x => SameId(x, beforeId));
            if (destination < 0) return false;
            replacement.Insert(destination, movingId);
            if (dockIds.SequenceEqual(replacement, StringComparer.OrdinalIgnoreCase)) return false;
            Files.Write(Path.Combine(dataPath, "dock.json"), replacement); dockIds = replacement; return true;
        }
        public bool Pin(AppEntry item)
        {
            if (IsPinned(item)) { dockIds.RemoveAll(id => String.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase)); }
            else
            {
                if (dockIds.Count >= 8) return false;
                dockIds.Add(item.Id);
            }
            Files.Write(Path.Combine(dataPath, "dock.json"), dockIds);
            return true;
        }
        public void Remove(AppEntry item)
        {
            userItems.RemoveAll(x => String.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            hidden.Add(item.Id);
            dockIds.RemoveAll(id => String.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase));
            Files.Write(Path.Combine(dataPath, "user-apps.json"), userItems);
            Files.Write(Path.Combine(dataPath, "hidden-ids.json"), hidden.ToArray());
            Files.Write(Path.Combine(dataPath, "dock.json"), dockIds);
            Items.Remove(item);
            Files.Write(Path.Combine(dataPath, "app-order.json"), Items.Select(x => x.Id).ToArray());
        }
        public void Add(string path)
        {
            var item = new AppEntry { Id = "user-" + Guid.NewGuid().ToString("N"), Name = Path.GetFileNameWithoutExtension(path), Target = path, Kind = "file", Arguments = "", WorkingDirectory = Path.GetDirectoryName(path), ResolvedExe = String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ? path : "" };
            if (String.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) ResolveShortcut(item);
            userItems.Add(item);
            Files.Write(Path.Combine(dataPath, "user-apps.json"), userItems);
            Items.Add(item);
            Files.Write(Path.Combine(dataPath, "app-order.json"), Items.Select(x => x.Id).ToArray());

        }
        private static void ResolveShortcut(AppEntry item)
        {
            object shell = null; object shortcut = null;
            try
            {
                Type type = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { item.Target });
                string target = Convert.ToString(shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null));
                item.Arguments = Convert.ToString(shortcut.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null));
                string working = Convert.ToString(shortcut.GetType().InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, shortcut, null));
                if (!String.IsNullOrWhiteSpace(working)) item.WorkingDirectory = working;
                if (String.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)) item.ResolvedExe = target;
            }
            catch (Exception ex) { Files.Log("快捷方式解析失败，仍通过原快捷方式启动：" + item.Target, ex); }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        public List<AppEntry> Search(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return Items.ToList();
            return Items.Where(x => x.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || x.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
    }

    internal static class Native
    {
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int GWL_EXSTYLE = -20;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_DPICHANGED = 0x02E0;
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfo(string path, uint attributes, out SHFILEINFO info, uint size, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int Width; public int Height; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAP { public int Type; public int Width; public int Height; public int WidthBytes; public ushort Planes; public ushort BitsPixel; public IntPtr Bits; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER
        {
            public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount; public uint Compression; public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter; public uint ColorsUsed; public uint ColorsImportant;
        }
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr bitmap); }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] public static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] public static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] pixels, ref BITMAPINFOHEADER info, uint usage);

        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT point, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        public static string ProcessPath(IntPtr hwnd)
        {
            uint id; GetWindowThreadProcessId(hwnd, out id);
            IntPtr handle = OpenProcess(0x1000, false, id);
            if (handle == IntPtr.Zero) return "";
            try { var text = new StringBuilder(32768); uint length = (uint)text.Capacity; return QueryFullProcessImageName(handle, 0, text, ref length) ? text.ToString() : ""; }
            finally { CloseHandle(handle); }
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint parameter, out RECT value, uint flags);
        public static Drawing.Rectangle PrimaryWorkAreaPixels()
        {
            RECT work;
            // 直接读取工作区，避免任务栏变化后 Screen 缓存仍保留旧尺寸。
            if (SystemParametersInfo(0x0030, 0, out work, 0) && work.Right > work.Left && work.Bottom > work.Top)
                return Drawing.Rectangle.FromLTRB(work.Left, work.Top, work.Right, work.Bottom);
            return Forms.Screen.PrimaryScreen.WorkingArea;
        }
        public static Rect PrimaryWorkArea()
        {
            Drawing.Rectangle work = PrimaryWorkAreaPixels();
            double scale = 1;
            try
            {
                uint x, y;
                if (GetDpiForMonitor(MonitorFromPoint(new POINT { X = work.Left + 1, Y = work.Top + 1 }, 1), 0, out x, out y) == 0) scale = x / 96.0;
            }
            catch (DllNotFoundException ex) { Files.Log("系统不支持按显示器读取 DPI，使用系统缩放。", ex); }
            catch (EntryPointNotFoundException ex) { Files.Log("系统缺少 DPI API，使用系统缩放。", ex); }
            return new Rect(work.Left / scale, work.Top / scale, work.Width / scale, work.Height / scale);
        }
    }

    internal sealed class IconResult
    {
        public BitmapSource Bitmap;
        public string Source;
        public string Path;
    }

    internal static class IconProvider
    {
        private static readonly Dictionary<string, IconResult> Cache = new Dictionary<string, IconResult>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();
        private static readonly Queue<Action> Work = new Queue<Action>();
        private static readonly AutoResetEvent WorkReady = new AutoResetEvent(false);
        private static Thread worker;
        private static string Key(AppEntry item) { return (item.IconPath ?? "") + "|" + item.Target + "|" + item.ResolvedExe + "|" + item.Kind; }
        public static BitmapSource Get(AppEntry item) { return Describe(item).Bitmap; }
        public static IconResult Describe(AppEntry item)
        {
            string key = Key(item); IconResult result;
            lock (CacheLock) { if (Cache.TryGetValue(key, out result)) return result; }
            result = Load(item);
            lock (CacheLock) { Cache[key] = result; }
            return result;
        }
        private static void QueueLoad(Action action)
        {
            lock (Work)
            {
                Work.Enqueue(action);
                if (worker == null)
                {
                    worker = new Thread(delegate()
                    {
                        while (true)
                        {
                            Action next = null;
                            lock (Work) { if (Work.Count > 0) next = Work.Dequeue(); }
                            if (next == null) { WorkReady.WaitOne(); continue; }
                            try { next(); } catch (Exception ex) { Files.Log("后台图标任务失败。", ex); }
                        }
                    });
                    worker.IsBackground = true; worker.Name = "高分辨率图标提取"; worker.SetApartmentState(ApartmentState.STA); worker.Start();
                }
            }
            WorkReady.Set();
        }
        private static IconResult Load(AppEntry item)
        {
            // 保留 PNG 的原像素和 ICO 中最大的帧，不先把小图放大到固定缓存尺寸。
            if (!String.IsNullOrWhiteSpace(item.IconPath) && File.Exists(item.IconPath))
            {
                try
                {
                    using (var stream = File.OpenRead(item.IconPath))
                    {
                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        BitmapFrame frame = decoder.Frames.OrderByDescending(x => (long)x.PixelWidth * x.PixelHeight).FirstOrDefault();
                        if (frame != null)
                        {
                            frame.Freeze();
                            return new IconResult { Bitmap = frame, Source = "原始资源最高分辨率帧", Path = item.IconPath };
                        }
                    }
                }
                catch (Exception ex) { Files.Log("指定图标读取失败，尝试系统高分辨率图标：" + item.Name, ex); }
            }
            var candidates = new List<string>();
            if (!String.IsNullOrWhiteSpace(item.ResolvedExe) && File.Exists(item.ResolvedExe)) candidates.Add(item.ResolvedExe);
            if (String.Equals(item.Kind, "app", StringComparison.OrdinalIgnoreCase)) candidates.Add("shell:AppsFolder\\" + item.Target);
            else if (!String.IsNullOrWhiteSpace(item.Target) && (File.Exists(item.Target) || Directory.Exists(item.Target))) candidates.Add(item.Target);
            IconResult best = null;
            foreach (string target in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    BitmapSource bitmap = ShellBitmap(target);
                    if (bitmap == null) continue;
                    var result = new IconResult { Bitmap = bitmap, Source = "Shell ImageFactory 256 请求（禁止放大）", Path = target };
                    if (best == null || Math.Min(bitmap.PixelWidth, bitmap.PixelHeight) > Math.Min(best.Bitmap.PixelWidth, best.Bitmap.PixelHeight)) best = result;
                    if (bitmap.PixelWidth >= 64 && bitmap.PixelHeight >= 64) return best;
                }
                catch (Exception ex) { Files.Log("系统高分辨率图标提取失败，尝试备用源：" + target, ex); }
            }
            if (best != null) return best;
            foreach (string target in candidates.Where(x => File.Exists(x) || Directory.Exists(x)))
            {
                Native.SHFILEINFO info;
                Native.SHGetFileInfo(target, 0, out info, (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), 0x00000100);
                if (info.hIcon == IntPtr.Zero) continue;
                try
                {
                    BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return new IconResult { Bitmap = bitmap, Source = "SHGetFileInfo 原始尺寸备用", Path = target };
                }
                catch (Exception ex) { Files.Log("备用图标转换失败：" + target, ex); }
                finally { Native.DestroyIcon(info.hIcon); }
            }
            return new IconResult { Bitmap = null, Source = "名称首字备用", Path = "" };
        }
        private static BitmapSource ShellBitmap(string target)
        {
            Native.IShellItemImageFactory factory = null; IntPtr bitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(Native.IShellItemImageFactory).GUID;
                int result = Native.SHCreateItemFromParsingName(target, IntPtr.Zero, ref iid, out factory);
                if (result < 0 || factory == null) return null;
                // ICONONLY 禁止文件缩略图；不设 SCALEUP，防止把低分辨率源伪装成 256 像素。
                result = factory.GetImage(new Native.SIZE { Width = 256, Height = 256 }, 0x00000005, out bitmap);
                if (result < 0 || bitmap == IntPtr.Zero) return null;
                return CopyShellBitmap(bitmap);
            }
            finally
            {
                if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
                if (factory != null && Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
            }
        }
        private static BitmapSource CopyShellBitmap(IntPtr handle)
        {
            Native.BITMAP native;
            if (Native.GetObject(handle, Marshal.SizeOf(typeof(Native.BITMAP)), out native) == 0) throw new InvalidOperationException("无法读取 Shell 图标尺寸。");
            int width = native.Width, height = Math.Abs(native.Height);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw new InvalidDataException("Shell 图标尺寸超出支持范围。");
            int stride = checked(width * 4); byte[] pixels = new byte[checked(stride * height)];
            var header = new Native.BITMAPINFOHEADER { Size = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0, SizeImage = (uint)pixels.Length };
            IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new InvalidOperationException("无法创建图标转换设备上下文。");
            try { if (Native.GetDIBits(dc, handle, 0, (uint)height, pixels, ref header, 0) != height) throw new InvalidOperationException("无法复制 Shell 图标像素。"); }
            finally { Native.DeleteDC(dc); }
            // Shell 的 32 位图保留预乘 Alpha；无 Alpha 的旧图标退化为不透明 BGRA。
            bool alpha = false;
            for (int i = 3; i < pixels.Length; i += 4) { if (pixels[i] != 0) { alpha = true; break; } }
            if (!alpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze(); return result;
        }
        public static FrameworkElement Visual(AppEntry item, double size)
        {
            var fallback = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.22), Background = new SolidColorBrush(Color.FromRgb(73, 84, 101)), Child = new TextBlock { Text = item.Name.Substring(0, 1), FontSize = size * 0.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true, UseLayoutRounding = true, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var container = new Grid { Width = size, Height = size, SnapsToDevicePixels = true, UseLayoutRounding = true }; container.Children.Add(fallback); container.Children.Add(image);
            Action<IconResult> apply = delegate(IconResult result)
            {
                if (result.Bitmap == null) return;
                image.Source = result.Bitmap; image.Visibility = Visibility.Visible; fallback.Visibility = Visibility.Collapsed;
            };
            IconResult cached;
            lock (CacheLock) { Cache.TryGetValue(Key(item), out cached); }
            if (cached != null) apply(cached);
            else QueueLoad(delegate
            {
                IconResult result = Describe(item);
                if (!image.Dispatcher.HasShutdownStarted) image.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { apply(result); }));
            });
            return container;
        }
    }
    internal static class Theme
    {
        public static readonly FontFamily Font = new FontFamily("Microsoft YaHei UI");
        public static Brush White(double alpha) { return new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 255, 255, 255)); }
        public static Style ButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, Font));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border)); border.Name = "border";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter); template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Border.BackgroundProperty, White(0.09), "border")); template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Border.BorderBrushProperty, White(0.65), "border")); template.Triggers.Add(focus);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true }; pressed.Setters.Add(new Setter(Border.BackgroundProperty, White(0.16), "border")); template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.3)); template.Triggers.Add(disabled);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }
        public static Border Glass(double radius)
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(125, 53, 56, 64), 0));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(150, 25, 28, 34), 1));
            return new Border { CornerRadius = new CornerRadius(radius), Background = gradient, BorderBrush = White(0.24), BorderThickness = new Thickness(1), Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 7, Opacity = 0.43 }, SnapsToDevicePixels = true };
        }
        private static void Spring(Animatable target, DependencyProperty property, double current, double[] values, int[] times)
        {
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            for (int i = 0; i < values.Length; i++)
                animation.KeyFrames.Add(new EasingDoubleKeyFrame(values[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(times[i]))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
        public static void Hover(Button button, double amount)
        {
            // 只变换内容；按钮槽位和命中范围不随回弹改变，快速扫过时不会反复触发进出。
            FrameworkElement visual = button.Content as FrameworkElement;
            if (visual == null) return;
            bool dock = amount >= 1.09;
            var transform = new TransformGroup(); var scale = new ScaleTransform(1, 1); var move = new TranslateTransform(); transform.Children.Add(scale); transform.Children.Add(move);
            visual.RenderTransform = transform; visual.RenderTransformOrigin = new Point(0.5, 0.75);
            button.MouseEnter += delegate
            {
                int[] t = dock ? new int[] { 75, 155, 235, 320 } : new int[] { 90, 170, 250 };
                double[] sizes = dock ? new double[] { amount + 0.045, amount - 0.018, amount + 0.007, amount } : new double[] { amount + 0.018, amount - 0.006, amount };
                Spring(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, sizes, t); Spring(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, sizes, t);
                Spring(move, TranslateTransform.YProperty, move.Y, dock ? new double[] { -7.5, -4.6, -6.5, -6 } : new double[] { -3, -1.5, -2 }, t);
            };
            button.MouseLeave += delegate
            {
                int[] t = new int[] { 85, 175, 280 };
                Spring(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, new double[] { 0.985, 1.005, 1 }, t);
                Spring(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, new double[] { 0.98, 1.008, 1 }, t);
                Spring(move, TranslateTransform.YProperty, move.Y, new double[] { 1.4, -0.5, 0 }, t);
            };
            button.Click += delegate
            {
                double rest = button.IsMouseOver ? amount : 1;
                double restY = button.IsMouseOver ? (dock ? -6 : -2) : 0;
                int[] t = dock ? new int[] { 55, 145, 245, 350, 445, 570 } : new int[] { 55, 135, 215, 310 };
                Spring(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, dock ? new double[] { 1.06, 1.18, 1.095, 1.15, 1.12, rest } : new double[] { 1.035, 1.085, 1.035, rest }, t);
                Spring(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, dock ? new double[] { 0.84, 1.22, 1.07, 1.17, 1.105, rest } : new double[] { 0.91, 1.10, 1.025, rest }, t);
                Spring(move, TranslateTransform.YProperty, move.Y, dock ? new double[] { 3, -13, -3.8, -8.5, -5, restY } : new double[] { 1.5, -5, -1, restY }, t);
            };
        }
        public static void Reflow(Button button, Point delta)
        {
            var move = new TranslateTransform(delta.X, delta.Y); button.RenderTransform = move;
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(delta.X, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(delta.Y, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
        }
        public static void Land(Button button)
        {
            FrameworkElement visual = button.Content as FrameworkElement;
            var group = visual == null ? null : visual.RenderTransform as TransformGroup;
            if (group == null) return;
            var scale = group.Children[0] as ScaleTransform; var move = group.Children[1] as TranslateTransform;
            int[] t = new int[] { 80, 165, 270 };
            Spring(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, new double[] { 1.085, 0.985, 1 }, t);
            Spring(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, new double[] { 0.94, 1.025, 1 }, t);
            Spring(move, TranslateTransform.YProperty, move.Y, new double[] { 2, -2, 0 }, t);
        }
        public static FrameworkElement SystemIcon(bool launchpad, double size)
        {
            var canvas = new Canvas { Width = 32, Height = 32 };
            if (launchpad)
            {
                for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++)
                {
                    var dot = new System.Windows.Shapes.Rectangle { Width = 5.6, Height = 5.6, RadiusX = 1.4, RadiusY = 1.4, Stroke = White(0.94), StrokeThickness = 1.15 };
                    Canvas.SetLeft(dot, 4 + col * 9); Canvas.SetTop(dot, 4 + row * 9); canvas.Children.Add(dot);
                }
            }
            else
            {
                var screen = new System.Windows.Shapes.Rectangle { Width = 26, Height = 19, RadiusX = 2.5, RadiusY = 2.5, Stroke = White(0.94), StrokeThickness = 1.6 };
                Canvas.SetLeft(screen, 3); Canvas.SetTop(screen, 3); canvas.Children.Add(screen);
                var stem = new System.Windows.Shapes.Line { X1 = 16, X2 = 16, Y1 = 22, Y2 = 27, Stroke = White(0.94), StrokeThickness = 1.6 }; canvas.Children.Add(stem);
                var bottom = new System.Windows.Shapes.Line { X1 = 9, X2 = 23, Y1 = 28, Y2 = 28, Stroke = White(0.94), StrokeThickness = 1.6 }; canvas.Children.Add(bottom);
            }
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }
    }

    internal sealed class GestureGuard
    {
        private bool dragging;
        private DateTime suppressUntil = DateTime.MinValue;
        public bool IsDragging { get { return dragging; } }
        public bool CanActivate(DateTime now) { return !dragging && now >= suppressUntil; }
        public void Begin() { dragging = true; }
        public void End(DateTime now) { dragging = false; suppressUntil = now.AddMilliseconds(280); }
        public static bool PastThreshold(Point start, Point current, double horizontal, double vertical)
        { return Math.Abs(current.X - start.X) > horizontal || Math.Abs(current.Y - start.Y) > vertical; }
    }

    internal sealed class DragCoordinator
    {
        private const string Format = "DesktopGlassDock.InternalReorder.v1";
        private readonly string token = Guid.NewGuid().ToString("N");
        private readonly DesktopController owner;
        private readonly GestureGuard guard = new GestureGuard();
        private string activeId;
        private string activeArea;
        private Button highlighted;
        private bool changed;
        private Exception dropError;
        public bool IsDragging { get { return guard.IsDragging; } }
        public bool CanActivate { get { return guard.CanActivate(DateTime.UtcNow); } }
        public DragCoordinator(DesktopController controller) { owner = controller; }
        public void AttachSource(Button button, string id, string area)
        {
            Point start = new Point(); bool armed = false;
            button.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            { if (!IsDragging && CanActivate) { start = e.GetPosition(button); armed = true; } };
            button.PreviewMouseLeftButtonUp += delegate { armed = false; };
            button.PreviewMouseRightButtonDown += delegate { armed = false; };
            button.LostMouseCapture += delegate { if (!IsDragging) armed = false; };
            button.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!armed || IsDragging || e.LeftButton != MouseButtonState.Pressed) return;
                if (!GestureGuard.PastThreshold(start, e.GetPosition(button), SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)) return;
                armed = false; e.Handled = true; Start(button, id, area);
            };
            button.QueryContinueDrag += delegate(object sender, QueryContinueDragEventArgs e)
            { if (IsDragging && e.EscapePressed) { e.Action = DragAction.Cancel; e.Handled = true; } };
        }
        private void Start(Button source, string id, string area)
        {
            if (IsDragging || !owner.Catalog.Items.Any(x => String.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) return;
            activeId = id; activeArea = area; changed = false; dropError = null; guard.Begin();
            double opacity = source.Opacity; source.Opacity = 0.42;
            DragDropEffects result = DragDropEffects.None;
            try
            {
                var data = new DataObject(); data.SetData(Format, token + "|" + area + "|" + id, false);
                result = DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
            }
            catch (Exception ex) { dropError = ex; }
            finally
            {
                ClearHighlight(); source.Opacity = opacity; guard.End(DateTime.UtcNow);
                owner.Launchpad.EndDrag(); activeId = null; activeArea = null;
                if (changed) owner.RefreshAfterDrag(id);
                Files.Log(changed ? "拖拽排序已保存：" + area + " / " + id : "拖拽已取消或顺序未变化：" + area + " / " + id + "，结果：" + result, null);
                if (dropError != null) Files.Alert("未能完成拖拽排序，原应用文件未被修改。", dropError);
            }
        }
        public bool Accept(IDataObject data, string area, out string id)
        {
            id = null;
            if (!IsDragging || data == null || !data.GetDataPresent(Format, false)) return false;
            string text = data.GetData(Format, false) as string;
            if (text == null) return false;
            string[] parts = text.Split(new char[] { '|' }, 3);
            if (parts.Length != 3 || !String.Equals(parts[0], token, StringComparison.Ordinal) || !String.Equals(parts[1], area, StringComparison.Ordinal) ||
                !String.Equals(activeArea, area, StringComparison.Ordinal) || !String.Equals(parts[2], activeId, StringComparison.Ordinal)) return false;
            if (!owner.Catalog.Items.Any(x => String.Equals(x.Id, parts[2], StringComparison.OrdinalIgnoreCase))) return false;
            id = parts[2]; return true;
        }
        public static string BeforeTarget(IEnumerable<string> order, string movingId, string targetId, bool after)
        {
            List<string> ids = order.Where(x => !String.Equals(x, movingId, StringComparison.OrdinalIgnoreCase)).ToList();
            int target = ids.FindIndex(x => String.Equals(x, targetId, StringComparison.OrdinalIgnoreCase));
            if (target < 0) return movingId;
            return after ? (target + 1 < ids.Count ? ids[target + 1] : null) : ids[target];
        }
        public static string PageEndBefore(IEnumerable<string> order, string movingId, int page)
        {
            List<string> remaining = order.Where(x => !String.Equals(x, movingId, StringComparison.OrdinalIgnoreCase)).ToList();
            int destination = Math.Min(remaining.Count, (Math.Max(0, page) + 1) * 35 - 1);
            return destination < remaining.Count ? remaining[destination] : null;
        }
        public void AttachTarget(Button button, string targetId, string area)
        {
            button.AllowDrop = true;
            button.DragOver += delegate(object sender, DragEventArgs e)
            {
                string id; bool accept = Accept(e.Data, area, out id);
                e.Effects = accept ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
                if (accept) Highlight(button, e.GetPosition(button).X >= button.ActualWidth / 2);
                else ClearHighlight();
            };
            button.DragLeave += delegate { if (highlighted == button) ClearHighlight(); };
            button.Drop += delegate(object sender, DragEventArgs e)
            {
                string id; e.Effects = DragDropEffects.None; e.Handled = true;
                if (!Accept(e.Data, area, out id)) return;
                bool after = e.GetPosition(button).X >= button.ActualWidth / 2;
                IEnumerable<string> order = area == "dock" ? owner.Catalog.DockItems.Select(x => x.Id) : owner.Launchpad.CurrentIds;
                string before = BeforeTarget(order, id, targetId, after);
                Drop(id, before, area); e.Effects = DragDropEffects.Move;
            };
        }
        public void Drop(string id, string beforeId, string area)
        {
            try
            {
                bool moved = area == "dock" ? owner.Catalog.MoveDock(id, beforeId) : owner.Catalog.MoveApp(id, beforeId, owner.Launchpad.CurrentIds);
                changed = changed || moved;
            }
            catch (Exception ex) { dropError = ex; }
            ClearHighlight();
        }
        private void Highlight(Button button, bool after)
        {
            if (highlighted != button) { ClearHighlight(); highlighted = button; }
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(235, 205, 239, 255));
            button.BorderThickness = after ? new Thickness(1, 1, 3, 1) : new Thickness(3, 1, 1, 1);
            button.Background = Theme.White(0.14);
        }
        public void ClearHighlight()
        {
            if (highlighted == null) return;
            highlighted.ClearValue(Control.BorderBrushProperty); highlighted.ClearValue(Control.BorderThicknessProperty); highlighted.ClearValue(Control.BackgroundProperty); highlighted = null;
        }
    }

    internal sealed class DockWindow : Window
    {
        private readonly DesktopController owner;
        private readonly StackPanel panel;
        private readonly StackPanel runningPanel;
        private readonly Border runningSeparator;
        private readonly LiquidGlassSurface shell;
        private readonly Dictionary<string, Button> runningButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private List<RunningAppGroup> runningGroups = new List<RunningAppGroup>();
        private string runningLayoutKey = "";
        private bool runningMenuOpen;
        private bool blankRightArmed;
        private bool blankRightShift;
        private int overflowCount;
        private HwndSource source;
        public IntPtr Handle { get; private set; }
        public double DesiredDockWidth { get { return 620 + (runningPanel.Children.Count == 0 ? 0 : 18 + runningPanel.Children.Count * 52); } }
        public int VisibleRunningCount { get { return runningButtons.Count; } }
        public int PersistentButtonCount { get { return panel.Children.OfType<Button>().Count(); } }
        public bool HasLaunchpadButton { get { return panel.Children.OfType<Button>().Any(x => String.Equals(x.Tag as string, "system-launchpad", StringComparison.Ordinal)); } }
        public int OverflowRunningCount { get { return overflowCount; } }
        public string GlassMode { get { return shell.BackdropMode; } }
        public bool NativeGlassAccepted { get { return shell.NativeBackdropAccepted; } }
        public IntPtr NativeGlassHandle { get { return shell.BackdropHandle; } }
        public DockWindow(DesktopController controller)
        {
            owner = controller;
            Title = "原生桌面 Dock"; Width = 620; Height = 112; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = false;
            FontFamily = Theme.Font; UseLayoutRounding = true; SnapsToDevicePixels = true;
            shell = new LiquidGlassSurface(36); shell.Margin = new Thickness(12, 7, 12, 13); shell.Padding = new Thickness(13, 1, 13, 1);
            panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            runningPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            runningSeparator = new Border { Width = 1, Height = 33, Margin = new Thickness(8, 0, 9, 0), Background = Theme.White(0.42), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, ToolTip = "正在运行的应用（按软件分组）" };
            var sections = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sections.Children.Add(panel); sections.Children.Add(runningSeparator); sections.Children.Add(runningPanel);
            shell.Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = sections }; Content = shell;
            shell.Attach(this);
            SourceInitialized += delegate
            {
                Handle = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, Native.GetWindowLong(Handle, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
                source = HwndSource.FromHwnd(Handle); source.AddHook(Hook);
                if (!Native.RegisterHotKey(Handle, 0x4450, 0x4003, 0x20))
                {
                    Files.Log("Ctrl+Alt+Space 快捷键注册失败，可能已被其他程序占用。", null);
                    owner.HotkeyAvailable = false;
                    owner.Notify("快捷键已被占用", "可通过 Dock 或托盘打开启动台，程序不会抢占已有快捷键。");
                }
                else { owner.HotkeyAvailable = true; Files.Log("已注册 Ctrl+Alt+Space。", null); }
            };
            Closed += delegate { if (Handle != IntPtr.Zero) Native.UnregisterHotKey(Handle, 0x4450); if (source != null) source.RemoveHook(Hook); shell.Dispose(); };
            shell.ToolTip = "右键 Dock 空白处打开应用启动台；Shift + 右键打开管理菜单。";
            var management = new ContextMenu();
            var show = new MenuItem { Header = "打开应用启动台" }; show.Click += delegate { owner.ShowLaunchpad(); }; management.Items.Add(show);
            var restore = new MenuItem { Header = "恢复 Windows 任务栏" }; restore.Click += delegate { owner.RestoreTaskbar(); }; management.Items.Add(restore);
            var quit = new MenuItem { Header = "退出 Dock（恢复 Windows 任务栏）" }; quit.Click += delegate { owner.Exit(); }; management.Items.Add(quit);
            management.Opened += delegate { runningMenuOpen = true; owner.Launchpad.SetContextOpen(true); };
            management.Closed += delegate { runningMenuOpen = false; owner.Launchpad.SetContextOpen(false); };
            shell.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                blankRightArmed = IsBlankSource(e.OriginalSource as DependencyObject) && owner.Drag.CanActivate;
                blankRightShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                if (blankRightArmed) e.Handled = true;
            };
            shell.PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                bool blankRelease = IsBlankSource(e.OriginalSource as DependencyObject);
                bool activate = blankRightArmed && blankRelease && owner.Drag.CanActivate;
                blankRightArmed = false;
                if (!activate) return;
                e.Handled = true;
                if (blankRightShift) { management.PlacementTarget = shell; management.Placement = PlacementMode.MousePoint; management.IsOpen = true; }
                else { Files.Log("右键 Dock 空白处打开应用启动台。", null); owner.ShowLaunchpad(); }
            };
        }
        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (msg == 0x0046) DesktopLayer.FilterPosition(hwnd, lparam);
            if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(3); }
            if (msg == Native.WM_HOTKEY && wparam.ToInt32() == 0x4450) { owner.ToggleLaunchpad(); handled = true; }
            if (msg == Native.WM_DPICHANGED) Dispatcher.BeginInvoke(new Action(owner.PositionWindows));
            return IntPtr.Zero;
        }
        private bool IsBlankSource(DependencyObject source)
        {
            // 子按钮和弹出菜单的右键仍属于各自的应用，只有玻璃空白能打开启动台。
            while (source != null)
            {
                if (source is ButtonBase || source is MenuItem || source is ContextMenu) return false;
                if (ReferenceEquals(source, shell)) return true;
                DependencyObject parent = null;
                if (source is Visual || source is System.Windows.Media.Media3D.Visual3D) parent = VisualTreeHelper.GetParent(source);
                if (parent == null)
                {
                    var content = source as FrameworkContentElement;
                    parent = content != null ? content.Parent : LogicalTreeHelper.GetParent(source);
                }
                source = parent;
            }
            return false;
        }
        public void RefreshApps() { RefreshApps(false, null); }
        public void RefreshApps(bool animate, string movedId)
        {
            var positions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            if (animate) foreach (Button old in panel.Children.OfType<Button>()) if (old.Tag is string) positions[(string)old.Tag] = old.TranslatePoint(new Point(), panel);
            panel.Children.Clear();

            Button desktop = SystemButton(false, "显示桌面 / 恢复窗口"); desktop.Click += delegate { if (owner.Drag.CanActivate) owner.ToggleDesktop(); }; panel.Children.Add(desktop);
            panel.Children.Add(new Border { Width = 1, Height = 27, Margin = new Thickness(6, 0, 6, 0), Background = Theme.White(0.25), VerticalAlignment = VerticalAlignment.Center });
            foreach (AppEntry entry in owner.Catalog.DockItems)
            {
                AppEntry item = entry;
                var button = new Button { Width = 52, Height = 70, Content = IconProvider.Visual(item, 46), ToolTip = item.Name, Style = Theme.ButtonStyle() };
                button.Tag = item.Id;
                Theme.Hover(button, 1.13); button.Click += delegate { owner.Launch(item); };
                owner.Drag.AttachSource(button, item.Id, "dock"); owner.Drag.AttachTarget(button, item.Id, "dock");
                button.ToolTip = item.Name + "（拖动调整顺序）";
                button.ContextMenu = owner.ItemMenu(item, false); panel.Children.Add(button);
            }
            if (animate)
            {
                panel.UpdateLayout();
                foreach (Button button in panel.Children.OfType<Button>())
                {
                    string id = button.Tag as string; if (id == null) continue;
                    Point old; if (positions.TryGetValue(id, out old)) { Point current = button.TranslatePoint(new Point(), panel); Theme.Reflow(button, new Point(old.X - current.X, old.Y - current.Y)); }
                    if (String.Equals(id, movedId, StringComparison.OrdinalIgnoreCase)) Theme.Land(button);
                }
            }
        }
        public void UpdateRunning(List<RunningAppGroup> groups)
        {
            runningGroups = groups ?? new List<RunningAppGroup>();
            int slots = Math.Max(1, Math.Min(8, (int)Math.Floor((Native.PrimaryWorkArea().Width - 24 - 620 - 18) / 52)));
            bool more = runningGroups.Count > slots;
            int shownCount = more ? Math.Max(0, slots - 1) : runningGroups.Count;
            var shown = runningGroups.Take(shownCount).ToList();
            string key = String.Join("|", shown.Select(x => x.Key)) + (more ? "|overflow" : "");
            // 指针停留或菜单展开时固定槽位，避免刷新把正在点击的图标移走。
            if (key != runningLayoutKey && (IsMouseOver || runningMenuOpen || owner.Drag.IsDragging)) return;
            if (key != runningLayoutKey)
            {
                runningLayoutKey = key; runningPanel.Children.Clear(); runningButtons.Clear();
                foreach (RunningAppGroup group in shown)
                {
                    var content = new Grid { Width = 46, Height = 58 };
                    var icon = IconProvider.Visual(group.IconEntry, 42); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(2, 3, 2, 0); content.Children.Add(icon);
                    var dot = new Border { Height = 4, Width = 4, CornerRadius = new CornerRadius(2), Background = Theme.White(0.80), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) }; content.Children.Add(dot);
                    var count = new TextBlock { FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(190, 36, 44, 55)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(3, 0, 3, 1) }; content.Children.Add(count);
                    var button = new Button { Width = 52, Height = 70, Content = content, Style = Theme.ButtonStyle(), Tag = group };
                    Theme.Hover(button, 1.13);
                    button.Click += delegate(object sender, RoutedEventArgs e) { var current = ((Button)sender).Tag as RunningAppGroup; if (current != null) owner.ActivateRunning(current, IntPtr.Zero); };
                    var context = new ContextMenu(); button.ContextMenu = context;
                    context.Opened += delegate { runningMenuOpen = true; owner.Launchpad.SetContextOpen(true); FillWindowsMenu(context, button.Tag as RunningAppGroup); };
                    context.Closed += delegate { runningMenuOpen = false; owner.Launchpad.SetContextOpen(false); };
                    runningButtons.Add(group.Key, button); runningPanel.Children.Add(button);
                }
                if (more)
                {
                    var overflow = new Button { Width = 52, Height = 70, Content = new TextBlock { Text = "···", FontSize = 28, Foreground = Brushes.White }, Style = Theme.ButtonStyle(), ToolTip = "更多正在运行的应用" };
                    Theme.Hover(overflow, 1.1); var menu = new ContextMenu(); overflow.ContextMenu = menu;
                    overflow.Click += delegate { if (owner.Drag.CanActivate) { menu.PlacementTarget = overflow; menu.IsOpen = true; } };
                    menu.Opened += delegate
                    {
                        runningMenuOpen = true; owner.Launchpad.SetContextOpen(true); menu.Items.Clear();
                        foreach (RunningAppGroup entry in runningGroups.Where(x => !runningButtons.ContainsKey(x.Key)))
                        {
                            RunningAppGroup group = entry; var item = new MenuItem { Header = group.Name + "（" + group.Windows.Count + " 个窗口）" };
                            foreach (RunningWindowInfo window in group.Windows) AddWindowMenuItem(item.Items, group, window);
                            menu.Items.Add(item);
                        }
                    };
                    menu.Closed += delegate { runningMenuOpen = false; owner.Launchpad.SetContextOpen(false); };
                    runningPanel.Children.Add(overflow);
                }
                runningSeparator.Visibility = runningPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                owner.PositionWindows();
            }
            overflowCount = Math.Max(0, runningGroups.Count - runningButtons.Count);
            foreach (RunningAppGroup group in shown)
            {
                Button button; if (!runningButtons.TryGetValue(group.Key, out button)) continue;
                button.Tag = group;
                var content = (Grid)button.Content; var dot = (Border)content.Children[1]; var count = (TextBlock)content.Children[2];
                dot.Width = group.IsActive ? 15 : 4; dot.Background = group.IsActive ? new SolidColorBrush(Color.FromRgb(150, 225, 255)) : Theme.White(0.80);
                count.Text = group.Windows.Count > 1 ? group.Windows.Count.ToString(CultureInfo.InvariantCulture) : ""; count.Visibility = group.Windows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                button.ToolTip = group.Name + " · " + group.Windows.Count + " 个窗口\n" + String.Join("\n", group.Windows.Take(3).Select(x => x.Title.Length > 80 ? x.Title.Substring(0, 80) + "…" : x.Title)) + "\n单击切换窗口；右键选择窗口";
            }
        }
        private void FillWindowsMenu(ContextMenu menu, RunningAppGroup group)
        {
            menu.Items.Clear(); if (group == null) return;
            var heading = new MenuItem { Header = group.Name + " · 正在运行", IsEnabled = false }; menu.Items.Add(heading); menu.Items.Add(new Separator());
            foreach (RunningWindowInfo window in group.Windows) AddWindowMenuItem(menu.Items, group, window);
        }
        private void AddWindowMenuItem(ItemCollection items, RunningAppGroup group, RunningWindowInfo window)
        {
            IntPtr handle = window.Handle; var item = new MenuItem { Header = new TextBlock { Text = window.Title, MaxWidth = 440, TextTrimming = TextTrimming.CharacterEllipsis } };
            item.Click += delegate { owner.ActivateRunning(group, handle); }; items.Add(item);
        }
        private Button SystemButton(bool launchpad, string tip)
        {
            var button = new Button { Width = 52, Height = 70, Content = Theme.SystemIcon(launchpad, 42), ToolTip = tip, Style = Theme.ButtonStyle() };
            button.Tag = launchpad ? "system-launchpad" : "system-desktop";
            Theme.Hover(button, 1.1); return button;
        }
        public bool CursorAtBottomEdge(int pixels)
        {
            Native.POINT cursor; Native.RECT rect; Drawing.Rectangle screen = Forms.Screen.PrimaryScreen.Bounds;
            return Handle != IntPtr.Zero && Native.GetCursorPos(out cursor) && Native.GetWindowRect(Handle, out rect) &&
                cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= screen.Bottom - pixels && cursor.Y < screen.Bottom;
        }
        public bool ContainsCursor()
        {
            if (!IsVisible || Handle == IntPtr.Zero) return false;
            Native.POINT cursor; Native.RECT rect;
            return Native.GetCursorPos(out cursor) && Native.GetWindowRect(Handle, out rect) && cursor.X >= rect.Left && cursor.X <= rect.Right && cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;
        }
    }

    internal sealed class LaunchpadWindow : Window
    {
        private readonly DesktopController owner;
        private readonly LiquidGlassSurface shell;
        private readonly TextBox search;
        private readonly Grid tiles;
        private readonly TextBlock pageText;
        private readonly Button previous;
        private readonly Button next;
        private readonly ScaleTransform scale;
        private readonly DispatcherTimer deactivateTimer;
        private readonly DispatcherTimer pageHoverTimer;
        private int hoverDirection;
        private int page;
        private int animationVersion;
        private bool opened;
        private bool contextOpen;
        public bool IsOpened { get { return opened; } }
        public List<string> CurrentIds { get { return owner.Catalog.Search(search.Text).Select(x => x.Id).ToList(); } }
        public IntPtr Handle { get { return new WindowInteropHelper(this).Handle; } }
        public LaunchpadWindow(DesktopController controller)
        {
            owner = controller;
            Title = "应用启动台"; Width = 1180; Height = 630; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true; FontFamily = Theme.Font; UseLayoutRounding = true; SnapsToDevicePixels = true;
            shell = new LiquidGlassSurface(30); shell.Margin = new Thickness(15); shell.Padding = new Thickness(35, 22, 35, 18);
            scale = new ScaleTransform(1, 1); shell.RenderTransform = scale; shell.RenderTransformOrigin = new Point(0.5, 0.5); Content = shell; shell.Attach(this);
            var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(37) }); shell.Child = root;
            var toolbar = new Grid(); Grid.SetRow(toolbar, 0); root.Children.Add(toolbar); var middle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top }; toolbar.Children.Add(middle);
            var searchBorder = new Border { Width = 540, Height = 39, CornerRadius = new CornerRadius(12), Background = Theme.White(0.09), BorderBrush = Theme.White(0.13), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            var searchGrid = new Grid(); searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) }); searchGrid.ColumnDefinitions.Add(new ColumnDefinition()); searchBorder.Child = searchGrid;
            searchGrid.Children.Add(new TextBlock { Text = "⌕", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 27, Foreground = Theme.White(0.55), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -3, 0, 0), IsHitTestVisible = false });
            search = new TextBox { Background = Brushes.Transparent, Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(1, 0, 8, 0), ToolTip = "搜索应用；按 Enter 打开第一个结果" }; Grid.SetColumn(search, 1); searchGrid.Children.Add(search);
            var placeholder = new TextBlock { Text = "搜索应用", Foreground = Theme.White(0.45), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0), IsHitTestVisible = false }; Grid.SetColumn(placeholder, 1); searchGrid.Children.Add(placeholder);
            search.TextChanged += delegate { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; page = 0; Refresh(false, 0); };
            search.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AppEntry first = owner.Catalog.Search(search.Text).FirstOrDefault(); if (first != null) owner.Launch(first); e.Handled = true; } };
            middle.Children.Add(searchBorder); SizeChanged += delegate { searchBorder.Width = Math.Max(180, Math.Min(540, ActualWidth - 270)); };
            var add = new Button { Content = "+  添加", Width = 76, Height = 38, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Style = Theme.ButtonStyle(), Background = Theme.White(0.09), ToolTip = "添加本机应用、快捷方式或文件" }; middle.Children.Add(add); add.Click += delegate { owner.AddFile(); };
            var close = new Button { Content = "×", Width = 36, Height = 38, FontSize = 22, VerticalAlignment = VerticalAlignment.Top, Style = Theme.ButtonStyle(), ToolTip = "关闭启动台（Esc）" }; close.HorizontalAlignment = HorizontalAlignment.Right; toolbar.Children.Add(close); close.Click += delegate { HideAnimated(); };
            tiles = new Grid { ClipToBounds = true, Background = Brushes.Transparent, AllowDrop = true }; for (int r = 0; r < 5; r++) tiles.RowDefinitions.Add(new RowDefinition()); for (int c = 0; c < 7; c++) tiles.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(tiles, 1); root.Children.Add(tiles);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            previous = new Button { Content = "‹", Width = 34, Height = 30, FontSize = 26, Style = Theme.ButtonStyle(), ToolTip = "上一页" }; previous.Click += delegate { ChangePage(-1); }; footer.Children.Add(previous);
            pageText = new TextBlock { Width = 85, Foreground = Theme.White(0.72), FontSize = 12, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; footer.Children.Add(pageText);
            next = new Button { Content = "›", Width = 34, Height = 30, FontSize = 26, Style = Theme.ButtonStyle(), ToolTip = "下一页" }; next.Click += delegate { ChangePage(1); }; footer.Children.Add(next); Grid.SetRow(footer, 2); root.Children.Add(footer);
            pageHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            pageHoverTimer.Tick += delegate
            {
                if (!owner.Drag.IsDragging || hoverDirection == 0) { StopPageHover(); return; }
                int oldPage = page; ChangePage(hoverDirection); if (oldPage == page) StopPageHover();
            };
            AttachPageHover(previous, -1); AttachPageHover(next, 1);
            tiles.DragOver += delegate(object sender, DragEventArgs e)
            {
                string id; bool accept = owner.Drag.Accept(e.Data, "launchpad", out id);
                e.Effects = accept ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
                owner.Drag.ClearHighlight();
                if (accept) { StopPageHover(); pageText.Text = "释放到页末"; }
            };
            tiles.DragLeave += delegate { UpdatePageLabel(); };
            tiles.Drop += delegate(object sender, DragEventArgs e)
            {
                string id; e.Effects = DragDropEffects.None; e.Handled = true;
                if (!owner.Drag.Accept(e.Data, "launchpad", out id)) return;
                owner.Drag.Drop(id, DragCoordinator.PageEndBefore(CurrentIds, id, page), "launchpad"); e.Effects = DragDropEffects.Move; UpdatePageLabel();
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (owner.Drag.IsDragging) return;
                if (e.Key == Key.Escape) { HideAnimated(); e.Handled = true; }
                else if (!search.IsKeyboardFocusWithin && e.Key == Key.PageDown) { ChangePage(1); e.Handled = true; }
                else if (!search.IsKeyboardFocusWithin && e.Key == Key.PageUp) { ChangePage(-1); e.Handled = true; }
            };
            deactivateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            deactivateTimer.Tick += delegate { deactivateTimer.Stop(); if (opened && !IsActive && !contextOpen && !owner.Drag.IsDragging && !owner.Dock.ContainsCursor()) HideAnimated(); };
            Deactivated += delegate { if (opened && !contextOpen && !owner.Drag.IsDragging) { deactivateTimer.Stop(); deactivateTimer.Start(); } };
            SourceInitialized += delegate { var handle = new WindowInteropHelper(this).Handle; Native.SetWindowLong(handle, Native.GWL_EXSTYLE, Native.GetWindowLong(handle, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW); };
        }
        private void AttachPageHover(Button arrow, int direction)
        {
            arrow.AllowDrop = true;
            arrow.DragOver += delegate(object sender, DragEventArgs e)
            {
                string id; bool accept = owner.Drag.Accept(e.Data, "launchpad", out id);
                e.Effects = accept ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true;
                owner.Drag.ClearHighlight();
                if (!accept) { StopPageHover(); return; }
                if (hoverDirection != direction) { StopPageHover(); hoverDirection = direction; pageHoverTimer.Start(); }
                arrow.Background = Theme.White(0.20); arrow.ToolTip = "保持悬停以拖到" + (direction < 0 ? "上一页" : "下一页");
            };
            arrow.DragLeave += delegate { StopPageHover(); };
            arrow.Drop += delegate(object sender, DragEventArgs e) { e.Effects = DragDropEffects.None; e.Handled = true; StopPageHover(); };
        }
        private void StopPageHover()
        {
            pageHoverTimer.Stop(); hoverDirection = 0;
            previous.ClearValue(Control.BackgroundProperty); next.ClearValue(Control.BackgroundProperty);
            previous.ToolTip = "上一页（拖动时悬停可翻页）"; next.ToolTip = "下一页（拖动时悬停可翻页）";
        }
        private void UpdatePageLabel()
        {
            int count = Math.Max(1, (owner.Catalog.Search(search.Text).Count + 34) / 35);
            pageText.Text = (page + 1) + " / " + count;
        }
        public void EndDrag()
        {
            StopPageHover(); UpdatePageLabel();
            if (opened && !IsActive && !contextOpen) { deactivateTimer.Stop(); deactivateTimer.Start(); }
        }
        public void SetContextOpen(bool value) { contextOpen = value; }
        public void RefreshAfterDrag(string movedId)
        {
            var positions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            foreach (Button old in tiles.Children.OfType<Button>()) if (old.Tag is string) positions[(string)old.Tag] = old.TranslatePoint(new Point(), tiles);
            Refresh(false, 0); tiles.UpdateLayout();
            foreach (Button button in tiles.Children.OfType<Button>())
            {
                string id = button.Tag as string; if (id == null) continue;
                Point old; if (positions.TryGetValue(id, out old)) { Point current = button.TranslatePoint(new Point(), tiles); Theme.Reflow(button, new Point(old.X - current.X, old.Y - current.Y)); }
                if (String.Equals(id, movedId, StringComparison.OrdinalIgnoreCase)) Theme.Land(button);
            }
        }
        public void Refresh(bool animate, int direction)
        {

            List<AppEntry> all = owner.Catalog.Search(search.Text); int count = Math.Max(1, (all.Count + 34) / 35); page = Math.Max(0, Math.Min(page, count - 1));
            tiles.Children.Clear();
            int index = 0;
            foreach (AppEntry entry in all.Skip(page * 35).Take(35))
            {
                AppEntry item = entry;
                var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                content.Children.Add(IconProvider.Visual(item, 45));
                content.Children.Add(new TextBlock { Text = item.Name, Foreground = Theme.White(0.95), FontSize = 11.5, MaxWidth = 128, Margin = new Thickness(2, 8, 2, 0), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
                var button = new Button { Content = content, Margin = new Thickness(4, 2, 4, 2), Style = Theme.ButtonStyle(), ToolTip = item.Name, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                button.Tag = item.Id; Theme.Hover(button, 1.055); button.Click += delegate { owner.Launch(item); }; button.ContextMenu = owner.ItemMenu(item, true);
                button.ToolTip = item.Name + "（拖动调整顺序，悬停页箭头可跨页移动）";
                owner.Drag.AttachSource(button, item.Id, "launchpad"); owner.Drag.AttachTarget(button, item.Id, "launchpad");
                Grid.SetColumn(button, index % 7); Grid.SetRow(button, index / 7); tiles.Children.Add(button); index++;
            }
            if (all.Count == 0) { var empty = new TextBlock { Text = "没有匹配的应用", Foreground = Theme.White(0.55), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumnSpan(empty, 7); Grid.SetRowSpan(empty, 5); tiles.Children.Add(empty); }
            UpdatePageLabel(); previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < count;
            tiles.BeginAnimation(OpacityProperty, null); tiles.Opacity = 1;
            var move = new TranslateTransform(); tiles.RenderTransform = move;
            if (animate)
            {
                tiles.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 32, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        private void ChangePage(int change)
        {
            int pages = Math.Max(1, (owner.Catalog.Search(search.Text).Count + 34) / 35); int target = page + change;
            if (target < 0 || target >= pages) return;
            page = target; Refresh(true, change);
        }
        public void ShowAnimated()
        {
            deactivateTimer.Stop();
            if (opened) { Activate(); search.Focus(); return; }
            opened = true; animationVersion++; owner.PositionWindows();
            search.Text = ""; page = 0; Refresh(false, 0);
            Show(); Activate();
            shell.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210)));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate { if (opened) search.Focus(); }));
        }
        public void HideAnimated()
        {
            deactivateTimer.Stop(); if (!opened || owner.Drag.IsDragging) return;
            opened = false; int version = ++animationVersion;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(135));
            fade.Completed += delegate { if (!opened && animationVersion == version) Hide(); };
            shell.BeginAnimation(OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.975, TimeSpan.FromMilliseconds(135)));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.975, TimeSpan.FromMilliseconds(135)));
        }
    }
    internal static class Launcher
    {
        public static void Run(AppEntry item)
        {
            if (String.IsNullOrWhiteSpace(item.Arguments) && !String.IsNullOrWhiteSpace(item.ResolvedExe) && TryActivate(item.ResolvedExe)) return;
            if (String.Equals(item.Kind, "app", StringComparison.OrdinalIgnoreCase))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(item.Target, "^[A-Za-z0-9_.!~+-]+$")) throw new InvalidDataException("应用标识包含不支持的字符。");
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + item.Target) { UseShellExecute = true });
                Files.Log("通过 Windows 应用标识启动：" + item.Name, null); return;
            }
            if (!File.Exists(item.Target)) throw new FileNotFoundException("原应用或快捷方式已不存在。可移除该入口后重新添加。", item.Target);
            var start = new ProcessStartInfo(item.Target) { Arguments = String.Equals(Path.GetExtension(item.Target), ".lnk", StringComparison.OrdinalIgnoreCase) ? "" : (item.Arguments ?? ""), UseShellExecute = true };
            if (!String.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory)) start.WorkingDirectory = item.WorkingDirectory;
            Process.Start(start); Files.Log("启动应用或文件：" + item.Name, null);
        }
        public static bool TryActivate(string executable)
        {
            if (!File.Exists(executable)) return false;
            string name = Path.GetFileName(executable);
            if (new string[] { "explorer.exe", "rundll32.exe", "ApplicationFrameHost.exe", "cmd.exe", "powershell.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
            string canonical = Path.GetFullPath(executable); IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != IntPtr.Zero) return true;
                if (String.Equals(Native.ProcessPath(hwnd), canonical, StringComparison.OrdinalIgnoreCase)) { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return false;
            Native.ShowWindow(found, Native.IsIconic(found) ? 9 : 5);
            if (!Native.SetForegroundWindow(found)) Files.Log("系统限制前台激活，请从任务栏确认窗口：" + name, null);
            else Files.Log("切换至已有应用窗口：" + name, null);
            return true;
        }
        public static void ToggleDesktop()
        {
            object shell = null;
            try
            {
                Type type = Type.GetTypeFromProgID("Shell.Application"); shell = Activator.CreateInstance(type);
                type.InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shell, null);
            }
            finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
        }
    }

    internal sealed class DesktopController
    {
        public readonly Catalog Catalog = new Catalog();
        public readonly DockWindow Dock;
        public readonly LaunchpadWindow Launchpad;
        public readonly DragCoordinator Drag;
        private Forms.NotifyIcon tray;
        private TaskbarManager taskbar;
        private DispatcherTimer visibilityTimer;
        private DispatcherTimer runningTimer;
        private int runningPollBusy;
        private string runningSnapshotKey = "";
        private DateTime lastRunningError = DateTime.MinValue;
        private DesktopVisibility.ForegroundHook foregroundHook;
        private DesktopLayer desktopLayer;
        private bool visibilityWriteFailed;
        private string lastVisibilityState = "";
        private bool exiting;
        public bool HotkeyAvailable;
        public DesktopController()
        {
            Catalog.Reload(); Drag = new DragCoordinator(this); Dock = new DockWindow(this); Launchpad = new LaunchpadWindow(this); Dock.RefreshApps(); Launchpad.Refresh(false, 0); PositionWindows();
        }
        public void Start(bool showLaunchpad)
        {
            tray = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Application, Text = "桌面 Dock · Ctrl+Alt+Space", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开应用启动台", null, delegate { Application.Current.Dispatcher.Invoke(new Action(ShowLaunchpad)); });
            menu.Items.Add("重新载入应用目录", null, delegate { Application.Current.Dispatcher.Invoke(new Action(Reload)); });
            menu.Items.Add("恢复 Windows 任务栏", null, delegate { Application.Current.Dispatcher.Invoke(new Action(RestoreTaskbar)); });
            menu.Items.Add("开机启动设置…", null, delegate { Application.Current.Dispatcher.Invoke(new Action(delegate
            {
                try
                {
                    string configured = "";
                    using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                    {
                        if (run != null) configured = Convert.ToString(run.GetValue("DesktopGlassDock", ""));
                    }
                    string expected = "\"" + Assembly.GetExecutingAssembly().Location + "\"";
                    bool enabled = String.Equals(configured.Trim(), expected, StringComparison.OrdinalIgnoreCase);
                    MessageBox.Show("当前开机启动：" + (enabled ? "已启用" : "未启用") + "\n\n可通过交付的 管理Dock.ps1 启用、关闭开机启动或恢复原桌面。\n\n配置目录：" + Files.Base, "开机启动状态", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex) { Files.Alert("无法读取开机启动状态。", ex); }
            })); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出 Dock（恢复 Windows 任务栏）", null, delegate { Application.Current.Dispatcher.Invoke(new Action(Exit)); }); tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Application.Current.Dispatcher.Invoke(new Action(ShowLaunchpad)); };
            // 只在真实驻留启动时隐藏任务栏；构造诊断窗口不会触碰系统任务栏。
            taskbar = new TaskbarManager();
            try { taskbar.Start(); PositionWindows(); }
            catch (Exception ex)
            {
                DisposeTaskbar(); PositionWindows();
                Notify("任务栏隐藏未完成", "已保留或恢复 Windows 任务栏。详情见 Dock 日志。" + ex.Message);
                Files.Log("隐藏 Windows 任务栏失败。", ex);
            }
            new WindowInteropHelper(Dock).EnsureHandle();
            desktopLayer = new DesktopLayer(Dock.Handle, () => Dock.NativeGlassHandle);
            UpdateVisibility();
            try { foregroundHook = new DesktopVisibility.ForegroundHook(Application.Current.Dispatcher, delegate { if (taskbar != null) taskbar.RefreshForShellUi(); UpdateVisibility(); }); }
            catch (Exception ex) { Files.Log("前台监听未启用，继续使用周期校验。", ex); }
            visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) }; visibilityTimer.Tick += delegate { UpdateVisibility(); }; visibilityTimer.Start();
            runningTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) }; runningTimer.Tick += delegate { PollRunningApps(); }; runningTimer.Start(); PollRunningApps();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            Application.Current.SessionEnding += delegate { Exit(); };
            if (showLaunchpad) Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ShowLaunchpad));
            Files.Log("桌面 Dock 已启动。", null);
        }
                private void PollRunningApps()
        {
            if (exiting || Interlocked.CompareExchange(ref runningPollBusy, 1, 0) != 0) return;
            AppEntry[] catalog = Catalog.Items.ToArray(); Dispatcher dispatcher = Application.Current.Dispatcher;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<RunningAppGroup> groups = RunningApps.Capture(catalog);
                    if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (exiting || Drag.IsDragging) return;
                        Dock.UpdateRunning(groups);
                        string signature = String.Join("|", groups.Select(x => x.Key + ":" + x.IsActive + ":" + String.Join(",", x.Windows.Select(w => w.Handle.ToInt64().ToString() + w.Title)))) + Dock.GlassMode;
                        if (signature != runningSnapshotKey)
                        {
                            runningSnapshotKey = signature;
                            try { Files.Write(Path.Combine(Files.Data, "running-apps.json"), new { Time = DateTime.Now.ToString("o"), GroupCount = groups.Count, VisibleGroups = Dock.VisibleRunningCount, OverflowGroups = Dock.OverflowRunningCount, NativeBackdropAccepted = Dock.NativeGlassAccepted, GlassMode = Dock.GlassMode, Groups = groups.Select(x => new { x.Key, x.Name, x.IsActive, Windows = x.Windows.Select(w => new { Handle = w.Handle.ToInt64(), w.Title }).ToArray() }).ToArray(), Limitations = new string[] { "按真实顶层窗口与可执行文件分组，不读取浏览器标签内容。", "背景合成 API 接受请求不等于视觉模糊已验收；物理点击仍需单独验收。" } }); }
                            catch (Exception ex) { Files.Log("无法写入正在运行区域的本机诊断。", ex); }
                        }
                    }));
                }
                catch (Exception ex)
                {
                    if ((DateTime.UtcNow - lastRunningError).TotalSeconds >= 60) { lastRunningError = DateTime.UtcNow; Files.Log("刷新运行窗口失败，保留上一次窗口列表。", ex); }
                }
                finally { Interlocked.Exchange(ref runningPollBusy, 0); }
            });
        }
        public void ActivateRunning(RunningAppGroup group, IntPtr window)
        {
            if (!Drag.CanActivate || group == null) return;
            Launchpad.HideAnimated();
            try
            {
                if (RunningApps.Activate(group, window)) Files.Log("切换正在运行的应用：" + group.Name, null);
                else Notify("窗口暂时无法切换", "窗口可能已关闭，或 Windows 限制了前台激活；可稍后重试，或按 Alt+Tab 切换窗口。");
            }
            catch (Exception ex) { Files.Alert("无法切换“" + group.Name + "”的窗口。", ex); }
        }
        private void DisplayChanged(object sender, EventArgs e) { if (!exiting) Application.Current.Dispatcher.BeginInvoke(new Action(PositionWindows)); }
        public void PositionWindows()
        {
            Rect work = Native.PrimaryWorkArea();
            Dock.Width = Math.Min(Dock.DesiredDockWidth, Math.Max(220, work.Width - 24)); Dock.Height = 112;
            Dock.Left = work.Left + (work.Width - Dock.Width) / 2; Dock.Top = Math.Max(work.Top, work.Bottom - Dock.Height - 28);
            Launchpad.Width = Math.Min(1180, Math.Max(320, work.Width - 60)); Launchpad.Height = Math.Min(630, Math.Max(240, work.Height - 60));
            Launchpad.Left = work.Left + (work.Width - Launchpad.Width) / 2;
            Launchpad.Top = Math.Max(work.Top + 14, work.Top + (work.Height - Launchpad.Height) / 2 - 34);
        }
        private void UpdateVisibility()
        {
            if (exiting) return;
            DesktopVisibility.Snapshot foreground = DesktopVisibility.Capture();
            // 前台仅用于诊断；Dock 常驻桌面层，让应用按实际窗口范围自然覆盖。
            bool show = DesktopVisibility.Decide(foreground.HasForeground, foreground.OwnWindow, foreground.ValidatedDesktop, false);
            try
            {
                if (desktopLayer != null && (!Dock.IsVisible || Dock.WindowState == WindowState.Minimized))
                    desktopLayer.Refresh();
                if (Dock.WindowState == WindowState.Minimized || Native.IsIconic(Dock.Handle)) Dock.WindowState = WindowState.Normal;
                if (show && !Dock.IsVisible) Dock.Show();
                if (desktopLayer != null) desktopLayer.Refresh();
            }
            catch (Exception ex)
            {
                if (desktopLayer != null) desktopLayer.RecordError("桌面层排列失败，保留当前位置并等待再次确认。", ex);
                else Files.Log("桌面层尚未初始化。", ex);
            }
            IntPtr glass = Dock.NativeGlassHandle;
            var state = new
            {
                Mode = "DesktopLayer", foreground.ForegroundHandle, foreground.ForegroundProcessId, foreground.ForegroundClass,
                foreground.ValidatedDesktop, ForegroundReason = foreground.Reason,
                VisibilityReason = "常驻桌面层，由普通应用窗口自然遮挡；前台变化不隐藏 Dock",
                Layer = desktopLayer == null ? null : desktopLayer.Status(),
                DockVisible = Dock.IsVisible, DockTopmost = Dock.Topmost,
                DockBackdropVisible = glass != IntPtr.Zero && Native.IsWindowVisible(glass),
                DockBackdropTopmost = glass != IntPtr.Zero && (Native.GetWindowLong(glass, Native.GWL_EXSTYLE) & 8) != 0,
                LaunchpadOpened = Launchpad.IsOpened
            };
            string key = Files.Json.Serialize(state);
            if (key != lastVisibilityState)
            {
                try { var record = new { UpdatedAt = DateTime.Now.ToString("o"), State = state }; Files.Write(Path.Combine(Files.Data, "desktop-visibility-status.json"), record); File.AppendAllText(Path.Combine(Files.Data, "desktop-visibility-history.jsonl"), Files.Json.Serialize(record) + Environment.NewLine, Encoding.UTF8); lastVisibilityState = key; }
                catch (Exception ex) { if (!visibilityWriteFailed) Files.Log("桌面显示状态记录失败。", ex); visibilityWriteFailed = true; }
            }
        }
        public void ToggleLaunchpad() { if (Launchpad.IsOpened) Launchpad.HideAnimated(); else ShowLaunchpad(); }
        public void ShowLaunchpad() { UpdateVisibility(); Launchpad.ShowAnimated(); UpdateVisibility(); }
        public void ToggleDesktop()
        {
            try { Launchpad.HideAnimated(); Launcher.ToggleDesktop(); Files.Log("切换显示桌面。", null); }
            catch (Exception ex) { Files.Alert("无法切换显示桌面。", ex); }
        }
        public void Launch(AppEntry item)
        {
            if (!Drag.CanActivate) return;
            try { Launchpad.HideAnimated(); Launcher.Run(item); }
            catch (Exception ex) { Files.Alert("无法打开“" + item.Name + "”。", ex); }
        }
        public ContextMenu ItemMenu(AppEntry item, bool includeRemove)
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "打开 " + item.Name }; open.Click += delegate { Launch(item); }; menu.Items.Add(open);
            var pin = new MenuItem { Header = Catalog.IsPinned(item) ? "从 Dock 取消固定" : "固定到 Dock" };
            pin.Click += delegate
            {
                try
                {
                    if (!Catalog.Pin(item)) { MessageBox.Show("Dock 最多固定 8 个应用。请先取消固定一个入口。", "桌面 Dock", MessageBoxButton.OK, MessageBoxImage.Information); return; }
                    Refresh();
                }
                catch (Exception ex) { Files.Alert("无法保存 Dock 固定设置。", ex); }
            }; menu.Items.Add(pin);
            if (includeRemove)
            {
                menu.Items.Add(new Separator()); var remove = new MenuItem { Header = "从启动台移除（保留原文件）" };
                remove.Click += delegate { try { Catalog.Remove(item); Refresh(); } catch (Exception ex) { Files.Alert("无法保存移除设置。", ex); } }; menu.Items.Add(remove);
            }
            menu.Opened += delegate { Launchpad.SetContextOpen(true); };
            menu.Closed += delegate { Launchpad.SetContextOpen(false); };
            return menu;
        }
        public void AddFile()
        {
            try
            {
                Launchpad.SetContextOpen(true);
                var dialog = new OpenFileDialog { Title = "添加到应用启动台（保留源文件）", Filter = "应用与快捷方式|*.exe;*.lnk;*.url|所有文件|*.*", Multiselect = true, CheckFileExists = true, RestoreDirectory = true };
                if (dialog.ShowDialog(Launchpad) == true) { foreach (string path in dialog.FileNames) Catalog.Add(path); Refresh(); }
            }
            catch (Exception ex) { Files.Alert("无法添加所选文件。", ex); }
            finally { Launchpad.SetContextOpen(false); }
        }
        public void Refresh() { Dock.RefreshApps(); Launchpad.Refresh(false, 0); }
        public void RefreshAfterDrag(string movedId) { Dock.RefreshApps(true, movedId); Launchpad.RefreshAfterDrag(movedId); }
        private void Reload() { try { Catalog.Reload(); Refresh(); } catch (Exception ex) { Files.Alert("重新载入应用目录失败。", ex); } }
        public void Notify(string title, string message)
        {
            Files.Log(title + "：" + message, null);
            if (tray != null) { tray.BalloonTipTitle = title; tray.BalloonTipText = message; tray.ShowBalloonTip(5000); }
        }
        public void RestoreTaskbar()
        {
            try
            {
                if (taskbar != null) taskbar.DisableAndRestore();
                else TaskbarManager.RestoreSavedState();
                PositionWindows();
                Notify("Windows 任务栏已恢复", "已关闭任务栏隐藏，后续启动也保留系统任务栏；可通过管理脚本 HideTaskbar 再次隐藏。");
            }
            catch (Exception ex) { Files.Alert("无法完整恢复 Windows 任务栏。请保留配置备份并运行管理脚本的 RestoreTaskbar 操作。", ex); }
        }
        internal void DisposeTaskbar()
        {
            if (foregroundHook != null) { foregroundHook.Dispose(); foregroundHook = null; }
            if (taskbar == null) return;
            try { taskbar.Dispose(); }
            catch (Exception ex) { Files.Log("退出时恢复任务栏失败，可通过管理脚本 RestoreTaskbar 重试。", ex); }
            finally { taskbar = null; }
        }
        public void Exit()
        {
            if (exiting) return; exiting = true;
            try
            {
                if (visibilityTimer != null) visibilityTimer.Stop(); if (runningTimer != null) runningTimer.Stop(); SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                if (desktopLayer != null) { desktopLayer.Dispose(); desktopLayer = null; }
                Launchpad.Close(); Dock.Close();
            }
            finally
            {
                DisposeTaskbar();
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; } }
                finally { Files.Log("桌面 Dock 已退出，任务栏恢复收尾已执行；恢复结果见前述日志。", null); Application.Current.Shutdown(); }
            }
        }
    }

    internal static class Diagnostics
    {
        public static int InteractionSelfTest()
        {
            string fixture = Path.Combine(Files.Data, "interaction-self-test", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string source = Path.Combine(fixture, "source"), settings = Path.Combine(fixture, "settings");
            Directory.CreateDirectory(source); Directory.CreateDirectory(settings);
            var cases = new Dictionary<string, bool>();
            Action<string, bool> check = delegate(string name, bool result) { cases[name] = result; };
            var apps = Enumerable.Range(1, 80).Select(i => new AppEntry { Id = "app-" + i.ToString("00"), Name = "测试应用 " + i, Target = Assembly.GetExecutingAssembly().Location, Kind = "file", PreferredDock = i <= 8 }).ToList();
            Files.Write(Path.Combine(source, "apps.json"), apps); Files.Write(Path.Combine(source, "dock.json"), apps.Take(8).Select(x => x.Id).ToArray());
            var catalog = new Catalog(source, settings); catalog.Reload();
            check("初始80个应用和8个Dock应用", catalog.Items.Count == 80 && catalog.DockItems.Count == 8);
            check("页内移到首位", catalog.MoveApp("app-03", "app-01", null) && catalog.Items[0].Id == "app-03");
            check("跨页移到第50项前", catalog.MoveApp("app-03", "app-50", null) && catalog.Items.FindIndex(x => x.Id == "app-03") + 1 == catalog.Items.FindIndex(x => x.Id == "app-50"));
            check("空白末尾插入", catalog.MoveApp("app-40", null, null) && catalog.Items.Last().Id == "app-40");
            string[] scope = catalog.Items.Where(x => Int32.Parse(x.Id.Substring(4)) % 2 == 0).Select(x => x.Id).ToArray();
            string[] unaffected = catalog.Items.Where(x => !scope.Contains(x.Id)).Select(x => x.Id).ToArray();
            check("搜索结果内排序", catalog.MoveApp("app-04", "app-02", scope) && catalog.Items.FindIndex(x => x.Id == "app-04") < catalog.Items.FindIndex(x => x.Id == "app-02"));
            check("搜索未匹配项保持相对顺序", unaffected.SequenceEqual(catalog.Items.Where(x => !scope.Contains(x.Id)).Select(x => x.Id)));
            string[] order = catalog.Items.Select(x => x.Id).ToArray();
            var reloaded = new Catalog(source, settings); reloaded.Reload();
            check("启动台顺序保存重载", order.SequenceEqual(reloaded.Items.Select(x => x.Id)));
            check("Dock移动到首位", catalog.MoveDock("app-08", "app-01") && catalog.DockItems[0].Id == "app-08");
            reloaded.Reload(); check("Dock顺序保存重载", catalog.DockItems.Select(x => x.Id).SequenceEqual(reloaded.DockItems.Select(x => x.Id)));
            check("系统入口及外部无效Id不可排序", !catalog.MoveDock("system-launchpad", "app-01") && !catalog.MoveDock("system-desktop", null) && !catalog.MoveApp("unknown", null, null) && !catalog.MoveApp("app-01", "unknown", null) && !catalog.MoveDock("app-01", "unknown"));
            check("自身和空范围幂等", !catalog.MoveApp("app-01", "app-01", null) && !catalog.MoveDock("app-01", "app-01") && !catalog.MoveApp("app-01", null, new string[0]));
            string[] before = catalog.Items.Select(x => x.Id).ToArray();
            string blocker = Path.Combine(settings, "app-order.json.tmp"); Directory.CreateDirectory(blocker); bool saveFailed = false;
            try { catalog.MoveApp("app-01", null, null); } catch (IOException) { saveFailed = true; } catch (UnauthorizedAccessException) { saveFailed = true; }
            check("保存失败不会提前改变内存顺序", saveFailed && before.SequenceEqual(catalog.Items.Select(x => x.Id))); Directory.Delete(blocker);
            string addedPath = Path.Combine(fixture, "新增测试入口.txt"); File.WriteAllText(addedPath, "仅用于入口保存测试，不启动。", Encoding.UTF8);
            catalog.Add(addedPath); string addedId = catalog.Items.Last().Id; reloaded.Reload();
            check("新增入口兼容既有顺序", reloaded.Items.Count == 81 && reloaded.Items.Last().Id == addedId);
            catalog.Remove(catalog.Items.First(x => x.Id == addedId)); reloaded.Reload();
            check("移除入口仅改配置并保留原文件", reloaded.Items.Count == 80 && !reloaded.Items.Any(x => x.Id == addedId) && File.Exists(addedPath));
            Files.Write(Path.Combine(settings, "app-order.json"), new string[] { "app-02", "app-02", "missing", null, "", "app-01" });
            Files.Write(Path.Combine(settings, "dock.json"), new string[] { "app-01", "app-01", "missing", null, "app-02" });
            reloaded.Reload();
            check("重复和失效排序Id安全归一化", reloaded.Items.Count == 80 && reloaded.Items.Select(x => x.Id).Distinct().Count() == 80 && reloaded.Items[0].Id == "app-02" && reloaded.DockItems.Count == 2);
            var guard = new GestureGuard(); DateTime now = DateTime.UtcNow;
            check("普通点击允许启动", guard.CanActivate(now));
            guard.Begin(); check("拖动过程中禁止启动", !guard.CanActivate(now));
            guard.End(now); check("完成和取消后的鼠标释放禁止启动", !guard.CanActivate(now.AddMilliseconds(200)) && guard.CanActivate(now.AddMilliseconds(300)));
            check("拖拽阈值保护普通点击", !GestureGuard.PastThreshold(new Point(0, 0), new Point(3, 3), 4, 4) && !GestureGuard.PastThreshold(new Point(0, 0), new Point(4, 4), 4, 4) && GestureGuard.PastThreshold(new Point(0, 0), new Point(5, 0), 4, 4) && GestureGuard.PastThreshold(new Point(0, 0), new Point(0, 5), 4, 4));
            check("目标前后插入计算", DragCoordinator.BeforeTarget(new string[] { "a", "b", "c" }, "a", "b", false) == "b" && DragCoordinator.BeforeTarget(new string[] { "a", "b", "c" }, "a", "b", true) == "c" && DragCoordinator.BeforeTarget(new string[] { "a", "b", "c" }, "a", "c", true) == null);
            var pageEndCatalog = new Catalog(source, Path.Combine(fixture, "page-end-settings")); pageEndCatalog.Reload();
            check("后页拖回第一页背景落在第35槽", pageEndCatalog.MoveApp("app-75", DragCoordinator.PageEndBefore(pageEndCatalog.Items.Select(x => x.Id), "app-75", 0), null) && pageEndCatalog.Items.FindIndex(x => x.Id == "app-75") == 34);
            check("前页拖到第二页背景落在第70槽", pageEndCatalog.MoveApp("app-01", DragCoordinator.PageEndBefore(pageEndCatalog.Items.Select(x => x.Id), "app-01", 1), null) && pageEndCatalog.Items.FindIndex(x => x.Id == "app-01") == 69);
            bool passed = cases.Values.All(x => x);
            Files.Write(Path.Combine(Files.Data, "interaction-self-test.json"), new { Passed = passed, Time = DateTime.Now.ToString("o"), FixtureDirectory = fixture, RealUserOrderUntouched = true, Cases = cases, Limitations = new string[] { "未模拟或实测物理鼠标拖拽，悬停自动翻页和弹性动画需要实际桌面验收。", "测试未启动任何应用，也未覆盖 Windows OLE 拖放消息循环的端到端行为。" } });
            Files.Log("拖拽交互逻辑自检完成，结果：" + passed, null); return passed ? 0 : 2;
        }
                public static int RunningSelfTest()
        {
            var catalog = new Catalog(); catalog.Reload();
            var groups = RunningApps.Capture(catalog.Items);
            var cases = new Dictionary<string, bool>();
            foreach (var result in RunningApps.LogicSelfTest()) cases["窗口模块：" + result.Key] = result.Value;
            cases["软件分组标识唯一"] = groups.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == groups.Count;
            cases["每组包含真实窗口和图标入口"] = groups.All(x => x.Windows.Count > 0 && x.Windows.All(w => w.Handle != IntPtr.Zero && !String.IsNullOrWhiteSpace(w.Title)) && x.IconEntry != null);
            var controller = new DesktopController();
            var orderPath = Path.Combine(Files.Data, "app-order.json"); string order = File.Exists(orderPath) ? File.ReadAllText(orderPath) : null;
            var checks = new List<object>();
            foreach (int size in new int[] { 0, 1, 8, 16, 40 })
            {
                var sample = new List<RunningAppGroup>();
                for (int n = 0; n < size; n++) sample.Add(new RunningAppGroup { Key = "fixture-" + n, Name = "隔离窗口组" + n, IconEntry = catalog.Items[0], Windows = new List<RunningWindowInfo> { new RunningWindowInfo { Handle = new IntPtr(n + 1), Title = "仅验证布局的隔离窗口" } } });
                controller.Dock.UpdateRunning(sample); controller.PositionWindows();
                Rect work = Native.PrimaryWorkArea(); bool fits = controller.Dock.Left >= work.Left && controller.Dock.Left + controller.Dock.Width <= work.Right + 1;
                bool reachable = controller.Dock.VisibleRunningCount + controller.Dock.OverflowRunningCount == size;
                cases["运行区域" + size + "组适配屏幕且所有分组可达"] = fits && reachable;
                checks.Add(new { GroupCount = size, controller.Dock.Width, controller.Dock.VisibleRunningCount, controller.Dock.OverflowRunningCount, Fits = fits, AllReachable = reachable });
            }
            cases["窗口枚举和布局不改变用户排列"] = (File.Exists(orderPath) ? File.ReadAllText(orderPath) : null) == order;
            bool passed = cases.Values.All(x => x);
            Files.Write(Path.Combine(Files.Data, "running-self-test.json"), new { Passed = passed, Time = DateTime.Now.ToString("o"), Cases = cases, LayoutChecks = checks, LiveGroupCount = groups.Count, LiveGroups = groups.Select(x => new { x.Key, x.Name, Windows = x.Windows.Select(w => new { Handle = w.Handle.ToInt64(), w.Title }).ToArray() }).ToArray(), Limitations = new string[] { "自检未切换或最小化任何真实窗口。", "原生模糊需在实际桌面中检查，不以组件渲染作为视觉验收。" } });
            controller.Launchpad.Close(); controller.Dock.Close(); return passed ? 0 : 2;
        }
        public static int SelfTest()
        {
            var catalog = new Catalog(); catalog.Reload();
            var invalid = new List<string>(); int iconCount = 0; int fallback = 0; var iconDetails = new List<object>(); int highResolutionIcons = 0; int lowResolutionIcons = 0;
            foreach (AppEntry item in catalog.Items)
            {
                bool valid = String.Equals(item.Kind, "app", StringComparison.OrdinalIgnoreCase) ? System.Text.RegularExpressions.Regex.IsMatch(item.Target, "^[A-Za-z0-9_.!~+-]+$") : File.Exists(item.Target);
                if (!valid) invalid.Add(item.Id + ": " + item.Target);
                IconResult icon = IconProvider.Describe(item);
                if (icon.Bitmap != null)
                {
                    iconCount++;
                    if (icon.Bitmap.PixelWidth >= 64 && icon.Bitmap.PixelHeight >= 64) highResolutionIcons++; else lowResolutionIcons++;
                }
                else fallback++;
                iconDetails.Add(new { Id = item.Id, Name = item.Name, PixelWidth = icon.Bitmap == null ? 0 : icon.Bitmap.PixelWidth, PixelHeight = icon.Bitmap == null ? 0 : icon.Bitmap.PixelHeight, Source = icon.Source, SourcePath = icon.Path, UpscaledByApplication = false, DockPinned = catalog.IsPinned(item) });
            }
            bool emptySearch = catalog.Search("").Count == catalog.Items.Count;
            bool noMatch = catalog.Search("__不存在的应用_" + Guid.NewGuid().ToString("N")).Count == 0;
            bool nameSearch = catalog.Items.Count == 0 || catalog.Search(catalog.Items[0].Name).Any(x => x.Id == catalog.Items[0].Id);
            var controller = new DesktopController(); Rect work = Native.PrimaryWorkArea();
            bool dockFits = controller.Dock.Left >= work.Left && controller.Dock.Top >= work.Top && controller.Dock.Left + controller.Dock.Width <= work.Right + 1 && controller.Dock.Top + controller.Dock.Height <= work.Bottom + 1;
            bool padFits = controller.Launchpad.Left >= work.Left && controller.Launchpad.Top >= work.Top && controller.Launchpad.Left + controller.Launchpad.Width <= work.Right + 1 && controller.Launchpad.Top + controller.Launchpad.Height <= work.Bottom + 1;
            bool noLaunchpadButton = !controller.Dock.HasLaunchpadButton;
            bool dockButtonsMatch = controller.Dock.PersistentButtonCount == catalog.DockItems.Count + 1;
            bool passed = invalid.Count == 0 && emptySearch && noMatch && nameSearch && dockFits && padFits && noLaunchpadButton && dockButtonsMatch;
            Files.Write(Path.Combine(Files.Data, "self-test.json"), new { Passed = passed, Time = DateTime.Now.ToString("o"), Runtime = Environment.Version.ToString(), AppCount = catalog.Items.Count, DockCount = controller.Dock.PersistentButtonCount, LaunchpadButtonRemoved = noLaunchpadButton, PersistentButtonCountMatches = dockButtonsMatch, PinnedAppCount = catalog.DockItems.Count, Pages = Math.Max(1, (catalog.Items.Count + 34) / 35), PageCapacity = 35, SearchEmpty = emptySearch, SearchNoMatch = noMatch, SearchName = nameSearch, PrimaryWorkArea = new { Left = work.Left, Top = work.Top, Width = work.Width, Height = work.Height }, DockFitsScreen = dockFits, LaunchpadFitsScreen = padFits, RealIcons = iconCount, InitialLetterFallbackIcons = fallback, HighResolutionIcons64OrMore = highResolutionIcons, NativeLowResolutionIcons = lowResolutionIcons, IconDetails = iconDetails, InvalidTargets = invalid, Limitations = new string[] { "此自检未启动任何应用，也未验证 UWP 标识是否已安装。", "未验证实际桌面截图、热键、全屏避让、重启保持或手动交互。" } });
            controller.Launchpad.Close(); controller.Dock.Close(); Files.Log("结构化自检完成，结果：" + passed, null); return passed ? 0 : 2;
        }
        public static int Render()
        {
            // 离线组件渲染没有消息循环，先完成图标加载再截取组件。
            var catalog = new Catalog(); catalog.Reload();
            foreach (AppEntry item in catalog.Items) IconProvider.Get(item);
            var controller = new DesktopController();
            SaveComponent(controller.Dock, Path.Combine(Files.Data, "dock-component.png"));
            SaveComponent(controller.Launchpad, Path.Combine(Files.Data, "launchpad-component.png"));
            Files.Log("已生成组件渲染预览；这不是实际桌面截图。", null);
            controller.Launchpad.Close(); controller.Dock.Close(); return 0;
        }
        private static void SaveComponent(Window window, string path)
        {
            var visual = (FrameworkElement)window.Content;
            visual.Measure(new Size(window.Width, window.Height)); visual.Arrange(new Rect(0, 0, window.Width, window.Height)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }

    internal static class Program
    {
        private static readonly string InstanceName = "DesktopGlassDock-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value.Replace('\\', '-');
        [STAThread]
        public static int Main(string[] arguments)
        {
            Mutex mutex = null; EventWaitHandle showEvent = null; EventWaitHandle quitEvent = null; EventWaitHandle stopEvent = null;
            bool ownsMutex = false;
            DesktopController controller = null;
            try
            {
                Files.Prepare();
                var args = new HashSet<string>(arguments, StringComparer.OrdinalIgnoreCase);
                if (args.Contains("--restore-taskbar") || args.Contains("--taskbar-status") || args.Contains("--taskbar-self-test"))
                {
                    string action = args.Contains("--restore-taskbar") ? "restore" : args.Contains("--taskbar-status") ? "status" : "self-test";
                    string resultPath = Path.Combine(Files.Data, action == "restore" ? "taskbar-cli-result.json" : action == "status" ? "taskbar-query.json" : "taskbar-self-test.json");
                    try
                    {
                        object result;
                        if (action == "restore") result = TaskbarManager.RestoreSavedState();
                        else if (action == "status") result = TaskbarManager.QuerySystemStatus();
                        else result = TaskbarManager.SelfTest();
                        Files.Write(resultPath, result);
                        return action == "self-test" && !(bool)result.GetType().GetProperty("Passed").GetValue(result, null) ? 2 : 0;
                    }
                    catch (Exception ex)
                    {
                        Files.Log("任务栏命令执行失败：" + action, ex);
                        try { Files.Write(resultPath, new { Passed = false, Time = DateTime.Now.ToString("o"), Action = action, Error = ex.Message }); }
                        catch (Exception writeError) { Files.Log("任务栏命令诊断写入失败。", writeError); }
                        return 2;
                    }
                }
                LiquidGlassSurface.NativeBackdropEnabled = !args.Contains("--no-native-glass");
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { Files.Alert("桌面组件遇到错误。当前操作未完成。", e.Exception); e.Handled = true; };
                if (args.Contains("--desktop-visibility-self-test"))
                {
                    object result = DesktopVisibility.SelfTest(); Files.Write(Path.Combine(Files.Data, "desktop-visibility-self-test.json"), result);
                    return (bool)result.GetType().GetProperty("Passed").GetValue(result, null) ? 0 : 2;
                }
                if (args.Contains("--interaction-self-test")) return Diagnostics.InteractionSelfTest();
                if (args.Contains("--running-self-test")) return Diagnostics.RunningSelfTest();
                if (args.Contains("--self-test")) return Diagnostics.SelfTest();
                if (args.Contains("--render")) return Diagnostics.Render();
                mutex = new Mutex(false, "Local\\" + InstanceName);
                try { ownsMutex = mutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex || args.Contains("--quit"))
                {
                    string eventName = "Local\\" + InstanceName + (args.Contains("--quit") ? "-Quit" : "-Show");
                    bool sent = false;
                    for (int attempt = 0; attempt < 20 && !sent; attempt++)
                    {
                        try { using (var signal = EventWaitHandle.OpenExisting(eventName)) { signal.Set(); sent = true; } }
                        catch (WaitHandleCannotBeOpenedException) { if (ownsMutex) break; Thread.Sleep(100); }
                    }
                    if (!sent && !args.Contains("--quit")) Files.Alert("已有 Dock 正在初始化，暂时无法打开启动台，请稍后重试。", null);
                    return sent || args.Contains("--quit") ? 0 : 3;
                }
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + InstanceName + "-Show");
                quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + InstanceName + "-Quit");
                stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
                controller = new DesktopController();
                EventWaitHandle localShow = showEvent, localQuit = quitEvent, localStop = stopEvent;
                var listener = new Thread(delegate()
                {
                    var handles = new WaitHandle[] { localShow, localQuit, localStop };
                    while (true)
                    {
                        int result = WaitHandle.WaitAny(handles); if (result == 2) return;
                        try { app.Dispatcher.BeginInvoke(new Action(delegate { if (result == 0) controller.ShowLaunchpad(); else controller.Exit(); })); }
                        catch (InvalidOperationException ex) { Files.Log("退出期间忽略实例信号。", ex); return; }
                    }
                }); listener.IsBackground = true; listener.Name = "桌面 Dock 实例控制"; listener.Start();
                controller.Start(args.Contains("--show-launchpad"));
                app.Run(); stopEvent.Set(); listener.Join(1000); return 0;
            }
            catch (Exception ex) { if (controller != null) controller.DisposeTaskbar(); Files.Alert("无法启动桌面 Dock。", ex); return 1; }
            finally
            {
                // 启动或消息循环异常也恢复任务栏，不能只依赖托盘的正常退出路径。
                if (controller != null) controller.DisposeTaskbar();
                if (stopEvent != null) { stopEvent.Set(); stopEvent.Dispose(); }
                if (showEvent != null) showEvent.Dispose(); if (quitEvent != null) quitEvent.Dispose();
                if (mutex != null) { if (ownsMutex) mutex.ReleaseMutex(); mutex.Dispose(); }
            }
        }
    }
}







