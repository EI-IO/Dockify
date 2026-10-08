// ============================================================================
//  Dockify · v1.2
//  Windows 11 任务栏两侧裁切 + 圆角，并可把「开始按钮 + 程序图标 + 系统托盘」
//  压成一条居中的圆角短条（紧凑 Dock）。
// ----------------------------------------------------------------------------
//  它做了什么
//    1) 用 SetWindowRgn 给任务栏窗口套一个圆角矩形区域 → 左右裁切 + 四角圆角。
//       窗口区域只「切」不「补」，且不影响布局，所以任务栏窗口从不移动，
//       自动隐藏/唤出与点击行为完全不受影响。
//    2) 「紧凑 Dock」：任务栏画面由子窗口
//       Windows.UI.Composition.DesktopWindowContentBridge（下称「内容区」）承载。
//       改它的宽度会让 Windows 重排任务栏：程序图标组位置不变，而系统托盘贴到
//       内容区右端。于是把内容区收窄到「图标右端 + 间距 + 托盘宽 + 余量」，
//       托盘就紧贴图标，并且增删图标时自动伸缩。
//    3) 托盘弹出面板（^ 隐藏图标面板）用 SetWinEventHook 跟着托盘毫秒级平移。
// ----------------------------------------------------------------------------
//  三条不能碰的红线（都是踩坑换来的，细节见 TaskbarEngine.BuildCompactDock 的注释）
//    A. 内容区宽度公式里**绝不能出现内容区位置 relX**：一平移宽度就跟着膨胀，
//       撞上「整条任务栏宽」后 Windows 会按整屏重排，短条直接报废。
//    B. UI Automation 只在任务栏**唤出**时才有按钮，隐藏时返回空树；
//       测不到就整轮跳过，绝不用过期数据推算。
//    C. 区域边界必须落在内容**里侧**，两侧圆角才会显现（区域只能切、不能补）。
// ----------------------------------------------------------------------------
//  构建：build.cmd（或 build.ps1），用 %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
//        编译，不需要 Visual Studio / .NET SDK，无第三方依赖。
//  语言：C# 5（Framework 自带的 csc 不支持更高版本）→ 没有字符串插值 / ?. / nameof。
//  配置：%APPDATA%\Dockify\settings.ini（首次启动自动生成，带注释、可手改）
//  诊断：%APPDATA%\Dockify\diag.txt（写不进去时退到 %TEMP%\Dockify-diag.txt）
// ----------------------------------------------------------------------------
//  文件结构（自上而下）
//    Native           全部 P/Invoke 与 Win32 常量
//    Settings         配置读写、版本迁移、范围裁剪
//    TaskbarEngine    核心引擎：测量 → 计算 → 套区域 / 摆内容区 / 移托盘
//    HiddenWindow     隐藏消息窗口（接收注销、关机、分辨率变化）
//    PctRow           设置窗口里的百分比滑块行控件（设置窗口私有）
//    SettingsForm     设置窗口
//    Program          单实例、托盘图标与菜单、命令行、自检与还原
// ============================================================================

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

// ===== 程序集信息（显示在"属性 → 详细信息"里，也让版本号不只是 0.0.0.0）=====
[assembly: System.Reflection.AssemblyTitle("Dockify")]
[assembly: System.Reflection.AssemblyProduct("Dockify")]
[assembly: System.Reflection.AssemblyDescription("Windows 11 任务栏两侧裁切 + 圆角，并可压成居中的紧凑 Dock")]
[assembly: System.Reflection.AssemblyCompany("Dockify contributors")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]
namespace Dockify
{
    // ---------------------------------------------------------------- Win32

    /// <summary>
    /// Win32 互操作层：本文件用到的全部 P/Invoke 与常量。这里只放声明，不放业务逻辑。
    /// </summary>
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        /// <summary>
        /// Win32 RECT。注意：GetWindowRect 在 DPI 感知进程里返回的是物理像素。
        /// </summary>
        internal struct RECT
        {
        /// <summary>左边界（物理像素）。</summary>
            public int Left;
        /// <summary>上边界。</summary>
            public int Top;
        /// <summary>右边界。</summary>
            public int Right;
        /// <summary>下边界。</summary>
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        /// <summary>
        /// SHAppBarMessage 用的结构体（这里只用来读「任务栏是否自动隐藏」）。
        /// </summary>
        internal struct APPBARDATA
        {
        /// <summary>结构体大小（调用前必须填 sizeof）。</summary>
            public int cbSize;
        /// <summary>任务栏窗口句柄。</summary>
            public IntPtr hWnd;
        /// <summary>回调消息（本程序不用）。</summary>
            public uint uCallbackMessage;
        /// <summary>贴合边缘（本程序不用）。</summary>
            public uint uEdge;
        /// <summary>任务栏矩形。</summary>
            public RECT rc;
        /// <summary>附加值（本程序不用）。</summary>
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        /// <summary>
        /// GetMonitorInfo 用的结构体（取显示器矩形，作为所有百分比换算的分母）。
        /// </summary>
        internal struct MONITORINFO
        {
        /// <summary>结构体大小。</summary>
            public int cbSize;
        /// <summary>显示器整块矩形（物理像素）—— 所有百分比换算的分母。</summary>
            public RECT rcMonitor;
        /// <summary>工作区矩形（扣掉任务栏）。</summary>
            public RECT rcWork;
        /// <summary>标志位（是否主显示器等）。</summary>
            public uint dwFlags;
        }

        /// <summary>
        /// EnumWindows 回调：遍历顶层窗口，用来找任务栏。
        /// </summary>
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        // 取窗口标题。用来区分"承载弹窗的岛"（标题为空）和"任务切换"岛（标题是"任务切换"）。
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

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

        /// <summary>
        /// SetWinEventHook 回调：托盘弹出面板出现/移动时被调用（毫秒级定位用）。
        /// </summary>
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
        // 查询窗口当前区域类型：0=没有区域/失败，1=空，2=简单，3=复杂
        [DllImport("user32.dll")] internal static extern int GetWindowRgnBox(IntPtr hWnd, out RECT lprc);

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

    /// <summary>
    /// 配置：全部可调项 + 读写 + 版本迁移。尺寸类字段都是「占显示器尺寸的浮点百分比」。
    /// </summary>
    internal sealed class Settings
    {
        /// <summary>总开关。取消勾选立刻把任务栏还原成原样。</summary>
        public bool Enabled;
        public double MarginLeft;      // 左裁切：占任务栏宽度的百分比（支持两位小数）
        public double MarginRight;     // 右裁切：占任务栏宽度的百分比
        public double MarginTop;       // 上边距：占任务栏高度的百分比
        public double MarginBottom;    // 下边距：占任务栏高度的百分比
        public double Radius;          // 圆角圆度：占屏幕高度的百分比（两位小数）
        public bool AllMonitors;
        /// <summary>内部常量是否按系统缩放（DPI）放大。</summary>
        public bool ScaleWithDpi;
        /// <summary>任务栏开着自动隐藏时是否也处理（默认开：窗口区域不影响隐藏/唤出行为）。</summary>
        public bool ApplyOnAutoHide;

        // 显示模式：0 = 只裁两端（任务栏仍占满屏宽），2 = 紧凑 Dock（重排任务栏内容 + 贴合内容的圆角短条）
        public int Mode;

        public double DockPadding;     // 短条留白：占屏幕宽度的百分比
        public double ClusterGap;      // 图标/托盘间距：占屏幕宽度的百分比

        public double DockOffsetX;     // 左侧裁切微调：占屏幕宽度百分比（正 = 留白，负 = 多裁贴近图标）
        public double DockWidthTrim;   // 内容长度微调：占屏幕宽度百分比（往窄了收）
        public double DockShiftX;      // 整体偏移：占屏幕宽度百分比（正 = 往右，负 = 往左）
        public bool VisualCenterComp;  // 视角重心补偿：勾选后短条按"视觉重心"再右移 VisualCenterBiasPct（默认关）
        // ---- 弹出窗口跟随（悬停预览 / ^ 展开面板）----
        // 原理：这些窗口由 Explorer 按【未加 Dock 的原始布局】定位，图标数量一变就会错位。
        // 所以我们按「图标组相对自然居中位置的位移」实时补正 —— 见 TaskbarEngine.PopupShiftPx。
        public bool FlyoutFollow;      // ^ 展开面板 / 隐藏图标面板：随图标变化动态跟随（默认开）
        public bool PreviewFollow;     // 悬停预览缩略图：随图标变化动态跟随（默认开）
        public double PreviewOffsetX;  // 悬停预览微调 %（正 = 往左），叠加在动态量之上
        // 校准快照：用户上次调偏移时的「图标组位移」。动态量 = 当前位移 − 这个快照，
        // 于是"调好的那一刻"偏移量分毫不差，之后图标增减自动跟随。
        // int.MinValue = 还没校准过（首次 Dock 成功时自动写入当前值）。
        public int CalibIconShift;
        public int SavedTaskbarAl;     // 用户原本的任务栏对齐方式（-1 = 无需还原）
        public int SettingsVersion;    // 配置版本（用于一次性迁移）
        public double FlyoutOffsetX;   // ^面板左移：占屏幕宽度百分比（0 = 不调整）

        public Settings()
        {
            Enabled = true;
            MarginLeft = 0.42;
            MarginRight = 0.42;
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
            DockShiftX = 0;          // 整体偏移 %：默认 0（不再预置 0.68% 的"观感居中"偏移）
            VisualCenterComp = false; // 默认关：短条按几何中心居中（视觉偏差因人因屏而异）
            FlyoutFollow = true;     // 默认开：^ 隐藏图标面板随图标增减自动对齐
            PreviewFollow = false;   // 默认关：悬停预览在本系统上没有可移动窗口，跟随无收益（详见 README）
            PreviewOffsetX = 0;      // 悬停预览残差（先用动态量，不够再调）
            CalibIconShift = int.MinValue;   // 未校准 → 首次 Dock 成功时自动取当前几何
            FlyoutOffsetX = 0;       // 默认 0：^ 面板不需要补偿（见 FlyoutFollow 的说明）
            SavedTaskbarAl = -1;
            SettingsVersion = 5;
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
        /// <summary>
        /// 把旧版本（TaskbarRound / 任务栏圆角）的配置搬过来，只做一次、只在没有新配置时做。
        /// </summary>
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

        /// <summary>
        /// 复制一份。设置窗口用它做「工作副本」：改动即时预览，点取消则丢弃。
        /// </summary>
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

            s.DockPadding = DockPadding;
            s.ClusterGap = ClusterGap;

            s.DockOffsetX = DockOffsetX;
            s.DockWidthTrim = DockWidthTrim;
            s.DockShiftX = DockShiftX;
            s.VisualCenterComp = VisualCenterComp;
            s.FlyoutFollow = FlyoutFollow;
            s.PreviewFollow = PreviewFollow;
            s.PreviewOffsetX = PreviewOffsetX;
            s.CalibIconShift = CalibIconShift;
            s.SavedTaskbarAl = SavedTaskbarAl;
            s.SettingsVersion = SettingsVersion;
            s.FlyoutOffsetX = FlyoutOffsetX;
            return s;
        }

        /// <summary>
        /// 从另一份配置覆盖全部字段（滑块改动后推给引擎用的就是它）。
        /// </summary>
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

            DockPadding = other.DockPadding;
            ClusterGap = other.ClusterGap;

