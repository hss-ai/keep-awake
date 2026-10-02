// keep_awake_tray.cs — 防待机托盘常驻工具(NotifyIcon + SetThreadExecutionState)+ 资源管理器单窗口合并
//
// 构建:python build_keep_awake.py 一键完成(生成 ico → csc 编译 → 装机 → 建桌面快捷方式),等价命令:
//   csc /nologo /target:winexe /codepage:65001 /optimize+ /win32icon:keep_awake.ico
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll
//       /out:KeepAwake.exe keep_awake_tray.cs explorer_tab_merge.cs
//
// 行为:
//   - 启动即开启「系统防待机」(屏幕允许自动关),托盘弹气泡提示;
//   - 左键单击托盘图标 = 开/关切换;图标三态:绿=防待机,蓝=防待机+屏幕常亮,灰=关;
//   - 右键菜单:✔防待机开启 / ✔屏幕常亮(勾上自动连防待机一起开) /
//     ✔资源管理器单窗口合并(新开的资源管理器自动并入既有窗口成标签页,Win11) /
//     ✔开机自启(HKCU Run 键,免管理员,exe 挪窝自愈)/ 退出;
//   - 唤醒请求只挂在本进程(SetThreadExecutionState),退出/被杀/注销系统自动撤销,不改电源计划;
//   - Mutex 单实例:重复启动直接退出,不多开图标。
// 注意:C# 5 语法(csc 4.0.30319 不支持 C#6+),不要用字符串插值 $""、?. 等新语法。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeepAwake {

static class Program {
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SetThreadExecutionState(uint esFlags);

    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED = 0x00000001;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [STAThread]
    static void Main() {
        bool createdNew;
        using (var single = new Mutex(true, "Local\\KeepAwake_Tray_SingleInstance", out createdNew)) {
            if (!createdNew) return; // 已有实例,静默退出
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
    }
}

// 应用级日志:防待机开关/护栏动作/启停留痕(出问题对时间线用;512KB 自动重开)
static class AppLog {
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "KeepAwake_app.log");
    public static void Write(string msg) {
        try {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 524288) fi.Delete();
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine);
        } catch {
        }
    }
}

