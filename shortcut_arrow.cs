// shortcut_arrow.cs — 去除桌面快捷方式左下角小箭头(HKLM Shell Icons\29 → 全透明空白图标)
//
// 原理:快捷方式小箭头是 shell 图标索引 29(shell32 内置箭头),注册表
//   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons
//   的字符串值 "29" 可整体覆盖该图标 → 指到一张全透明 .ico,箭头即消失。
//   这是 Winaero 等工具的标准做法。HKCU 同名键实测(Win11 26300,tests/probe_arrow.py)
//   完全不被认账,别抄那些走 HKCU 的教程。
//   不动 lnkfile\IsShortcut(那个做法会连带弄坏固定到任务栏/开始屏幕等功能)。
// 权限:写 HKLM 要管理员。托盘进程免管理员常驻,切换时自我提权拉起一次性
//   CLI 实例(--arrow on|off)干完写注册表+刷图标即退(与 --restore-power 同款套路)。
// 图标:不依赖系统 DLL 里"恰好透明"的图标序号(教程里的 imageres.dll,197 之类随
//   版本漂移,选错渲染成黑方块),运行时手写一张 16×16 全透明 ICO 到
//   %LOCALAPPDATA%\KeepAwake\blank.ico。⚠暗坑(26300 实测):32bpp ICO 的 alpha
//   若【全 0】,GDI 判"这图没有 alpha 通道",按 RGB 渲染成不透明黑方块——AND 掩码
//   全 1 也救不了(这条渲染路径不读掩码;PIL 默认写的 PNG-entry ICO 同样黑)。
//   正解:让一个像素 alpha=1 触发 alpha 合成,其余全 0 → 整图透明。
// 可逆:开启前若发现 "29" 已被别的工具写过,原值备份到独立的 HKCU 配置键,
//   关闭时还原——不顺手清掉别人的配置。(备份不放 Software\KeepAwake 下面:
//   电源护栏恢复完会把整个键删净,同键塞别的东西会让它的 DeleteSubKey 抛异常。)
// 生效:分两层。注册表一写入,【新进程】立即按新图标合成(SHGetFileInfo/新开窗口
//   都是新状态);但【桌面】由常驻多年的 Explorer 进程绘制,"29=箭头"映射与合成
//   结果都缓存在该进程内——F5、ie4uinit、WM_SETTINGCHANGE、SHChangeNotify(
//   ASSOCCHANGED) 实测全刷不动(26300 实证,2026 年各家教程也一律要求重启资源
//   管理器)。故切换成功后托盘弹问句代办重启:先快照已开文件夹 → taskkill
//   explorer → 重启 → 原样重开,单窗口合并器自动并回标签页;拒绝则下次开机生效。
// 注意:C# 5 语法(系统 csc 不认 C#6+),不要用字符串插值 $""、?. 等。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeepAwake {

static class ShortcutArrowCleaner {

    const string ShellIconsPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Icons";
    const string ValueName = "29";
    const string CfgPath = "Software\\KeepAwakeShortcutArrow";  // 独立键,勿并入 Software\KeepAwake(见文件头)
    const string BackupName = "ShellIcon29Backup";

    // ==== 状态 ====

    /// <summary>"29" 已被覆盖(小箭头已去除)吗?菜单勾选以此实况为准。</summary>
    public static bool IsEnabled() {
        try {
            using (var key = Registry.LocalMachine.OpenSubKey(ShellIconsPath)) {
                if (key == null) return false;
                return !string.IsNullOrEmpty(key.GetValue(ValueName) as string);
            }
        } catch {
            return false;
        }
    }

    // ==== 托盘入口(免管理员进程调这里) ====

    /// <summary>切换开关。免管理员时自我提权拉一次性 CLI 实例代写;
    /// 返回 false = UAC 被取消/提权失败/写入失败,系统未变更。</summary>
    public static bool SetEnabled(bool on) {
        if (IsElevated()) {                    // 托盘进程本身已是管理员:直接干
            Apply(on);
            return true;
        }
        try {
            var psi = new ProcessStartInfo {
                FileName = Application.ExecutablePath,
                Arguments = "--arrow " + (on ? "on" : "off"),
                Verb = "runas",                // UAC 弹一次,这正是要管理员的唯一一步
                UseShellExecute = true
            };
            using (var p = Process.Start(psi)) {
                if (p == null) return false;
                if (!p.WaitForExit(120000)) return false;   // UAC 弹窗两分钟没人点,放弃
                return p.ExitCode == 0;
            }
        } catch (System.ComponentModel.Win32Exception) {
            return false;                      // UAC 点了「否」(错误 1223),用户取消,不算故障
        } catch (Exception) {
            return false;
        }
    }

