// keep_awake_tray.cs — 防待机托盘常驻工具(NotifyIcon + SetThreadExecutionState)+ 资源管理器单窗口合并
//
// 构建:python build_keep_awake.py 一键完成(生成 ico → csc 编译 → 装机 → 建桌面快捷方式),等价命令:
//   csc /nologo /target:winexe /codepage:65001 /optimize+ /win32icon:keep_awake.ico
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll
//       /r:UIAutomationClient.dll /r:UIAutomationTypes.dll  (仅 GAC 有,构建脚本负责解析路径)
//       /out:KeepAwake.exe keep_awake_tray.cs explorer_tab_merge.cs key_remap.cs shortcut_arrow.cs audio_mute.cs
//
// 行为:
//   - 启动即开启「系统防待机」(屏幕允许自动关),托盘弹气泡提示;
//   - 左键单击托盘图标 = 开/关切换;图标三态:绿=防待机,蓝=防待机+屏幕常亮,灰=关;
//   - 右键菜单:✔防待机开启 / ✔屏幕常亮(勾上自动连防待机一起开) /
//     ✔资源管理器单窗口合并(新开的资源管理器自动并入既有窗口成标签页,Win11;
//       开关选择持久化 HKCU KeepAwakePrefs,关过一次就记住)/
//     键映射▸✔F2→Ctrl+W ✔Win(右)→Ctrl(右)(低级键盘钩子,接替 PowerToys
//       Keyboard Manager;两条映射各自独立开关,选择持久化 KeepAwakePrefs
//       v1.4.13。Win(右)含三件套识别:用户
//       无线键盘把右侧键合成为 Win左+Shift左+F23(实测),钩子按"0x5B 暂扣
//       25ms 内有无伴随键"区分它与真左 Win——右键变 Ctrl、左 Win 原义保留;
//       持键状态 keyup 丢失有 1.5s 零事件看门狗自愈 v1.4.15,详见 key_remap.cs) /
//     ✔开机自启(HKCU Run 键,免管理员,exe 挪窝自愈)/
//     ✔启动时静音(每次进程真正跑起来——手动双击或开机自启——都把默认播放设备
//       SetMute 静音:幂等"设置"非切换、不改音量值,取消静音即恢复原音量;
//       开关持久化 HKCU Software\KeepAwakePrefs 默认开,详见 audio_mute.cs) /
//     更多工具▸✔去除快捷方式小箭头(非常用功能折叠进子菜单;切换时写 HKLM,
//       自我提权拉一次性 --arrow 实例,UAC 弹一次,详见 shortcut_arrow.cs) /
//     ▸键盘按键失灵…修复(v1.4.14:间歇性单键失灵的一键保守复位——重枚举键盘设备
//       + 清 Scancode Map/粘滞键/筛选键,自我提权拉 --keyboard-reset 实例,
//       详见 keyboard_reset.cs) / 退出;
//   - 唤醒请求只挂在本进程(SetThreadExecutionState),退出/被杀/注销系统自动撤销;
//     防待机开启期间另把「在此时间后休眠」的交流侧临时清零(见 PowerHibernateGuard),
//     关闭/退出恢复,异常退出下次启动自愈——这是唯一一处系统级修改;
//   - Mutex 单实例:重复启动直接退出,不多开图标。
// 注意:C# 5 语法(csc 4.0.30319 不支持 C#6+),不要用字符串插值 $""、?. 等新语法。
// DPI:app.manifest 声明 dpiAware=true 并由构建脚本 /win32manifest 嵌入(缺了它 exe 裸奔)。
//   高分屏(本机 175%)上进程若启动时不感知、运行中被 UIA 之类库"中途翻"成 DPI aware,
//   托盘右键菜单会按 96 DPI 布局直接按物理像素渲染——字体缩成 9pt*(96/168)(v1.3.3 修)。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
        // 「键盘按键失灵」一键修复的一次性提权实例(菜单经 UAC 拉起):重枚举键盘设备
        // + 清异常开关,写完结果文件即退(同 --arrow 的套路)
        if (args.Length > 0 && args[0] == "--keyboard-reset") {
            return KeyboardReset.ApplyFromCli();
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
    // 串行化:启动期 UI 线程(开关初值)与钩子线程(安装回执)同毫秒并发 Append,
    // File.AppendAllText 撞文件占用抛异常被吞=整行丢失(v1.4.13 实证丢过"Win 开启"行)
    static readonly object logLock = new object();
    public static void Write(string msg) {
        lock (logLock) {
            try {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 524288) fi.Delete();
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine);
            } catch {
            }
        }
    }
}