// ==== 电源护栏:防待机开启期间临时清零「在此时间后休眠」(HIBERNATEIDLE) ====
// 为什么需要:SetThreadExecutionState 挡得住"闲置超时睡眠",挡不住 Win11 新型待机的
// 「睡眠后休眠 - 固定超时」——2026-10-02 凌晨实测:屏幕熄灭后系统保持活跃约 4 小时,
// 维护周期进入真睡眠,30 分钟后(机器 HIBERNATEIDLE=0x708)被强制休眠至早晨
// (事件日志证据:Hibernate from Sleep - Fixed Timeout)。
// 护栏:开启防待机时把 HIBERNATEIDLE(AC+DC)写 0(从不),原值备份在 HKCU;
// 关闭/退出恢复;启动时发现未撤销的备份(上次异常退出)先自愈,保证不留残留。
static class PowerHibernateGuard {
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr userPowerKey, out IntPtr activeSchemeGuid);
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(IntPtr userPowerKey, ref Guid schemeGuid);
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadACValue(IntPtr root, ref Guid scheme, ref Guid subgroup,
        ref Guid setting, out int type, byte[] buffer, ref uint bufferSize);
    // 注意:真导出名是 PowerWriteACValueIndex,且第 5 参直接收 DWORD 值(不是 buffer+size,
    // 传 buffer 指针会把指针当超时写进去——实测翻车,39791600s 就是堆地址)
    [DllImport("powrprof.dll", SetLastError = true, EntryPoint = "PowerWriteACValueIndex")]
    private static extern uint PowerWriteAcIndex(IntPtr root, ref Guid scheme, ref Guid subgroup,
        ref Guid setting, uint value);
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadDCValue(IntPtr root, ref Guid scheme, ref Guid subgroup,
        ref Guid setting, out int type, byte[] buffer, ref uint bufferSize);
    [DllImport("powrprof.dll", SetLastError = true, EntryPoint = "PowerWriteDCValueIndex")]
    private static extern uint PowerWriteDcIndex(IntPtr root, ref Guid scheme, ref Guid subgroup,
        ref Guid setting, uint value);
    [DllImport("powrprof.dll", SetLastError = true, EntryPoint = "PowerWriteDCValueIndex")]
    private static extern uint PowerWriteDCValue(IntPtr root, ref Guid scheme, ref Guid subgroup,
        ref Guid setting, byte[] buffer, uint bufferSize);

    static Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");      // 不能 readonly:要按 ref 传给 P/Invoke
    static Guid HibernateIdle = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");
    const string RegPath = "Software\\KeepAwake";

    static Guid ActiveScheme() {
        IntPtr p;
        if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero)
            throw new InvalidOperationException("PowerGetActiveScheme 失败");
        return (Guid)Marshal.PtrToStructure(p, typeof(Guid));
    }

    static uint ReadVal(bool ac, Guid scheme) {
        int type;
        byte[] buf = new byte[4];
        uint size = 4;
        uint rc = ac
            ? PowerReadACValue(IntPtr.Zero, ref scheme, ref SubSleep, ref HibernateIdle, out type, buf, ref size)
            : PowerReadDCValue(IntPtr.Zero, ref scheme, ref SubSleep, ref HibernateIdle, out type, buf, ref size);
        if (rc != 0) throw new InvalidOperationException("PowerRead 失败 rc=" + rc);
        return BitConverter.ToUInt32(buf, 0);
    }

    static void WriteVal(bool ac, Guid scheme, uint val) {
        uint rc = ac
            ? PowerWriteAcIndex(IntPtr.Zero, ref scheme, ref SubSleep, ref HibernateIdle, val)
            : PowerWriteDcIndex(IntPtr.Zero, ref scheme, ref SubSleep, ref HibernateIdle, val);
        if (rc != 0) throw new InvalidOperationException("PowerWrite 失败 rc=" + rc);
    }

    /// <summary>开启防待机时调用:清零 HIBERNATEIDLE(AC+DC),原值备份 HKCU。</summary>
    public static void Engage() {
        try {
            SelfHealIfPending();                       // 上次异常退出的残留先恢复,再重新护栏
            Guid scheme = ActiveScheme();
            uint ac = ReadVal(true, scheme);
            uint dc = ReadVal(false, scheme);
            using (var key = Registry.CurrentUser.CreateSubKey(RegPath)) {
                key.SetValue("HibernateIdleAcBackup", ac, RegistryValueKind.DWord);
                key.SetValue("HibernateIdleDcBackup", dc, RegistryValueKind.DWord);
                key.SetValue("GuardPending", 1, RegistryValueKind.DWord);
            }
            if (ac != 0) WriteVal(true, scheme, 0);
            if (dc != 0) WriteVal(false, scheme, 0);
            Guid s = scheme;
            PowerSetActiveScheme(IntPtr.Zero, ref s);   // 立即生效
            AppLog.Write("护栏生效:睡眠后休眠 " + ac + "s/" + dc + "s → 从不(原值已备份)");
        } catch (Exception ex) {
            AppLog.Write("护栏 Engage 失败: " + ex.Message);
        }
    }

    /// <summary>关闭防待机/退出时调用:恢复原值。</summary>
    public static void Disengage() {
        try {
            if (!Pending()) return;
            Restore();
            AppLog.Write("护栏撤销:睡眠后休眠已恢复原值");
        } catch (Exception ex) {
            AppLog.Write("护栏 Disengage 失败: " + ex.Message);
        }
    }

    /// <summary>启动时调用:上次异常退出没来得及恢复的话,先恢复。</summary>
    public static void SelfHealIfPending() {
        try {
            if (!Pending()) return;
            Restore();
            AppLog.Write("护栏自愈:发现上次未撤销的修改,已恢复原值");
        } catch (Exception ex) {
            AppLog.Write("护栏自愈失败: " + ex.Message);
        }
    }

    static bool Pending() {
        using (var key = Registry.CurrentUser.OpenSubKey(RegPath)) {
            if (key == null) return false;
            return Convert.ToInt32(key.GetValue("GuardPending", 0)) == 1;
        }
    }

    static void Restore() {
        uint ac;
        uint dc;
        using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true)) {
            ac = Convert.ToUInt32(key.GetValue("HibernateIdleAcBackup", 0));
            dc = Convert.ToUInt32(key.GetValue("HibernateIdleDcBackup", 0));
            key.SetValue("GuardPending", 0, RegistryValueKind.DWord);
        }
        Guid scheme = ActiveScheme();
        WriteVal(true, scheme, ac);
        WriteVal(false, scheme, dc);
        Guid s = scheme;
        PowerSetActiveScheme(IntPtr.Zero, ref s);
    }
}