    // ==== CLI 入口(提权实例:Program.Main 收到 --arrow on|off 后转这里) ====

    /// <summary>返回进程退出码:0=成功,1=失败,2=用法不对。</summary>
    public static int ApplyFromCli(string onOff) {
        try {
            if (onOff != "on" && onOff != "off") return 2;
            if (!IsElevated()) {
                AppLog.Write("--arrow 需要管理员:请从托盘菜单切换(会弹一次 UAC),裸跑不生效");
                return 1;
            }
            Apply(onOff == "on");
            return 0;
        } catch (Exception ex) {
            AppLog.Write("--arrow 失败: " + ex.Message);
            return 1;
        }
    }

    // ==== 干活 ====

    static void Apply(bool on) {
        EnsureBlankIco();
        using (var key = Registry.LocalMachine.CreateSubKey(ShellIconsPath)) {
            string prev = key.GetValue(ValueName) as string;
            if (on) {
                if (!string.IsNullOrEmpty(prev) && prev != BlankIcoPath()) {
                    using (var cfg = Registry.CurrentUser.CreateSubKey(CfgPath))
                        cfg.SetValue(BackupName, prev, RegistryValueKind.String);
                }
                key.SetValue(ValueName, BlankIcoPath(), RegistryValueKind.String);
                AppLog.Write("小箭头:已去除(29 → " + BlankIcoPath() + ")");
            } else {
                string backup = null;
                using (var cfg = Registry.CurrentUser.OpenSubKey(CfgPath)) {
                    if (cfg != null) backup = cfg.GetValue(BackupName) as string;
                }
                if (!string.IsNullOrEmpty(backup)) {
                    key.SetValue(ValueName, backup, RegistryValueKind.String);   // 还原别的工具的原值
                    using (var cfg = Registry.CurrentUser.CreateSubKey(CfgPath))
                        cfg.DeleteValue(BackupName, false);
                    AppLog.Write("小箭头:已还原为覆盖前的原值(别的工具写的 29 交还)");
                } else {
                    key.DeleteValue(ValueName, false);
                    AppLog.Write("小箭头:已恢复系统默认(29 删净)");
                }
            }
        }
        // Shell Icons 键里没别的值就整键删掉,不留空壳
        try {
            using (var check = Registry.LocalMachine.OpenSubKey(ShellIconsPath)) {
                if (check != null && check.ValueCount == 0 && check.SubKeyCount == 0)
                    Registry.LocalMachine.DeleteSubKey(ShellIconsPath, false);
            }
        } catch {
        }
        RefreshIcons();
    }

    // ==== 全透明空白图标 ====

    public static string BlankIcoPath() {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeepAwake", "blank.ico");
    }

    // 字节手写,构造与 tests/probe_arrow.py make_blank_ico() 一一对应,改一处改两处:
    // ICO 头 6B + 目录项 16B + BITMAPINFOHEADER 40B + 像素 16*16*4B(首像素 alpha=1,
    // 其余全 0)+ AND 掩码 16 行*4B(全 0xFF;此渲染路径实测不读掩码,留着求个保险)。
    static byte[] BlankIcoBytes() {
        const int pixel = 16 * 16 * 4;
        const int mask = 16 * 4;                       // AND 掩码每行 4 字节(16px→2B 补齐到 4B)
        int bmpSize = 40 + pixel + mask;
        byte[] ico = new byte[6 + 16 + bmpSize];
        ico[2] = 1;                                    // type = icon
        ico[4] = 1;                                    // 图像数量 = 1
        ico[6] = 16;                                   // 宽
        ico[7] = 16;                                   // 高
        ico[10] = 1;                                   // planes
        ico[12] = 32;                                  // bpp
        ico[14] = (byte)(bmpSize & 0xff);              // 数据长度(低字节)
        ico[15] = (byte)((bmpSize >> 8) & 0xff);       // 数据长度(高字节)
        ico[18] = 22;                                  // 数据偏移 = 6 + 16
        ico[22] = 40;                                  // biSize
        ico[26] = 16;                                  // biWidth
        ico[30] = 32;                                  // biHeight = XOR + AND 双倍高度
        ico[34] = 1;                                   // biPlanes
        ico[36] = 32;                                  // biBitCount(偏移 36,别数错)
        ico[62 + 3] = 1;                               // 首像素 alpha=1:全 0 会被判无 alpha 渲染成黑方块(见文件头)
        int maskAt = 6 + 16 + 40 + pixel;
        for (int i = 0; i < mask; i++) ico[maskAt + i] = 0xff;
        return ico;
    }

