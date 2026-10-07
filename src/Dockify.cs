// Dockify —— 让 Windows 任务栏两侧不贴边、四角变圆角
//
// 原理：任务栏是 Explorer 的顶层窗口（主屏 Shell_TrayWnd / 副屏 Shell_SecondaryTrayWnd）。
// 用 SetWindowRgn 给它一个「圆角矩形」窗口区域，四边各留出配置的边距，
// 于是任务栏就被裁成一个悬空的圆角条。窗口区域不会让内容重排，
// 所以边距要小一点（默认左右 8px），否则会切到开始按钮 / 托盘的图标。
//
// 目标框架：.NET Framework 4.x（Win10/11 自带），用系统自带 csc.exe 编译，无外部依赖。
// 语言级别：C# 5（Framework 自带 csc 不支持更高版本），所以不用字符串插值 / ?. / nameof。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;
using Microsoft.Win32;

namespace Dockify
{
    // ---------------------------------------------------------------- Win32

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        internal delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        internal const uint EVENT_OBJECT_SHOW = 0x8002;
        internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        [DllImport("user32.dll")]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateRoundRectRgn(int nLeft, int nTop, int nRight, int nBottom,
                                                        int nWidthEllipse, int nHeightEllipse);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr hObject);

        [DllImport("shell32.dll")]
        internal static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        internal const int ABM_GETSTATE = 0x00000004;
        internal const int ABM_GETTASKBARPOS = 0x00000005;
        internal const uint ABS_AUTOHIDE = 0x00000001;

        // uEdge 取值
        internal const uint ABE_LEFT = 0;
        internal const uint ABE_TOP = 1;
        internal const uint ABE_RIGHT = 2;
        internal const uint ABE_BOTTOM = 3;

        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        // GetWindow 命令
        internal const uint GW_HWNDNEXT = 2;
        internal const uint GW_CHILD = 5;

        // 任务栏里我们要动的子窗口
        internal const string ChildTray = "TrayNotifyWnd";
        internal const string ChildBridge = "Windows.UI.Composition.DesktopWindowContentBridge";
    }

    // ------------------------------------------------------------- Settings

    internal sealed class Settings
    {
        public bool Enabled;
        public double MarginLeft;      // 左裁切：占任务栏宽度的百分比（支持两位小数）
        public double MarginRight;     // 右裁切：占任务栏宽度的百分比
        public double MarginTop;       // 上边距：占任务栏高度的百分比
        public double MarginBottom;    // 下边距：占任务栏高度的百分比
        public double Radius;          // 圆角圆度：占屏幕高度的百分比（两位小数）
        public bool AllMonitors;
        public bool ScaleWithDpi;
        public bool ApplyOnAutoHide;

        // 显示模式：0 = 只裁两端（任务栏仍占满屏宽），2 = 紧凑 Dock（重排任务栏内容 + 贴合内容的圆角短条）
        public int Mode;
        public int DockWidthPercent;   // 紧凑 Dock：任务栏内容区宽度（占任务栏宽度百分比）
        public double DockPadding;     // 短条留白：占屏幕宽度的百分比
        public double ClusterGap;      // 图标/托盘间距：占屏幕宽度的百分比
        public bool CenterCluster;     // 紧凑 Dock：把整组内容居中
        public double DockOffsetX;     // 左侧裁切微调：占屏幕宽度百分比（正 = 留白，负 = 多裁贴近图标）
        public double DockWidthTrim;   // 内容长度微调：占屏幕宽度百分比（往窄了收）
        public int SavedTaskbarAl;     // 用户原本的任务栏对齐方式（-1 = 无需还原）
        public int SettingsVersion;    // 配置版本（用于一次性迁移）
        public double FlyoutOffsetX;   // ^面板左移：占屏幕宽度百分比（0 = 不调整）

        public Settings()
        {
            Enabled = true;
            MarginLeft = 0.4;
            MarginRight = 0.4;
            MarginTop = 0;
            MarginBottom = 0;
            Radius = 1.11;         // ≈12px（1080p）
            AllMonitors = true;
            ScaleWithDpi = true;
            // 区域坐标是相对任务栏窗口自身的，任务栏滑进滑出不会影响裁切效果，
            // 所以自动隐藏开着也照样裁 —— 默认打开。
            ApplyOnAutoHide = true;
            // 显示模式：0 = 只裁两端（安全默认），2 = 紧凑 Dock
            Mode = 0;
            // 以下都是"占屏幕尺寸的百分比"，默认值 = 1080p 下调好的那套
            DockPadding = 1.4;       // ≈27px：短条留白
            ClusterGap = 0.83;       // ≈16px：图标与托盘之间的空隙
            DockOffsetX = -1.56;     // ≈-30px：负值 = 往右多裁、贴住 Windows 图标
            DockWidthTrim = 1.04;    // ≈20px：内容区往窄了收
            FlyoutOffsetX = 7.8;     // ≈150px：^ 隐藏图标面板左移
            SavedTaskbarAl = -1;
            SettingsVersion = 4;
        }

        public bool IsDock
        {
            get { return Mode == 2; }
        }

        private static string Dir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "Dockify");
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(Dir, "settings.ini"); }
        }

        public static string ConfigPath
        {
            get { return FilePath; }
        }

        public static string LastSaveError;    // Save() 失败时的原因（写进诊断日志，便于排查）

        // 旧版本（TaskbarRound / 任务栏圆角）的配置目录 → 自动搬一次，避免用户重调
        private static bool _oldConfigChecked;
        private static void MigrateOldConfigOnce()
        {
            if (_oldConfigChecked) return;
            _oldConfigChecked = true;
            try
            {
                if (File.Exists(FilePath)) return;                 // 新配置已存在，什么都不做
                string oldDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarRound");
                string oldFile = Path.Combine(oldDir, "settings.ini");
                if (!File.Exists(oldFile)) return;
                Directory.CreateDirectory(Dir);
                File.Copy(oldFile, FilePath, true);
            }
            catch { }
        }

        public static bool Exists()
        {
            try { return File.Exists(FilePath); }
            catch { return false; }
        }

        public Settings Clone()
        {
            Settings s = new Settings();
            s.Enabled = Enabled;
            s.MarginLeft = MarginLeft;
            s.MarginRight = MarginRight;
            s.MarginTop = MarginTop;
            s.MarginBottom = MarginBottom;
            s.Radius = Radius;
            s.AllMonitors = AllMonitors;
            s.ScaleWithDpi = ScaleWithDpi;
            s.ApplyOnAutoHide = ApplyOnAutoHide;
            s.Mode = Mode;
            s.DockWidthPercent = DockWidthPercent;
            s.DockPadding = DockPadding;
            s.ClusterGap = ClusterGap;
            s.CenterCluster = CenterCluster;
            s.DockOffsetX = DockOffsetX;
            s.DockWidthTrim = DockWidthTrim;
            s.SavedTaskbarAl = SavedTaskbarAl;
            s.SettingsVersion = SettingsVersion;
            s.FlyoutOffsetX = FlyoutOffsetX;
            return s;
        }

        public void CopyFrom(Settings other)
        {
            Enabled = other.Enabled;
            MarginLeft = other.MarginLeft;
            MarginRight = other.MarginRight;
            MarginTop = other.MarginTop;
            MarginBottom = other.MarginBottom;
            Radius = other.Radius;
            AllMonitors = other.AllMonitors;
            ScaleWithDpi = other.ScaleWithDpi;
            ApplyOnAutoHide = other.ApplyOnAutoHide;
            Mode = other.Mode;
            DockWidthPercent = other.DockWidthPercent;
            DockPadding = other.DockPadding;
            ClusterGap = other.ClusterGap;
            CenterCluster = other.CenterCluster;
            DockOffsetX = other.DockOffsetX;
            DockWidthTrim = other.DockWidthTrim;
            SavedTaskbarAl = other.SavedTaskbarAl;
            SettingsVersion = other.SettingsVersion;
            FlyoutOffsetX = other.FlyoutOffsetX;
        }

        public string Signature()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11}",
                Enabled ? 1 : 0,
                "0",   // MarginLeft 之类已单独进签名，见下
                "0",
                MarginRight.ToString("0.###", CultureInfo.InvariantCulture),
                MarginTop.ToString("0.###", CultureInfo.InvariantCulture),
                MarginBottom.ToString("0.###", CultureInfo.InvariantCulture), Radius,
                AllMonitors ? 1 : 0, ScaleWithDpi ? 1 : 0, Mode, DockWidthPercent, DockPadding,
                CenterCluster ? 1 : 0, DockOffsetX);
        }

        public string Describe()
        {
            if (IsDock)
            {
                return string.Format(CultureInfo.InvariantCulture, "紧凑 Dock · 间距 {0:0.##}% · 留白 {1:0.##}% · 圆角 {2:0.##}% · 左微调 {3:0.##}%",
                    ClusterGap, DockPadding, Radius, DockOffsetX);
            }
            return string.Format(CultureInfo.InvariantCulture, "裁两端 · 左 {0:0.##}% 右 {1:0.##}% · 圆角 {2:0.##}%",
                MarginLeft, MarginRight, Radius);
        }

        public static Settings Load()
        {
            MigrateOldConfigOnce();
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    int n;
                    bool hasInt = int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                    double d;
                    bool hasDbl = double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                    bool b = (val == "1" || val.ToLowerInvariant() == "true");
                    switch (key)
                    {
                        case "enabled": s.Enabled = b; break;
                        case "marginleft": if (hasDbl) s.MarginLeft = ClampD(d, 0, 20); break;
                        case "marginright": if (hasDbl) s.MarginRight = ClampD(d, 0, 20); break;
                        case "margintop": if (hasDbl) s.MarginTop = ClampD(d, 0, 20); break;
                        case "marginbottom": if (hasDbl) s.MarginBottom = ClampD(d, 0, 20); break;
                        case "radius": if (hasDbl) s.Radius = ClampD(d, 0, 2000); break;
                        case "allmonitors": s.AllMonitors = b; break;
                        case "scalewithdpi": s.ScaleWithDpi = b; break;
                        case "applyonautohide": s.ApplyOnAutoHide = b; break;
                        case "mode":
                            // 旧的 Mode=1（移动整个任务栏窗口）已被证明会被 Windows 驳回，统一并到紧凑 Dock
                            if (hasInt) s.Mode = (n == 0 ? 0 : 2);
                            break;
                        case "dockwidthpercent": if (hasInt) s.DockWidthPercent = Clamp(n, 20, 100); break;
                        case "dockpadding": if (hasDbl) s.DockPadding = ClampD(d, 0, 2000); break;
                        case "clustergap": if (hasDbl) s.ClusterGap = ClampD(d, 0, 2000); break;
                        case "centercluster": s.CenterCluster = b; break;
                        case "dockoffsetx": if (hasDbl) s.DockOffsetX = ClampD(d, -2000, 2000); break;
                        case "dockwidthtrim": if (hasDbl) s.DockWidthTrim = ClampD(d, 0, 2000); break;
                        case "savedtaskbaral": if (hasInt) s.SavedTaskbarAl = Clamp(n, -1, 1); break;
                        case "settingsversion": if (hasInt) s.SettingsVersion = n; break;
                        case "flyoutoffsetx": if (hasDbl) s.FlyoutOffsetX = ClampD(d, 0, 2000); break;
                    }
                }
            }
            catch { /* 配置读不出来就用默认值 */ }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Dockify \u8bbe\u7f6e\uff08\u4e2d\u6587\u6ce8\u91ca\u884c\u4ee5 # \u5f00\u5934\uff0c\u53ef\u624b\u6539\uff09");
                sb.AppendLine("Enabled=" + (Enabled ? "1" : "0"));
                sb.AppendLine("MarginLeft=" + MarginLeft.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("MarginRight=" + MarginRight.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("MarginTop=" + MarginTop.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("MarginBottom=" + MarginBottom.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("Radius=" + Radius.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("AllMonitors=" + (AllMonitors ? "1" : "0"));
                sb.AppendLine("ScaleWithDpi=" + (ScaleWithDpi ? "1" : "0"));
                sb.AppendLine("ApplyOnAutoHide=" + (ApplyOnAutoHide ? "1" : "0"));
                sb.AppendLine("# Mode: 0 = 只裁两端, 2 = 紧凑 Dock（内容居中 + 贴合内容的圆角短条）");
                sb.AppendLine("Mode=" + Mode.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("DockWidthPercent=" + DockWidthPercent.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("DockPadding=" + DockPadding.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("ClusterGap=" + ClusterGap.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("CenterCluster=" + (CenterCluster ? "1" : "0"));
                sb.AppendLine("DockOffsetX=" + DockOffsetX.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("DockWidthTrim=" + DockWidthTrim.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("SavedTaskbarAl=" + SavedTaskbarAl.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("SettingsVersion=" + SettingsVersion.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("FlyoutOffsetX=" + FlyoutOffsetX.ToString("0.###", CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { LastSaveError = ex.GetType().Name + ": " + ex.Message; }
        }

        // 把百分比字段统一裁剪到界面允许的范围（迁移完成后调用一次）
        public void ClampAll()
        {
            Radius = ClampD(Radius, 0, 6);
            DockPadding = ClampD(DockPadding, 0, 10);
            ClusterGap = ClampD(ClusterGap, 0, 5);
            DockOffsetX = ClampD(DockOffsetX, -30, 20);
            DockWidthTrim = ClampD(DockWidthTrim, 0, 20);
            FlyoutOffsetX = ClampD(FlyoutOffsetX, 0, 50);
            MarginLeft = ClampD(MarginLeft, 0, 20);
            MarginRight = ClampD(MarginRight, 0, 20);
            MarginTop = ClampD(MarginTop, 0, 6);
            MarginBottom = ClampD(MarginBottom, 0, 6);
        }

        private static double ClampD(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }

    // --------------------------------------------------------------- Engine

    internal sealed class TaskbarEngine
    {
        private readonly Dictionary<IntPtr, string> _applied = new Dictionary<IntPtr, string>();
        private int _trayShift;        // 托盘子窗口被平移了多少像素
        private bool _shiftBlocked;    // 平移自检失败，永久放弃平移
        private int _dockCooldown;     // Dock 失败后的冷却节拍数（防止两个模式来回跳）
        // 弹出面板"一步到位"用的 WinEvent 钩子（毫秒级响应，不再等 0.5 秒的心跳）
        private Native.WinEventDelegate _winEventProc;
        private IntPtr _winEventHook = IntPtr.Zero;
        private readonly Dictionary<IntPtr, Native.RECT> _hookBase = new Dictionary<IntPtr, Native.RECT>();
        private readonly Dictionary<IntPtr, int> _hookStamp = new Dictionary<IntPtr, int>();
        private uint _barPid;
        private int _shiftMisses;      // 平移连续没见效的次数
        private double _lastFactor = 1.0;
        private readonly Dictionary<IntPtr, Native.RECT> _qsBase = new Dictionary<IntPtr, Native.RECT>();
        private readonly Dictionary<IntPtr, Native.RECT> _qsLast = new Dictionary<IntPtr, Native.RECT>();
        private readonly Dictionary<IntPtr, int> _qsFirstTick = new Dictionary<IntPtr, int>();
        private bool _barVisible;      // 上一轮任务栏是否在屏幕内
        private readonly StringBuilder _report = new StringBuilder();
        private bool _autoHide;
        private int _ticks;
        private bool _dpiApiOk = true;
        private bool _clusterUsed;     // 动过任务栏内容区（退出时要还原）
        private bool _clusterOk;       // 本轮成功摆出了紧凑 Dock
        private bool _clusterBlocked;  // 内容区摆位被系统驳回，本运行周期不再尝试
        private int _bridgeWidth;      // 当前内容区宽度（物理像素，0 = 还没设过）
        private int _bridgeRelX;       // 内容区相对任务栏客户区的 x
        private bool _bridgeSized;     // 内容区尺寸这一轮是否已摆好
        private int _bridgeReverts;    // 内容区摆位被系统拽回的连续次数（防死磕）

        public Settings Settings;

        public string Status;
        public int AppliedBars;
        public int TaskbarsFound;      // 本轮枚举到几个任务栏（0 = 进程上下文受限，什么都看不到）

        // 最近一次 Tick 的详细过程（--diag 会写到文件里）
        public string Report
        {
            get { return _report.ToString(); }
        }

        public TaskbarEngine(Settings settings)
        {
            Settings = settings;
            Status = "就绪";
        }

        public bool AutoHideDetected
        {
            get { return _autoHide; }
        }

        // 紧凑 Dock 的子窗口摆位被系统驳回（本运行周期已退回裁两端）
        public bool ClusterBlocked
        {
            get { return _clusterBlocked; }
        }

        // 枚举所有任务栏窗口
        private static List<IntPtr> FindTaskbars(bool primaryOnly)
        {
            List<IntPtr> list = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(hWnd)) return true;
                StringBuilder sb = new StringBuilder(128);
                Native.GetClassName(hWnd, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls == "Shell_TrayWnd")
                {
                    list.Add(hWnd);
                }
                else if (cls == "Shell_SecondaryTrayWnd" && !primaryOnly)
                {
                    list.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static bool QueryAutoHide()
        {
            // 注册表最准：StuckRects3 的 Settings 字节数组，byte[8] 的 bit0 = 自动隐藏
            try
            {
                string[] keys = new string[]
                {
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3",
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects2"
                };
                for (int i = 0; i < keys.Length; i++)
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(keys[i], false))
                    {
                        if (k == null) continue;
                        byte[] b = k.GetValue("Settings") as byte[];
                        if (b != null && b.Length > 8) return (b[8] & 0x01) != 0;
                    }
                }
            }
            catch { }

            // 退路：问 AppBar
            try
            {
                Native.APPBARDATA abd = new Native.APPBARDATA();
                abd.cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA));
                IntPtr state = Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd);
                return (((uint)state.ToInt64()) & Native.ABS_AUTOHIDE) != 0;
            }
            catch { return false; }
        }

        private int DpiFor(IntPtr hWnd)
        {
            if (_dpiApiOk)
            {
                try
                {
                    uint d = Native.GetDpiForWindow(hWnd);
                    if (d >= 48) return (int)d;
                }
                catch (EntryPointNotFoundException) { _dpiApiOk = false; }
                catch (DllNotFoundException) { _dpiApiOk = false; }
            }
            try
            {
                IntPtr mon = Native.MonitorFromWindow(hWnd, 2 /* MONITOR_DEFAULTTONEAREST */);
                uint dx, dy;
                if (Native.GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0 && dx >= 48)
                    return (int)dx;
            }
            catch { }
            return 96;
        }

        // 百分比 → 像素（横向按屏幕宽度，纵向按屏幕高度）
        private static int PctW(double pct, Native.RECT mon) { return Pct(pct, mon.Right - mon.Left); }
        private static int PctH(double pct, Native.RECT mon) { return Pct(pct, mon.Bottom - mon.Top); }

        // 带符号的百分比 → 像素（左侧裁切微调会取负值，不能走 Pct 的非负保护）
        private static int PctSigned(double pct, int baseSize)
        {
            return (int)Math.Round(pct / 100.0 * baseSize, MidpointRounding.AwayFromZero);
        }

        // 百分比 → 像素
        private static int Pct(double pct, int baseSize)
        {
            double v = pct / 100.0 * baseSize;
            int r = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            return r < 0 ? 0 : r;
        }

        private static int Scale(int value, double factor)
        {
            if (value <= 0) return 0;
            int v = (int)Math.Round(value * factor, MidpointRounding.AwayFromZero);
            return v < 1 ? 1 : v;
        }

        // 给单个任务栏套上圆角矩形区域；返回是否成功（每一步都记进 _report，方便 --diag 排查）
        private bool ApplyRegion(IntPtr hWnd, int marginLeft, int marginRight, int marginTop, int marginBottom, int radius)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(hWnd, out r))
            {
                _report.AppendLine("  ! GetWindowRect 失败, err=" + Marshal.GetLastWin32Error());
                return false;
            }
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0)
            {
                _report.AppendLine("  ! 窗口尺寸异常 " + w + "x" + h);
                return false;
            }

            // 边距加起来都不能把窗口吃没了
            if (w <= marginLeft + marginRight + 4 || h <= marginTop + marginBottom + 4)
            {
                _report.AppendLine("  ! 边距太大，窗口装不下：" + w + "x" + h);
                return false;
            }

            int maxRadius = Math.Min((w - marginLeft - marginRight) / 2, (h - marginTop - marginBottom) / 2);
            if (radius > maxRadius) radius = maxRadius;
            if (radius < 0) radius = 0;

            // 注意：GDI 区域右下边界是开区间，所以右/下用 w-mr、h-mb
            IntPtr rgn = Native.CreateRoundRectRgn(marginLeft, marginTop, w - marginRight, h - marginBottom,
                                                  radius * 2, radius * 2);
            if (rgn == IntPtr.Zero)
            {
                _report.AppendLine("  ! CreateRoundRectRgn 返回 0, err=" + Marshal.GetLastWin32Error());
                return false;
            }

            _report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  区域 = ({0},{1})-({2},{3}) 圆角 {4}px  (窗口 {5}x{6})",
                marginLeft, marginTop, w - marginRight, h - marginBottom, radius, w, h));

            if (Native.SetWindowRgn(hWnd, rgn, true) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                Native.DeleteObject(rgn);   // 失败时区域还是我们的，要自己删
                _report.AppendLine("  ! SetWindowRgn 失败, err=" + err + " (5=拒绝访问)");
                return false;
            }
            _report.AppendLine("  OK SetWindowRgn 成功");
            return true;                    // 成功后区域归系统所有，不能再删
        }

        // ================= 弹出面板"一步到位" =================
        // 以前是在 0.5 秒的心跳里摆位置 → 面板先出现在 Explorer 给的位置，下一拍才被挪走，
        // 视觉上就是"二次移动"。现在用 WinEvent 钩子：面板一 show / 一被移动，立刻摆好（毫秒级）。
        public void InstallFlyoutHook()
        {
            if (_winEventHook != IntPtr.Zero) return;
            try
            {
                _winEventProc = new Native.WinEventDelegate(OnFlyoutEvent);
                _winEventHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
                _report.AppendLine("  WinEvent 钩子 = " + _winEventHook);
            }
            catch { }
        }

        public void RemoveFlyoutHook()
        {
            try
            {
                if (_winEventHook != IntPtr.Zero) { Native.UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
            }
            catch { }
        }

        // 按类名决定用哪个设置值：^ 隐藏图标面板 与 快捷设置（网络/音量/电池）各一个
        private double FlyoutOffsetFor(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(160);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "TopLevelWindowForOverflowXamlIsland" || cls == "NotifyIconOverflowWindow")
                return Settings.FlyoutOffsetX;          // ^ 隐藏图标面板
            // 网络/音量/电池 的快捷设置面板：按用户要求**不再移动**（Explorer 自己锚定得没问题）
            return 0;
        }

        private void OnFlyoutEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild,
                                   uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero || idObject != 0) return;      // 只要窗口本身的事件
                if (ev != Native.EVENT_OBJECT_SHOW && ev != Native.EVENT_OBJECT_LOCATIONCHANGE) return;

                double off = FlyoutOffsetFor(hwnd);
                if (off <= 0) return;

                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid != _barPid) return;                            // 只认 explorer 自己的窗口

                Native.RECT cur;
                if (!Native.GetWindowRect(hwnd, out cur)) return;
                int w = cur.Right - cur.Left;
                int ht = cur.Bottom - cur.Top;
                if (w < 120 || ht < 80) return;                        // 提示/菜单不要
                if (w > 900 || ht > 1100) return;                      // 太大的不是托盘面板

                if (ev == Native.EVENT_OBJECT_SHOW)
                {
                    _hookBase[hwnd] = cur;                              // 记下 Explorer 给它的位置
                    _hookStamp[hwnd] = Environment.TickCount;
                }
                Native.RECT b;
                if (!_hookBase.TryGetValue(hwnd, out b)) return;
                int stamp;
                if (!_hookStamp.TryGetValue(hwnd, out stamp)) return;
                if (unchecked(Environment.TickCount - stamp) > 3000) return;   // 只在一开始那 3 秒内摆位

                int tx = b.Left - PctW(off, MonitorRect(hwnd));
                if (Math.Abs(cur.Left - tx) <= 2) return;
                Native.MoveWindow(hwnd, tx, cur.Top, w, ht, true);
            }
            catch { }
        }
        // 托盘弹出面板（快捷设置 / 隐藏图标面板）当前是否开着。
        // 面板开着的时候必须**暂停自动适应**：面板出现/消失会让测量值抖动，
        // 一补偿整组就会带着任务栏来回跳。
        private bool AnyTrayFlyoutVisible(IntPtr barHwnd)
        {
            uint barPid;
            Native.GetWindowThreadProcessId(barHwnd, out barPid);
            bool found = false;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (found) return true;
                if (!Native.IsWindowVisible(h)) return true;
                StringBuilder sb = new StringBuilder(160);
                Native.GetClassName(h, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls != "Xaml_WindowedPopupClass" &&
                    cls != "TopLevelWindowForOverflowXamlIsland" &&
                    cls != "NotifyIconOverflowWindow") return true;

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid != barPid) return true;          // 只要 explorer 自己的

                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                int w = r.Right - r.Left;
                int ht = r.Bottom - r.Top;
                if (w < 120 || ht < 80) return true;
                if (cls == "Xaml_WindowedPopupClass" && (w > 800 || ht > 1000)) return true;
                found = true;
                return true;
            }, IntPtr.Zero);
            return found;
        }
        private static void ClearRegion(IntPtr hWnd)
        {
            if (!Native.IsWindow(hWnd)) return;
            Native.SetWindowRgn(hWnd, IntPtr.Zero, true);   // NULL 区域 = 恢复成完整矩形
        }

        // 把所有任务栏恢复原样（供 --reset 和退出时调用，不依赖任何状态）
        public static void ResetTaskbars()
        {
            List<IntPtr> bars = FindTaskbars(false);
            for (int i = 0; i < bars.Count; i++)
            {
                RestoreBarChildren(bars[i]);   // 内容区恢复整宽、托盘回到最右
                ForceFullWidth(bars[i]);
                ClearRegion(bars[i]);
            }
        }

        // 把一个任务栏强行还原成整屏宽（用于「程序被强杀后任务栏卡在 Dock 短条」的救急）
        private static void ForceFullWidth(IntPtr h)
        {
            Native.RECT mon = MonitorRect(h);
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            int barH = r.Bottom - r.Top;
            int monW = mon.Right - mon.Left;

            // 滑出屏幕（自动隐藏）时不碰它，交给 Explorer
            if (r.Bottom > mon.Bottom || r.Top < mon.Top) return;
            if ((r.Right - r.Left) >= monW - 8 && r.Left == mon.Left) return;   // 已经是整屏宽

            bool bottom = Math.Abs(r.Bottom - mon.Bottom) <= Math.Abs(r.Top - mon.Top);
            int y = bottom ? (mon.Bottom - barH) : mon.Top;
            Native.MoveWindow(h, mon.Left, y, monW, barH, true);
        }

        private void ResetApplied()
        {
            foreach (KeyValuePair<IntPtr, string> kv in _applied)
            {
                ForceFullWidth(kv.Key);     // 万一任务栏窗口被外面改过，一并还原
                ClearRegion(kv.Key);
            }
            _applied.Clear();
            RestoreCluster();               // 紧凑 Dock 动过的子窗口放回原位
            AppliedBars = 0;
        }

        public void Reset()
        {
            ResetApplied();
            Status = "已恢复任务栏原样";
        }

        // 每次「节拍」跑一遍：发现任务栏几何 / 设置变化就重新套区域
        public void Tick()
        {
            _ticks++;
            bool force = (_ticks % 2 == 0);   // 每 1 秒强制重刷一次，防止被系统改回去

            _report.Length = 0;
            _report.AppendLine("时间     : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            _report.AppendLine("进程位数 : " + (IntPtr.Size * 8) + " 位" +
                               (IntPtr.Size == 4 ? "  ← 32 位进程改不了 64 位资源管理器的窗口区域，必须用 x64 编译" : ""));
            _report.AppendLine("设置文件 : " + Settings.ConfigPath + "  存在=" + Settings.Exists());
            _report.AppendLine("设置     : " + Settings.Describe() + "  Enabled=" + Settings.Enabled +
                               " AllMonitors=" + Settings.AllMonitors + " ScaleWithDpi=" + Settings.ScaleWithDpi +
                               " ApplyOnAutoHide=" + Settings.ApplyOnAutoHide);

            if (!Settings.Enabled)
            {
                if (_applied.Count > 0) ResetApplied();
                    Status = "已暂停（右键托盘图标 → 启用）";
                _report.AppendLine("结果     : 已停用，跳过");
                return;
            }

            _autoHide = QueryAutoHide();
            List<IntPtr> bars = FindTaskbars(!Settings.AllMonitors);
            _report.AppendLine("自动隐藏 : " + _autoHide);
            TaskbarsFound = bars.Count;
            _report.AppendLine("任务栏   : 找到 " + bars.Count + " 个");
            _clusterOk = false;
            if (bars.Count > 0) Native.GetWindowThreadProcessId(bars[0], out _barPid);

            for (int i = 0; i < bars.Count; i++)
            {
                Native.RECT br;
                Native.GetWindowRect(bars[i], out br);
                _report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  #{0} hwnd={1} 可见={2} 矩形={3},{4}-{5},{6} dpi={7}",
                    i, bars[i], Native.IsWindowVisible(bars[i]), br.Left, br.Top, br.Right, br.Bottom, DpiFor(bars[i])));
            }

            if (bars.Count == 0)
            {
                _applied.Clear();
                AppliedBars = 0;
                Status = "未找到任务栏（进程看不到任务栏：多半是启动上下文受限）";
                _report.AppendLine("结果     : 没枚举到任务栏窗口");
                return;
            }

            if (_autoHide && !Settings.ApplyOnAutoHide)
            {
                if (_applied.Count > 0) ResetApplied();
                    Status = "已跳过：任务栏开了自动隐藏";
                _report.AppendLine("结果     : 自动隐藏且未勾选『自动隐藏时也裁切』，跳过");
                return;
            }

            // 清掉已经消失的任务栏，以及因为改了「所有显示器」而不再管辖的那些
            List<IntPtr> stale = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, string> kv in _applied)
            {
                if (!bars.Contains(kv.Key) || !Native.IsWindow(kv.Key)) stale.Add(kv.Key);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                ForceFullWidth(stale[i]);   // 不再管辖的要先把位置和区域都还回去
                ClearRegion(stale[i]);
                _applied.Remove(stale[i]);
            }

            string setSig = Settings.Signature();
            int ok = 0;

            for (int i = 0; i < bars.Count; i++)
            {
                IntPtr h = bars[i];
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) continue;

                // 任务栏刚被唤出（从隐藏变回可见）→ 立刻重刷一次，别等心跳
                Native.RECT monNow = MonitorRect(h);
                bool visNow = (r.Bottom <= monNow.Bottom && r.Top >= monNow.Top);
                if (visNow && !_barVisible)
                {
                    force = true;
                    _report.AppendLine("  任务栏刚唤出 → 立刻重刷裁切");
                }
                _barVisible = visNow;

                int dpi = Settings.ScaleWithDpi ? DpiFor(h) : 96;
                double factor = Settings.ScaleWithDpi ? (dpi / 96.0) : 1.0;
                _lastFactor = factor;

                // 紧凑 Dock：把开始按钮 / 任务按钮 / 托盘三个子窗口重排成一个居中的紧凑簇，
                // 任务栏窗口本身不动（所以自动隐藏照常工作，不会和 Explorer 打架）
                bool compact = false;
                int pillLeft = 0, pillRight = 0;
                if (Settings.IsDock && !_clusterBlocked && _dockCooldown <= 0)
                {
                    compact = BuildCompactDock(h, factor, r, out pillLeft, out pillRight);
                    if (!compact)
                    {
                        _dockCooldown = 20;        // 失败就冷却 10 秒，绝不让两个模式每拍互切
                        _report.AppendLine("  Dock 本轮失败 → 冷却 20 拍（10 秒）");
                    }
                    if (compact && pillLeft < 0)
                    {
                        ok++;                       // 保持现有裁切，别碰区域
                                    _report.AppendLine("  hwnd=" + h + " 保持现有裁切");
                        continue;
                    }
                    if (!compact && _clusterUsed)
                    {
                        // Dock 这轮不可用（例如任务栏改成居左了）→ 把内容区/托盘放回去
                        RestoreCluster();
                        _report.AppendLine("  Dock 不可用 → 内容区/托盘已还原");
                    }
                }
                else if (_clusterUsed)
                {
                    RestoreCluster();          // 退出紧凑 Dock：子窗口放回原位
                }
                if (_dockCooldown > 0) _dockCooldown--;

                int barW = r.Right - r.Left;
                int barH = r.Bottom - r.Top;

                int ml, mr, mt, mb;
                if (compact)
                {
                    // 短条贴合内容：左右边距 = 内容两侧要裁掉的部分
                    ml = pillLeft; mr = pillRight; mt = 0; mb = 0;
                }
                else
                {
                    // 百分比 × 任务栏自身尺寸（天然适配分辨率与缩放，不再依赖 DPI 换算）
                    ml = PctW(Settings.MarginLeft, monNow);
                    mr = PctW(Settings.MarginRight, monNow);
                    mt = PctH(Settings.MarginTop, monNow);
                    mb = PctH(Settings.MarginBottom, monNow);
                }

                string sig = string.Format(CultureInfo.InvariantCulture, "{0}x{1}@{2}#{3}#{4}#{5},{6}",
                    barW, barH, dpi, setSig, compact ? "compact" : "crop", ml, mr);

                string prev;
                bool changed = !_applied.TryGetValue(h, out prev) || prev != sig;
                if (!changed && !force)
                {
                    ok++;
                    _report.AppendLine("  hwnd=" + h + " 跳过（尺寸和设置都没变）");
                    continue;
                }

                _report.AppendLine("  hwnd=" + h + " 应用中… 缩放系数=" + factor.ToString("0.###", CultureInfo.InvariantCulture) +
                                   (compact ? " [紧凑Dock]" : " [裁两端]"));
                int useRadius = PctH(Settings.Radius, monNow);
                bool done = ApplyRegion(h, ml, mr, mt, mb, useRadius);
                if (done)
                {
                    _applied[h] = sig;
                    ok++;

                }
            }


            AppliedBars = ok;
            if (ok == 0) Status = "裁切失败：被拒绝访问（err=5）";
            else if (_clusterBlocked) Status = "紧凑 Dock 摆位被系统驳回，已退回裁两端";
            else if (Settings.IsDock && !_clusterOk) Status = "没找到任务栏子窗口，已按裁两端处理";
            else Status = "已应用 · " + Settings.Describe();
            _report.AppendLine("结果     : 成功 " + ok + " / " + bars.Count + " 个任务栏");
            if (_ticks % 10 == 0 && _report.ToString().IndexOf("屏幕外") < 0) Program.WriteLiveDiag(this);
        }

        // 在任务栏的子窗口里按类名找一个窗口
        private static IntPtr FindChildByClass(IntPtr parent, string cls)
        {
            IntPtr c = Native.GetWindow(parent, Native.GW_CHILD);
            int guard = 0;
            while (c != IntPtr.Zero && guard < 40)
            {
                StringBuilder sb = new StringBuilder(128);
                Native.GetClassName(c, sb, sb.Capacity);
                if (sb.ToString() == cls) return c;
                c = Native.GetWindow(c, Native.GW_HWNDNEXT);
                guard++;
            }
            return IntPtr.Zero;
        }

        private static bool GetRect(IntPtr h, out Native.RECT r)
        {
            return Native.GetWindowRect(h, out r);
        }

        // 用 UI Automation 问 Windows：任务栏上「程序图标区」和「系统托盘区」分别在屏幕什么位置。
        // 重要：任务栏被自动隐藏（滑出屏幕）时，UIA 里根本没有这些按钮 —— 所以必须先确认任务栏是「唤出」状态。
        private bool MeasureContent(IntPtr barHwnd, Native.RECT barRect,
                                    out int iconL, out int iconR, out int trayL, out int trayR)
        {
            iconL = 0; iconR = 0; trayL = 0; trayR = 0;
            try
            {
                AutomationElement root = AutomationElement.RootElement;
                if (root == null) return false;

                Condition cond = new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd");
                AutomationElement bar = root.FindFirst(TreeScope.Children, cond);
                if (bar == null) return false;

                AutomationElementCollection all = bar.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                if (all == null || all.Count == 0) return false;

                int barW = barRect.Right - barRect.Left;
                System.Collections.Generic.List<int[]> xs = new System.Collections.Generic.List<int[]>();
                for (int i = 0; i < all.Count; i++)
                {
                    AutomationElement e = all[i];
                    System.Windows.Rect rc = e.Current.BoundingRectangle;
                    if (rc.IsEmpty || rc.Width <= 1 || rc.Height <= 1) continue;
                    if (rc.Width > barW * 0.9) continue;                        // 整条任务栏的背景容器
                    if (e.Current.ControlType != ControlType.Button) continue;  // 只信按钮
                    if (rc.Width < 30 && rc.Right >= barRect.Right - 30) continue; // 「显示桌面」细条
                    xs.Add(new int[] { (int)rc.Left, (int)rc.Right });
                }
                if (xs.Count < 3) return false;      // 任务栏没唤出时这里会是 0

                xs.Sort(delegate(int[] a, int[] b) { return a[0].CompareTo(b[0]); });

                // 托盘宽度取「托盘窗口」的宽度（稳定、与语言无关）
                int trayWndW = Scale(240, 1.0);
                IntPtr trayWnd = FindChildByClass(barHwnd, Native.ChildTray);
                if (trayWnd != IntPtr.Zero)
                {
                    Native.RECT tr;
                    if (Native.GetWindowRect(trayWnd, out tr) && (tr.Right - tr.Left) > 20)
                        trayWndW = tr.Right - tr.Left;
                }

                int unionR = xs[xs.Count - 1][1];
                int split = unionR - trayWndW;
                bool anyTray = false;
                for (int i = 0; i < xs.Count; i++)
                {
                    if (xs[i][0] >= split - 6)
                    {
                        if (!anyTray) { trayL = xs[i][0]; anyTray = true; }
                        trayR = xs[i][1];
                    }
                    else
                    {
                        if (iconL == 0) iconL = xs[i][0];
                        iconR = xs[i][1];
                    }
                }
                if (!anyTray || iconR <= iconL) return false;   // 分不出图标/托盘就别乱动

                _report.AppendLine("  UIA: 图标 " + iconL + ".." + iconR + "  托盘 " + trayL + ".." + trayR +
                                   "  (共 " + xs.Count + " 个按钮)");
                return true;
            }
            catch (Exception ex)
            {
                _report.AppendLine("  ! UIA 测量异常: " + ex.GetType().Name);
                return false;
            }
        }
        // 紧凑 Dock。实测结论（也是这次不再跑偏的关键）：
        //   1. 任务栏画面由子窗口 «...DesktopWindowContentBridge» 承载，改它的宽度会让 Windows 重排内容；
        //   2. 重排后 程序图标区停在原处，而**托盘贴在内容区右端**；
        //   3. 于是把内容区右端设到「图标右端 + 间距 + 托盘宽度」，托盘就刚好贴在图标右边 —— 不会重叠；
        //   4. 图标变多 → 图标右端变远 → 内容区自动变宽（这就是「新增图标自适应伸缩」），变少则自动收窄；
        //   5. 全部是**绝对计算**，没有任何"拿上次结果再推一次"的反馈，所以不可能累积跑偏；
        //   6. 任务栏窗口自始至终不动 → 自动隐藏照常工作。
        private bool BuildCompactDock(IntPtr h, double factor, Native.RECT barRect, out int pillLeft, out int pillRight)
        {
            pillLeft = -1;      // -1 = 本轮不要动裁切（保持现状），别把短条弄没了
            pillRight = -1;

            // 任务栏被自动隐藏/正在滑动时：既不该动它，UIA 也拿不到按钮 → 本轮完全跳过
            Native.RECT mon = MonitorRect(h);
            if (barRect.Bottom > mon.Bottom || barRect.Top < mon.Top)
            {
                _report.AppendLine("  (任务栏在屏幕外/滑动中，保持现有裁切)");
                return true;
            }

            // 托盘弹出面板开着 → 暂停自动适应，保持现有裁切（否则任务栏会一直跳）
            if (AnyTrayFlyoutVisible(h))
            {
                _report.AppendLine("  (托盘弹出面板开着 → 暂停自动适应，保持现有裁切)");
                return true;
            }

            IntPtr bridge = FindChildByClass(h, Native.ChildBridge);
            if (bridge == IntPtr.Zero)
            {
                _report.AppendLine("  ! 没找到内容区子窗口 " + Native.ChildBridge + "，退回裁两端");
                return false;
            }

            Native.RECT client;
            if (!Native.GetClientRect(h, out client)) return false;
            int cw = client.Right;
            int ch = client.Bottom;
            if (cw < 200 || ch < 8) return false;

            int iconL, iconR, trayL, trayR;
            if (!MeasureContent(h, barRect, out iconL, out iconR, out trayL, out trayR))
            {
                _report.AppendLine("  ! 量不到图标/托盘位置（任务栏没唤出？），保持现有裁切");
                return true;
            }

            // 【任务栏"居左"对齐时，紧凑 Dock 原理上做不到】
            //   居左时图标组钉在任务栏最左边，要让托盘贴到图标右边，就得把内容区压得比图标组还窄 ——
            //   不可能。硬算只会得到一条盖住整条任务栏、还把面板顶飞的短条。
            //
            // ⚠️ 判据必须用**注册表里的真实设置**（TaskbarAl: 0=居左 1=居中），
            //    不能用"图标是否靠左"来猜 —— 我们为了居中本来就会把图标往左推（relX 可达 -150），
            //    一猜就会把自己的 Dock 误判成"居左" → 退回裁两端 → 还原后又不靠左了 → 再进 Dock，
            //    于是**两个模式每 0.5 秒来回跳**（这就是用户看到的现象）。
            bool reallyLeftAligned = false;
            try { reallyLeftAligned = (Program.ReadTaskbarAl() == 0); } catch { reallyLeftAligned = false; }
            if (reallyLeftAligned && (iconL - barRect.Left) < cw / 3)
            {
                Status = "任务栏是「居左」对齐：紧凑 Dock 不适用，已按裁两端处理";
                _report.AppendLine("  ! 注册表显示任务栏居左对齐 → 退回裁两端");
                return false;
            }

            int gap = PctW(Settings.ClusterGap, mon);
            int pad = PctW(Settings.DockPadding, mon);
            // 注意：不能用 Scale() —— 它开头就是 if (value <= 0) return 0，
            // 会把负值（= 往右多裁、贴近图标）直接吞成 0，导致这个滑块怎么调都没反应。
            int off = PctSigned(Settings.DockOffsetX, mon.Right - mon.Left);
            // 托盘宽度优先用「托盘窗口」的宽度：它稳定，而且比按按钮量的偏大（安全，不会切到时钟）
            int trayW = TrayWindowWidth(h);
            if (trayW <= 0) trayW = (trayR - trayL > 0 ? trayR - trayL : Scale(240, factor));

            // 【实测结论，纠正了之前的一个错误假设】
            //   改内容区宽度时，Windows **不会**把程序图标组挪走 —— 图标组钉在原处，
            //   只有托盘跟着内容区右边缘走。所以：
            //     ① 内容区左端永远放 0，不做任何"平移居中"（那只是在挪短条，不是在挪内容）；
            //     ② 宽度只要让右端落在「图标右端 + 间距 + 托盘宽度」，托盘就贴在图标右边；
            //     ③ 短条按**实测**的图标/托盘位置框住内容，再把短的一侧补齐，
            //        使短条关于屏幕中心对称 —— 这样增删图标后它始终居中，且永远盖得住内容。
            // ============ 长度 + 短条（不再平移内容区） ============
            // 【为什么废弃"整组平移"】
            //   实测（diag 12:04:50）：为了把短条拉回屏幕中心不断左推内容区，而宽度公式里含 -relX，
            //   左推会让宽度一起变大 → 宽度撞上"整条任务栏宽" → Windows 立刻按整屏宽重排
            //   （托盘弹回最右）→ 短条只剩半截、托盘跑到短条外面。不可控，彻底不用。
            //
            // 现在只有三个稳定机制：
            //   ① 宽度按"图标右端 + 间距 + 托盘宽 + 余量"算，托盘贴身不重叠、增删图标自动伸缩；
            //   ② **硬上限**：宽度绝不允许等于整条任务栏宽（否则触发整屏重排，全局崩）；
            //   ③ 短条紧贴内容；若明显偏心，就把短的那一侧**有限度地**补上（最多 120px），
            //      既让观感居中、又不会再出现"Windows 图标旁一大坨空白"。
            int inset = Scale(10, factor);               // 区域右边界收进内容里侧的量（为的是看见圆角）
            int safety = inset;                          // 托盘右侧余量：刚好够切一刀

            // ============ 宽度：**与内容区位置 relX 无关** ============
            // 这是能安全平移的钥匙。上次崩掉的唯一原因就是宽度公式里含 -relX：
            // 一左移宽度跟着膨胀 → 撞到整屏宽 → Windows 按整屏重排 → 托盘弹回最右。
            // 实测：图标组本身**屏幕居中**（中心≈960），托盘贴内容区右端；
            //       平移 relX 时图标 1:1 跟着走（11:50 那拍 relX=-148 → 图标中心 812=960-148，分毫不差）。
            // 所以宽度只需要由「图标中心到内容区左端的距离 + 半个图标组 + 间距 + 托盘宽」决定。
            int relXOld = _bridgeRelX;
            if (_shiftBlocked) relXOld = 0;
            int cluster = iconR - iconL;
            if (cluster < Scale(60, factor)) cluster = Scale(60, factor);
            int iconCenterRel = (iconL + iconR) / 2 - (barRect.Left + relXOld);
            int minW = cluster + 2 * (gap + trayW) + safety;                     // 不重叠的下限
            if (minW < Scale(300, factor)) minW = Scale(300, factor);
            int wantW = iconCenterRel + cluster / 2 + gap + trayW + safety;
            // 内容长度微调：按用户设置把内容区收窄（收太多时 Windows 会开始隐藏图标，属正常现象）
            int trim = PctW(Settings.DockWidthTrim, mon);
            if (trim > 0)
            {
                wantW -= trim;
                _report.AppendLine("  内容长度微调 -" + trim + "px → 宽 " + wantW);
            }
            if (wantW < minW) wantW = minW;
            int maxW = cw - Scale(60, factor);                                   // 硬上限：绝不等于整条任务栏宽
            if (wantW > maxW) wantW = maxW;
            if (wantW < minW)
            {
                Status = "图标太多，紧凑 Dock 放不下，已按裁两端处理";
                _report.AppendLine("  ! 图标太多（需要 " + minW + "px，上限 " + maxW + "px）→ 退回裁两端");
                return false;
            }

            // ============ 平移：让「图标 + 间距 + 托盘」整块落在屏幕中心 ============
            int center = (mon.Left + mon.Right) / 2;
            // 居中基准用**短条本身**的中心（而不是整块中心）——
            // 这样左侧多裁/多加留白时，短条依然保持居中。
            int curCenter = ((iconL - off) + (relXOld + wantW - inset)) / 2;
            int delta = center - curCenter;
            int applied = 0;
            if (!_shiftBlocked && Math.Abs(delta) > Scale(3, factor) && Math.Abs(delta) < cw / 3)
            {
                int newRelX = relXOld + delta;
                int minRelX = -(iconL - barRect.Left - Scale(40, factor));   // 图标左端别出屏
                if (newRelX < minRelX) newRelX = minRelX;
                if (newRelX + wantW > cw) newRelX = cw - wantW;
                applied = newRelX - relXOld;
                _report.AppendLine("  整组平移 " + applied + "px（往" + (applied < 0 ? "左" : "右") + "）");
            }

            // 自检：上一拍平移过、这一拍整块中心却还差很远（>150px）→ 最多 3 次就永久放弃平移
            if (!_shiftBlocked && applied == 0 && Math.Abs(curCenter - center) > Scale(150, factor))
            {
                _shiftMisses++;
                if (_shiftMisses >= 3)
                {
                    _shiftBlocked = true;
                    _report.AppendLine("  ! 平移连续 3 次没效果 → 永久放弃平移，只紧贴内容");
                }
            }
            else if (applied != 0 || Math.Abs(curCenter - center) <= Scale(150, factor))
            {
                _shiftMisses = 0;
            }
            int relX = relXOld + applied;

            // ============ 短条：紧贴「平移后」的整块 ± 留白 ============
            int groupL = iconL + applied - pad;
            int groupR = relX + wantW - inset;

            // 「左侧留白」滑块：> 0 时给短条左侧再加宽（默认 0 = 紧贴内容、无多余空白）
            int wantL = groupL - off;      // off 为负 = 多裁左侧（贴近 Windows 图标）
            int wantR = groupR;
            if (!_bridgeSized || Math.Abs(wantW - _bridgeWidth) >= 4 || Math.Abs(relX - _bridgeRelX) >= 4)
            {
                _bridgeWidth = wantW;
                _bridgeRelX = relX;
                _bridgeSized = true;
                Native.MoveWindow(bridge, relX, 0, wantW, ch, true);
                _report.AppendLine("  内容区 -> 想设 相对x=" + relX + " 宽=" + wantW +
                                   "  (= 图标右端 " + iconR + " + 间距 " + gap + " + 托盘宽 " + trayW + ")");
                Native.RECT chk;
                if (Native.GetWindowRect(bridge, out chk))
                    _report.AppendLine("  内容区实际: 相对x=" + (chk.Left - barRect.Left) +
                                       " 宽=" + (chk.Right - chk.Left) +
                                       (Math.Abs((chk.Left - barRect.Left) - relX) > 2 ? "   <<< 被系统改回去了！" : ""));

                // 校验有没有被系统拽回去；连续被拽回就放弃，不跟它死磕
                Native.RECT after;
                if (Native.GetWindowRect(bridge, out after) && Math.Abs((after.Right - after.Left) - wantW) > 8)
                {
                    _bridgeReverts++;
                    _report.AppendLine("  ! 内容区宽度被系统拽回（第 " + _bridgeReverts + " 次）");
                    if (_bridgeReverts >= 3)
                    {
                        _clusterBlocked = true;
                        _report.AppendLine("  ! 连续被拽回，本次运行不再尝试紧凑 Dock");
                        return false;
                    }
                }
                else
                {
                    _bridgeReverts = 0;
                }
            }

            // 短条 = 内容范围（必要时补过对称）± 留白
            int unionL = wantL;
            int unionR = wantR;
            int left = unionL - barRect.Left;
            int right = cw - (unionR - barRect.Left);
            if (left < 0) left = 0;
            if (right < 0) right = 0;
            if (left + right > cw - 8) { left = 0; right = 0; }

            pillLeft = left;
            pillRight = right;

            // 托盘窗口挪到「图标右端 + 间距」，音量/时钟弹出面板才会跟着走
            MoveTrayToContentRight(h, client, barRect, relX + wantW);

            _clusterOk = true;
            _clusterUsed = true;
            _report.AppendLine("  短条=" + unionL + ".." + unionR +
                               " 裁掉左 " + left + " / 右 " + right);
            return true;
        }
        // 把托盘窗口挪到内容右端（相对父窗口客户区）
        // 托盘窗口的宽度（稳定值；比按按钮量的偏大，用作安全余量）
        private int TrayWindowWidth(IntPtr h)
        {
            IntPtr tray = FindChildByClass(h, Native.ChildTray);
            if (tray == IntPtr.Zero) return 0;
            Native.RECT tr;
            if (!Native.GetWindowRect(tray, out tr)) return 0;
            return tr.Right - tr.Left;
        }
        private void MoveTrayToContentRight(IntPtr h, Native.RECT client, Native.RECT barRect, int contentRightAbs)
        {
            IntPtr tray = FindChildByClass(h, Native.ChildTray);
            if (tray == IntPtr.Zero) return;
            Native.RECT tr;
            if (!Native.GetWindowRect(tray, out tr)) return;
            int trayW = tr.Right - tr.Left;
            int trayH = tr.Bottom - tr.Top;
            if (trayW <= 0 || trayH <= 0) return;

            int wantRelX = (contentRightAbs - barRect.Left) - trayW;
            int curRelX = tr.Left - barRect.Left;
            int relY = tr.Top - barRect.Top;
            _trayShift = wantRelX - (client.Right - trayW);   // 相对"贴最右"的自然位置平移了多少
            if (Math.Abs(curRelX - wantRelX) <= 2) return;

            if (!Native.MoveWindow(tray, wantRelX, relY, trayW, trayH, true))
            {
                _report.AppendLine("  ! 托盘窗口挪位失败, err=" + Marshal.GetLastWin32Error());
                return;
            }
            _report.AppendLine("  托盘窗口挪到内容右端 相对x=" + wantRelX);
        }

        private static Native.RECT MonitorRect(IntPtr h)
        {
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            IntPtr mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && Native.GetMonitorInfo(mon, ref mi)) return mi.rcMonitor;

            Native.RECT fallback = new Native.RECT();
            fallback.Right = Screen.PrimaryScreen.Bounds.Width;
            fallback.Bottom = Screen.PrimaryScreen.Bounds.Height;
            return fallback;
        }

        // 退出紧凑 Dock：内容区恢复整宽、托盘回到最右端
        private void RestoreCluster()
        {
            _clusterUsed = false;
            _trayShift = 0;
            _bridgeWidth = 0;
            _bridgeSized = false;

            List<IntPtr> bars = FindTaskbars(false);
            for (int i = 0; i < bars.Count; i++)
            {
                RestoreBarChildren(bars[i]);
                _report.AppendLine("  任务栏 " + bars[i] + " 的内容区/托盘已还原");
            }
        }

        // 让 explorer 重新布局任务栏内容。改回内容区宽度后**必须**发这一下，
        // 否则画面会停在旧布局上（表现为任务栏内容整体偏向一侧）。
        private static void NudgeLayout(IntPtr h)
        {
            try
            {
                Native.RECT cr;
                if (!Native.GetClientRect(h, out cr)) return;
                if (cr.Right <= 0) return;
                int lp = (cr.Bottom << 16) | (cr.Right & 0xFFFF);
                Native.SendMessage(h, 0x0005 /* WM_SIZE */, IntPtr.Zero, new IntPtr(lp));
            }
            catch { }
        }
        // 把任务栏的内容区与托盘还原成系统默认布局（内容区整宽、托盘贴最右）
        private static void RestoreBarChildren(IntPtr h)
        {
            Native.RECT client;
            if (!Native.GetClientRect(h, out client)) return;
            if (client.Right <= 0 || client.Bottom <= 0) return;

            IntPtr bridge = FindChildByClass(h, Native.ChildBridge);
            if (bridge != IntPtr.Zero)
                Native.MoveWindow(bridge, 0, 0, client.Right, client.Bottom, true);

            IntPtr tray = FindChildByClass(h, Native.ChildTray);
            if (tray != IntPtr.Zero)
            {
                Native.RECT tr, br;
                if (Native.GetWindowRect(tray, out tr) && Native.GetWindowRect(h, out br))
                {
                    int w = tr.Right - tr.Left;
                    int ht = tr.Bottom - tr.Top;
                    if (w > 0 && ht > 0)
                        Native.MoveWindow(tray, client.Right - w, tr.Top - br.Top, w, ht, true);
                }
            }

            NudgeLayout(h);     // 关键：逼 explorer 按新宽度重排内容，否则画面会停在偏移位置
        }
    }

    // --------------------------------------------------------- HiddenWindow

    // 一个不显示的窗口，用来收系统消息（注销 / 关机 / 分辨率变化时立刻响应）
    internal sealed class HiddenWindow : NativeWindow
    {
        public event EventHandler EnvironmentChanged;
        public event EventHandler ShowSettingsRequested;

        public HiddenWindow()
        {
            // 必须是顶层窗口（广播消息只发给顶层窗口），类名用系统预定义的 STATIC
            CreateParams cp = new CreateParams();
            cp.ClassName = "STATIC";
            cp.Caption = "Dockify.Hidden";
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_QUERYENDSESSION = 0x0011;
            const int WM_ENDSESSION = 0x0016;
            const int WM_SETTINGCHANGE = 0x001A;
            const int WM_DISPLAYCHANGE = 0x007E;

            if (m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION)
            {
                TaskbarEngine.ResetTaskbars();
            }
            else if (m.Msg == WM_SETTINGCHANGE || m.Msg == WM_DISPLAYCHANGE)
            {
                EventHandler h = EnvironmentChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
            else if (Program.ShowSettingsMessageId != 0 && (uint)m.Msg == Program.ShowSettingsMessageId)
            {
                EventHandler h2 = ShowSettingsRequested;
                if (h2 != null) h2(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }
    }

    // -------------------------------------------------------- SettingsForm

    // 一行「滑块 + 数字框」
    internal sealed class SliderRow
    {
        public TrackBar Bar;
        public NumericUpDown Num;
        public Label Caption;
        public Label Unit;
        public bool Syncing;
    }

    internal sealed class SettingsForm : Form
    {
        private const int FormW = 460;

        private readonly TaskbarEngine _engine;
        private readonly Settings _original;
        private readonly Settings _working;

        private CheckBox _chkEnabled;
        private RadioButton _radioCrop;
        private RadioButton _radioDock;
        private PctRow _rowRadius;
        private TrackBar _barPctLeft;
        private TrackBar _barPctRight;
        private NumericUpDown _numPctLeft;
        private NumericUpDown _numPctRight;
        private PctRow _rowDockWidth;
        private PctRow _rowDockGap;
        private PctRow _rowDockOffset;
        private PctRow _rowWidth;
        private Label _lblCropHeader;
        private Label _lblDockHeader;

        private CheckBox _chkAdvanced;
        private Panel _advanced;
        private NumericUpDown _numTop;
        private NumericUpDown _numBottom;
        private CheckBox _chkAll;
        private CheckBox _chkDpi;
        private CheckBox _chkAutoHide;
        private PctRow _rowFlyout;

        private Label _statusLabel;
        private Button _btnCancel;
        private Button _btnOk;
        private bool _syncingAll;

        public SettingsForm(TaskbarEngine engine)
        {
            _engine = engine;
            _original = engine.Settings.Clone();
            _working = engine.Settings.Clone();

            Text = "Dockify · 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(FormW, 540);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            _chkEnabled = new CheckBox();
            _chkEnabled.Text = "启用（取消勾选 = 任务栏立刻恢复原样）";
            _chkEnabled.Location = new Point(16, 12);
            _chkEnabled.Size = new Size(400, 24);
            _chkEnabled.Checked = _working.Enabled;
            _chkEnabled.CheckedChanged += delegate { _working.Enabled = _chkEnabled.Checked; Live(); };
            Controls.Add(_chkEnabled);

            Label hint = new Label();
            hint.Location = new Point(16, 38);
            hint.Size = new Size(FormW - 32, 44);
            hint.ForeColor = Color.FromArgb(120, 120, 120);
            hint.Text = "找不到托盘图标？Win11 默认把它藏进任务栏的 ^ 溢出区：" +
                        "设置 → 个性化 → 任务栏 → 其他系统托盘图标。";
            Controls.Add(hint);

            Label lblMode = new Label();
            lblMode.Text = "模式";
            lblMode.Location = new Point(16, 80);
            lblMode.Size = new Size(80, 22);
            Controls.Add(lblMode);

            _radioCrop = new RadioButton();
            _radioCrop.Text = "裁两端 —— 整屏宽，只把两头裁圆（图标不动）";
            _radioCrop.Location = new Point(28, 100);
            _radioCrop.Size = new Size(410, 24);
            _radioCrop.Checked = !_working.IsDock;
            _radioCrop.CheckedChanged += delegate
            {
                if (!_radioCrop.Checked) return;
                _working.Mode = 0;
                UpdateModeEnabled();
                Live();
            };
            Controls.Add(_radioCrop);

            _radioDock = new RadioButton();
            _radioDock.Text = "紧凑 Dock —— 图标与托盘搬成一坨居中（自动隐藏照常）";
            _radioDock.Location = new Point(28, 126);
            _radioDock.Size = new Size(412, 24);
            _radioDock.Checked = _working.IsDock;
            _radioDock.CheckedChanged += delegate
            {
                if (!_radioDock.Checked) return;
                _working.Mode = 2;
                UpdateModeEnabled();
                Live();
            };
            Controls.Add(_radioDock);

            _rowRadius = MakePctRow(this, 178, 0, 6, _working.Radius, "圆角圆度", delegate(double v) { _working.Radius = v; Live(); });

            _lblCropHeader = MakeHeader("裁两端时", 220);
            MakePercentRow(this, 242, 0, 20, _working.MarginLeft, "左裁切",
                delegate(double v) { _working.MarginLeft = v; Live(); }, out _barPctLeft, out _numPctLeft);
            MakePercentRow(this, 290, 0, 20, _working.MarginRight, "右裁切",
                delegate(double v) { _working.MarginRight = v; Live(); }, out _barPctRight, out _numPctRight);

            _lblDockHeader = MakeHeader("紧凑 Dock 时", 332);
            _rowDockWidth = MakePctRow(this, 354, 0, 5, _working.ClusterGap, "图标/托盘间距", delegate(double v) { _working.ClusterGap = v; Live(); });

            _rowDockGap = MakePctRow(this, 402, 0, 10, _working.DockPadding, "短条留白", delegate(double v) { _working.DockPadding = v; Live(); });

                        _rowWidth = MakePctRow(this, 442, 0, 20, _working.DockWidthTrim, "内容长度微调", delegate(double v) { _working.DockWidthTrim = v; Live(); });

            _rowDockOffset = MakePctRow(this, 490, -30, 20, _working.DockOffsetX, "左侧裁切微调", delegate(double v) { _working.DockOffsetX = v; Live(); });

            Button btnDefaults = new Button();
            btnDefaults.Text = "恢复默认";
            btnDefaults.Location = new Point(16, 516);
            btnDefaults.Size = new Size(92, 28);
            btnDefaults.Click += delegate { ApplyDefaults(); };
            Controls.Add(btnDefaults);

            _chkAdvanced = new CheckBox();
            _chkAdvanced.Text = "更多设置";
            _chkAdvanced.Location = new Point(280, 520);
            _chkAdvanced.Size = new Size(110, 24);
            _chkAdvanced.CheckedChanged += delegate { LayoutForAdvanced(); };
            Controls.Add(_chkAdvanced);

            _advanced = BuildAdvancedPanel();
            _advanced.Visible = false;
            Controls.Add(_advanced);

            _btnCancel = new Button();
            _btnCancel.Text = "取消";
            _btnCancel.Size = new Size(84, 28);
            _btnCancel.Click += delegate { Cancel(); };
            Controls.Add(_btnCancel);

            _btnOk = new Button();
            _btnOk.Text = "确定";
            _btnOk.Size = new Size(84, 28);
            _btnOk.Click += delegate
            {
                _engine.Settings.CopyFrom(_working);
                _engine.Settings.Save();
                _engine.Tick();

                // 没生效就别急着关窗 —— 把原因摆给用户看，否则就是"点确定没反应"
                if (_engine.Settings.Enabled && _engine.AppliedBars == 0)
                {
                    _statusLabel.Text = "状态：" + _engine.Status + " ← 没生效，请关掉后在资源管理器里双击 exe 再试";
                    return;
                }
                Close();
            };
            Controls.Add(_btnOk);

            _statusLabel = new Label();
            _statusLabel.Size = new Size(FormW - 32, 22);
            _statusLabel.ForeColor = Color.FromArgb(190, 60, 60);
            _statusLabel.Text = "状态：" + _engine.Status;
            Controls.Add(_statusLabel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            UpdateModeEnabled();
            LayoutForAdvanced();
        }

        // 程序是从托盘/后台起来的，Windows 默认不许它抢前台 —— 不强制一下，
        // 设置窗口会开在别的窗口后面，用户点了「设置…」却什么都看不见。
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                bool wasTopMost = TopMost;
                TopMost = true;
                Activate();
                BringToFront();
                TopMost = wasTopMost;
            }
            catch { }
        }

        private Label MakeHeader(string text, int y)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.Location = new Point(16, y);
            lbl.Size = new Size(300, 20);
            lbl.ForeColor = Color.FromArgb(70, 70, 70);
            lbl.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(lbl);
            return lbl;
        }

        // 百分比行（两位小数）：给"裁切量"用 —— 百分比天然适配分辨率与系统缩放
        // 百分比滑块的容器（TrackBar 用"百分之一"的整数刻度，数值框显示两位小数）
        private sealed class PctRow
        {
            public TrackBar Bar;
            public NumericUpDown Num;
        }

        private PctRow MakePctRow(Control parent, int y, double minPct, double maxPct, double value,
                                  string caption, Action<double> onChange)
        {
            Label cap = new Label();
            cap.Text = caption;
            cap.Location = new Point(28, y + 5);
            cap.Size = new Size(88, 22);
            parent.Controls.Add(cap);

            TrackBar tb = new TrackBar();
            tb.Minimum = (int)Math.Round(minPct * 100.0);
            tb.Maximum = (int)Math.Round(maxPct * 100.0);
            tb.Value = Math.Max(tb.Minimum, Math.Min(tb.Maximum, (int)Math.Round(value * 100.0)));
            tb.TickFrequency = Math.Max(1, (tb.Maximum - tb.Minimum) / 8);
            tb.SmallChange = Math.Max(1, (tb.Maximum - tb.Minimum) / 200);
            tb.LargeChange = Math.Max(1, (tb.Maximum - tb.Minimum) / 20);
            tb.AutoSize = false;
            tb.Height = 28;
            tb.Location = new Point(118, y);
            tb.Size = new Size(228, 28);
            parent.Controls.Add(tb);

            NumericUpDown nu = new NumericUpDown();
            nu.DecimalPlaces = 2;
            nu.Increment = 0.05m;
            nu.Minimum = (decimal)minPct;
            nu.Maximum = (decimal)maxPct;
            nu.Value = Math.Max(nu.Minimum, Math.Min(nu.Maximum, (decimal)value));
            nu.Location = new Point(354, y + 2);
            nu.Size = new Size(62, 24);
            parent.Controls.Add(nu);

            Label un = new Label();
            un.Text = "%";
            un.Location = new Point(420, y + 5);
            un.Size = new Size(28, 22);
            parent.Controls.Add(un);

            PctRow row = new PctRow();
            row.Bar = tb;
            row.Num = nu;

            bool syncing = false;
            tb.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                nu.Value = tb.Value / 100m;
                syncing = false;
                onChange(tb.Value / 100.0);
            };
            nu.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                int v = (int)Math.Round(nu.Value * 100m);
                if (v < tb.Minimum) v = tb.Minimum;
                if (v > tb.Maximum) v = tb.Maximum;
                tb.Value = v;
                syncing = false;
                onChange((double)nu.Value);
            };
            return row;
        }

        private static void SyncPctRow(PctRow row, double v)
        {
            if (row == null) return;
            int iv = (int)Math.Round(v * 100.0);
            if (iv < row.Bar.Minimum) iv = row.Bar.Minimum;
            if (iv > row.Bar.Maximum) iv = row.Bar.Maximum;
            row.Bar.Value = iv;
            row.Num.Value = Math.Max(row.Num.Minimum, Math.Min(row.Num.Maximum, (decimal)v));
        }
        private void MakePercentRow(Control parent, int y, double minPct, double maxPct, double value,
                                    string caption, Action<double> onChange,
                                    out TrackBar outBar, out NumericUpDown outNum)
        {
            Label cap = new Label();
            cap.Text = caption;
            cap.Location = new Point(28, y + 5);
            cap.Size = new Size(88, 22);
            parent.Controls.Add(cap);

            TrackBar tb = new TrackBar();
            tb.Minimum = (int)Math.Round(minPct * 100.0);
            tb.Maximum = (int)Math.Round(maxPct * 100.0);
            tb.Value = (int)Math.Round(value * 100.0);
            tb.TickFrequency = Math.Max(1, (tb.Maximum - tb.Minimum) / 8);
            tb.SmallChange = 5;                 // 0.05%
            tb.LargeChange = 25;                // 0.25%
            tb.AutoSize = false;
            tb.Height = 28;
            tb.Location = new Point(118, y);
            tb.Size = new Size(228, 28);
            parent.Controls.Add(tb);

            NumericUpDown nu = new NumericUpDown();
            nu.DecimalPlaces = 2;
            nu.Increment = 0.05m;
            nu.Minimum = (decimal)minPct;
            nu.Maximum = (decimal)maxPct;
            nu.Value = (decimal)value;
            nu.Location = new Point(354, y + 2);
            nu.Size = new Size(62, 24);
            parent.Controls.Add(nu);

            Label un = new Label();
            un.Text = "%";
            un.Location = new Point(420, y + 5);
            un.Size = new Size(28, 22);
            parent.Controls.Add(un);

            bool syncing = false;
            tb.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                nu.Value = tb.Value / 100m;
                syncing = false;
                onChange(tb.Value / 100.0);
            };
            nu.ValueChanged += delegate
            {
                if (syncing) return;
                syncing = true;
                int v = (int)Math.Round(nu.Value * 100m);
                if (v < tb.Minimum) v = tb.Minimum;
                if (v > tb.Maximum) v = tb.Maximum;
                tb.Value = v;
                syncing = false;
                onChange((double)nu.Value);
            };

            outBar = tb;
            outNum = nu;
        }

        private static void SyncPct(TrackBar bar, NumericUpDown num, double v)
        {
            int iv = (int)Math.Round(v * 100.0);
            if (iv < bar.Minimum) iv = bar.Minimum;
            if (iv > bar.Maximum) iv = bar.Maximum;
            bar.Value = iv;
            num.Value = (decimal)v;
        }
        private SliderRow MakeSlider(int y, int min, int max, int value, string caption, string unit)
        {
            return MakeSlider(this, y, min, max, value, caption, unit);
        }

        private SliderRow MakeSlider(Control parent, int y, int min, int max, int value, string caption, string unit)
        {
            SliderRow row = new SliderRow();

            row.Caption = new Label();
            row.Caption.Text = caption;
            row.Caption.Location = new Point(28, y + 5);
            row.Caption.Size = new Size(88, 22);

            row.Bar = new TrackBar();
            row.Bar.Minimum = min;
            row.Bar.Maximum = max;
            row.Bar.Value = value;
            row.Bar.TickFrequency = Math.Max(1, (max - min) / 8);
            row.Bar.SmallChange = 1;
            row.Bar.LargeChange = Math.Max(1, (max - min) / 8);
            row.Bar.AutoSize = false;
            row.Bar.Height = 28;
            row.Bar.Location = new Point(118, y);
            row.Bar.Size = new Size(228, 28);

            row.Num = new NumericUpDown();
            row.Num.Minimum = min;
            row.Num.Maximum = max;
            row.Num.Value = value;
            row.Num.Location = new Point(354, y + 2);
            row.Num.Size = new Size(62, 24);

            row.Unit = new Label();
            row.Unit.Text = unit;
            row.Unit.Location = new Point(420, y + 5);
            row.Unit.Size = new Size(28, 22);

            row.Bar.ValueChanged += delegate
            {
                if (row.Syncing) return;
                row.Syncing = true;
                row.Num.Value = row.Bar.Value;
                row.Syncing = false;
            };
            row.Num.ValueChanged += delegate
            {
                if (row.Syncing) return;
                row.Syncing = true;
                int v = (int)row.Num.Value;
                if (v < row.Bar.Minimum) v = row.Bar.Minimum;
                if (v > row.Bar.Maximum) v = row.Bar.Maximum;
                row.Bar.Value = v;
                row.Syncing = false;
            };

            parent.Controls.Add(row.Caption);
            parent.Controls.Add(row.Bar);
            parent.Controls.Add(row.Num);
            parent.Controls.Add(row.Unit);
            return row;
        }

        private Panel BuildAdvancedPanel()
        {
            Panel p = new Panel();
            p.Location = new Point(0, 608);
            p.Size = new Size(FormW, 144);

            Label l1 = new Label();
            l1.Text = "上边距";
            l1.Location = new Point(28, 6);
            l1.Size = new Size(60, 22);
            p.Controls.Add(l1);

            _numTop = new NumericUpDown();
            _numTop.DecimalPlaces = 2;
            _numTop.Increment = 0.05m;
            _numTop.Minimum = 0;
            _numTop.Maximum = 6;
            _numTop.Value = (decimal)_working.MarginTop;
            _numTop.Location = new Point(92, 4);
            _numTop.Size = new Size(62, 24);
            _numTop.ValueChanged += delegate { _working.MarginTop = (double)_numTop.Value; Live(); };
            p.Controls.Add(_numTop);

            Label l1u = new Label();
            l1u.Text = "%";
            l1u.Location = new Point(156, 6);
            l1u.Size = new Size(24, 22);
            p.Controls.Add(l1u);

            Label l2 = new Label();
            l2.Text = "下边距";
            l2.Location = new Point(210, 6);
            l2.Size = new Size(60, 22);
            p.Controls.Add(l2);

            _numBottom = new NumericUpDown();
            _numBottom.DecimalPlaces = 2;
            _numBottom.Increment = 0.05m;
            _numBottom.Minimum = 0;
            _numBottom.Maximum = 6;
            _numBottom.Value = (decimal)_working.MarginBottom;
            _numBottom.Location = new Point(274, 4);
            _numBottom.Size = new Size(62, 24);
            _numBottom.ValueChanged += delegate { _working.MarginBottom = (double)_numBottom.Value; Live(); };
            p.Controls.Add(_numBottom);

            Label l2u = new Label();
            l2u.Text = "%";
            l2u.Location = new Point(338, 6);
            l2u.Size = new Size(24, 22);
            p.Controls.Add(l2u);

            _chkAll = new CheckBox();
            _chkAll.Text = "所有显示器都处理";
            _chkAll.Location = new Point(28, 32);
            _chkAll.Size = new Size(180, 22);
            _chkAll.Checked = _working.AllMonitors;
            _chkAll.CheckedChanged += delegate { _working.AllMonitors = _chkAll.Checked; Live(); };
            p.Controls.Add(_chkAll);

            _chkDpi = new CheckBox();
            _chkDpi.Text = "数值随系统缩放";
            _chkDpi.Location = new Point(214, 32);
            _chkDpi.Size = new Size(180, 22);
            _chkDpi.Checked = _working.ScaleWithDpi;
            _chkDpi.CheckedChanged += delegate { _working.ScaleWithDpi = _chkDpi.Checked; Live(); };
            p.Controls.Add(_chkDpi);

            _chkAutoHide = new CheckBox();
            _chkAutoHide.Text = "任务栏自动隐藏时也处理";
            _chkAutoHide.Location = new Point(28, 56);
            _chkAutoHide.Size = new Size(260, 22);
            _chkAutoHide.Checked = _working.ApplyOnAutoHide;
            _chkAutoHide.CheckedChanged += delegate { _working.ApplyOnAutoHide = _chkAutoHide.Checked; Live(); };
            p.Controls.Add(_chkAutoHide);

            // 托盘弹出面板的额外左移量：由用户自己调（0 = 不调整）
            _rowFlyout = MakePctRow(p, 76, 0, 50, _working.FlyoutOffsetX, "^面板左移", delegate(double v) { _working.FlyoutOffsetX = v; });






            return p;
        }

        private void LayoutForAdvanced()
        {
            bool adv = _chkAdvanced.Checked;
            _advanced.Visible = adv;
            // 高级区展开时把窗体加高，主按钮跟着往下挪
            int height = adv ? 824 : 710;
            ClientSize = new Size(FormW, height);
            int btnY = adv ? 718 : 604;
            _btnCancel.Location = new Point(FormW - 196, btnY);
            _btnOk.Location = new Point(FormW - 104, btnY);
            if (_statusLabel != null) _statusLabel.Location = new Point(16, btnY + 32);
        }

        private void UpdateModeEnabled()
        {
            bool dock = _working.IsDock;
            _barPctLeft.Enabled = !dock;
            _numPctLeft.Enabled = !dock;
            _barPctRight.Enabled = !dock;
            _numPctRight.Enabled = !dock;
            _lblCropHeader.ForeColor = dock ? Color.FromArgb(170, 170, 170) : Color.FromArgb(70, 70, 70);

            _rowDockWidth.Bar.Enabled = dock;
            _rowDockWidth.Num.Enabled = dock;
            _rowWidth.Bar.Enabled = dock;
            _rowWidth.Num.Enabled = dock;
            _rowDockOffset.Bar.Enabled = dock;
            _rowDockOffset.Num.Enabled = dock;
            _rowWidth.Num.Enabled = dock;
            _rowDockGap.Bar.Enabled = dock;
            _rowDockGap.Num.Enabled = dock;
            _lblDockHeader.ForeColor = dock ? Color.FromArgb(70, 70, 70) : Color.FromArgb(170, 170, 170);
        }

        // 把 _working 的当前值推回所有控件
        private void SyncFromWorking()
        {
            _syncingAll = true;
            _chkEnabled.Checked = _working.Enabled;
            _radioCrop.Checked = !_working.IsDock;
            _radioDock.Checked = _working.IsDock;
            SyncPctRow(_rowRadius, _working.Radius);
            SyncPct(_barPctLeft, _numPctLeft, _working.MarginLeft);
            SyncPct(_barPctRight, _numPctRight, _working.MarginRight);
            SyncPctRow(_rowDockWidth, _working.ClusterGap);
            SyncPctRow(_rowDockGap, _working.DockPadding);
            _numTop.Value = (decimal)_working.MarginTop;
            _numBottom.Value = (decimal)_working.MarginBottom;
            _chkAll.Checked = _working.AllMonitors;
            _chkDpi.Checked = _working.ScaleWithDpi;
            _chkAutoHide.Checked = _working.ApplyOnAutoHide;
            SyncPctRow(_rowFlyout, _working.FlyoutOffsetX);
            SyncPctRow(_rowWidth, _working.DockWidthTrim);
            SyncPctRow(_rowDockOffset, _working.DockOffsetX);
            _syncingAll = false;
            UpdateModeEnabled();
        }

        // 改动即时预览：直接改引擎正在用的设置对象，让托盘那边的节拍去套用
        private void Live()
        {
            if (_syncingAll) return;
            _engine.Settings.CopyFrom(_working);
            _engine.Tick();
            if (_statusLabel != null) _statusLabel.Text = "状态：" + _engine.Status;
        }

        private void ApplyDefaults()
        {
            Settings d = new Settings();
            d.Enabled = _working.Enabled;
            _working.CopyFrom(d);
            SyncFromWorking();
            Live();
        }

        private void Cancel()
        {
            _engine.Settings.CopyFrom(_original);
            _engine.Tick();
            Close();
        }
    }

    // ---------------------------------------------------------- Program

    internal static class Program
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "Dockify";

        private static Mutex _mutex;
        private static TaskbarEngine _engine;
        private static NotifyIcon _tray;
        private static ToolStripMenuItem _miEnable;
        private static ToolStripMenuItem _miAutoStart;
        private static ToolStripMenuItem _miModeCrop;
        private static ToolStripMenuItem _miModeDock;
        private static HiddenWindow _hidden;
        private static System.Windows.Forms.Timer _timer;
        private static Icon _icon;
        private static IntPtr _iconHandle = IntPtr.Zero;
        private static string _lastTip = "";
        private static bool _warnedFailure;
        private static bool _warnedCluster;

        // 用注册消息来叫醒已经在跑的实例（第二次双击 exe 时把设置窗口弹出来）
        private static readonly uint ShowSettingsMessage = Native.RegisterWindowMessage("Dockify.ShowSettings.v1");

        public static uint ShowSettingsMessageId
        {
            get { return ShowSettingsMessage; }
        }

        private static void PostShowSettings()
        {
            try
            {
                if (ShowSettingsMessage == 0) return;
                Native.PostMessage(new IntPtr(0xFFFF) /* HWND_BROADCAST */, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            bool doReset = false;
            bool doApplyOnly = false;
            bool openSettings = false;
            bool doDiag = false;
            bool relaunched = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant().TrimStart('-', '/');
                if (a == "reset" || a == "restore") doReset = true;
                else if (a == "apply") doApplyOnly = true;
                else if (a == "settings" || a == "config") openSettings = true;
                else if (a == "diag" || a == "diagnose") doDiag = true;
                else if (a == "relaunched") relaunched = true;      // 已经由资源管理器代为重启过
            }

            SetDpiAwareness();

            bool createdNew;
            _mutex = new Mutex(true, "Dockify.SingleInstance.v1", out createdNew);

            // 恢复模式：不需要常驻
            if (doReset)
            {
                TaskbarEngine.ResetTaskbars();
                RestoreAlignment();          // 顺便把任务栏对齐方式还原成用户原本的
                return;
            }

            // 只套用一次就退出（便于脚本 / 计划任务调用；退出码 0=成功，2=没套上）
            if (doApplyOnly)
            {
                TaskbarEngine once = new TaskbarEngine(Settings.Load());
                once.Tick();
                Environment.ExitCode = once.AppliedBars > 0 ? 0 : 2;
                if (Environment.ExitCode != 0)
                {
                    WriteDiag(once);
                    Echo(once, false);
                }
                return;
            }

            // 诊断模式：跑一遍并把过程写到文件里
            if (doDiag)
            {
                TaskbarEngine d = new TaskbarEngine(Settings.Load());
                d.Tick();
                WriteDiag(d);
                Echo(d, true);
                Environment.ExitCode = d.AppliedBars > 0 ? 0 : 2;
                return;
            }

            // 已经有一个实例在跑了：让那个实例把设置窗口弹出来
            // （用户双击 exe 却看不到界面，多半就是这里以前被静默吃掉了）
            if (!createdNew)
            {
                PostShowSettings();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool firstRun = !Settings.Exists();
            Settings boot = Settings.Load();
            if (firstRun) boot.Save();       // 第一次启动就把默认配置写到磁盘，方便直接编辑
            if (boot.SettingsVersion < 4)
            {
                // v3 及以前这几个是「像素」→ v4 起统一改成「占屏幕尺寸的百分比」
                double sw = 1920, sh = 1080;
                try { sw = Screen.PrimaryScreen.Bounds.Width; sh = Screen.PrimaryScreen.Bounds.Height; } catch { }
                if (sw < 100) sw = 1920;
                if (sh < 100) sh = 1080;
                if (boot.SettingsVersion >= 3)
                {
                    boot.Radius = Math.Round(boot.Radius / sh * 100.0, 2);
                    boot.DockPadding = Math.Round(boot.DockPadding / sw * 100.0, 2);
                    boot.ClusterGap = Math.Round(boot.ClusterGap / sw * 100.0, 2);
                    boot.DockOffsetX = Math.Round(boot.DockOffsetX / sw * 100.0, 2);
                    boot.DockWidthTrim = Math.Round(boot.DockWidthTrim / sw * 100.0, 2);
                    boot.FlyoutOffsetX = Math.Round(boot.FlyoutOffsetX / sw * 100.0, 2);
                }
                else
                {
                    // 更老（v2 及以前）的默认像素值，直接套用新的默认百分比
                    boot.Radius = 1.11; boot.DockPadding = 1.4; boot.ClusterGap = 0.83;
                    boot.DockOffsetX = -1.56; boot.DockWidthTrim = 1.04; boot.FlyoutOffsetX = 7.8;
                }
                boot.SettingsVersion = 4;
                boot.Save();
            }
            boot.ClampAll();     // 迁移/读取之后统一裁剪到合法范围
            if (boot.SettingsVersion < 3)
            {
                // v2 及以前裁切量是「像素」，v3 起改成「百分比」→ 一次性换算（按 1920 宽估算）
                boot.MarginLeft = Math.Round(boot.MarginLeft / 19.2, 2);
                boot.MarginRight = Math.Round(boot.MarginRight / 19.2, 2);
                boot.MarginTop = Math.Round(boot.MarginTop / 19.2, 2);
                boot.MarginBottom = Math.Round(boot.MarginBottom / 19.2, 2);
                boot.SettingsVersion = 3;
                boot.Save();
            }
            if (boot.SettingsVersion < 2)
            {
                // 老配置：那时"托盘面板跟随"是默认开的，实测证明它会把面板推偏（补偿两次），所以关掉
                boot.SettingsVersion = 2;
                boot.Save();
            }
            ForceCenterAlignment(boot);      // 紧凑 Dock 只在居中的任务栏上成立
            _engine = new TaskbarEngine(boot);
            _engine.Tick();
            _engine.InstallFlyoutHook();     // 弹出面板毫秒级定位（一步到位）

            // 连任务栏都枚举不到 → 这个进程的启动上下文受限（例如从受限终端里被拉起来的），
            // 怎么改都没用。让资源管理器代为重启一次，换一个干净的上下文（只试一次，避免死循环）。
            if (!relaunched && _engine.TaskbarsFound == 0)
            {
                RelaunchViaExplorer();
                return;
            }

            _iconHandle = IntPtr.Zero;
            _icon = CreateIcon(out _iconHandle);

            _hidden = new HiddenWindow();
            _hidden.EnvironmentChanged += delegate { _engine.Tick(); };
            _hidden.ShowSettingsRequested += delegate { OpenSettings(); };

            BuildTray();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 500;
            _timer.Tick += delegate
            {
                try
                {
                    _engine.Tick();
                    WarnOnceIfBlocked();
                    RefreshTray();
                }
                catch { /* 单次失败不影响常驻 */ }
            };
            _timer.Start();

            Application.ApplicationExit += delegate { Restore(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { Restore(); };

            if (firstRun || openSettings)
            {
                // 第一次运行 / 带 --settings：直接把设置窗口摆出来，别让人找不着
                Native.PostMessage(_hidden.Handle, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
            }

            Application.Run(new ApplicationContext());

            Restore();
        }

        private static void SetDpiAwareness()
        {
            try
            {
                // PER_MONITOR_AWARE_V2 = -4：不做坐标虚拟化，GetWindowRect / SetWindowRgn 都是物理像素
                if (Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch { }
            try { Native.SetProcessDPIAware(); }
            catch { }
        }

        private static void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            _miEnable = new ToolStripMenuItem("启用圆角裁切");
            _miEnable.CheckOnClick = true;
            _miEnable.Checked = _engine.Settings.Enabled;
            _miEnable.Click += delegate
            {
                _engine.Settings.Enabled = _miEnable.Checked;
                _engine.Settings.Save();
                _engine.Tick();
                RefreshTray();
            };
            menu.Items.Add(_miEnable);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miSettings = new ToolStripMenuItem("设置…");
            miSettings.Click += delegate { OpenSettings(); };
            menu.Items.Add(miSettings);

            ToolStripMenuItem miReset = new ToolStripMenuItem("恢复任务栏原样");
            miReset.Click += delegate
            {
                _engine.Settings.Enabled = false;
                _engine.Settings.Save();
                _miEnable.Checked = false;
                _engine.Reset();
                RefreshTray();
            };
            menu.Items.Add(miReset);

            ToolStripMenuItem miReapply = new ToolStripMenuItem("立即重新裁切");
            miReapply.Click += delegate { _engine.Tick(); RefreshTray(); };
            menu.Items.Add(miReapply);

            // 模式快切
            ToolStripMenuItem miMode = new ToolStripMenuItem("模式");
            ToolStripMenuItem miModeCrop = new ToolStripMenuItem("裁两端（整屏宽）");
            miModeCrop.CheckOnClick = false;
            miModeCrop.Click += delegate { SetMode(0); };
            ToolStripMenuItem miModeDock = new ToolStripMenuItem("紧凑 Dock（图标+托盘居中）");
            miModeDock.CheckOnClick = false;
            miModeDock.Click += delegate { SetMode(2); };
            miMode.DropDownItems.Add(miModeCrop);
            miMode.DropDownItems.Add(miModeDock);
            menu.Items.Add(miMode);
            _miModeCrop = miModeCrop;
            _miModeDock = miModeDock;

            menu.Items.Add(new ToolStripSeparator());


            _miAutoStart = new ToolStripMenuItem("开机自动启动");
            _miAutoStart.CheckOnClick = true;
            _miAutoStart.Checked = IsAutoStart();
            _miAutoStart.Click += delegate
            {
                try
                {
                    SetAutoStart(_miAutoStart.Checked);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("设置开机启动失败：" + ex.Message, "Dockify",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _miAutoStart.Checked = IsAutoStart();
                }
            };
            menu.Items.Add(_miAutoStart);

            ToolStripMenuItem miAbout = new ToolStripMenuItem("关于");
            miAbout.Click += delegate
            {
                MessageBox.Show(
                    "任务栏圆角 (Dockify)\n\n" +
                    "用窗口区域 (SetWindowRgn) 把任务栏裁成两侧不贴边的圆角条。\n" +
                    "当前：" + _engine.Status + "\n\n" +
                    "配置文件：" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) +
                    "\\Dockify\\settings.ini\n" +
                    "命令行：Dockify.exe --reset 可强制恢复任务栏原样",
                    "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(miAbout);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { Application.Exit(); };
            menu.Items.Add(miExit);

            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.Text = "Dockify";
            _tray.Visible = true;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { OpenSettings(); };
            RefreshTray();
        }

        // 被系统拒绝访问（err=5）时说明一次原因，别让人对着没反应的托盘图标发懵
        private static void WarnOnceIfBlocked()
        {
            if (_warnedCluster && _engine.ClusterBlocked)
            {
                // 已经提示过
            }
            else if (_engine.ClusterBlocked && !_warnedCluster)
            {
                _warnedCluster = true;
                try
                {
                    _tray.BalloonTipTitle = "任务栏圆角：紧凑 Dock 被系统驳回";
                    _tray.BalloonTipText = "Windows 拒绝把任务栏里的图标/托盘重新摆位（可能被系统拽回去了），" +
                                           "已自动退回『裁两端』模式。想真正把托盘挪到中间，需要装 Windhawk 的" +
                                           "『taskbar centered condensed』模块，详见 README。";
                    _tray.ShowBalloonTip(12000);
                }
                catch { }
                return;
            }

            if (_warnedFailure) return;
            if (!_engine.Settings.Enabled) return;
            if (_engine.AppliedBars > 0) return;
            _warnedFailure = true;
            string logPath = WriteDiag(_engine);
            try
            {
                _tray.BalloonTipTitle = "任务栏圆角：裁切被系统拒绝";
                _tray.BalloonTipText = "调用 SetWindowRgn 返回『拒绝访问』。常见原因：本程序是被带受限权限的进程" +
                                       "（例如某个沙箱/助手会话的终端）拉起来的。请关掉本程序，直接在文件资源管理器里双击 " +
                                       "Dockify.exe 运行。诊断日志：" +
                                       (logPath != null ? logPath : "（写入失败）");
                _tray.ShowBalloonTip(10000);
            }
            catch { }
        }

        private static void RefreshTray()
        {
            if (_tray == null) return;
            string tip = "任务栏圆角 — " + _engine.Status;
            if (tip.Length > 62) tip = tip.Substring(0, 62);
            if (tip != _lastTip)
            {
                _tray.Text = tip;
                _lastTip = tip;
            }
            if (_miEnable != null && _miEnable.Checked != _engine.Settings.Enabled)
                _miEnable.Checked = _engine.Settings.Enabled;
            if (_miModeCrop != null) _miModeCrop.Checked = !_engine.Settings.IsDock;
            if (_miModeDock != null) _miModeDock.Checked = _engine.Settings.IsDock;
        }

        // 上下文受限时，请资源管理器代为启动一次：资源管理器有自己的干净令牌，
        // 由它拉起来的进程才能正常操作任务栏窗口。
        private static void RelaunchViaExplorer()
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "\"" + Application.ExecutablePath + "\" --relaunched");
            }
            catch { }
        }
        private static void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm(_engine))
            {
                f.ShowDialog();
            }
            RefreshTray();
        }

        // 切换显示模式（0=裁两端, 1=悬浮 Dock）
        private static void SetMode(int mode)
        {
            _engine.Settings.Mode = mode;
            _engine.Settings.Save();
            _engine.Tick();
            RefreshTray();
        }

        // 一键苹果 Dock 风格：图标与托盘排成一坨居中 + 贴合内容的圆角短条
        // 任务栏对齐方式存在 HKCU\...\Explorer\Advanced\TaskbarAl：0 = 居左，1 = 居中。
        // 紧凑 Dock 只在「居中」下成立，所以启动时临时改成居中、退出时还原用户原本的设置 ——
        // 这样就不用为「居左」用户单独出一套方案。
        private const string AdvKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam,
                                                        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        internal static int ReadTaskbarAl()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AdvKey, false))
                {
                    if (k == null) return 1;
                    object o = k.GetValue("TaskbarAl");
                    if (o == null) return 1;                       // 没有这个值 = 默认居中
                    return Convert.ToInt32(o) == 0 ? 0 : 1;
                }
            }
            catch { return 1; }
        }

        private static bool WriteTaskbarAl(int v)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(AdvKey))
                {
                    if (k == null) return false;
                    k.SetValue("TaskbarAl", v, RegistryValueKind.DWord);
                }
                // 让资源管理器立刻重读任务栏设置
                IntPtr res;
                SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "TraySettings",
                                   SMTO_ABORTIFHUNG, 1500, out res);
                return true;
            }
            catch { return false; }
        }

        // 启动时：把任务栏临时改成居中（并记住用户原本的设置）
        private static void ForceCenterAlignment(Settings st)
        {
            try
            {
                int cur = ReadTaskbarAl();
                if (st.SavedTaskbarAl >= 0)
                {
                    // 上次没还原干净（例如被强杀）：先确保现在是居中
                    if (cur != 1) WriteTaskbarAl(1);
                    return;
                }
                if (cur == 0)
                {
                    st.SavedTaskbarAl = cur;
                    st.Save();
                    WriteTaskbarAl(1);
                }
            }
            catch { }
        }

        // 退出时：还原用户原本的对齐方式
        private static void RestoreAlignment()
        {
            try
            {
                Settings st = Settings.Load();
                if (st.SavedTaskbarAl < 0) return;
                WriteTaskbarAl(st.SavedTaskbarAl);
                st.SavedTaskbarAl = -1;
                st.Save();
            }
            catch { }
        }
        // 把最近一拍的完整测量过程写到 diag.txt（覆盖式）。
        // 这样即便我这边没法操作任务栏，也能读到真实的图标/托盘/内容区数值来定位问题。
        internal static void WriteLiveDiag(TaskbarEngine engine)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockify", "diag.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Dockify 实时诊断 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("状态: " + engine.Status + "   生效任务栏数: " + engine.AppliedBars);
                if (!string.IsNullOrEmpty(Settings.LastSaveError))
                    sb.AppendLine("配置写入失败: " + Settings.LastSaveError);
                sb.AppendLine(engine.Report);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
        private static void Restore()
        {
            try { if (_timer != null) _timer.Stop(); } catch { }
            try { if (_engine != null) { _engine.RemoveFlyoutHook(); _engine.Reset(); } } catch { }
            RestoreAlignment();              // 把任务栏对齐方式还回去
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
            try { if (_iconHandle != IntPtr.Zero) Native.DestroyIcon(_iconHandle); } catch { }
        }

        // 诊断报告路径（--diag 或裁切失败时写）
        public static string DiagPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "Dockify", "diag.txt");
            }
        }

        // 写诊断报告；AppData 写不进去（受限进程常见）就退到临时目录。
        // 返回实际写入的路径，全都写不进去则返回 null。
        private static string WriteDiag(TaskbarEngine engine)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Dockify 诊断报告");
            sb.AppendLine("程序   : " + Application.ExecutablePath);
            sb.AppendLine("用户   : " + Environment.UserName);
            sb.AppendLine("系统   : " + Environment.OSVersion.VersionString);
            sb.AppendLine("运行时 : .NET " + Environment.Version);
            sb.AppendLine("状态   : " + engine.Status);
            sb.AppendLine();
            sb.Append(engine.Report);

            string[] targets = new string[]
            {
                DiagPath,
                Path.Combine(Path.GetTempPath(), "Dockify-diag.txt")
            };
            for (int i = 0; i < targets.Length; i++)
            {
                try
                {
                    string dir = Path.GetDirectoryName(targets[i]);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(targets[i], sb.ToString(), new UTF8Encoding(false));
                    return targets[i];
                }
                catch { }
            }
            return null;
        }

        // 有控制台时（用 /target:exe 调试编译）把报告打到屏幕上；winexe 下是空操作
        private static void Echo(TaskbarEngine engine, bool always)
        {
            try
            {
                if (!always && engine.AppliedBars > 0) return;
                Console.WriteLine("Dockify 诊断");
                Console.WriteLine("程序 : " + Application.ExecutablePath);
                Console.WriteLine("用户 : " + Environment.UserName);
                Console.WriteLine("状态 : " + engine.Status);
                Console.WriteLine("日志 : " + DiagPath);
                Console.WriteLine();
                Console.WriteLine(engine.Report);
            }
            catch { }
        }

        private static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RunValueName);
                    return v != null && v.ToString().Trim().Length > 0;
                }
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (k == null) throw new InvalidOperationException("打不开注册表启动项");
                if (on)
                {
                    string exe = Application.ExecutablePath;
                    k.SetValue(RunValueName, "\"" + exe + "\"");
                }
                else
                {
                    k.DeleteValue(RunValueName, false);
                }
            }
        }

        // 托盘图标：一个蓝色圆角小条。用位图现画，避免额外资源文件。
        private static Icon CreateIcon(out IntPtr handle)
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (GraphicsPath path = RoundedRect(new RectangleF(2f, 10f, 28f, 13f), 5f))
                {
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 62, 130, 255)))
                    {
                        g.FillPath(brush, path);
                    }
                }
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                {
                    g.FillEllipse(dot, 7f, 15f, 4.5f, 4.5f);
                    g.FillEllipse(dot, 14f, 15f, 4.5f, 4.5f);
                    g.FillEllipse(dot, 21f, 15f, 4.5f, 4.5f);
                }
            }
            handle = bmp.GetHicon();
            Icon ic = Icon.FromHandle(handle);
            bmp.Dispose();
            return ic;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }
    }
}