            DockOffsetX = other.DockOffsetX;
            DockWidthTrim = other.DockWidthTrim;
            DockShiftX = other.DockShiftX;
            VisualCenterComp = other.VisualCenterComp;
            FlyoutFollow = other.FlyoutFollow;
            PreviewFollow = other.PreviewFollow;
            PreviewOffsetX = other.PreviewOffsetX;
            CalibIconShift = other.CalibIconShift;
            SavedTaskbarAl = other.SavedTaskbarAl;
            SettingsVersion = other.SettingsVersion;
            FlyoutOffsetX = other.FlyoutOffsetX;
        }

        /// <summary>
        /// 把「影响该不该重刷区域」的全部设置拼成一个字符串。只有它变了才重新套区域，避免无谓重绘。
        /// </summary>
        /// <returns>签名串。与上一次不同才需要重新套区域。</returns>
        public string Signature()
        {
            // 影响「该不该重刷区域」的全部设置。
            // 用 Join 拼接而不是 string.Format —— 之前占位符只有 12 个、参数却有 15 个，
            // 多出来的被静默忽略，导致「短条留白 / 左侧裁切微调」掉出签名、改了不重刷。
            string[] parts = new string[]
            {
                Enabled ? "1" : "0",
                MarginLeft.ToString("0.###", CultureInfo.InvariantCulture),
                MarginRight.ToString("0.###", CultureInfo.InvariantCulture),
                MarginTop.ToString("0.###", CultureInfo.InvariantCulture),
                MarginBottom.ToString("0.###", CultureInfo.InvariantCulture),
                Radius.ToString("0.###", CultureInfo.InvariantCulture),
                AllMonitors ? "1" : "0",
                ScaleWithDpi ? "1" : "0",
                ApplyOnAutoHide ? "1" : "0",
                Mode.ToString(CultureInfo.InvariantCulture),
                DockPadding.ToString("0.###", CultureInfo.InvariantCulture),
                ClusterGap.ToString("0.###", CultureInfo.InvariantCulture),
                DockOffsetX.ToString("0.###", CultureInfo.InvariantCulture),
                DockWidthTrim.ToString("0.###", CultureInfo.InvariantCulture),
                DockShiftX.ToString("0.###", CultureInfo.InvariantCulture),
                VisualCenterComp ? "1" : "0",
                FlyoutFollow ? "1" : "0",
                PreviewFollow ? "1" : "0",
                PreviewOffsetX.ToString("0.###", CultureInfo.InvariantCulture),
                FlyoutOffsetX.ToString("0.###", CultureInfo.InvariantCulture)
            };
            return string.Join("|", parts);
        }

        /// <summary>
        /// 状态栏与诊断日志用的一句话摘要。
        /// </summary>
        /// <returns>形如「紧凑 Dock · 间距 0.83% · 留白 1.4% · …」的一句话摘要。</returns>
        public string Describe()
        {
            if (IsDock)
            {
                return string.Format(CultureInfo.InvariantCulture, "紧凑 Dock · 间距 {0:0.##}% · 留白 {1:0.##}% · 圆角 {2:0.##}% · 左微调 {3:0.##}% · 整体偏移 {4:0.##}%",
                    ClusterGap, DockPadding, Radius, DockOffsetX, DockShiftX);
            }
            return string.Format(CultureInfo.InvariantCulture, "裁两端 · 左 {0:0.##}% 右 {1:0.##}% · 圆角 {2:0.##}%",
                MarginLeft, MarginRight, Radius);
        }

        /// <summary>
        /// 读配置。文件不存在 → 返回出厂默认；老版本配置按 SettingsVersion 逐级迁移。
        /// </summary>
        /// <returns>读到的配置。文件不存在时返回出厂默认；老配置会先逐级迁移再返回。</returns>
        public static Settings Load()
        {
            MigrateOldConfigOnce();
            Settings s = new Settings();
            bool sawVersionKey = false;
            try
            {
                if (!File.Exists(FilePath)) return s;     // 没有配置文件 → 用出厂默认（已是 v4）
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

                        case "dockpadding": if (hasDbl) s.DockPadding = ClampD(d, 0, 2000); break;
                        case "clustergap": if (hasDbl) s.ClusterGap = ClampD(d, 0, 2000); break;

                        case "dockoffsetx": if (hasDbl) s.DockOffsetX = ClampD(d, -2000, 2000); break;
                        case "dockwidthtrim": if (hasDbl) s.DockWidthTrim = ClampD(d, 0, 2000); break;
                        case "dockshiftx": if (hasDbl) s.DockShiftX = ClampD(d, -2000, 2000); break;
                        case "visualcentercomp": s.VisualCenterComp = b; break;
                        case "flyoutfollow": s.FlyoutFollow = b; break;
                        case "previewfollow": s.PreviewFollow = b; break;
                        case "previewoffsetx": if (hasDbl) s.PreviewOffsetX = ClampD(d, -2000, 2000); break;
                        case "calibiconshift": if (hasInt) s.CalibIconShift = n; break;
                        case "savedtaskbaral": if (hasInt) s.SavedTaskbarAl = Clamp(n, -1, 1); break;
                        case "settingsversion": if (hasInt) { s.SettingsVersion = n; sawVersionKey = true; } break;
                        case "flyoutoffsetx": if (hasDbl) s.FlyoutOffsetX = ClampD(d, 0, 2000); break;
                    }
                }
            }
            catch { /* 配置读不出来就用默认值 */ }
            // 老版本（v1~v3）的 ini 里根本没有 SettingsVersion 这个键，若不特判就会被当成出厂值 v4
            if (!sawVersionKey) s.SettingsVersion = 1;
            return s;
        }

        /// <summary>
        /// 写回 ini。失败不抛异常（托盘程序不该为写配置崩），原因记在 LastSaveError 里供诊断查看。
        /// </summary>
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