    static void EnsureBlankIco() {
        string path = BlankIcoPath();
        byte[] ico = BlankIcoBytes();
        try {
            if (File.Exists(path) && BytesEqual(File.ReadAllBytes(path), ico)) return;
            // 逐字节比对而非比长度:修掉 alpha 全 0 的旧版坏文件(同是 1150 字节,长度判断治不了)
        } catch {
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, ico);
    }

    static bool BytesEqual(byte[] a, byte[] b) {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>测试钩子:只落空白图标、不碰注册表(tests/probe_arrow.py 用它逐字节
    /// 校验成品 exe 手写的 ICO,免管理员)。</summary>
    public static void WriteBlankIcoTo(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, BlankIcoBytes());
    }

    // ==== 刷新图标 ====

    static void RefreshIcons() {
        // ① SHChangeNotify(SHCNE_ASSOCCHANGED):真正管用的刷新——实测(26300)光靠
        //    ②③ 坏图标会卡在系统图标缓存里刷不掉,这一下才把缓存打穿
        try {
            SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0 /*SHCNF_IDNOWAIT*/,
                IntPtr.Zero, IntPtr.Zero);
        } catch {
        }
        // ② ie4uinit -show:重建桌面图标缓冲(Win10/11 自带,免重启 Explorer)
        try {
            var psi = new ProcessStartInfo("ie4uinit.exe", "-show") {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        } catch {
        }
        // ③ 广播 WM_SETTINGCHANGE("Icons"):让 Explorer 重读 Shell Icons
        try {
            UIntPtr result;
            SendMessageTimeout(new IntPtr(0xffff) /*HWND_BROADCAST*/, 0x001a /*WM_SETTINGCHANGE*/,
                UIntPtr.Zero, "Icons", 2 /*SMTO_ABORTIFHUNG*/, 3000, out result);
        } catch {
        }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // ==== 桌面生效的最后一步:重启资源管理器(托盘弹问句后代办) ====

    /// <summary>重启资源管理器,并把之前打开的文件夹原样重开(合并器会自动并成标签页)。
    /// 为什么必须重启:桌面图标由常驻 Explorer 进程合成,"29=箭头"映射缓存在进程内,
    /// F5/图标缓存通知实测全刷不动(见文件头);每个文件夹间隔重开,给合并器留并入节奏。
    /// 在后台线程跑(约 8~10s),别卡托盘 UI。</summary>
    public static void RestartExplorerAndRestoreFolders() {
        var folders = new List<string>();
        try {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            foreach (dynamic w in shell.Windows()) {
                try {
                    string p = w.Document.Folder.Self.Path;
                    if (!string.IsNullOrEmpty(p)) folders.Add(p);
                } catch {
                }
            }
        } catch (Exception ex) {
            AppLog.Write("小箭头:文件夹快照失败(还原跳过): " + ex.Message);
        }
        AppLog.Write("小箭头:重启资源管理器,快照 " + folders.Count + " 个文件夹");
        try {
            var kill = new ProcessStartInfo("taskkill", "/f /im explorer.exe") {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(kill).WaitForExit(3000);
        } catch {
        }
        Thread.Sleep(800);                          // 等旧进程退干净
        Process.Start("explorer.exe");
        Thread.Sleep(3500);                         // 等桌面/任务栏就绪
        foreach (string p in folders) {
            try {
                Process.Start("explorer.exe", "\"" + p + "\"");
                Thread.Sleep(1200);                 // 合并器逐个并入新开窗口
            } catch {
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

    static bool IsElevated() {
        using (var id = WindowsIdentity.GetCurrent()) {
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

}