// 「资源管理器单窗口合并」开关持久化(v1.4.12):与启动静音同键——刻意独立于电源护栏的
// Software\KeepAwake 键(那处 Restore 完会把整个键删净,混放会被顺手清掉)。未写过 =
// 默认开(保持既有行为);用户关过一次就记住,重启不再被强行拉回开。
static class ExplorerMergePref {
    const string RegPath = "Software\\KeepAwakePrefs";
    const string ValueName = "ExplorerMergeEnabled";

    public static bool Get() {
        try {
            using (var key = Registry.CurrentUser.OpenSubKey(RegPath)) {
                if (key == null) return true;                    // 未写过 = 默认开
                return Convert.ToInt32(key.GetValue(ValueName, 1)) != 0;
            }
        } catch {
            return true;
        }
    }

    public static void Set(bool on) {
        try {
            using (var key = Registry.CurrentUser.CreateSubKey(RegPath)) {
                key.SetValue(ValueName, on ? 1 : 0, RegistryValueKind.DWord);
            }
        } catch (Exception ex) {
            AppLog.Write("资源管理器合并开关写入失败: " + ex.Message);
        }
    }
}

// 键映射两条开关的持久化(v1.4.13):与合并/静音同键,理由同上(独立于会被电源护栏
// Restore 删净的 Software\KeepAwake 键)。未写过 = 默认开,既有行为不变。
static class KeymapPref {
    const string RegPath = "Software\\KeepAwakePrefs";
    public const string F2Name = "KeymapF2Enabled";
    public const string WinName = "KeymapWinEnabled";

    public static bool Get(string name) {
        try {
            using (var key = Registry.CurrentUser.OpenSubKey(RegPath)) {
                if (key == null) return true;                    // 未写过 = 默认开
                return Convert.ToInt32(key.GetValue(name, 1)) != 0;
            }
        } catch {
            return true;
        }
    }

    public static void Set(string name, bool on) {
        try {
            using (var key = Registry.CurrentUser.CreateSubKey(RegPath)) {
                key.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
            }
        } catch (Exception ex) {
            AppLog.Write("键映射开关写入失败(" + name + "): " + ex.Message);
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
        Guid active = ActiveScheme();   // 用户此刻正在用的方案:恢复全程不得替他切换选择
        Guid scheme = active;           // 要写回原值的方案(备份时的那个,见下)
        using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true)) {
            uint ac = Convert.ToUInt32(key.GetValue("HibernateIdleAcBackup", 0));
            object dcRaw = key.GetValue("HibernateIdleDcBackup");
            // 优先写回备份时的方案(已删的方案退回当前活动方案;v1.4.0 之前的旧备份没有此键,同样退回)
            object schemeRaw = key.GetValue("HibernateIdleSchemeBackup");
            if (schemeRaw is string) {
                Guid stored = new Guid((string)schemeRaw);
                if (stored != Guid.Empty && SchemeExists(stored)) scheme = stored;
            }
            WriteVal(true, scheme, ac);
            if (dcRaw != null) {
                // v1.3.0 曾把直流也归零:迁移场景把直流恢复一次并清掉旧键
                WriteVal(false, scheme, Convert.ToUInt32(dcRaw));
                key.DeleteValue("HibernateIdleDcBackup");
            }
            // GuardPending 必须在【全部写入成功之后】才撤:先撤的话,上面任何一步抛异常,
            // 下次启动 Pending()=false 不再自愈,备份从此死在注册表里,方案永久停在「从不」
            key.SetValue("GuardPending", 0, RegistryValueKind.DWord);
        }
        // 重新应用【用户当前】的方案(刷新立即生效)。⚠不能 reapply 写回的 scheme:
        // 防待机期间用户切了方案的话,那会把用户硬切回旧方案——他只是开了个防待机而已
        Guid s = active;
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
    readonly ToolStripMenuItem miKeymapWin; // └ Win(右) → Ctrl(右)
    readonly ToolStripMenuItem miAutoStart;
    readonly ToolStripMenuItem miMuteStartup;  // 启动时静音(默认开,持久化 HKCU)
    readonly ToolStripMenuItem miAdblock;      // 公众号文章去广告(v1.5.0,KeepAwake 托管 #196 守卫层)
    readonly ToolStripMenuItem miMore;     // 更多工具:非常用功能折叠在此,主菜单保持短
    readonly ToolStripMenuItem miArrow;
    readonly ToolStripMenuItem miKeyboardReset;  // 键盘按键失灵一键修复(v1.4.14)
    readonly System.Windows.Forms.Timer timer;
    readonly Icon iconOn;
    readonly Icon iconOnDisplay;
    readonly Icon iconOff;
    readonly ExplorerTabMerger merger;   // 资源管理器单窗口合并(默认开)
    readonly KeyRemapper remapper;       // 全局键映射(两条,默认开)
    readonly WechatAdblockSupervisor adblock;  // 公众号去广告守护(mitmdump 保活+模板补丁+降级中继)
    DateTime onSince = DateTime.Now;

