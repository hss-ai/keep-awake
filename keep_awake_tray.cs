// keep_awake_tray.cs — 防待机托盘常驻工具(NotifyIcon + SetThreadExecutionState)+ 资源管理器单窗口合并
//
// 构建:python build_keep_awake.py 一键完成(生成 ico → csc 编译 → 装机 → 建桌面快捷方式),等价命令:
//   csc /nologo /target:winexe /codepage:65001 /optimize+ /win32icon:keep_awake.ico
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll
//       /r:UIAutomationClient.dll /r:UIAutomationTypes.dll  (仅 GAC 有,构建脚本负责解析路径)
//       /out:KeepAwake.exe keep_awake_tray.cs explorer_tab_merge.cs key_remap.cs shortcut_arrow.cs
//
// 行为:
//   - 启动即开启「系统防待机」(屏幕允许自动关),托盘弹气泡提示;
//   - 左键单击托盘图标 = 开/关切换;图标三态:绿=防待机,蓝=防待机+屏幕常亮,灰=关;
//   - 右键菜单:✔防待机开启 / ✔屏幕常亮(勾上自动连防待机一起开) /
//     ✔资源管理器单窗口合并(新开的资源管理器自动并入既有窗口成标签页,Win11) /
//     键映射▸✔F2→Ctrl+W ✔Win(左)→Ctrl(左)(低级键盘钩子,接替 PowerToys
//       Keyboard Manager;两条映射各自独立开关,v1.4.3 起) /
//     ✔开机自启(HKCU Run 键,免管理员,exe 挪窝自愈)/
//     更多工具▸✔去除快捷方式小箭头(非常用功能折叠进子菜单;切换时写 HKLM,
//       自我提权拉一次性 --arrow 实例,UAC 弹一次,详见 shortcut_arrow.cs) / 退出;
//   - 唤醒请求只挂在本进程(SetThreadExecutionState),退出/被杀/注销系统自动撤销;
//     防待机开启期间另把「在此时间后休眠」的交流侧临时清零(见 PowerHibernateGuard),
//     关闭/退出恢复,异常退出下次启动自愈——这是唯一一处系统级修改;
//   - Mutex 单实例:重复启动直接退出,不多开图标。
// 注意:C# 5 语法(csc 4.0.30319 不支持 C#6+),不要用字符串插值 $""、?. 等新语法。
// DPI:app.manifest 声明 dpiAware=true 并由构建脚本 /win32manifest 嵌入(缺了它 exe 裸奔)。
//   高分屏(本机 175%)上进程若启动时不感知、运行中被 UIA 之类库"中途翻"成 DPI aware,
//   托盘右键菜单会按 96 DPI 布局直接按物理像素渲染——字体缩成 9pt*(96/168)(v1.3.3 修)。

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
    static int Main(string[] args) {
        // 维护开关:进程被强杀/崩溃留下护栏残留时,手动一键恢复电源设置(正常退出不需要)
        if (args.Length > 0 && args[0] == "--restore-power") {
            PowerHibernateGuard.SelfHealIfPending();
            return 0;
        }
        // 去小箭头的一次性提权实例(托盘 SetEnabled 经 UAC 拉起):写完 HKLM+刷图标即退,
        // 不进下面的互斥/托盘逻辑(同 --restore-power 的套路)
        if (args.Length > 1 && args[0] == "--arrow") {
            return ShortcutArrowCleaner.ApplyFromCli(args[1]);
        }
        // 测试钩子:只生成空白图标,不碰注册表(tests/probe_arrow.py 校验成品 exe 的 ICO 字节)
        if (args.Length > 1 && args[0] == "--write-blank-ico") {
            ShortcutArrowCleaner.WriteBlankIcoTo(args[1]);
            return 0;
        }
        bool createdNew;
        using (var single = new Mutex(true, "Local\\KeepAwake_Tray_SingleInstance", out createdNew)) {
            if (!createdNew) return 0; // 已有实例,静默退出
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
        }
        return 0;
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

// ==== 电源护栏:防待机开启期间临时清零「在此时间后休眠」的【仅交流侧】 ====
// 为什么需要:SetThreadExecutionState 挡得住"闲置超时睡眠",挡不住 Win11 新型待机的
// 「睡眠后休眠 - 固定超时」——2026-10-02 凌晨实测:屏幕熄灭后系统保持活跃约 4 小时,
// 维护周期进入真睡眠,30 分钟后(机器 HIBERNATEIDLE=0x708)被强制休眠至早晨
// (事件日志证据:Hibernate from Sleep - Fixed Timeout)。
// 为什么只动交流:直流(电池)侧的睡后休眠是合盖/电池场景的保电闸门,动了它出门
// 一天可能把电耗光(v1.3.0 教训);插电过夜才是防待机的主战场,交流侧归零足够。
// 护栏:开启防待机时把交流 HIBERNATEIDLE 写 0(从不),原值备份在 HKCU;
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

    /// <summary>开启防待机时调用:仅清零交流侧 HIBERNATEIDLE,原值备份 HKCU。
    /// 直流(电池)侧刻意不动——合盖/电池场景的「睡后休眠」保电逻辑必须原样保留。</summary>
    public static void Engage() {
        try {
            SelfHealIfPending();                       // 上次异常退出的残留先恢复,再重新护栏
            Guid scheme = ActiveScheme();
            uint ac = ReadVal(true, scheme);
            using (var key = Registry.CurrentUser.CreateSubKey(RegPath)) {
                key.SetValue("HibernateIdleAcBackup", ac, RegistryValueKind.DWord);
                // 连方案一起备份:防待机期间用户若切了电源方案,恢复时得写回原方案,
                // 否则原值落进新方案,旧方案的休眠超时就永久停在「从不」
                key.SetValue("HibernateIdleSchemeBackup", scheme.ToString(), RegistryValueKind.String);
                key.SetValue("GuardPending", 1, RegistryValueKind.DWord);
            }
            if (ac != 0) WriteVal(true, scheme, 0);
            Guid s = scheme;
            PowerSetActiveScheme(IntPtr.Zero, ref s);   // 立即生效
            AppLog.Write("护栏生效(仅交流):睡眠后休眠 AC " + ac + "s → 从不;直流不动(合盖/电池照常休眠)");
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

    /// <summary>方案在系统里还能读到吗(不存在/损坏返回 false)。</summary>
    static bool SchemeExists(Guid scheme) {
        int type;
        byte[] buf = new byte[4];
        uint size = 4;
        return PowerReadACValue(IntPtr.Zero, ref scheme, ref SubSleep, ref HibernateIdle,
            out type, buf, ref size) == 0;
    }

    static void Restore() {
        Guid scheme = ActiveScheme();
        using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true)) {
            uint ac = Convert.ToUInt32(key.GetValue("HibernateIdleAcBackup", 0));
            object dcRaw = key.GetValue("HibernateIdleDcBackup");
            // 优先写回备份时的方案(已删的方案退回当前活动方案;v1.4.0 之前的旧备份没有此键,同样退回)
            object schemeRaw = key.GetValue("HibernateIdleSchemeBackup");
            if (schemeRaw is string) {
                Guid stored = new Guid((string)schemeRaw);
                if (stored != Guid.Empty && SchemeExists(stored)) scheme = stored;
            }
            key.SetValue("GuardPending", 0, RegistryValueKind.DWord);
            WriteVal(true, scheme, ac);
            if (dcRaw != null) {
                // v1.3.0 曾把直流也归零:迁移场景把直流恢复一次并清掉旧键
                WriteVal(false, scheme, Convert.ToUInt32(dcRaw));
                key.DeleteValue("HibernateIdleDcBackup");
            }
        }
        Guid s = scheme;
        PowerSetActiveScheme(IntPtr.Zero, ref s);
        Registry.CurrentUser.DeleteSubKey(RegPath, false);   // 恢复完毕,配置键删净
    }
}