                sb.AppendLine("DockPadding=" + DockPadding.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("ClusterGap=" + ClusterGap.ToString("0.###", CultureInfo.InvariantCulture));

                sb.AppendLine("DockOffsetX=" + DockOffsetX.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("DockWidthTrim=" + DockWidthTrim.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("# DockShiftX: 整体偏移 %（屏宽），正 = 往右，负 = 往左，0 = 自动居中");
                sb.AppendLine("DockShiftX=" + DockShiftX.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("# VisualCenterComp: 1 = 开启视角重心补偿，0 = 关闭（默认）");
                sb.AppendLine("VisualCenterComp=" + (VisualCenterComp ? "1" : "0"));
                sb.AppendLine("# ---- 弹出窗口跟随（悬停预览 / ^ 展开面板）----");
                sb.AppendLine("# FlyoutFollow/PreviewFollow: 1 = 随图标增减自动对齐（推荐），0 = 只用固定微调");
                sb.AppendLine("FlyoutFollow=" + (FlyoutFollow ? "1" : "0"));
                sb.AppendLine("PreviewFollow=" + (PreviewFollow ? "1" : "0"));
                sb.AppendLine("# PreviewOffsetX: 悬停预览微调 %（正 = 往左），叠加在动态跟随之上");
                sb.AppendLine("PreviewOffsetX=" + PreviewOffsetX.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("# CalibIconShift: 校准时记录的图标组位移（程序内部用，勿手改；-2147483648 = 待校准）");
                sb.AppendLine("CalibIconShift=" + CalibIconShift.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("SavedTaskbarAl=" + SavedTaskbarAl.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("SettingsVersion=" + SettingsVersion.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("FlyoutOffsetX=" + FlyoutOffsetX.ToString("0.###", CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { LastSaveError = ex.GetType().Name + ": " + ex.Message; }
        }

        // 把百分比字段统一裁剪到界面允许的范围（迁移完成后调用一次）
        /// <summary>
        /// 配置迁移链：必须按 v1 → v2 → v3 → v4 的顺序逐级做，每级只做一次。
        /// 由 Program 在读取配置后调用；结束后统一 ClampAll 裁到合法范围。
        /// </summary>
        /// <param name="boot">刚读出来的配置，会被就地修改并存盘。</param>
        public static void Migrate(Settings boot)
        {
            // ===== 配置迁移链：必须按 v1 → v2 → v3 → v4 的顺序逐级做，每级只做一次 =====
            // （原来的顺序是把版本先改成 4，再判断"版本 < 3"，导致老配置的裁切量永远换算不到 ✗）
            double sw = 1920, sh = 1080;
            try { sw = Screen.PrimaryScreen.Bounds.Width; sh = Screen.PrimaryScreen.Bounds.Height; } catch { }
            if (sw < 100) sw = 1920;
            if (sh < 100) sh = 1080;

            if (boot.SettingsVersion < 2)
            {
                // 那时"托盘面板跟随"默认开着，实测会把面板推偏（补偿了两次），改为关闭
                boot.SettingsVersion = 2;
                boot.Save();
            }
            if (boot.SettingsVersion < 3)
            {
                // 裁切量：v2 及以前是「像素」→ v3 起改成「占屏幕尺寸百分比」
                boot.MarginLeft = Math.Round(boot.MarginLeft / sw * 100.0, 2);
                boot.MarginRight = Math.Round(boot.MarginRight / sw * 100.0, 2);
                boot.MarginTop = Math.Round(boot.MarginTop / sh * 100.0, 2);
                boot.MarginBottom = Math.Round(boot.MarginBottom / sh * 100.0, 2);
                boot.SettingsVersion = 3;
                boot.Save();
            }
            if (boot.SettingsVersion < 4)
            {
                // 只有 v3 是"这些项已是像素值"；v1/v2 根本没有这些项，
                // 用的是构造函数里的新默认值（已经是百分比），再除一次就毁了。
                if (boot.SettingsVersion == 3)
                {
                    boot.Radius = Math.Round(boot.Radius / sh * 100.0, 2);
                    boot.DockPadding = Math.Round(boot.DockPadding / sw * 100.0, 2);
                    boot.ClusterGap = Math.Round(boot.ClusterGap / sw * 100.0, 2);
                    boot.DockOffsetX = Math.Round(boot.DockOffsetX / sw * 100.0, 2);
                    boot.DockWidthTrim = Math.Round(boot.DockWidthTrim / sw * 100.0, 2);
                    boot.FlyoutOffsetX = Math.Round(boot.FlyoutOffsetX / sw * 100.0, 2);
                }
                boot.DockShiftX = 0.68;     // 这一项 v4 才有：老配置给个与出厂默认一致的起点
                boot.SettingsVersion = 4;
                boot.Save();
            }
            if (boot.SettingsVersion < 5)
            {
                // v5 起：悬停预览缩略图 / ^ 展开面板 改为「动态跟随 + 残差」。
                // CalibIconShift 保持 int.MinValue → 首次 Dock 成功时以【当时几何】为基准，
                // 于是用户此前调好的固定偏移在当下分毫不变，之后图标增减才自动跟随。
                boot.SettingsVersion = 5;
                boot.Save();
            }
            boot.ClampAll();     // 迁移/读取之后统一裁剪到合法范围
        }

        /// <summary>
        /// 把所有百分比字段裁到界面允许的范围。迁移或读取之后统一调一次。
        /// </summary>
        public void ClampAll()
        {
            Radius = ClampD(Radius, 0, 6);
            DockPadding = ClampD(DockPadding, 0, 10);
            ClusterGap = ClampD(ClusterGap, 0, 5);
            DockOffsetX = ClampD(DockOffsetX, -30, 20);
            DockWidthTrim = ClampD(DockWidthTrim, 0, 20);
            DockShiftX = ClampD(DockShiftX, -25, 25);
            PreviewOffsetX = ClampD(PreviewOffsetX, -25, 25);
            FlyoutOffsetX = ClampD(FlyoutOffsetX, 0, 50);
            MarginLeft = ClampD(MarginLeft, 0, 20);
            MarginRight = ClampD(MarginRight, 0, 20);
            MarginTop = ClampD(MarginTop, 0, 6);
            MarginBottom = ClampD(MarginBottom, 0, 6);
        }

        /// <summary>
        /// 把数值夹紧到 [lo, hi]。
        /// </summary>
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

    /// <summary>
    /// 核心引擎：每个 Tick 测量一次任务栏 → 算出圆角区域与内容区几何 → 应用。所有 Win32 副作用都收敛在这里。
    /// </summary>
    internal sealed class TaskbarEngine
    {
        private readonly Dictionary<IntPtr, string> _applied = new Dictionary<IntPtr, string>();

        private bool _shiftBlocked;    // 平移自检失败，永久放弃平移
        private int _dockCooldown;     // Dock 失败后的冷却节拍数（防止两个模式来回跳）
        // 弹出面板"一步到位"用的 WinEvent 钩子（毫秒级响应，不再等 0.5 秒的心跳）
        private Native.WinEventDelegate _winEventProc;
        // 托盘弹出面板的 WinEvent 钩子句柄（IntPtr.Zero = 没装上）。
        private IntPtr _winEventHook = IntPtr.Zero;
        private readonly Dictionary<IntPtr, Native.RECT> _hookBase = new Dictionary<IntPtr, Native.RECT>();
        private readonly Dictionary<IntPtr, int> _hookStamp = new Dictionary<IntPtr, int>();
        // 上一次摆位时用的图标组位移。它一变（= 图标增减 / Dock 重新居中）就立刻重摆已开着的弹窗。
        private int _rowShiftApplied = int.MinValue;

        // ---- 悬停预览：它画在一个全屏 XAML 岛里，没有自己的窗口，只能整体平移那个岛 ----
        private IntPtr _island;                        // 承载弹窗的岛（缓存）
        // 最近一次**成功**构建紧凑 Dock 时的内容区平移量。
        // 悬停预览岛的位移目标必须用它，不能用本拍的 _clusterOk：
        // 弹窗打开的瞬间本拍会"保持现状"跳过 Dock 构建，那时 _clusterOk=false，
        // 若据此算成 0 就会把刚摆好的岛又推回去（实测就是这个 bug 导致"二次调整后又偏回去"）。
        private int _dockRelXSticky;
        // 「图标组位移」的粘性副本。**这才是弹窗/预览补正需要的量**：
        // 弹窗打开的那几拍会"保持现状"跳过 Dock 构建，那时 _rowShift 被清成 0，
        // 直接用它就会把刚摆好的位置又推回去（用户实测的"二次调整后又偏回去"）。
        private int _rowShiftSticky;
        // 任务栏所属进程（Explorer）的 PID，用来过滤钩子回调，避免误动别的窗口。
        private readonly HashSet<uint> _barPids = new HashSet<uint>();   // 所有任务栏所属进程（多屏时不止一个）
        private int _shiftMisses;      // 平移连续没见效的次数

private bool _barVisible;      // 上一轮任务栏是否在屏幕内
        private readonly StringBuilder _report = new StringBuilder();
        // 任务栏是否开了自动隐藏（缓存一轮的结果，避免每次都去查）。
        private bool _autoHide;
        // 心跳计数：每 Tick 加一。用来节流 —— 区域巡检 5 秒一次、实时诊断 10 拍一次。
        private int _ticks;
        // 图标组相对「未加 Dock 时自然居中位置」的位移（负数 = 被往左推了）。
        // 弹出窗口（悬停预览缩略图 / ^ 展开面板）的补正量就由它推导 —— 见 PopupShiftPx。
        // 每拍开头清零，只有紧凑 Dock 成功那条路径才会写入。
        private int _rowShift;
        // GetDpiForMonitor 是否可用。老系统上会失败，失败后一律按 96 DPI 处理。
        private bool _dpiApiOk = true;
        private bool _clusterUsed;     // 动过任务栏内容区（退出时要还原）
        private bool _clusterOk;       // 本轮成功摆出了紧凑 Dock
        private bool _clusterBlocked;  // 内容区摆位被系统驳回，本运行周期不再尝试
        private int _bridgeWidth;      // 当前内容区宽度（物理像素，0 = 还没设过）
        private int _bridgeRelX;       // 内容区相对任务栏客户区的 x
        private bool _bridgeSized;     // 内容区尺寸这一轮是否已摆好
        private int _bridgeReverts;    // 内容区摆位被系统拽回的连续次数（防死磕）

        // 「视角重心补偿」要右移多少（占显示器宽度百分比）。
        // 只在设置里勾选了 VisualCenterComp 时生效，默认关闭。
        private const double VisualCenterBiasPct = 1.32;


        // ---- 内部几何常量：都是「未缩放的像素」，用时经 Scale(x, factor) 按 DPI 放大 ----
        private const int MinClusterWidthPx = 60;      // 图标组最小宽度（防止算出 0 宽）
        private const int MinDockWidthPx = 300;        // 紧凑 Dock 允许的最小宽度
        private const int MaxWidthReservePx = 60;      // 硬上限：内容区最多到 cw − 这个值（绝不能等于整条任务栏宽）
        private const int EdgeInsetPx = 10;            // 区域右边界收进内容里侧的量（这样右侧圆角才看得见）
        private const int TrayFallbackWidthPx = 240;   // 量不到托盘窗口时的兜底宽度
        private const int IconLeftGuardPx = 40;        // 平移时图标左端距屏幕边缘至少留这么多
        private const int ShowDesktopStripPx = 30;     // 「显示桌面」细条的宽度阈值（不算图标）
        private const double BackgroundWidthRatio = 0.9; // 宽过任务栏这个比例的容器 = 背景，不是按钮

        // ---- 自动居中的自检参数 ----
        private const int ShiftEpsilonPx = 3;          // 位移小于这个值就别动，免得抖
        private const int ShiftAbandonPx = 150;        // 自检：整块中心还差这么远 → 认为平移没生效
        private const int ShiftMissLimit = 3;          // 连续几次没生效就永久放弃自动居中

        // ---- 节奏（单位：心跳拍数，一拍 = 500ms）----
        private const int RegionCheckEveryTicks = 10;  // 每 10 拍（5 秒）巡检一次区域是否被系统清掉
        private const int LiveDiagEveryTicks = 10;     // 每 10 拍写一次实时诊断
        private const int DockRetryCooldownTicks = 20; // 模式判定失败后的冷却（10 秒），避免两种模式每拍互切

        // ---- 托盘弹出面板跟随 ----
        private const int FlyoutCorrectionWindowMs = 3000; // 只在这段时间内纠偏，之后交给 Explorer
        private const int FlyoutMaxTracked = 32;           // 记录表上限，超过就清理

        public Settings Settings;

        public string Status;
        /// <summary>本拍成功套用区域的任务栏个数（0 = 一个都没成，通常意味着权限不够）。</summary>
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

        /// <summary>
        /// 问系统：任务栏当前是否开启了自动隐藏。
        /// </summary>
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

        /// <summary>
        /// 取某个窗口所在显示器的 DPI（用于把内部常量按缩放放大）。
        /// </summary>
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
        /// <summary>
        /// 百分比 → 像素（**横向**按显示器宽度换算）。
        /// </summary>
        /// <summary>
        /// 百分比 → 像素（非负保护：0 或负值一律返回 0）。
        /// </summary>
        private static int PctW(double pct, Native.RECT mon) { return Pct(pct, mon.Right - mon.Left); }
        /// <summary>
        /// 百分比 → 像素（**纵向**按显示器高度换算）。
        /// </summary>
        private static int PctH(double pct, Native.RECT mon) { return Pct(pct, mon.Bottom - mon.Top); }

        // 带符号的百分比 → 像素（左侧裁切微调会取负值，不能走 Pct 的非负保护）
        /// <summary>
        /// 带符号的百分比 → 像素。左侧裁切微调会取负值，不能走 Pct 的非负保护。
        /// </summary>
        /// <param name="pct">百分比（负值按 0 处理）。</param>
        /// <param name="baseSize">基准尺寸（物理像素）。</param>
        /// <returns>对应的物理像素。</returns>
        /// <param name="pct">百分比，允许为负。</param>
        /// <param name="baseSize">基准尺寸（显示器宽或高，物理像素）。</param>
        /// <returns>对应的物理像素，保留符号。</returns>
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

        /// <summary>
        /// 按 DPI 缩放一个非负像素常量（内部阈值用，非用户设置）。
        /// </summary>
        /// <param name="value">未缩放的像素常量（负值按 0 处理）。</param>
        /// <param name="factor">DPI 缩放系数。</param>
        /// <returns>缩放后的物理像素，至少为 1。</returns>
        private static int Scale(int value, double factor)
        {
            if (value <= 0) return 0;
            int v = (int)Math.Round(value * factor, MidpointRounding.AwayFromZero);
            return v < 1 ? 1 : v;
        }

        // 给单个任务栏套上圆角矩形区域；返回是否成功（每一步都记进 _report，方便 --diag 排查）
        /// <summary>
        /// 给任务栏套上圆角矩形区域。每一步都写进 _report，失败时保留系统原状。
        /// </summary>
        /// <param name="hWnd">任务栏窗口句柄。</param>
        /// <param name="marginLeft">左边裁掉多少物理像素。</param>
        /// <param name="marginRight">右边裁掉多少物理像素。</param>
        /// <param name="marginTop">上边裁掉多少物理像素。</param>
        /// <param name="marginBottom">下边裁掉多少物理像素。</param>
        /// <param name="radius">圆角半径（物理像素）。</param>
        /// <returns>true = 区域已成功套上；false = 系统拒绝（常见原因：本进程权限低于任务栏）。</returns>
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
        /// <summary>
        /// 装 WinEvent 钩子：托盘弹出面板一出现就摆位（毫秒级，避免「先弹错位置再跳一下」）。
        /// </summary>
        public void InstallFlyoutHook()
        {
            if (_winEventHook != IntPtr.Zero) return;
            try
            {
                _winEventProc = new Native.WinEventDelegate(OnFlyoutEvent);
                _winEventHook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
                _report.AppendLine("  WinEvent 钩子 = " + _winEventHook);
                if (_winEventHook == IntPtr.Zero)
                    _report.AppendLine("  ! SetWinEventHook 失败 → 弹出面板跟随不可用（受限上下文常见）");
            }
            catch { }
        }

        /// <summary>
        /// 卸载钩子并释放回调委托（防止 GC 回收导致回调失效）。
        /// </summary>
        public void RemoveFlyoutHook()
        {
            try
            {
                if (_winEventHook != IntPtr.Zero) { Native.UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
            }
            catch { }
        }

        // 悬停预览缩略图的尺寸上限。Win11 里"预览"和"快捷设置"都用 Xaml_WindowedPopupClass，
        // 快捷设置约 480 高、明显更"高"，所以用高度把它们区分开：矮的 = 预览（跟随），高的 = 不碰。
        private const int PreviewMaxHeightPx = 320;
        private const int PreviewMaxWidthPx = 1600;
        // 【实测结论 2026-10-08】Win11 的"悬停窗口预览"**没有独立窗口**：它画在一个全屏
        // 1920x1080 的 XAML 岛（XamlExplorerHostIslandWindow）里，位置由 Explorer 自己的
        // XAML 布局算。所以"移动预览小窗"这条路不存在 —— 下面的类名匹配只对老系统
        // （TaskListThumbnailWnd 时代）有效，新系统上不会命中，属预期行为。
        // 预览偏位的真正来源见 README「已知限制」里的"图标组平移"那条。

        /// <summary>
        /// 这个弹出窗口要往左挪多少像素（**正数 = 往左**，可为负）；0 = 不用管。
        /// 组成：动态跟随量 + 用户微调残差。
        ///   · 动态量 = −(当前图标组位移 − 校准时位移)：这些窗口由 Explorer 按【未加 Dock 的原始布局】
        ///     定位，图标数量一变 Dock 几何就变，位移差多少就补多少 —— 这就是"实时对齐"。
        ///   · 残差 = 设置里的 ^面板微调 / 悬停预览微调（% 屏宽，正 = 往左），是用户在动态量之上做的微调。
        /// </summary>
        /// <param name="hwnd">WinEvent 回调里拿到的窗口句柄。</param>
        /// <param name="isPreview">出参：true = 这是悬停预览缩略图。</param>
        /// <returns>要左移的像素数；0 = 不处理这个窗口。</returns>
        private int PopupShiftPx(IntPtr hwnd, out bool isPreview)
        {
            isPreview = false;
            StringBuilder sb = new StringBuilder(160);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();

            bool isTrayPopup = (cls == "TopLevelWindowForOverflowXamlIsland" || cls == "NotifyIconOverflowWindow");
            if (!isTrayPopup)
            {
                if (cls == "TaskListThumbnailWnd")
                {
                    isPreview = true;
                }
                else if (cls == "Xaml_WindowedPopupClass")
                {
                    Native.RECT pr;
                    if (!Native.GetWindowRect(hwnd, out pr)) return 0;
                    if ((pr.Bottom - pr.Top) <= PreviewMaxHeightPx) isPreview = true;   // 矮 → 预览；高 → 快捷设置
                }
                if (!isPreview) return 0;
                // 网络/音量/电池 的快捷设置面板按用户要求**不再移动**（Explorer 自己锚定得没问题）
                if (!Settings.PreviewFollow) return 0;              // 关掉跟随时预览完全不动
            }
            else if (!Settings.FlyoutFollow)
            {
                return PctSigned(Settings.FlyoutOffsetX, MonitorRect(hwnd).Right - MonitorRect(hwnd).Left);
            }

            int monW = MonitorRect(hwnd).Right - MonitorRect(hwnd).Left;
            int residual = PctSigned(isPreview ? Settings.PreviewOffsetX : Settings.FlyoutOffsetX, monW);
            // 动态补正 = **−图标组位移（绝对量）**。
            // 实测（2026-10-08）：这些窗口由 Explorer 按"图标没被 Dock 平移过"的位置摆放，
            // 要把图标被推走的那一整段补回来；用户当年手调的 7.58%(≈146px) 就是这个量（当时 rowShift≈−144）。
            // 不能用"相对校准"：那样在校准状态下动态项会归零，反而把该补的量抵消掉（踩过）。
            int followPx = -_rowShiftSticky;
            if (isPreview && !Settings.PreviewFollow) followPx = 0;

            long total = (long)residual + followPx;
            if (Math.Abs(total) > 4000) return residual;            // 安全阀：算出离谱值就退回只用残差
            return (int)total;
        }

        /// <summary>
        /// 钩子回调：把 ^ 面板一步摆到托盘左侧（同一面板只纠偏一小段时间，避免和 Explorer 抢位置）。
        /// </summary>
        private void OnFlyoutEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild,
                                   uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero || idObject != 0) return;      // 只要窗口本身的事件
                if (ev != Native.EVENT_OBJECT_SHOW && ev != Native.EVENT_OBJECT_LOCATIONCHANGE) return;

                // 【预览岛】Explorer 在弹窗动画 / 关闭 / 重排时会自己挪这个岛 → 立刻把偏移补回来。
                // 这就是"二次调整正常、短时间后又偏回去、且第二次不再调整"的根治：
                // 以前靠"记录过一次就不再检查"，被挪回去后再也纠不回来。
                if (IsFlyoutIsland(hwnd))
                {
                    // 预览岛的位移目标 = 「图标组位移」的粘性副本，**不依赖本拍 _clusterOk**：
                    // 弹窗打开时本拍会跳过 Dock 构建，那时 _clusterOk=false，据此归零就会把岛推回去。
                    // 只有"关掉程序"或"切到裁两端"才真正归零。
                    if (!Settings.Enabled || !Settings.IsDock) { _dockRelXSticky = 0; _rowShiftSticky = 0; }
                    FollowPreviewIsland(Settings.Enabled && Settings.IsDock ? _rowShiftSticky : 0);
                    return;
                }

                bool isPreview;
                int shift = PopupShiftPx(hwnd, out isPreview);      // 正数 = 往左挪
                if (shift == 0) return;

                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (!_barPids.Contains(pid)) return;                    // 只认任务栏所属进程的窗口

                Native.RECT cur;
                if (!Native.GetWindowRect(hwnd, out cur)) return;
                int w = cur.Right - cur.Left;
                int ht = cur.Bottom - cur.Top;
                if (w < 120 || ht < 80) return;                        // 提示/菜单不要
                if (isPreview)
                {
                    if (w > PreviewMaxWidthPx || ht > PreviewMaxHeightPx) return;   // 预览不会太大
                }
                else if (w > 900 || ht > 1100) return;                 // 太大的不是托盘面板

                if (ev == Native.EVENT_OBJECT_SHOW)
                {
                    // 悬停预览会频繁开关 → 登记表要定期清理，避免常驻进程里无界增长。
                    // 超过 12 秒没动静的条目直接丢弃；条目仍然过多就整表清空兜底。
                    if (_hookBase.Count > FlyoutMaxTracked)
                    {
                        List<IntPtr> dead = new List<IntPtr>();
                        foreach (KeyValuePair<IntPtr, int> kv in _hookStamp)
                            if (unchecked(Environment.TickCount - kv.Value) > FlyoutCorrectionWindowMs * 4) dead.Add(kv.Key);
                        for (int gi = 0; gi < dead.Count; gi++) { _hookBase.Remove(dead[gi]); _hookStamp.Remove(dead[gi]); }
                        if (_hookBase.Count > FlyoutMaxTracked) { _hookBase.Clear(); _hookStamp.Clear(); }
                    }
                    _hookBase[hwnd] = cur;                              // 记下 Explorer 给它的位置
                    _hookStamp[hwnd] = Environment.TickCount;
                }
                Native.RECT b;
                if (!_hookBase.TryGetValue(hwnd, out b)) return;
                int stamp;
                if (!_hookStamp.TryGetValue(hwnd, out stamp)) return;
                if (unchecked(Environment.TickCount - stamp) > FlyoutCorrectionWindowMs) return;   // 只在一开始那 3 秒内摆位

                int tx = b.Left - shift;
                if (Math.Abs(cur.Left - tx) <= 2) return;
                Native.MoveWindow(hwnd, tx, cur.Top, w, ht, true);
            }
            catch { }
        }
        /// <summary>图标组相对自然居中位置的当前位移（弹出窗口跟随用；未 Dock 时为 0）。</summary>
        public int RowShift { get { return _rowShift; } }

        /// <summary>
        /// 图标数量变化会让 Dock 几何变化 → **已经开着的弹出窗口要立刻跟着挪**（不用等下一拍）。
        /// 只动我们登记过、且仍然可见的窗口；窗口已关掉就把记录清掉（顺便防止字典无界增长）。
        /// </summary>
        private void RepositionTrackedPopups()
        {
            if (_hookBase.Count == 0) return;
            List<IntPtr> gone = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, Native.RECT> kv in _hookBase)
            {
                IntPtr h = kv.Key;
                if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) { gone.Add(h); continue; }
                bool isPreview;
                int shift = PopupShiftPx(h, out isPreview);
                if (shift == 0) continue;
                Native.RECT cur;
                if (!Native.GetWindowRect(h, out cur)) continue;
                int tx = kv.Value.Left - shift;          // 基准位置 − 位移，避免逐次累加漂移
                if (Math.Abs(cur.Left - tx) <= 2) continue;
                Native.MoveWindow(h, tx, cur.Top, cur.Right - cur.Left, cur.Bottom - cur.Top, true);
            }
            for (int gi = 0; gi < gone.Count; gi++) { _hookBase.Remove(gone[gi]); _hookStamp.Remove(gone[gi]); }
        }

        /// <summary>
        /// 找到承载任务栏弹窗的那个**全屏 XAML 岛**（XamlExplorerHostIslandWindow）。
        /// 注意：Alt-Tab 的"任务切换"岛类名相同但标题非空、且不可见，要排除掉。
        /// </summary>
        /// <summary>
        /// 这个句柄是不是"承载任务栏弹窗的全屏 XAML 岛"。
        /// 用途：钩子收到它的移动事件就立刻把偏移补回来（Explorer 会自己挪它）。
        /// </summary>
        private bool IsFlyoutIsland(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (_island != IntPtr.Zero && hwnd == _island) return true;
            StringBuilder sb = new StringBuilder(160);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() != "XamlExplorerHostIslandWindow") return false;
            StringBuilder tt = new StringBuilder(64);
            Native.GetWindowText(hwnd, tt, tt.Capacity);
            if (tt.Length != 0) return false;                     // "任务切换"岛不算
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;
            if ((r.Right - r.Left) < 800) return false;
            _island = hwnd;
            return true;
        }

        private IntPtr FindFlyoutIsland()
        {
            if (_island != IntPtr.Zero && Native.IsWindow(_island)) return _island;
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                StringBuilder sb = new StringBuilder(160);
                Native.GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() != "XamlExplorerHostIslandWindow") return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!_barPids.Contains(pid)) return true;
                StringBuilder tt = new StringBuilder(64);
                Native.GetWindowText(h, tt, tt.Capacity);
                if (tt.Length != 0) return true;                 // "任务切换"岛
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                if ((r.Right - r.Left) < 800) return true;       // 不是全屏那个
                found = h;
                return false;
            }, IntPtr.Zero);
            _island = found;
            return found;
        }

        /// <summary>
        /// 把悬停预览岛放回自然位置（静态版：--reset 与退出时用，不能依赖实例状态）。
        /// </summary>
        private static void RestoreIslandToNatural()
        {
            try
            {
                IntPtr found = IntPtr.Zero;
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!Native.IsWindowVisible(h)) return true;
                    StringBuilder sb = new StringBuilder(160);
                    Native.GetClassName(h, sb, sb.Capacity);
                    if (sb.ToString() != "XamlExplorerHostIslandWindow") return true;
                    StringBuilder tt = new StringBuilder(64);
                    Native.GetWindowText(h, tt, tt.Capacity);
                    if (tt.Length != 0) return true;
                    Native.RECT r;
                    if (!Native.GetWindowRect(h, out r)) return true;
                    if ((r.Right - r.Left) < 800) return true;
                    found = h;
                    return false;
                }, IntPtr.Zero);
                if (found == IntPtr.Zero) return;
                Native.RECT fr;
                if (!Native.GetWindowRect(found, out fr)) return;
                int monL = MonitorRect(found).Left;
                if (Math.Abs(fr.Left - monL) <= 2) return;          // 已在自然位置
                Native.MoveWindow(found, monL, fr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top, true);
            }
            catch { }
        }

        /// <summary>
        /// 让「鼠标悬停程序图标时的窗口预览」重新对上图标。
        /// 原理（2026-10-08 实测）：预览没有自己的窗口，它画在上面那个全屏 XAML 岛里，
        /// 而 Explorer 给预览定位用的是"图标**没被 Dock 平移过**"的位置。
        /// 但**移动这个岛，岛里的预览会 1:1 跟着走**（实测：岛左移 144px → 预览中心 958 → 812，误差 2px 内）。
        /// 所以我们把岛也平移同样的量（= 内容区平移量 _bridgeRelX），预览就回到图标正上方；
        /// 关掉「预览动态跟随」时把岛放回自然位置。
        /// </summary>
        /// <param name="wantX">相对自然位置的位移（与 _bridgeRelX 同向同量）。</param>
        private void FollowPreviewIsland(int wantX)
        {
            if (!Settings.PreviewFollow) wantX = 0;              // 关掉跟随 → 岛回自然位置
            IntPtr isl = FindFlyoutIsland();
            if (isl == IntPtr.Zero) return;
            Native.RECT r;
            if (!Native.GetWindowRect(isl, out r)) return;
            // 自然位置 = 所在显示器的左边界（全屏岛本来就贴齐显示器，副屏也适用）
            Native.RECT mon = MonitorRect(isl);
            int target = mon.Left + wantX;
            if (wantX != 0)
                target -= PctSigned(Settings.PreviewOffsetX, mon.Right - mon.Left);   // 用户残差（正 = 再往左）
            // 【关键】每次都核对**实际位置**，绝不因为"记录过一次"就跳过：
            // Explorer 会在弹窗动画/关闭/重排时把岛挪回原位，只有持续核对才能把它按住。
            if (Math.Abs(r.Left - target) <= 2) return;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (!Native.MoveWindow(isl, target, r.Top, w, ht, true))
            {
                _report.AppendLine("  ! 预览岛跟随失败, err=" + Marshal.GetLastWin32Error());
                return;
            }
            _report.AppendLine("  预览岛跟随 → 目标位移 " + wantX + "px（x " + r.Left + " → " + target + "）");
        }

        /// <summary>
        /// 把「当前几何」记为弹出窗口的校准基准。用户在设置里调好偏移并点确定时调用，
        /// 于是动态跟随项归零、只保留用户调出来的那个偏移量。
        /// </summary>
        public void RecalibratePopups()
        {
            Settings.CalibIconShift = _rowShift;
            Settings.Save();
        }

        /// <summary>
        /// 把"预览岛 / ^ 面板"的对正数据写到 %APPDATA%\Dockify\align.txt。
        /// 用途：① 排障时用户发这份数据；② 自动化测试直接读（不用截图）。
        /// </summary>
        public void MeasurePopupAlignment()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Dockify 弹窗/预览对正测量  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("版本     : " + Application.ProductVersion);
            sb.AppendLine("当前     : " + Status);
            sb.AppendLine("_rowShift      = " + _rowShift + "   _rowShiftSticky = " + _rowShiftSticky + "   （预览岛应平移的量）");
            sb.AppendLine("_dockRelXSticky = " + _dockRelXSticky + "   （旧法用的量，已弃用）");

            // 找预览岛：报告它的实际 x 与"应有 x"（自然位置 + rowShift）
            IntPtr isl = FindFlyoutIsland();
            if (isl == IntPtr.Zero)
            {
                sb.AppendLine("预览岛   : 未找到（此刻可能没悬停 → 正常；悬停一个图标后再点本项）");
            }
            else
            {
                Native.RECT ir;
                if (Native.GetWindowRect(isl, out ir))
                {
                    Native.RECT mon = MonitorRect(isl);
                    int want = mon.Left + _rowShiftSticky;
                    sb.AppendLine("预览岛   : x=" + ir.Left + "  （自然=" + mon.Left + "，应有=" + want + "，偏差=" + (ir.Left - want) + "px）");
                }
            }

            // 枚举所有弹窗（^ 面板 / 预览岛 / 其它），连同它们的矩形一并写出
            sb.AppendLine("--- 当前可见的弹窗/岛窗口 ---");
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                StringBuilder cls = new StringBuilder(160);
                Native.GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c.IndexOf("Overflow", StringComparison.Ordinal) < 0 &&
                    c.IndexOf("XamlExplorerHostIsland", StringComparison.Ordinal) < 0 &&
                    c.IndexOf("NotifyIconOverflow", StringComparison.Ordinal) < 0)
                    return true;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                sb.AppendLine("  [" + c + "]  " + r.Left + "," + r.Top + "-" + r.Right + "," + r.Bottom +
                              "  宽" + (r.Right - r.Left) + " 高" + (r.Bottom - r.Top));
                return true;
            }, IntPtr.Zero);

            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockify");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "align.txt"), sb.ToString(), new System.Text.UTF8Encoding(false));
            }
            catch { }
            System.Media.SystemSounds.Asterisk.Play();   // 给个提示音：写好了
        }

        // 托盘弹出面板（快捷设置 / 隐藏图标面板）当前是否开着。
        // 面板开着的时候必须**暂停自动适应**：面板出现/消失会让测量值抖动，
        // 一补偿整组就会带着任务栏来回跳。
        /// <summary>
        /// 托盘弹出面板是否开着。开着就暂停自动适应，否则任务栏会一直跳。
        /// </summary>
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
        /// <summary>
        /// 清掉窗口区域，恢复成完整矩形。
        /// </summary>
        // ================= 区域与还原 =================
        private static void ClearRegion(IntPtr hWnd)
        {
            if (!Native.IsWindow(hWnd)) return;
            Native.SetWindowRgn(hWnd, IntPtr.Zero, true);   // NULL 区域 = 恢复成完整矩形
        }

        // 把所有任务栏恢复原样（供 --reset 和退出时调用，不依赖任何状态）
        /// <summary>
        /// 一键还原：内容区回整屏宽、托盘回最右、区域清空，并通知 Explorer 重排。
        /// </summary>
        public static void ResetTaskbars()
        {
            // 悬停预览岛也放回自然位置（--reset / 退出时不能留偏移）
            RestoreIslandToNatural();
            List<IntPtr> bars = FindTaskbars(false);
            for (int i = 0; i < bars.Count; i++)
            {
                RestoreBarChildren(bars[i]);   // 内容区恢复整宽、托盘回到最右
                ForceFullWidth(bars[i]);
                ClearRegion(bars[i]);
            }
        }

        // 把一个任务栏强行还原成整屏宽（用于「程序被强杀后任务栏卡在 Dock 短条」的救急）
        /// <summary>
        /// 把任务栏的内容区与托盘位置还原成系统自然状态（不改窗口本身）。
        /// </summary>
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

        /// <summary>
        /// 清掉「已套用」记录。停用、自动隐藏、退出时都要清。
        /// </summary>
        private void ResetApplied()
        {
            foreach (KeyValuePair<IntPtr, string> kv in _applied)
            {
                ForceFullWidth(kv.Key);     // 万一任务栏窗口被外面改过，一并还原
                ClearRegion(kv.Key);
            }
            _applied.Clear();
            _dockRelXSticky = 0;
            _rowShiftSticky = 0;      // 停用/还原时预览岛也归零
            RestoreCluster();               // 紧凑 Dock 动过的子窗口放回原位
            AppliedBars = 0;
        }

        /// <summary>
        /// 停用并立刻还原任务栏。
        /// </summary>
        public void Reset()
        {
            ResetApplied();
            RestoreIslandToNatural();     // 悬停预览岛也要放回自然位置，别把偏移留在系统里
            Status = "已恢复任务栏原样";
        }

        // 每次「节拍」跑一遍：发现任务栏几何 / 设置变化就重新套区域
        /// <summary>
        /// 一次心跳（默认 500ms）：枚举任务栏 → 逐个决定用哪种模式 → 应用。所有异常都在这里兜住。
        /// </summary>
        // ================= 心跳：一次完整的测量 → 应用 =================
        public void Tick()
        {
            _ticks++;
            bool force = false;
            _rowShift = 0;      // 只有紧凑 Dock 成功时才重新写入

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
            // 多显示器：把所有任务栏的进程都记下来（原来只记 bars[0]，副屏的弹出面板会被判成"外人"）
            _barPids.Clear();
            for (int bi = 0; bi < bars.Count; bi++)
            {
                uint bp;
                Native.GetWindowThreadProcessId(bars[bi], out bp);
                _barPids.Add(bp);
            }

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
            _dockRelXSticky = 0;
            _rowShiftSticky = 0;      // 停用/还原时预览岛也归零
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
                // 巡检：每 5 秒检查一次区域是否被系统清掉（原来每 1 秒无条件重刷，导致 Explorer 持续重绘）
                if (!force && _ticks % RegionCheckEveryTicks == 0)
                {
                    Native.RECT rgnBox;
                    if (Native.GetWindowRgnBox(h, out rgnBox) == 0)
                    {
                        force = true;
                        _report.AppendLine("  巡检：区域被系统清掉了 → 重新套用");
                    }
                }

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

// 紧凑 Dock：把开始按钮 / 任务按钮 / 托盘三个子窗口重排成一个居中的紧凑簇，
                // 任务栏窗口本身不动（所以自动隐藏照常工作，不会和 Explorer 打架）
                bool compact = false;
                int pillLeft = 0, pillRight = 0;
                if (Settings.IsDock && !_clusterBlocked && _dockCooldown <= 0)
                {
                    compact = BuildCompactDock(h, factor, r, out pillLeft, out pillRight);
                    if (!compact)
                    {
                        _dockCooldown = DockRetryCooldownTicks;        // 失败就冷却 10 秒，绝不让两个模式每拍互切
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
            // 图标增减 → Dock 几何变了就立刻重摆已开着的弹窗（悬停预览 / ^ 展开面板），
            // 这就是"新增图标时动态调整偏移"和"托盘展开界面实时调整"的落点。
            if (_rowShift != _rowShiftApplied)
            {
                _rowShiftApplied = _rowShift;
                RepositionTrackedPopups();
            }

            // 悬停预览：把承载它的全屏 XAML 岛平移同样的量（用「图标组位移」，不是内容区绝对 x）
            // 预览岛的位移目标 = 「图标组位移」的粘性副本，**不依赖本拍 _clusterOk**：
            // 弹窗打开时本拍会跳过 Dock 构建，那时 _clusterOk=false，据此归零就会把岛推回去。
            // 只有"关掉程序"或"切到裁两端"才真正归零。
            if (!Settings.Enabled || !Settings.IsDock) { _dockRelXSticky = 0; _rowShiftSticky = 0; }
            FollowPreviewIsland(_rowShiftSticky);

            _report.AppendLine("结果     : 成功 " + ok + " / " + bars.Count + " 个任务栏");
            if (_ticks % LiveDiagEveryTicks == 0) Program.WriteLiveDiag(this);   // 每 10 拍写一次实时诊断
        }

        // 在任务栏的子窗口里按类名找一个窗口
        /// <summary>
        /// 按类名找直接子窗口（内容区 / 托盘都靠它定位）。
        /// </summary>
        // ================= 测量：找出图标组与托盘的真实位置 =================
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
        /// <summary>
        /// 用 UI Automation 量出图标组与托盘的屏幕位置。任务栏没唤出时返回 false（不猜）。
        /// </summary>
        /// <param name="barHwnd">任务栏窗口句柄（UIA 从这里往下找按钮）。</param>
        /// <param name="barRect">任务栏窗口矩形，用来把屏幕坐标换算成相对坐标并过滤干扰元素。</param>
        /// <param name="iconL">出参：程序图标组左端（屏幕坐标）。</param>
        /// <param name="iconR">出参：程序图标组右端。</param>
        /// <param name="trayL">出参：系统托盘左端。</param>
        /// <param name="trayR">出参：系统托盘右端。</param>
        /// <returns>false = 任务栏没唤出（UIA 返回空树）或按钮太少，调用方必须保持现状、不要乱猜。</returns>
        private bool MeasureContent(IntPtr barHwnd, Native.RECT barRect,
                                    out int iconL, out int iconR, out int trayL, out int trayR)
        {
            iconL = 0; iconR = 0; trayL = 0; trayR = 0;
            try
            {
                AutomationElement root = AutomationElement.RootElement;
                if (root == null) return false;

                // 【多显示器关键点】直接取**这个句柄**对应的 UIA 元素。
                // 以前这里写死 ClassNameProperty=="Shell_TrayWnd" 从根往下找：
                // 主屏没问题，但副屏任务栏（Shell_SecondaryTrayWnd）会量到主屏的按钮，
                // 于是副屏拿着主屏坐标去算几何 → 画面必错（错位/裁穿，但不崩）。
                AutomationElement bar;
                try { bar = AutomationElement.FromHandle(barHwnd); }
                catch { bar = null; }
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
                    if (rc.Width > barW * BackgroundWidthRatio) continue;                        // 整条任务栏的背景容器
                    if (e.Current.ControlType != ControlType.Button) continue;  // 只信按钮
                    if (rc.Width < ShowDesktopStripPx && rc.Right >= barRect.Right - ShowDesktopStripPx) continue; // 「显示桌面」细条
                    xs.Add(new int[] { (int)rc.Left, (int)rc.Right });
                }
                if (xs.Count < 3) return false;      // 任务栏没唤出时这里会是 0

                xs.Sort(delegate(int[] a, int[] b) { return a[0].CompareTo(b[0]); });

                // 托盘宽度取「托盘窗口」的宽度（稳定、与语言无关）
                // 按真实 DPI 缩放（原来写死 factor=1，高缩放下托盘宽度估计偏小）
                double mf = Settings.ScaleWithDpi ? (DpiFor(barHwnd) / 96.0) : 1.0;
                int trayWndW = Scale(TrayFallbackWidthPx, mf);
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
        /// <summary>
        /// 紧凑 Dock 的核心：算内容区宽度、算整体平移、算出短条的左右边界。返回 false 表示退回「裁两端」。
        /// </summary>
        /// <param name="h">任务栏窗口句柄。</param>
        /// <param name="factor">DPI 缩放系数（120 DPI → 1.25），用于缩放内部阈值。</param>
        /// <param name="barRect">任务栏窗口矩形。</param>
        /// <param name="pillLeft">出参：短条左边界（相对任务栏客户区，-1 = 本轮不要动裁切）。</param>
        /// <param name="pillRight">出参：短条右边界（-1 = 不要动）。</param>
        /// <returns>true = 本轮保持现有裁切（跳过/暂停）；false = 做不到紧凑 Dock，调用方退回「裁两端」。</returns>
        /// <summary>
        /// 本轮该不该「保持现有裁切、什么都不做」。两种情况：
        /// ① 任务栏被自动隐藏 / 正在滑动 —— 动它会打架，而且 UIA 这时候拿不到按钮；
        /// ② 托盘弹出面板开着 —— 这时改任务栏会让面板跟着乱跳。
        /// </summary>
        /// <param name="h">任务栏窗口句柄。</param>
        /// <param name="barRect">任务栏窗口矩形。</param>
        /// <param name="mon">该任务栏所在显示器的矩形。</param>
        /// <returns>true = 本轮跳过（保持现状）。</returns>
        private bool ShouldHoldCurrentCrop(IntPtr h, Native.RECT barRect, Native.RECT mon)
        {
            if (barRect.Bottom > mon.Bottom || barRect.Top < mon.Top)
            {
                _report.AppendLine("  (任务栏在屏幕外/滑动中，保持现有裁切)");
                return true;
            }
            if (AnyTrayFlyoutVisible(h))
            {
                _report.AppendLine("  (托盘弹出面板开着 → 暂停自动适应，保持现有裁切)");
                return true;
            }
            return false;
        }
        private bool BuildCompactDock(IntPtr h, double factor, Native.RECT barRect, out int pillLeft, out int pillRight)
        {
            pillLeft = -1;      // -1 = 本轮不要动裁切（保持现状），别把短条弄没了
            pillRight = -1;

            Native.RECT mon = MonitorRect(h);
            if (ShouldHoldCurrentCrop(h, barRect, mon)) return true;

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
            if (trayW <= 0) trayW = (trayR - trayL > 0 ? trayR - trayL : Scale(TrayFallbackWidthPx, factor));

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
            //   ③ 短条紧贴内容（左边界 = 图标左端 − 留白 + 手动微调；右边界 = 内容区右端 − 1 刀）；
            //      「整体偏移」由用户手动调，程序自己不做对称补齐（做过实测：硬补会切到图标）。
            int inset = Scale(EdgeInsetPx, factor);               // 区域右边界收进内容里侧的量（为的是看见圆角）
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
            if (cluster < Scale(MinClusterWidthPx, factor)) cluster = Scale(MinClusterWidthPx, factor);
            int iconCenterRel = (iconL + iconR) / 2 - (barRect.Left + relXOld);
            int minW = cluster + 2 * (gap + trayW) + safety;                     // 不重叠的下限
            if (minW < Scale(MinDockWidthPx, factor)) minW = Scale(MinDockWidthPx, factor);
            int wantW = iconCenterRel + cluster / 2 + gap + trayW + safety;
            // 内容长度微调：按用户设置把内容区收窄（收太多时 Windows 会开始隐藏图标，属正常现象）
            int trim = PctW(Settings.DockWidthTrim, mon);
            if (trim > 0)
            {
                wantW -= trim;
                _report.AppendLine("  内容长度微调 -" + trim + "px → 宽 " + wantW);
            }
            if (wantW < minW) wantW = minW;
            int maxW = cw - Scale(MaxWidthReservePx, factor);                                   // 硬上限：绝不等于整条任务栏宽
            if (wantW > maxW) wantW = maxW;
            if (wantW < minW)
            {
                Status = "图标太多，紧凑 Dock 放不下，已按裁两端处理";
                _report.AppendLine("  ! 图标太多（需要 " + minW + "px，上限 " + maxW + "px）→ 退回裁两端");
                return false;
            }

            // ============ 平移：让「图标 + 间距 + 托盘」整块落在屏幕中心 ============
            // 居中目标 = 显示器几何中心（默认行为，不再叠加任何偏差）。
            // 可选「视角重心补偿」：短条左边界贴着「图标左端 − 留白」、右边界贴着「内容区右端 − 1 刀」，
            // 两侧"多出来的边"并不对称，所以短条的【几何中心】与【视觉重心】本来不重合。
            // 勾选后按 VisualCenterBiasPct 再右移一点（默认关闭 —— 这个偏差因人因屏而异，实测容易看错）。
            // 居中目标 = 显示器几何中心。
            // 想让整条 Dock 整体左右挪，用设置里的「整体偏移」（% 屏宽）—— 那是唯一的整体位移入口；
            // 想按"视觉重心"再偏一点，用可选的「视角重心补偿」。
            int center = (mon.Left + mon.Right) / 2;
            if (Settings.VisualCenterComp)
                center += PctSigned(VisualCenterBiasPct, mon.Right - mon.Left);
            // 居中基准用**短条本身**的中心（而不是整块中心）——
            // 这样左侧多裁/多加留白时，短条依然保持居中。
            int curCenter = ((iconL - off) + (relXOld + wantW - inset)) / 2;
            // 手动「整体偏移」：永远生效，叠加在自动居中之上；
            // 自动居中若被自检判定无效（_shiftBlocked），至少手动值还能用。
            int manual = PctSigned(Settings.DockShiftX, mon.Right - mon.Left);
            int delta = _shiftBlocked ? manual : (center - curCenter + manual);
            int applied = 0;
            if (Math.Abs(delta) > Scale(ShiftEpsilonPx, factor) && Math.Abs(delta) < cw / 3)
            {
                int newRelX = relXOld + delta;
                int minRelX = -(iconL - barRect.Left - Scale(IconLeftGuardPx, factor));   // 图标左端别出屏
                if (newRelX < minRelX) newRelX = minRelX;
                if (newRelX + wantW > cw) newRelX = cw - wantW;
                applied = newRelX - relXOld;
                _report.AppendLine("  整组平移 " + applied + "px（往" + (applied < 0 ? "左" : "右") + "，其中手动偏移 " + manual + "px）");
            }

            // 自检：上一拍平移过、这一拍整块中心却还差很远（>150px）→ 最多 3 次就永久放弃平移
            if (!_shiftBlocked && manual == 0 && applied == 0 && Math.Abs(curCenter - center) > Scale(ShiftAbandonPx, factor))
            {
                _shiftMisses++;
                if (_shiftMisses >= ShiftMissLimit)
                {
                    _shiftBlocked = true;
                    _report.AppendLine("  ! 平移连续 3 次没效果 → 永久放弃平移，只紧贴内容");
                }
            }
            else if (applied != 0 || Math.Abs(curCenter - center) <= Scale(ShiftAbandonPx, factor))
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

            // ============ 图标组位移：弹出窗口补正的唯一依据 ============
            // 未加 Dock 时程序图标组是**屏幕居中**的（本项目实测结论），自然左端 = (cw − cluster)/2；
            // 加 Dock 后被平移了 applied，两者之差就是"Explorer 算弹出窗口位置时不知道的那段位移"。
            // 悬停预览与 ^ 展开面板都按它补正 —— 图标增减时它会自己变，所以不需要写死百分比。
            _rowShift = (iconL + applied - barRect.Left) - (cw - cluster) / 2;
            _dockRelXSticky = relX;
            _rowShiftSticky = _rowShift;      // 供悬停预览岛跟随使用（弹窗打开时本拍会跳过构建，所以要粘住上次的值）
            _report.AppendLine("  图标组位移 " + _rowShift + "px（相对自然居中位置，负=被左推）");
            if (Settings.CalibIconShift == int.MinValue)
            {
                // 首次跑通（或从旧版本升级上来）：把当前几何记为基准，
                // 这样"用户此前调好的偏移量"在当下分毫不差，之后图标增减才自动跟随。
                Settings.CalibIconShift = _rowShift;
                Settings.Save();
                _report.AppendLine("  已自动校准弹出窗口基准 CalibIconShift=" + _rowShift);
            }

            _clusterOk = true;
            _clusterUsed = true;
            _report.AppendLine("  短条=" + unionL + ".." + unionR +
                               " 裁掉左 " + left + " / 右 " + right);
            return true;
        }
        // 把托盘窗口挪到内容右端（相对父窗口客户区）
        // 托盘窗口的宽度（稳定值；比按按钮量的偏大，用作安全余量）
        /// <summary>
        /// 托盘窗口宽度。比从 UIA 推更稳，且与系统语言无关。
        /// </summary>
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
        /// <summary>
        /// 退出/关机时把任务栏彻底还原：内容区、托盘、区域、对齐方式。
        /// </summary>
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
    /// <summary>
    /// 隐藏的消息窗口：给单实例与系统广播（注销/关机/分辨率变化/设置变更）一个落点。
    /// </summary>
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
    /// <summary>
    /// 设置窗口里的整数滑块行（TrackBar + 数值框 + 单位）。
    /// </summary>

    /// <summary>
    /// 设置窗口：主界面 + 「更多设置」面板，改动即时预览（Live）。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const int FormW = 460;

        private readonly TaskbarEngine _engine;
        private readonly Settings _original;
        private readonly Settings _working;

        private CheckBox _chkEnabled;
        // ---- 主界面控件（模式 / 裁切 / 紧凑 Dock）----
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
        /// <summary>「更多设置」面板（勾选后展开，窗体随之加高）。</summary>
        // ---- 「更多设置」面板里的控件 ----
        private Panel _advanced;
        /// <summary>「更多设置 → 上边距」数值框（% 屏高）。</summary>
        private NumericUpDown _numTop;
        /// <summary>「更多设置 → 下边距」数值框（% 屏高）。</summary>
        private NumericUpDown _numBottom;
        private CheckBox _chkAll;
        private CheckBox _chkDpi;
        // 「任务栏自动隐藏时也处理」已固定为开启（区域是相对任务栏窗口自身的，滑进滑出不影响），
        // 所以不再提供勾选框 —— 字段一并去掉，避免留下无处同步的控件引用。
        private CheckBox _chkVisualCenter;
        private CheckBox _chkFlyoutFollow;
        private CheckBox _chkPreviewFollow;
        private PctRow _rowPreview;
        private PctRow _rowFlyout;
        private PctRow _rowShift;

        private Label _statusLabel;
        /// <summary>取消按钮。</summary>
        private Button _btnCancel;
        /// <summary>确定按钮。</summary>
        private Button _btnOk;
        /// <summary>批量回填控件时为 true，用来抑制 ValueChanged 里的 Live() 重入。</summary>
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
                // 用户动过哪个偏移，就把"当前几何"记为校准基准 → 动态跟随项归零，
                // 只保留他调出来的那个残差；之后图标增减才继续自动跟随。
                bool offsetChanged = (_original.FlyoutOffsetX != _working.FlyoutOffsetX ||
                                      _original.PreviewOffsetX != _working.PreviewOffsetX);
                _engine.Settings.CopyFrom(_working);
                if (offsetChanged) _engine.RecalibratePopups();
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
        /// <summary>
        /// 百分比滑块行：TrackBar 用「百分之一」的整数刻度，数值框显示两位小数，两者双向同步。
        /// </summary>
        private sealed class PctRow
        {
        /// <summary>百分比滑块的轨道（刻度是"百分之一"的整数）。</summary>
            public TrackBar Bar;
        /// <summary>与轨道同步的数值框（显示两位小数）。</summary>
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
            // 与 SyncPctRow 保持同一口径：数值框也要夹紧，否则超范围的值会被写进配置
            decimal dv = (decimal)v;
            if (dv < num.Minimum) dv = num.Minimum;
            if (dv > num.Maximum) dv = num.Maximum;
            num.Value = dv;
        }
        private Panel BuildAdvancedPanel()
        {
            Panel p = new Panel();
            // 紧贴上方「更多设置」勾选框（y=544 结束）之后开始，别留 80px 空白
            p.Location = new Point(0, 556);
            p.Size = new Size(FormW, 186);

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

            // 「任务栏自动隐藏时也处理」已固定开启、不再给勾选框（见字段处的说明）——
            // 这里显式写死，避免任何路径把它改回关闭。
            _working.ApplyOnAutoHide = true;

            // 视角重心补偿：短条的几何中心与视觉重心本不重合，勾选后按 VisualCenterBiasPct 再右移一点。
            // 默认关闭 —— 这个偏差因人因屏而异（实测很容易看错）。
            // 位置挪到上一项「任务栏自动隐藏时也处理」让出来的那一行。
            _chkVisualCenter = new CheckBox();
            _chkVisualCenter.Text = "视角重心补偿";
            _chkVisualCenter.Location = new Point(28, 56);
            _chkVisualCenter.Size = new Size(180, 22);
            _chkVisualCenter.Checked = _working.VisualCenterComp;
            _chkVisualCenter.CheckedChanged += delegate { _working.VisualCenterComp = _chkVisualCenter.Checked; Live(); };
            p.Controls.Add(_chkVisualCenter);

            // 托盘弹出面板的额外左移量：由用户自己调（0 = 不调整）
            // 两个"动态跟随"开关：关掉就退回旧行为（只用固定微调，不随图标增减调整）
            _chkFlyoutFollow = new CheckBox();
            _chkFlyoutFollow.Text = "面板动态跟随";
            _chkFlyoutFollow.Location = new Point(28, 80);
            _chkFlyoutFollow.Size = new Size(132, 22);
            _chkFlyoutFollow.Checked = _working.FlyoutFollow;
            _chkFlyoutFollow.CheckedChanged += delegate { _working.FlyoutFollow = _chkFlyoutFollow.Checked; Live(); };
            p.Controls.Add(_chkFlyoutFollow);

            _chkPreviewFollow = new CheckBox();
            _chkPreviewFollow.Text = "预览动态跟随";
            _chkPreviewFollow.Location = new Point(172, 80);
            _chkPreviewFollow.Size = new Size(132, 22);
            _chkPreviewFollow.Checked = _working.PreviewFollow;
            _chkPreviewFollow.CheckedChanged += delegate { _working.PreviewFollow = _chkPreviewFollow.Checked; Live(); };
            p.Controls.Add(_chkPreviewFollow);

            // 三行微调都是"动态跟随之上"的残差：正 = 往左
            _rowFlyout = MakePctRow(p, 104, -25, 25, _working.FlyoutOffsetX, "^面板微调", delegate(double v) { _working.FlyoutOffsetX = v; Live(); });
            _rowShift = MakePctRow(p, 128, -25, 25, _working.DockShiftX, "整体偏移", delegate(double v) { _working.DockShiftX = v; Live(); });
            _rowPreview = MakePctRow(p, 152, -25, 25, _working.PreviewOffsetX, "预览微调", delegate(double v) { _working.PreviewOffsetX = v; Live(); });

            return p;
        }

        private void LayoutForAdvanced()
        {
            bool adv = _chkAdvanced.Checked;
            _advanced.Visible = adv;
            // 展开：面板 556..742 → 按钮 748、状态行 780、窗体 810（内容到 802，只留 8px）
            // 收起：主界面内容到 544 → 按钮 572、状态行 604、窗体 640
            //       （旧值 604/710 会在下半部留出 110px 空白）
            int height = adv ? 810 : 640;
            ClientSize = new Size(FormW, height);
            int btnY = adv ? 748 : 572;
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
            if (_chkVisualCenter != null) _chkVisualCenter.Enabled = dock;   // 高级面板可能还没建好
            if (_chkFlyoutFollow != null) _chkFlyoutFollow.Enabled = dock;
            if (_chkPreviewFollow != null) _chkPreviewFollow.Enabled = dock;
            if (_rowPreview != null) { _rowPreview.Bar.Enabled = dock; _rowPreview.Num.Enabled = dock; }
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
            // 「任务栏自动隐藏时也处理」已无勾选框，固定为开启
            _working.ApplyOnAutoHide = true;
            SyncPctRow(_rowFlyout, _working.FlyoutOffsetX);
            SyncPctRow(_rowShift, _working.DockShiftX);
            SyncPctRow(_rowPreview, _working.PreviewOffsetX);
            if (_chkFlyoutFollow != null) _chkFlyoutFollow.Checked = _working.FlyoutFollow;
            if (_chkPreviewFollow != null) _chkPreviewFollow.Checked = _working.PreviewFollow;
            SyncPctRow(_rowWidth, _working.DockWidthTrim);
            SyncPctRow(_rowDockOffset, _working.DockOffsetX);
            _syncingAll = false;
            UpdateModeEnabled();
        }

        // 改动即时预览：直接改引擎正在用的设置对象，让托盘那边的节拍去套用
        /// <summary>
        /// 把工作副本推给引擎并立刻重算 —— 所有滑块的「拖动即时生效」都靠它。
        /// </summary>
        private void Live()
        {
            if (_syncingAll) return;
            _engine.Settings.CopyFrom(_working);
            _engine.Tick();
            if (_statusLabel != null) _statusLabel.Text = "状态：" + _engine.Status;
        }

        /// <summary>
        /// 把当前面板恢复成出厂默认值。
        /// </summary>
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

    /// <summary>
    /// 程序入口：单实例、托盘图标与菜单、命令行分支、启动自检与退出还原。
    /// </summary>
    internal static class Program
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "Dockify";
        private const string TaskName = "Dockify";    // 计划任务名（最高权限自启用）

        private static Mutex _mutex;
        /// <summary>核心引擎实例（整个进程只有这一个）。</summary>
        private static TaskbarEngine _engine;
        /// <summary>托盘图标。</summary>
        private static NotifyIcon _tray;
        /// <summary>托盘「启用圆角裁切」菜单项。</summary>
        private static ToolStripMenuItem _miEnable;
        /// <summary>托盘「开机自动启动」菜单项。</summary>
        private static ToolStripMenuItem _miAutoStart;
        /// <summary>托盘「模式 → 裁两端」菜单项。</summary>
        private static ToolStripMenuItem _miModeCrop;
        /// <summary>托盘「模式 → 紧凑 Dock」菜单项。</summary>
        private static ToolStripMenuItem _miModeDock;
        /// <summary>隐藏消息窗口：收注销/关机/分辨率变化/设置变更广播。</summary>
        private static HiddenWindow _hidden;
        // 心跳间隔（毫秒）。任务栏增删图标后靠它自动适应。
        private const int HeartbeatIntervalMs = 500;
        /// <summary>心跳定时器（WinForms 定时器 → 回调在 UI 线程，天然与界面操作串行）。</summary>
        private static System.Windows.Forms.Timer _timer;
        /// <summary>托盘图标对象（丢弃前要 Dispose，句柄另由 _iconHandle 管理）。</summary>
        private static Icon _icon;
        private static IntPtr _iconHandle = IntPtr.Zero;
        private static string _lastTip = "";
        /// <summary>是否已经就"紧凑 Dock 做不到"提示过一次。</summary>
        private static bool _warnedFailure;
        /// <summary>是否已经就"任务栏操作被拒绝"提示过一次（避免反复弹气泡）。</summary>
        private static bool _warnedCluster;

        // 用注册消息来叫醒已经在跑的实例（第二次双击 exe 时把设置窗口弹出来）
        private static readonly uint ShowSettingsMessage = Native.RegisterWindowMessage("Dockify.ShowSettings.v1");

        public static uint ShowSettingsMessageId
        {
            get { return ShowSettingsMessage; }
        }

        /// <summary>
        /// 通知已在运行的实例弹出设置窗口（双击 exe 却看不到界面时的兜底）。
        /// </summary>
        private static void PostShowSettings()
        {
            try
            {
                if (ShowSettingsMessage == 0) return;
                Native.PostMessage(new IntPtr(0xFFFF) /* HWND_BROADCAST */, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>命令行开关。字段名与 Program.Main 里的局部变量一一对应。</summary>
        private struct CommandLine
        {
            public bool Reset;         // --reset / --restore：还原任务栏后退出
            public bool ApplyOnly;     // --apply：只套用一次就退出（退出码 0=成功 2=失败）
            public bool OpenSettings;  // --settings / --config：打开设置窗口
            public bool Diag;          // --diag / --diagnose：跑一遍并把过程写进诊断报告
            public bool Relaunched;    // --relaunched：本进程是被"代为重启"拉起来的（内部用）
        }

        /// <summary>
        /// 解析命令行。前缀 `--` 与 `/` 等价、大小写不敏感、未知参数忽略。
        /// </summary>
        /// <param name="args">原始命令行参数。</param>
        /// <returns>各开关的取值。</returns>
        private static CommandLine ParseArgs(string[] args)
        {
            CommandLine c = new CommandLine();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant().TrimStart('-', '/');
                if (a == "reset" || a == "restore") c.Reset = true;
                else if (a == "apply") c.ApplyOnly = true;
                else if (a == "settings" || a == "config") c.OpenSettings = true;
                else if (a == "diag" || a == "diagnose") c.Diag = true;
                else if (a == "relaunched") c.Relaunched = true;
            }
            return c;
        }

        /// <summary>把异常写到 %APPDATA%\Dockify\last-error.txt（排障用）。</summary>
        internal static void LogError(string where, Exception ex)
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockify");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "last-error.txt"),
                    DateTime.Now.ToString("HH:mm:ss") + "  [" + where + "]" + Environment.NewLine + (ex == null ? "(null)" : ex.ToString()),
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // 启动顺序（改这块前先读一遍）：
            //   ① 解析命令行 → ② 设置 DPI 感知 → ③ 抢单实例互斥体（提权重启时要等旧实例退出）
            //   → ④ --reset / --apply / --diag 这类"跑一次就退"的分支
            //   → ⑤ 读配置 + 迁移 + 首启写盘 → ⑥ 建引擎、装弹出面板钩子
            //   → ⑦ 上下文受限就请资源管理器代为重启 → ⑧ 托盘图标 + 500ms 心跳 → ⑨ 消息循环
            AppDomain.CurrentDomain.UnhandledException += delegate(object se, UnhandledExceptionEventArgs ea)
            {
                LogError("UnhandledException", ea.ExceptionObject as Exception);
            };

            CommandLine cmd = ParseArgs(args);
            bool doReset = cmd.Reset;
            bool doApplyOnly = cmd.ApplyOnly;
            bool openSettings = cmd.OpenSettings;
            bool doDiag = cmd.Diag;
            bool relaunched = cmd.Relaunched;

            SetDpiAwareness();

            bool createdNew;
            _mutex = new Mutex(true, "Dockify.SingleInstance.v1", out createdNew);
            // 以管理员身份重启时，旧实例可能还没退干净 → 给它最多 5 秒把互斥体让出来，
            // 否则新实例会以为"已经有一个在跑"而直接退出，结果一个都不剩。
            if (!createdNew && relaunched)
            {
                for (int w = 0; w < 20 && !createdNew; w++)
                {
                    Thread.Sleep(250);
                    _mutex.Dispose();
                    _mutex = new Mutex(true, "Dockify.SingleInstance.v1", out createdNew);
                }
            }

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

            // 【权限门禁】本程序要改任务栏（资源管理器的窗口），Windows 会拒绝普通进程的
            // SetWindowRgn / MoveWindow —— 必须管理员运行才生效。
            // 所以：非管理员启动 → 立刻尝试"以管理员重启自己"（弹 UAC）。
            //   用户在 UAC 上点"是" → 新的管理员实例接管，本进程退出；
            //   点"否"（或提权失败） → 本进程直接退出，不常驻、不假装在工作。
            if (!IsElevated())
            {
                TryRelaunchElevated();
                return;     // 两种情况都退：交给管理员实例（或等用户下次以管理员启动）
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool firstRun = !Settings.Exists();
            Settings boot = Settings.Load();
            Settings.Migrate(boot);          // 老配置逐级迁移 + 统一裁剪范围
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
            _timer.Interval = HeartbeatIntervalMs;
            _timer.Tick += delegate
            {
                try { _engine.Tick(); } catch (Exception tex) { LogError("Tick", tex); }
                try
                {
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

            // 弹窗位置如果哪天看着偏了：把"当前几何"记成新基准，一键重校（不用开设置窗）
            ToolStripMenuItem miRecal = new ToolStripMenuItem("重新校准弹出面板");
            miRecal.Click += delegate { _engine.RecalibratePopups(); _engine.Tick(); };
            menu.Items.Add(miRecal);

            // 【一键对正测量】把"预览岛 / ^ 面板"当前的实际对正数据写进 %APPDATA%\Dockify\align.txt，
            // 供诊断与自动化测试读取。日常用户也可在排障时发这份数据。
            ToolStripMenuItem miAlign = new ToolStripMenuItem("测量弹窗/预览对正（写 align.txt）");
            miAlign.Click += delegate { _engine.MeasurePopupAlignment(); };
            menu.Items.Add(miAlign);

            // 不是管理员时才给这一项：任务栏操作在普通权限下会被系统拒绝，一键提权最省事。
            if (!IsElevated())
            {
                ToolStripMenuItem miElevate = new ToolStripMenuItem("以管理员身份重启…");
                miElevate.Click += delegate { RelaunchElevated(); };
                menu.Items.Add(miElevate);
            }

            ToolStripMenuItem miAbout = new ToolStripMenuItem("关于");
            miAbout.Click += delegate
            {
                MessageBox.Show(
                    "任务栏圆角 · Dockify v1.2\n\n" +
                    "用窗口区域 (SetWindowRgn) 把任务栏裁成两侧不贴边的圆角条。\n" +
                    "当前：" + _engine.Status + "\n\n" +
                    "配置文件：" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) +
                    "\\Dockify\\settings.ini\n" +
                    "命令行：Dockify.exe --reset 可强制恢复任务栏原样",
                    "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            menu.Items.Add(miAbout);
            menu.Items.Add(new ToolStripSeparator());

            // 重新加载设置：本程序启动时只读一次 settings.ini，之后不监视文件。
            // 手改了配置、或想让新几何重新测量时，用这项最省事（不必去任务管理器结束进程）。
            // 注意：本进程已是管理员 → 拉起的子进程直接继承管理员，不会再弹 UAC。
            ToolStripMenuItem miRestart = new ToolStripMenuItem("重启 Dockify（重新加载设置）");
            miRestart.Click += delegate { RestartSelf(); };
            menu.Items.Add(miRestart);

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { Application.Exit(); };
            menu.Items.Add(miExit);

            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.Text = "Dockify v1.2";
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
        /// <summary>
        /// 紧凑 Dock 要求任务栏居中。若用户原本是「居左」，临时改写系统设置并把原值记进配置，退出时还原。
        /// </summary>
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
        /// <summary>
        /// 把任务栏对齐方式还原成用户原本的设置。
        /// </summary>
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
        /// <summary>
        /// 周期性写「实时诊断」，只在任务栏可见时写，用于长期观察。
        /// </summary>
        private static string _lastLiveDiagBody;   // 上一次写入的诊断正文（不含时间戳），用于"没变就不写"
        internal static void WriteLiveDiag(TaskbarEngine engine)
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockify", "diag.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // 先拼"不带时间戳"的正文，和上次一样就直接返回 ——
                // 原来每 5 秒无条件重写整个文件，既浪费又让 mtime 失去"有变化"的含义。
                StringBuilder body = new StringBuilder();
                body.AppendLine("状态: " + engine.Status + "   生效任务栏数: " + engine.AppliedBars);
                if (!string.IsNullOrEmpty(Settings.LastSaveError))
                    body.AppendLine("配置写入失败: " + Settings.LastSaveError);
                body.AppendLine(engine.Report);
                string text = body.ToString();
                if (text == _lastLiveDiagBody) return;
                _lastLiveDiagBody = text;
                body.Insert(0, "Dockify 实时诊断 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
                File.WriteAllText(path, body.ToString(), new UTF8Encoding(false));
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
        /// <summary>
        /// 写诊断报告（含本轮完整测量与套用过程），写不进 AppData 就退到临时目录。
        /// </summary>
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

        /// <summary>当前进程是否以管理员身份运行。</summary>
        private static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// 以管理员身份重启自己（弹 UAC）。用户取消 UAC 时静默返回、什么都不做。
        /// 用于：普通权限实例改不动任务栏（Windows 会拒绝 SetWindowRgn）时一键提权。
        /// </summary>
        private static void RelaunchElevated()
        {
            if (!TryRelaunchElevated()) return;         // 用户点了"否" → 留在原样
            Application.Exit();                          // 已提权实例接管 → 自己退出（退出的钩子会先还原任务栏）
        }

        /// <summary>
        /// 尝试以管理员身份重启自己（弹 UAC），返回新实例是否成功拉起。
        /// 这是"启动时自动提权"的通用方法：很多地方都用得上（程序首次启动门禁、托盘"以管理员重启"等）。
        /// </summary>
        /// <returns>true = 用户接受 UAC、新的管理员实例已拉起；false = 用户在 UAC 上点了"否"或启动失败。</returns>
        private static bool TryRelaunchElevated()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--relaunched";     // 告诉新实例：旧实例可能还没退干净，等一等互斥体
                psi.UseShellExecute = true;
                psi.Verb = "runas";                 // 触发 UAC 提权
                System.Diagnostics.Process.Start(psi);
                return true;                        // UAC 已接受，新的管理员实例起来了
            }
            catch { return false; }                 // 用户在 UAC 上点了"否"，或系统拒绝
        }

        /// <summary>
        /// 重启自己（重新加载 settings.ini）。托盘菜单「重启 Dockify」用。
        /// 不走 runas：本进程若已是管理员，子进程直接继承管理员令牌 ✓（不会再弹 UAC）；
        /// 若本进程是普通权限，那子进程也是普通权限 —— 但那种情况启动门禁会立刻把它提权。
        /// </summary>
        private static void RestartSelf()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--relaunched";     // 新实例等一等旧实例让出互斥体
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
            Application.Exit();                      // 退出钩子会先还原任务栏，交给新实例重新接管
        }

        /// <summary>跑一次 schtasks.exe（隐藏窗口），返回是否成功。</summary>
        private static bool RunSchtasks(string args)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo("schtasks.exe", args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                using (System.Diagnostics.Process pr = System.Diagnostics.Process.Start(psi))
                {
                    pr.WaitForExit(8000);
                    return pr.HasExited && pr.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        /// <summary>计划任务是否存在（存在即认为已自启）。</summary>
        private static bool AutoStartTaskExists()
        {
            return RunSchtasks("/Query /TN \"" + TaskName + "\"");
        }

        /// <summary>删掉经典的 Run 键启动项（升级到计划任务后要清掉，避免开两次）。</summary>
        private static void RemoveRunValue()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    if (k != null) k.DeleteValue(RunValueName, false);
            }
            catch { }
        }

        /// <summary>
        /// 是否已设为开机自启。两种方式任一存在即为真：
        /// ① 计划任务 Dockify（**最高权限**，开机即管理员身份、不弹 UAC）
        /// ② 经典 Run 键（普通权限，作为没有管理员权限时的退路）
        /// </summary>
        private static bool IsAutoStart()
        {
            if (AutoStartTaskExists()) return true;
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

        /// <summary>
        /// 设置开机自启。
        /// 优先建「计划任务 + 最高权限」：这样开机后就是管理员身份静默启动，不需要每次点 UAC ——
        /// 这正是本程序需要的权限（否则 SetWindowRgn 会被系统拒绝）。
        /// 没有管理员权限时建不了这种任务，自动退回经典 Run 键（以普通权限启动）。
        /// </summary>
        private static void SetAutoStart(bool on)
        {
            string exe = Application.ExecutablePath;
            if (on)
            {
                bool taskOk = RunSchtasks("/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe +
                                          "\\\"\" /SC ONLOGON /RL HIGHEST /DELAY 0000:10 /F");
                if (taskOk) { RemoveRunValue(); return; }     // 任务建好了，清掉 Run 键免得开两次

                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) throw new InvalidOperationException("打不开注册表启动项");
                    k.SetValue(RunValueName, "\"" + exe + "\"");
                }
            }
            else
            {
                RunSchtasks("/Delete /TN \"" + TaskName + "\" /F");
                RemoveRunValue();
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
            // Icon.FromHandle 返回的 Icon 不拥有句柄；Clone 出副本后才能安全 DestroyIcon，
            // 否则 NotifyIcon 与 DestroyIcon 竞争会残留/失效。
            Icon ic = (Icon)Icon.FromHandle(handle).Clone();
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