    public TrayContext() {
        AppLog.Write("启动(v1.5.1)");
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
        miKeymapWin = new ToolStripMenuItem("Win(右) → Ctrl(右)");
        miKeymapWin.Click += OnToggleKeymapWin;
        miKeymap = new ToolStripMenuItem("键映射");
        miKeymap.DropDownItems.Add(miKeymapF2);
        miKeymap.DropDownItems.Add(miKeymapWin);
        miAutoStart = new ToolStripMenuItem("开机自启");
        miAutoStart.Click += OnToggleAutoStart;
        miMuteStartup = new ToolStripMenuItem("启动时静音");
        miMuteStartup.Click += OnToggleMuteStartup;
        miAdblock = new ToolStripMenuItem("公众号文章去广告(微信)");
        miAdblock.Click += OnToggleAdblock;
        miArrow = new ToolStripMenuItem("去除快捷方式小箭头");
        miArrow.Click += OnToggleArrow;
        miKeyboardReset = new ToolStripMenuItem("键盘按键失灵…修复");
        miKeyboardReset.Click += OnKeyboardReset;
        miMore = new ToolStripMenuItem("更多工具");
        miMore.DropDownItems.Add(miArrow);
        miMore.DropDownItems.Add(miKeyboardReset);
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
        menu.Items.Add(miMuteStartup);
        menu.Items.Add(miAdblock);
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
        // v1.4.11 Win11 闸:Win10/无标签页系统整功能停用(那边键盘兜底的 Ctrl+L 会把
        // 既有窗口当前视图导航走,审查 C1);v1.4.12 起开关选择持久化——关过一次就记住,
        // 重启不再被强行拉回开(未写过 = 默认开,保持既有行为)
        bool mergeSupported = ExplorerTabMerger.OsSupportsTabs();
        bool mergeWanted = ExplorerMergePref.Get();
        miExplorer.Checked = mergeSupported && mergeWanted;
        merger.Enabled = miExplorer.Checked;
        if (!miExplorer.Checked) {
            AppLog.Write(mergeSupported
                ? "资源管理器合并:按记住的开关关闭"
                : "资源管理器合并:系统无标签页能力(Win10?),已停用");
        }

        remapper = new KeyRemapper();
        // v1.4.13:两条映射的开关选择持久化(与合并开关同款;未写过 = 默认开,既有行为不变)。
        // 关态的日志在托盘侧补:KeyRemapper 的 setter 对「等于当前值」早退不落日志,
        // 不补的话「记住的关」在时间线上无痕,排障无从对起
        miKeymapF2.Checked = KeymapPref.Get(KeymapPref.F2Name);   // 默认开:接替 PowerToys 键映射
        remapper.F2Enabled = miKeymapF2.Checked;
        miKeymapWin.Checked = KeymapPref.Get(KeymapPref.WinName); // 默认开:右 Win 变右 Ctrl(左 Win 保留)
        remapper.WinEnabled = miKeymapWin.Checked;
        if (!miKeymapF2.Checked) AppLog.Write("键映射 F2→Ctrl+W: 按记住的开关关闭");
        if (!miKeymapWin.Checked) AppLog.Write("键映射 Win(右)→Ctrl(右): 按记住的开关关闭");

        miAutoStart.Checked = AutoStartEnabled();
        if (miAutoStart.Checked) SetAutoStart(true); // 路径自愈:exe 挪窝后指向当前实例

        // 公众号文章去广告(v1.5.0):三层防线的守卫层由本进程托管(知识库 #196)。
        // 后端(mitmproxy venv+CA+payload.htm)缺失则功能整体停用置灰;开关关闭 =
        // 杀 mitmdump 走纯转发降级(文章可用、广告回来),不动 hosts/CA。
        bool adblockBackend = WechatAdblockSupervisor.BackendPresent();
        bool adblockWanted = WechatAdblockPref.Get();
        miAdblock.Checked = adblockBackend && adblockWanted;
        if (!adblockBackend) {
            miAdblock.Enabled = false;
            AppLog.Write("公众号去广告: 后端缺失(" + WechatAdblockSupervisor.BackendDir()
                         + "),功能停用");
        }
        else if (!adblockWanted) {
            AppLog.Write("公众号去广告: 按记住的开关关闭(纯转发降级)");
        }
        adblock = new WechatAdblockSupervisor(miAdblock.Checked);

        // 启动时静音:手动双击/开机自启走的是同一条路,都在这里;Mutex 挡掉的
        // 重复启动到不了这里,不会二次静音。开关关着就完全不碰音频。
        miMuteStartup.Checked = MuteAtStartupPref.Get();
        if (miMuteStartup.Checked) StartupMute.Apply();

        miArrow.Checked = ShortcutArrowCleaner.IsEnabled(); // 小箭头状态以 HKLM 实况为准

        TurnOn(); // 启动即开启
        tray.BalloonTipTitle = "防待机已开启";
        string muteNote = miMuteStartup.Checked ? "已按「启动时静音」把系统音量静音,要出声按音量键。\n" : "";
        tray.BalloonTipText = muteNote + "资源管理器合并、键映射(F2→Ctrl+W、Win→Ctrl)已启动。左键图标:开/关防待机;右键菜单更多。";
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
        if (!miExplorer.Checked && !ExplorerTabMerger.OsSupportsTabs()) {
            // Win10 上尝试开启:说明原因而不是静默无效(v1.4.11;此前 README 写「开关无效
            // 但不报错」,实际那边会走键盘兜底搞破坏,现在从入口拦住)
            tray.BalloonTipTitle = "资源管理器单窗口合并";
            tray.BalloonTipText = "此功能依赖 Windows 11 资源管理器原生标签页,当前系统不支持,无法开启。";
            tray.ShowBalloonTip(2500);
            return;
        }
        miExplorer.Checked = !miExplorer.Checked;
        merger.Enabled = miExplorer.Checked;
        ExplorerMergePref.Set(miExplorer.Checked);   // v1.4.12:记住选择,重启不再重置
    }