internal sealed class TrayContext : ApplicationContext {

    readonly NotifyIcon tray;    readonly ToolStripMenuItem miState;
    readonly ToolStripMenuItem miOn;
    readonly ToolStripMenuItem miDisplay;
    readonly ToolStripMenuItem miExplorer;
    readonly ToolStripMenuItem miKeymap;    // 键映射(父项):映射多了,收进二级菜单
    readonly ToolStripMenuItem miKeymapF2;  // └ F2 → Ctrl+W
    readonly ToolStripMenuItem miKeymapWin; // └ Win(左) → Ctrl(左)
    readonly ToolStripMenuItem miAutoStart;
    readonly ToolStripMenuItem miMore;     // 更多工具:非常用功能折叠在此,主菜单保持短
    readonly ToolStripMenuItem miArrow;
    readonly System.Windows.Forms.Timer timer;
    readonly Icon iconOn;
    readonly Icon iconOnDisplay;
    readonly Icon iconOff;
    readonly ExplorerTabMerger merger;   // 资源管理器单窗口合并(默认开)
    readonly KeyRemapper remapper;       // 全局键映射(两条,默认开)
    DateTime onSince = DateTime.Now;

    public TrayContext() {
        AppLog.Write("启动(v1.4.3)");
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
        miKeymapF2 = new ToolStripMenuItem("F2 → Ctrl+W");
        miKeymapF2.Click += OnToggleKeymapF2;
        miKeymapWin = new ToolStripMenuItem("Win(左) → Ctrl(左)");
        miKeymapWin.Click += OnToggleKeymapWin;
        miKeymap = new ToolStripMenuItem("键映射");
        miKeymap.DropDownItems.Add(miKeymapF2);
        miKeymap.DropDownItems.Add(miKeymapWin);
        miAutoStart = new ToolStripMenuItem("开机自启");
        miAutoStart.Click += OnToggleAutoStart;
        miArrow = new ToolStripMenuItem("去除快捷方式小箭头");
        miArrow.Click += OnToggleArrow;
        miMore = new ToolStripMenuItem("更多工具");
        miMore.DropDownItems.Add(miArrow);
        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += OnExit;
        menu.Items.Add(miState);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miOn);
        menu.Items.Add(miDisplay);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExplorer);
        menu.Items.Add(miKeymap);
        menu.Items.Add(miAutoStart);
        menu.Items.Add(miMore);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        tray = new NotifyIcon();
        tray.Icon = iconOff;
        tray.ContextMenuStrip = menu;
        tray.Text = "防待机";
        tray.Visible = true;
        tray.MouseClick += TrayMouseClick;
        // 注销/关机时进程会被直接终止,没机会走菜单退出——挂会话事件,临终前恢复系统设置
        SystemEvents.SessionEnding += OnSessionEnding;

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 30000; // 30s 刷新一次托盘提示里的保持时长
        timer.Tick += delegate { RefreshState(); };
        timer.Start();

        merger = new ExplorerTabMerger();
        miExplorer.Checked = true; // 默认开启:新开的资源管理器并入既有窗口
        merger.Enabled = true;

        remapper = new KeyRemapper();
        miKeymapF2.Checked = true;    // 默认开启:接替 PowerToys 键映射(用户唯一在用的那条)
        remapper.F2Enabled = true;
        miKeymapWin.Checked = true;   // 默认开启:左 Win 变左 Ctrl(右 Win 保留开始菜单)
        remapper.WinEnabled = true;

        miAutoStart.Checked = AutoStartEnabled();
        if (miAutoStart.Checked) SetAutoStart(true); // 路径自愈:exe 挪窝后指向当前实例

        miArrow.Checked = ShortcutArrowCleaner.IsEnabled(); // 小箭头状态以 HKLM 实况为准

        TurnOn(); // 启动即开启
        tray.BalloonTipTitle = "防待机已开启";
        tray.BalloonTipText = "资源管理器合并、键映射(F2→Ctrl+W、Win→Ctrl)已启动。左键图标:开/关防待机;右键菜单更多。";
        tray.ShowBalloonTip(2500);
    }

    void OnToggleOn(object sender, EventArgs e) {
        if (miOn.Checked) TurnOff();
        else TurnOn();
    }

    // 开/关只从这里走(三个入口:左键图标/菜单/常亮隐含开启)——
    // v1.4.0 之前「常亮隐含开启」只翻 Checked 漏了护栏,Win11 睡后休眠固定计时器
    // 没被清零,凌晨强制休眠正是护栏要挡的事
    void TurnOn() {
        if (miOn.Checked) return;
        miOn.Checked = true;
        onSince = DateTime.Now;
        RefreshState();
        PowerHibernateGuard.Engage();
        AppLog.Write("防待机:开启");
    }

    void TurnOff() {
        if (!miOn.Checked) return;
        miOn.Checked = false;
        miDisplay.Checked = false; // 总开关关掉,常亮自然失效
        RefreshState();
        PowerHibernateGuard.Disengage();
        AppLog.Write("防待机:关闭");
    }

    void OnToggleDisplay(object sender, EventArgs e) {
        bool wantDisplay = !miDisplay.Checked;
        if (wantDisplay) TurnOn(); // 常亮隐含防待机,自动一起开(护栏一并生效)
        miDisplay.Checked = wantDisplay && miOn.Checked;
        RefreshState();
    }

    void OnToggleExplorer(object sender, EventArgs e) {
        miExplorer.Checked = !miExplorer.Checked;
        merger.Enabled = miExplorer.Checked;
    }

    void OnToggleKeymapF2(object sender, EventArgs e) {
        miKeymapF2.Checked = !miKeymapF2.Checked;
        remapper.F2Enabled = miKeymapF2.Checked;
    }

    void OnToggleKeymapWin(object sender, EventArgs e) {
        miKeymapWin.Checked = !miKeymapWin.Checked;
        remapper.WinEnabled = miKeymapWin.Checked;
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

    // ==== 去除快捷方式小箭头(非常用,折叠在「更多工具」子菜单) ====

    // 切换要写 HKLM:自我提权拉一次性 --arrow 实例,UAC 弹一次(这是本工具唯一
    // 需要管理员的瞬间,常驻与其它功能照旧免管理员)。
    // 桌面生效需重启资源管理器:常驻 Explorer 把「29=箭头」映射缓存在进程里,
    // F5/图标缓存通知全刷不动(26300 实测)——弹问句代办,并快照还原已开文件夹。
    void OnToggleArrow(object sender, EventArgs e) {
        bool want = !miArrow.Checked;
        bool ok = false;
        try {
            ok = ShortcutArrowCleaner.SetEnabled(want);
        } catch (Exception ex) {
            AppLog.Write("小箭头切换异常: " + ex.Message);
        }
        miArrow.Checked = ShortcutArrowCleaner.IsEnabled(); // 以注册表实况为准(同开机自启)
        if (!ok) {
            tray.BalloonTipTitle = "去除快捷方式小箭头";
            tray.BalloonTipText = "需要管理员权限(写 HKLM 注册表),UAC 被取消或未确认,本次未修改。";
            tray.ShowBalloonTip(2500);
            return;
        }
        if (DialogResult.Yes == MessageBox.Show(
                "已写入注册表。\n\n桌面上的生效需要重启资源管理器(任务栏与桌面会闪几秒),"
                + "已打开的文件夹窗口会自动还原成标签页。\n\n现在重启吗?\n(选「否」:下次开机后自然生效)",
                "去除快捷方式小箭头", MessageBoxButtons.YesNo, MessageBoxIcon.Question)) {
            System.Threading.Tasks.Task.Run((Action)delegate {
                try {
                    ShortcutArrowCleaner.RestartExplorerAndRestoreFolders();
                } catch (Exception ex) {
                    AppLog.Write("小箭头:资源管理器重启失败: " + ex.Message);
                }
            });
        }
        tray.BalloonTipTitle = "去除快捷方式小箭头";
        tray.BalloonTipText = miArrow.Checked
            ? "已去除。桌面在资源管理器重启后生效(刚才询问过;未重启则下次开机生效)。"
            : "已恢复系统默认小箭头。桌面在资源管理器重启/下次开机后回到默认。";
        tray.ShowBalloonTip(2500);
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

    void OnSessionEnding(object sender, SessionEndingEventArgs e) {
        // SystemEvents 回调跑在非 UI 线程:不碰菜单/图标(跨线程 WinForms 滥用),也不调
        // SetThreadExecutionState——它按线程记账,这里清的是本回调线程的空请求;进程将亡,
        // UI 线程挂的请求随进程消失。真正要还的只有电源护栏(与线程无关)。
        try {
            PowerHibernateGuard.Disengage();
            AppLog.Write("会话结束(注销/关机),护栏已恢复");
        } catch {
        }
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
        TurnOff();
        AppLog.Write("退出");
        remapper.Dispose();
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