internal sealed class TrayContext : ApplicationContext {

    readonly NotifyIcon tray;    readonly ToolStripMenuItem miState;
    readonly ToolStripMenuItem miOn;
    readonly ToolStripMenuItem miDisplay;
    readonly ToolStripMenuItem miExplorer;
    readonly ToolStripMenuItem miAutoStart;
    readonly System.Windows.Forms.Timer timer;
    readonly Icon iconOn;
    readonly Icon iconOnDisplay;
    readonly Icon iconOff;
    readonly ExplorerTabMerger merger;   // 资源管理器单窗口合并(默认开)
    DateTime onSince = DateTime.Now;

    public TrayContext() {
        AppLog.Write("启动(v1.3.0)");
        PowerHibernateGuard.SelfHealIfPending();
        iconOn = MakeIcon(Color.FromArgb(39, 174, 96));         // 绿:防待机
        iconOnDisplay = MakeIcon(Color.FromArgb(41, 128, 185)); // 蓝:防待机+屏幕常亮
        iconOff = MakeIcon(Color.FromArgb(128, 128, 128));      // 灰:关闭

        var menu = new ContextMenuStrip();
        miState = new ToolStripMenuItem("状态");
        miState.Enabled = false;
        miOn = new ToolStripMenuItem("防待机开启");
        miOn.Click += OnToggleOn;
        miDisplay = new ToolStripMenuItem("屏幕常亮");
        miDisplay.Click += OnToggleDisplay;
        miExplorer = new ToolStripMenuItem("资源管理器单窗口合并");
        miExplorer.Click += OnToggleExplorer;
        miAutoStart = new ToolStripMenuItem("开机自启");
        miAutoStart.Click += OnToggleAutoStart;
        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += OnExit;
        menu.Items.Add(miState);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miOn);
        menu.Items.Add(miDisplay);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExplorer);
        menu.Items.Add(miAutoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        tray = new NotifyIcon();
        tray.Icon = iconOff;
        tray.ContextMenuStrip = menu;
        tray.Text = "防待机";
        tray.Visible = true;
        tray.MouseClick += TrayMouseClick;

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 30000; // 30s 刷新一次托盘提示里的保持时长
        timer.Tick += delegate { RefreshState(); };
        timer.Start();

        merger = new ExplorerTabMerger();
        miExplorer.Checked = true; // 默认开启:新开的资源管理器并入既有窗口
        merger.Enabled = true;

        miAutoStart.Checked = AutoStartEnabled();
        if (miAutoStart.Checked) SetAutoStart(true); // 路径自愈:exe 挪窝后指向当前实例

        miOn.Checked = true; // 启动即开启
        RefreshState();
        PowerHibernateGuard.Engage();
        AppLog.Write("防待机:开启");
        tray.BalloonTipTitle = "防待机已开启";
        tray.BalloonTipText = "资源管理器合并已启动(新窗口并入标签)。左键图标:开/关防待机;右键菜单更多。";
        tray.ShowBalloonTip(2500);
    }

    void OnToggleOn(object sender, EventArgs e) {
        miOn.Checked = !miOn.Checked;
        if (miOn.Checked) onSince = DateTime.Now;
        else miDisplay.Checked = false; // 总开关关掉,常亮自然失效
        RefreshState();
        if (miOn.Checked) {
            PowerHibernateGuard.Engage();
            AppLog.Write("防待机:开启");
        } else {
            PowerHibernateGuard.Disengage();
            AppLog.Write("防待机:关闭");
        }
    }

    void OnToggleDisplay(object sender, EventArgs e) {
        bool wantDisplay = !miDisplay.Checked;
        if (wantDisplay && !miOn.Checked) { // 常亮隐含防待机,自动一起开
            miOn.Checked = true;
            onSince = DateTime.Now;
        }
        miDisplay.Checked = wantDisplay && miOn.Checked;
        RefreshState();
    }

    void OnToggleExplorer(object sender, EventArgs e) {
        miExplorer.Checked = !miExplorer.Checked;
        merger.Enabled = miExplorer.Checked;
    }

    void OnToggleAutoStart(object sender, EventArgs e) {
        try {
            SetAutoStart(!miAutoStart.Checked);
        } catch (Exception ex) {
            tray.BalloonTipTitle = "开机自启设置失败";
            tray.BalloonTipText = ex.Message;
            tray.ShowBalloonTip(2500);
        }
        miAutoStart.Checked = AutoStartEnabled(); // 以注册表实际状态为准
    }

    // ==== 开机自启(HKCU Run,免管理员) ====

    const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string RunValueName = "KeepAwake";

    static bool AutoStartEnabled() {
        try {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath)) {
                if (key == null) return false;
                return !string.IsNullOrEmpty(key.GetValue(RunValueName) as string);
            }
        } catch {
            return false;
        }
    }

    static void SetAutoStart(bool on) {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)) {
            if (on) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
            else key.DeleteValue(RunValueName, false);
        }
    }

    void TrayMouseClick(object sender, MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) OnToggleOn(sender, e);
    }

    void RefreshState() {
        bool on = miOn.Checked;
        bool display = on && miDisplay.Checked;
        uint flags = Program.ES_CONTINUOUS
                   | (on ? Program.ES_SYSTEM_REQUIRED : 0)
                   | (display ? Program.ES_DISPLAY_REQUIRED : 0);
        Program.SetThreadExecutionState(flags);

        tray.Icon = on ? (display ? iconOnDisplay : iconOn) : iconOff;
        string text = on
            ? ("防待机:开启" + (display ? "(屏幕常亮)" : "") + " · " + ElapsedText())
            : "防待机:已关闭";
        if (text.Length > 63) text = text.Substring(0, 63); // NotifyIcon.Text 长度上限
        tray.Text = text;
        miState.Text = text;
    }

    string ElapsedText() {
        TimeSpan t = DateTime.Now - onSince;
        if (t.TotalMinutes < 1) return "刚开启";
        int hours = (int)t.TotalHours;
        int mins = ((int)t.TotalMinutes) % 60;
        return hours > 0 ? ("已保持 " + hours + "小时" + mins + "分")
                         : ("已保持 " + mins + "分钟");
    }

    void OnExit(object sender, EventArgs e) {
        miOn.Checked = false;
        miDisplay.Checked = false;
        RefreshState(); // 撤销唤醒请求,恢复系统默认
        PowerHibernateGuard.Disengage();
        AppLog.Write("退出");
        merger.Dispose();
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    // 运行时 GDI+ 画托盘图标:彩色圆底 + 白色咖啡杯(caffeine 梗),免带 .ico 资源文件
    static Icon MakeIcon(Color back) {
        using (var bmp = new Bitmap(32, 32)) {
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var br = new SolidBrush(back)) g.FillEllipse(br, 1, 1, 30, 30);
                using (var white = new SolidBrush(Color.White)) {
                    g.FillRectangle(white, 8, 12, 13, 3); // 杯口沿
                    g.FillRectangle(white, 10, 14, 9, 9); // 杯身
                }
                using (var pen = new Pen(Color.White, 2.5f)) {
                    g.DrawArc(pen, 19, 15, 8, 6, -80, 160); // 杯柄
                    g.DrawLine(pen, 12, 7, 12, 10);         // 蒸汽×2
                    g.DrawLine(pen, 16, 7, 16, 10);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}

}
