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
//     ✔资源管理器单窗口合并(新开的资源管理器自动并入既有窗口成标签页,Win11)/ 退出;
//   - 唤醒请求只挂在本进程(SetThreadExecutionState),退出/被杀/注销系统自动撤销,不改电源计划;
//   - Mutex 单实例:重复启动直接退出,不多开图标。
// 注意:C# 5 语法(csc 4.0.30319 不支持 C#6+),不要用字符串插值 $""、?. 等新语法。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

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

internal sealed class TrayContext : ApplicationContext {

    readonly NotifyIcon tray;
    readonly ToolStripMenuItem miState;
    readonly ToolStripMenuItem miOn;
    readonly ToolStripMenuItem miDisplay;
    readonly ToolStripMenuItem miExplorer;
    readonly System.Windows.Forms.Timer timer;
    readonly Icon iconOn;
    readonly Icon iconOnDisplay;
    readonly Icon iconOff;
    readonly ExplorerTabMerger merger;   // 资源管理器单窗口合并(默认开)
    DateTime onSince = DateTime.Now;

    public TrayContext() {
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
        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += OnExit;
        menu.Items.Add(miState);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miOn);
        menu.Items.Add(miDisplay);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExplorer);
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

        miOn.Checked = true; // 启动即开启
        RefreshState();
        tray.BalloonTipTitle = "防待机已开启";
        tray.BalloonTipText = "资源管理器合并已启动(新窗口并入标签)。左键图标:开/关防待机;右键菜单更多。";
        tray.ShowBalloonTip(2500);
    }

    void OnToggleOn(object sender, EventArgs e) {
        miOn.Checked = !miOn.Checked;
        if (miOn.Checked) onSince = DateTime.Now;
        else miDisplay.Checked = false; // 总开关关掉,常亮自然失效
        RefreshState();
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