    void OnToggleKeymapF2(object sender, EventArgs e) {
        miKeymapF2.Checked = !miKeymapF2.Checked;
        remapper.F2Enabled = miKeymapF2.Checked;
        KeymapPref.Set(KeymapPref.F2Name, miKeymapF2.Checked);   // v1.4.13:记住选择,重启不再重置
    }

    void OnToggleKeymapWin(object sender, EventArgs e) {
        miKeymapWin.Checked = !miKeymapWin.Checked;
        remapper.WinEnabled = miKeymapWin.Checked;
        KeymapPref.Set(KeymapPref.WinName, miKeymapWin.Checked); // v1.4.13:同上
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
    // SetEnabled 里 WaitForExit(120000) 同步等 UAC 结果:必须跑在后台线程——UI 线程
    // 是 WinEvent 合并钩子的宿主,UAC 弹窗没被点期间它一挂就是两分钟,合并窗口全错过;
    // 提权完成后经 UI 线程的同步上下文 Post 回去再碰菜单/弹问句(跨线程 WinForms 滥用)。
    // 桌面生效需重启资源管理器:常驻 Explorer 把「29=箭头」映射缓存在进程里,
    // F5/图标缓存通知全刷不动(26300 实测)——弹问句代办,并快照还原已开文件夹。
    void OnToggleArrow(object sender, EventArgs e) {
        bool want = !miArrow.Checked;
        miArrow.Enabled = false;   // 防重入:后台等待期间再点一次会拉起两个提权实例
        var ui = System.Threading.SynchronizationContext.Current;   // ApplicationContext 非 Control 无 BeginInvoke,靠同步上下文回 UI 线程
        System.Threading.Tasks.Task.Run((Action)delegate {
            bool ok = false;
            try {
                ok = ShortcutArrowCleaner.SetEnabled(want);
            } catch (Exception ex) {
                AppLog.Write("小箭头切换异常: " + ex.Message);
            }
            ui.Post(delegate(object s) {
                miArrow.Enabled = true;
                miArrow.Checked = ShortcutArrowCleaner.IsEnabled(); // 以注册表实况为准(同开机自启)
                if (!ok) {
                    tray.BalloonTipTitle = "去除快捷方式小箭头";
                    tray.BalloonTipText = "需要管理员权限(写 HKLM 注册表),UAC 被取消或未确认,本次未修改。";
                    tray.ShowBalloonTip(2500);
                    return;
                }
                // v1.4.11 问句按实况组文案:①合并器关着时还原是一堆独立窗口,不能再承诺
                // 「还原成标签页」(merge-side-3);②检测到进行中的文件复制/移动先警示
                // ——taskkill /f 会连着 explorer.exe 里的进度窗一起中断(审查 C5)
                string restoreNote = miExplorer.Checked
                    ? "已打开的文件夹窗口会自动还原成标签页。"
                    : "已打开的文件夹窗口会逐一重新打开(合并功能当前未开启,将是独立窗口)。";
                string opWarn = ShortcutArrowCleaner.HasRunningFileOperations()
                    ? "\n⚠ 检测到正在进行的文件复制/移动操作——重启资源管理器会把它中断,建议先等它完成。"
                    : "";
                if (DialogResult.Yes == MessageBox.Show(
                        "已写入注册表。\n\n桌面上的生效需要重启资源管理器(任务栏与桌面会闪几秒),"
                        + restoreNote + opWarn + "\n\n现在重启吗?\n(选「否」:下次开机后自然生效)",
                        "去除快捷方式小箭头", MessageBoxButtons.YesNo, MessageBoxIcon.Question)) {
                    System.Threading.Tasks.Task.Run((Action)delegate {
                        try {
                            // v1.4.11:先限时快照,失败/超时(explorer COM 无征兆挂起,合并模块
                            // 2026-10-04 实测)时回 UI 征询是否仍重启——此前裸 COM 枚举挂死时
                            // taskkill 照杀,已开标签全丢且问句承诺的「自动还原」失实(merge-side-2)
                            List<string> folders = ShortcutArrowCleaner.SnapshotOpenFolders(3000);
                            if (folders == null) {
                                AppLog.Write("小箭头:文件夹快照失败/超时,回 UI 征询是否仍重启");
                                var decided = new AutoResetEvent(false);
                                bool go = false;
                                ui.Post(delegate(object s2) {
                                    go = DialogResult.Yes == MessageBox.Show(
                                        "已打开文件夹窗口的快照获取失败(资源管理器可能无响应),\n"
                                        + "重启后这些窗口将无法自动还原。\n\n仍要现在重启吗?",
                                        "去除快捷方式小箭头", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                                    decided.Set();
                                }, null);
                                if (!decided.WaitOne(120000) || !go) {
                                    AppLog.Write("小箭头:快照失败后放弃重启(用户否/超时无应答)");
                                    return;
                                }
                                folders = new List<string>();
                            }
                            ShortcutArrowCleaner.RestartExplorerAndRestoreFolders(folders);
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
            }, null);
        });
    }

    // ==== 键盘按键失灵一键修复(v1.4.14) ====
    // 为什么是菜单项而不是静默守护:故障是间歇性的、没有可查的持久痕迹(2026-10-16
    // 排查实录),无从自动判定"现在是不是坏了",也不该在正常时反复重启键盘设备。
    // 由用户判断"此刻确实失灵"再点一次,是我们能在无根因前提下做的最有效复位。
    // 执行要跑在后台线程:UAC 无人点击期间 WaitForExit 会挂住,UI 线程一挂就是两
    // 分钟(同 OnToggleArrow 的教训);完成后经同步上下文回 UI 弹结果窗。
    void OnKeyboardReset(object sender, EventArgs e) {
        miKeyboardReset.Enabled = false;   // 防重入
        var ui = System.Threading.SynchronizationContext.Current;
        System.Threading.Tasks.Task.Run((Action)delegate {
            KeyboardResetResult r = null;
            try {
                r = KeyboardReset.Run();
            } catch (Exception ex) {
                AppLog.Write("键盘修复入口异常: " + ex.Message);
            }
            var res = r;
            ui.Post(delegate(object s2) {
                miKeyboardReset.Enabled = true;
                ShowKeyboardResetReport(res);
            }, null);
        });
    }

    // 把修复结果如实摆出来——成功就确认,失败/可疑就明说,不粉饰。
    void ShowKeyboardResetReport(KeyboardResetResult r) {
        if (r == null) {
            MessageBox.Show("修复过程异常中止,未能取得结果。\n详细信息见 %TEMP%\\KeepAwake_app.log。",
                "键盘按键失灵修复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (r.NeedAdmin) {
            MessageBox.Show("本项需要管理员权限来重新枚举键盘设备,UAC 被取消或未确认,本次未做任何修改。\n\n"
                + "请重新点击并确认 UAC 弹窗。", "键盘按键失灵修复",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sb = new StringBuilder();
        if (r.Restarted > 0) {
            sb.Append("已逐个重新枚举 ").Append(r.Restarted).Append(" 个键盘设备。\n");
        }
        if (r.ScanDevicesDone) {
            sb.Append("已完成一次全局设备重新枚举(含内置键盘)。\n");
        }
        if (r.Restarted == 0 && !r.ScanDevicesDone) {
            sb.Append("没有成功重新枚举任何键盘设备。\n");
        }
        if (r.ScanCodeMapCleared) {
            sb.Append("已清除全局 Scancode Map(原值已备份,可还原)。\n");
        }
        foreach (var f in r.FlagsReset) {
            sb.Append("已恢复 ").Append(f).Append(" 默认值。\n");
        }
        if (r.SkippedDisconnected > 0) {
            sb.Append("(跳过 ").Append(r.SkippedDisconnected).Append(" 个未连接的键盘设备)");
        }
        if (sb.Length == 0) sb.Append("未做任何修改。\n");

        // 有失败项 → 警示框,把每条原因摆出来,别让用户以为修好了
        if (r.Failures.Count > 0) {
            var warn = new StringBuilder(sb.ToString()).Append("\n以下动作未成功:\n");
            foreach (var f in r.Failures) warn.Append("· ").Append(f).Append('\n');
            if (r.Warnings.Count > 0) {
                warn.Append("\n另有一项【未改动、仅提示】:\n");
                foreach (var w in r.Warnings) warn.Append("· ").Append(w).Append('\n');
            }
            warn.Append("\n若按键仍失灵,可尝试重启电脑;细节见 %TEMP%\\KeepAwake_app.log。");
            MessageBox.Show(warn.ToString(), "键盘按键失灵修复",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 全成功:若有可疑项一并提示(不阻断),否则简单确认
        string ok = sb.ToString() + "\n请试一下刚才失灵的按键。若仍无效,可重启电脑再观察。";
        if (r.Warnings.Count > 0) {
            ok += "\n\n另有一项【未改动、仅提示】:\n";
            foreach (var w in r.Warnings) ok += "· " + w + "\n";
        }
        MessageBox.Show(ok, "键盘按键失灵修复", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    void OnToggleMuteStartup(object sender, EventArgs e) {
        // 只翻偏好(下次启动生效与否),不立刻动音频——条目名就是"启动时"静音,
        // 立即静音交给音量键,与「开机自启」开关同款纯偏好语义
        miMuteStartup.Checked = !miMuteStartup.Checked;
        MuteAtStartupPref.Set(miMuteStartup.Checked);
    }

    void OnToggleAdblock(object sender, EventArgs e) {
        // 开 = mitmdump 反代+模板补丁;关 = 杀 mitmdump 走纯转发降级(文章可用、
        // 广告回来)。hosts/CA 属装机层不动,重开即恢复
        miAdblock.Checked = !miAdblock.Checked;
        WechatAdblockPref.Set(miAdblock.Checked);
        adblock.Enabled = miAdblock.Checked;
        AppLog.Write(miAdblock.Checked
            ? "公众号去广告: 开启(mitmdump 反代+模板补丁)"
            : "公众号去广告: 关闭(纯转发降级)");
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
        adblock.Dispose();   // 杀 mitmdump 子进程+停中继;下次启动守护随托盘回归
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
