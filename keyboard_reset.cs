// keyboard_reset.cs — 「键盘按键失灵」一键修复(重新枚举键盘设备 + 清异常开关)
//
// 起因(2026-10-16 用户实测,前一轮排障未找到根因):内置键盘的左 Shift 间歇性失效,
// 右 Shift 正常,过一阵自己恢复。全项排查(设备状态/Scancode Map/粘滞键/筛选键/
// 键盘布局/系统事件)都没查出异常,「原样不动过一阵就好了」——典型的驱动栈或 HID
// 枚举层偶发卡住,不留下可查的持久痕迹。此模块不追根因,只做【可逆、无残留】的
// 强制复位:让键盘设备栈整条重新枚举一次,顺带清掉几个会拦截按键的全局开关。
//
// 为什么「重新枚举」是对的复位手段:
//   间歇性单键失效最常见的成因是设备/驱动栈状态卡死(热插拔控制器或 HID 栈没
//   重新收命令)。pnputil /restart-device 会走一遍真正的 disable→enable:
//   卸载并重新启动该实例的驱动、把设备重新挂回驱动栈,状态卡死随之被清掉。
//   本机实测(见下)重启后设备立刻回到「已启动」,Get-PnpDevice 状态 OK。
//
// 做了什么(严格限定在「安全且可逆」的动作):
//   1) 逐个重启【已连接】的键盘设备(pnputil /restart-device,管理员):
//      未连接的设备直接跳过(pnputil 对它们只会报「设备没有连接」——实测)。
//      ⚠PS/2 内置键盘走不通这条——Windows 直接拒(「此操作系统产品不支持此
//      命令」/「设备正在等待系统重新启动」),因为禁它等于让整机失去唯一键盘,
//      内核不允许。这是【系统级限制,非本模块 bug】;
//   1b) 因此再跑一次 pnputil /scan-devices:全系统硬件重新枚举。它不逐个禁用
//      设备,而是让 PnP 管理器把设备栈整体重新实例化——PS/2 内置键盘正是靠这
//      一步被复位(本机实测 /scan-devices 返回成功,键盘全程无中断)。这一步
//      是覆盖内置键盘的关键,单个 /restart-device 只解决 HID 那部分;
//   2) 清除全局 Scancode Map(HKLM\...\Keyboard Layout):非默认的重映射会静默
//      改键位/吞键,用户自己或第三方工具(SharpKeys 之类)写的都可能。⚠本机实测
//      该值当前【不存在】,属正常——没有就不写,绝不多创建一个空值;
//   3) 把粘滞键/筛选键的 Flags 恢复 Windows 默认值(粘滞 510 / 筛选 126):
//      这两个开关会让单键行为诡异(粘滞键下 Shift 要按两次才生效),且用户
//      往往是自己误按了快捷键(Shift 连按五次)打开后忘了。⚠只在【值与默认
//      不同时】才改,默认状态原样不动——避免无谓地写注册表。
//
// 刻意【不做】什么(以及为什么——这是"保守"的含义):
//   - 不碰 Kbfilter 驱动过滤器。本机 UpperFilters = "kbdclass\0Kbfilter"、服务
//     Start=3/ImagePath 为空,看着可疑,但它是一个真实的驱动层:摘掉可能导致
//     联想的键盘增强功能失效,或让笔记本内置键盘彻底不认。属"改动大于收益",
//     只在报告里如实告知用户,由他自己决定。
//   - 不重启资源管理器、不重启电脑、不【禁用】任何设备(禁用内置 PS/2 键盘
//     系统本来就不允许;且万一重枚举失败用户就没有输入手段——本模块所有动作
//     都是"重新枚举"而非"禁用",失败了键盘仍在)。
//   - 不卸载 HID 过滤器、不动电源管理里的设备休眠超时(那是防待机之外的另一件事)。
//
// 权限:重枚举设备要管理员。托盘进程免管理员常驻,照 ShortcutArrowCleaner 的同款
//   套路自我提权拉一次性 --keyboard-reset 实例干完即退(UAC 弹一次)。
//   全部动作失败也不阻断装机——菜单项如实报告每一步结果。
//
// 安全性:只写「用户可自行在注册表里改回默认」的两个 Flags 键,只删一个
//   明确异常才该不存在的 Scancode Map,且删前把原值备份到 HKCU 配置键(删了能还原)。
//   其余全是设备栈操作,不留配置残留。
// 注意:C# 5 语法(系统 csc 不认 C#6+),不要用字符串插值 $""、?. 等。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeepAwake {

internal sealed class KeyboardResetResult {
    public int Restarted;              // 成功 /restart-device 的设备数(通常只有 HID 那些)
    public int SkippedDisconnected;    // 跳过的未连接设备数
    public bool ScanDevicesDone;       // 全局 /scan-devices 是否成功(覆盖 PS/2 内置键盘的关键一步)
    public bool ScanCodeMapCleared;    // 是否清了 Scancode Map(默认无此值→false)
    public string ScanCodeMapBackup;   // 删前的原值(还原用);无则 null
    // 三个集合在构造函数里初始化:/warnaserror+ 会把「字段从未赋值」的 CS0649 当错误,
    // 而它们靠各处 .Add() 填充,不给初值编译器判不出"已赋值"
    public List<string> FlagsReset = new List<string>();   // 被恢复默认的无障碍开关名
    public List<string> Warnings = new List<string>();     // 只报告、不动手的可疑项(如 Kbfilter)
    public List<string> Failures = new List<string>();     // 失败的动作描述
    public bool NeedAdmin;             // 因权限不足整体未执行

    public bool AnyChange {
        get { return Restarted > 0 || ScanDevicesDone || ScanCodeMapCleared || FlagsReset.Count > 0; }
    }
}

static class KeyboardReset {

    const string ScanCodeMapPath = "SYSTEM\\CurrentControlSet\\Control\\Keyboard Layout";
    const string ScanCodeMapName = "Scancode Map";
    const string BackupCfgPath = "Software\\KeepAwakeKeyboard";   // 独立键,勿并入 Software\KeepAwake(见文件头注)

    // Windows 默认的无障碍键位标志位(粘滞 510 / 筛选 126)。写死而非读"当前用户模板",
    // 因为模板本身可能已被谁改过;用出厂值才对得起"恢复默认"。
    const int DefaultStickyFlags = 510;
    const int DefaultFilterFlags = 126;
    const string StickyKeyPath = "Control Panel\\Accessibility\\StickyKeys";
    const string FilterKeyPath = "Control Panel\\Accessibility\\Keyboard Response";

    const int RestartTimeoutMs = 20000;   // 单设备重枚举上限,超时算失败继续下一个

    // ==== 托盘入口(免管理员进程调这里) ====

    /// <summary>一键修复。内部在需要时自我提权拉一次性 --keyboard-reset 实例。
    /// 永不抛异常——失败信息进 result.Failures,交调用方弹窗如实展示。</summary>
    public static KeyboardResetResult Run() {
        var result = new KeyboardResetResult();
        try {
            // 第 2、3 步(注册表)其实不需要管理员:纯 HKCU 写入 + HKLM 的 Scancode Map
            // 删除。HKLM 删值要管理员。统一走提权,避免"一半做了"的状态歧义。
            if (IsElevated()) {
                ApplyAll(result);
            } else {
                RunElevated(result);
            }
        } catch (Exception ex) {
            result.Failures.Add("整体异常:" + ex.Message);
            AppLog.Write("键盘修复:整体异常 " + ex.Message);
        }
        return result;
    }

    // ==== CLI 入口(提权实例:Program.Main 收到 --keyboard-reset 后转这里) ====
    // 结果不能走 stdout(提权实例是 winexe,无控制台),统一写日志+一个标记文件,
    // 由父进程读回。退出码:0=跑完(哪怕有单步失败),1=未提权。

    static string ResultPath() {
        return Path.Combine(Path.GetTempPath(), "KeepAwake_kbdreset_result.txt");
    }

    public static int ApplyFromCli() {
        try {
            if (!IsElevated()) {
                AppLog.Write("--keyboard-reset 需要管理员:请从托盘菜单点击(会弹一次 UAC)");
                return 1;
            }
            var r = new KeyboardResetResult();
            ApplyAll(r);
            WriteResult(r);
            AppLog.Write("键盘修复(提权实例)完成:逐个重枚举 " + r.Restarted + " 个,全局重枚举="
                         + (r.ScanDevicesDone ? "成功" : "失败")
                         + (r.Warnings.Count > 0 ? (",可疑/说明 " + r.Warnings.Count + " 条(仅报告)") : ""));
            return 0;
        } catch (Exception ex) {
            AppLog.Write("--keyboard-reset 失败: " + ex.Message);
            return 1;
        }
    }

    // 父进程:提权实例退出后读回结果。读不到就如实报"结果未知"。
    static void RunElevated(KeyboardResetResult result) {
        string path = ResultPath();
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        bool ok = false;
        try {
            var psi = new ProcessStartInfo {
                FileName = Application.ExecutablePath,
                Arguments = "--keyboard-reset",
                Verb = "runas",
                UseShellExecute = true
            };
            using (var p = Process.Start(psi)) {
                if (p == null) { result.NeedAdmin = true; return; }
                ok = p.WaitForExit(120000);   // UAC 两分钟没人点就放弃
            }
        } catch (System.ComponentModel.Win32Exception) {
            result.NeedAdmin = true;         // 错误 1223:UAC 点了「否」
            return;
        } catch (Exception ex) {
            result.Failures.Add("提权启动失败:" + ex.Message);
            return;
        }
        if (!ok) { result.Failures.Add("提权实例超时未返回"); return; }
        ReadResult(result);
    }

    static void WriteResult(KeyboardResetResult r) {
        try {
            var sb = new StringBuilder();
            sb.Append("RESTARTED=").Append(r.Restarted).Append('\n');
            sb.Append("SKIPPED=").Append(r.SkippedDisconnected).Append('\n');
            sb.Append("SCANDEV=").Append(r.ScanDevicesDone ? "1" : "0").Append('\n');
            sb.Append("SCANMAP=").Append(r.ScanCodeMapCleared ? "1" : "0").Append('\n');
            sb.Append("SCANBACKUP=").Append(r.ScanCodeMapBackup ?? "").Append('\n');
            foreach (var f in r.FlagsReset) sb.Append("FLAG=").Append(f).Append('\n');
            foreach (var w in r.Warnings) sb.Append("WARN=").Append(w).Append('\n');
            foreach (var f in r.Failures) sb.Append("FAIL=").Append(f).Append('\n');
            File.WriteAllText(ResultPath(), sb.ToString(), Encoding.UTF8);
        } catch {
        }
    }

    static void ReadResult(KeyboardResetResult r) {
        try {
            if (!File.Exists(ResultPath())) { r.Failures.Add("未取到提权实例的结果文件"); return; }
            foreach (var raw in File.ReadAllLines(ResultPath(), Encoding.UTF8)) {
                if (raw.StartsWith("RESTARTED=")) r.Restarted = ParseInt(raw);
                else if (raw.StartsWith("SKIPPED=")) r.SkippedDisconnected = ParseInt(raw);
                else if (raw.StartsWith("SCANDEV=1")) r.ScanDevicesDone = true;
                else if (raw.StartsWith("SCANMAP=1")) r.ScanCodeMapCleared = true;
                else if (raw.StartsWith("SCANBACKUP=")) {
                    var v = raw.Substring("SCANBACKUP=".Length);
                    if (v.Length > 0) r.ScanCodeMapBackup = v;
                }
                else if (raw.StartsWith("FLAG=")) r.FlagsReset.Add(raw.Substring("FLAG=".Length));
                else if (raw.StartsWith("WARN=")) r.Warnings.Add(raw.Substring("WARN=".Length));
                else if (raw.StartsWith("FAIL=")) r.Failures.Add(raw.Substring("FAIL=".Length));
            }
        } catch (Exception ex) {
            r.Failures.Add("结果文件读取失败:" + ex.Message);
        } finally {
            try { if (File.Exists(ResultPath())) File.Delete(ResultPath()); } catch { }
        }
    }

    static int ParseInt(string s) {
        int i = s.IndexOf('=');
        int v;
        return i >= 0 && int.TryParse(s.Substring(i + 1), out v) ? v : 0;
    }

    // ==== 干活(提权实例内执行) ====

    static void ApplyAll(KeyboardResetResult r) {
        DetectSuspicious(r);       // 先采只读的可疑项(即使后续失败,报告也全)
        ClearScanCodeMap(r);
        ResetAccessibilityFlags(r);
        RestartKeyboardDevices(r);
    }

    // 1) 重新枚举键盘设备。两个动作叠加覆盖全部键盘:
    //    a) 逐个 /restart-device —— 只对 HID 类生效;PS/2 内置键盘系统不给做
    //       (禁它等于整机失去唯一键盘),那一类失败属【预期】,不算故障;
    //    b) /scan-devices 全局硬件重枚举 —— 不逐个禁设备,而是让 PnP 管理器把
    //       设备栈整体重新实例化,PS/2 内置键盘正是靠这步复位,键盘无中断。
    static void RestartKeyboardDevices(KeyboardResetResult r) {
        try {
            List<string> connected = new List<string>();
            int disconnected = 0;
            CountDevices(connected, ref disconnected);
            r.SkippedDisconnected = disconnected;
            foreach (var id in connected) {
                try {
                    string outp = RunPnputilCapture("/restart-device", id);
                    if (outp == null) { r.Failures.Add("重新枚举超时:" + ShortId(id)); continue; }
                    if (IsPs2Id(id)) {
                        // PS/2:系统硬拒是预期内,只记一条说明,不污染 Failures
                        r.Warnings.Add("内置 PS/2 键盘(" + ShortId(id) + ")不允许单独重启"
                            + "(Windows 禁止禁用唯一键盘),已改由全局设备重枚举复位。");
                        AppLog.Write("键盘修复:" + ShortId(id) + " PS/2,单独重启被系统拒绝(预期),依赖 /scan-devices");
                    } else if (outp.IndexOf("成功", StringComparison.OrdinalIgnoreCase) >= 0) {
                        r.Restarted++;
                        AppLog.Write("键盘修复:已重新枚举设备 " + id);
                    } else {
                        r.Failures.Add("重新枚举失败(跳过):" + ShortId(id));
                    }
                } catch (Exception ex) {
                    r.Failures.Add("重新枚举异常 " + ShortId(id) + ":" + ex.Message);
                }
            }
        } catch (Exception ex) {
            r.Failures.Add("枚举键盘设备失败:" + ex.Message);
        }

        // 全局硬件重枚举(覆盖 PS/2 内置键盘的关键一步)
        try {
            string scan = RunPnputilCapture("/scan-devices", null);
            if (scan != null && (scan.IndexOf("扫描完成", StringComparison.OrdinalIgnoreCase) >= 0
                              || scan.IndexOf("成功", StringComparison.OrdinalIgnoreCase) >= 0)) {
                r.ScanDevicesDone = true;
                AppLog.Write("键盘修复:全局设备重枚举(/scan-devices)完成");
            } else {
                r.Failures.Add("全局设备重枚举失败" + (scan == null ? "(超时)" : ""));
            }
        } catch (Exception ex) {
            r.Failures.Add("全局设备重枚举异常:" + ex.Message);
        }
    }

    static bool IsPs2Id(string id) {
        return id != null && id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase);
    }

    // 2) 清除全局 Scancode Map。默认【不存在】此值(本机实测),没有就不写;
    //    有则先备份原值再删——用户能通过还原备份找回。
    static void ClearScanCodeMap(KeyboardResetResult r) {
        try {
            using (var key = Registry.LocalMachine.OpenSubKey(ScanCodeMapPath)) {
                if (key == null) return;                     // 键都不在,无映射可言
                object val = key.GetValue(ScanCodeMapName);
                if (val == null) return;                     // 无 Scancode Map = 正常,不动
                string prev = Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture);
                r.ScanCodeMapBackup = prev;
            }
            using (var key = Registry.LocalMachine.OpenSubKey(ScanCodeMapPath, true)) {
                if (key != null) key.DeleteValue(ScanCodeMapName, false);
            }
            using (var cfg = Registry.CurrentUser.CreateSubKey(BackupCfgPath)) {
                cfg.SetValue("ScanCodeMapBackup", r.ScanCodeMapBackup, RegistryValueKind.String);
            }
            r.ScanCodeMapCleared = true;
            AppLog.Write("键盘修复:已清除 Scancode Map(原值已备份到 HKCU)");
        } catch (Exception ex) {
            r.Failures.Add("清除 Scancode Map 失败:" + ex.Message);
        }
    }

    // 3) 粘滞键/筛选键恢复出厂 Flags。只在与默认不同时才写,默认原样不动。
    static void ResetAccessibilityFlags(KeyboardResetResult r) {
        TryResetFlag(r, StickyKeyPath, "Flags", DefaultStickyFlags, "粘滞键");
        TryResetFlag(r, FilterKeyPath, "Flags", DefaultFilterFlags, "筛选键");
    }

    static void TryResetFlag(KeyboardResetResult r, string path, string name, int defVal, string label) {
        try {
            int cur;
            using (var key = Registry.CurrentUser.OpenSubKey(path)) {
                if (key == null) return;                    // 从没设置过 = 用默认,不动
                object v = key.GetValue(name);
                if (v == null) return;
                cur = Convert.ToInt32(v);
            }
            if (cur == defVal) return;                      // 已是默认,不动
            using (var key = Registry.CurrentUser.CreateSubKey(path)) {
                key.SetValue(name, defVal, RegistryValueKind.DWord);
            }
            r.FlagsReset.Add(label + "(" + cur + "→" + defVal + ")");
            AppLog.Write("键盘修复:" + label + " Flags " + cur + " → " + defVal);
        } catch (Exception ex) {
            r.Failures.Add(label + " 恢复默认失败:" + ex.Message);
        }
    }

    // 只读采集可疑项:Kbfilter 等驱动过滤器。这里只报告,绝不改动(见文件头)。
    static void DetectSuspicious(KeyboardResetResult r) {
        try {
            string filters = ReadKeyboardUpperFilters();
            if (!string.IsNullOrEmpty(filters)) {
                bool hasKb = filters.IndexOf("kbfilter", StringComparison.OrdinalIgnoreCase) >= 0;
                if (hasKb) {
                    r.Warnings.Add("键盘驱动过滤器链含 Kbfilter(联想键盘增强层,部分机型上会"
                        + "干扰按键识别)。本工具不动它——摘除可能导致内置键盘不认,需你自己评估。");
                }
            }
        } catch {
        }
    }

    // 读键盘类 GUID 的 UpperFilters。GUID = keyboard class。
    static string ReadKeyboardUpperFilters() {
        string classGuid = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
        using (var key = Registry.LocalMachine.OpenSubKey(
                   "SYSTEM\\CurrentControlSet\\Control\\Class\\" + classGuid)) {
            if (key == null) return null;
            return key.GetValue("UpperFilters") as string;
        }
    }

    // ==== 设备枚举(走 pnputil,免 P/Invoke 复杂度;本机实测可用) ====

    // 枚举键盘设备:已连接的进 ids,未连接的只计数(不重启——pnputil 对它们只会报
    // 「设备没有连接」,实测)。走 pnputil 输出,免 SetupAPI P/Invoke(其 GUID 传参
    // 与本地化输出都易踩坑,而 pnputil 在本机稳定可用)。
    static void CountDevices(List<string> connected, ref int disconnected) {
        var psi = new ProcessStartInfo("pnputil", "/enum-devices /class Keyboard") {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.Default   // 本机 pnputil 输出为本地代码页(GBK)
        };
        using (var p = Process.Start(psi)) {
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            ParseInstances(outp, connected, ref disconnected);
        }
    }

    // 从 pnputil /enum-devices 的中文输出里解析「实例 ID」与「状态」成对记录。
    // 本机实测格式(GBK):每个设备一个块,块内含「实例 ID:  <id>」「状态:  <state>」
    // 「类名:  Keyboard」。已连接(已启动/OK)进 connected;其余(已断开连接/
    // Unknown)只计 disconnected——不对它们调 /restart-device(必然失败)。
    static void ParseInstances(string text, List<string> connected, ref int disconnected) {
        string curId = null;
        bool curOk = false;
        bool curIsKeyboard = false;   // 过滤 /class Keyboard 已保证,但双保险
        foreach (var rawLine in text.Split('\n')) {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            if (line.StartsWith("实例 ID:")) {
                Flush(connected, ref disconnected, curId, curIsKeyboard, curOk);
                curId = line.Substring("实例 ID:".Length).Trim();
                curOk = false;
                curIsKeyboard = false;
            } else if (line.StartsWith("状态:")) {
                var st = line.Substring("状态:".Length).Trim();
                // 已启动 / OK / Started 视为连接可用;已断开连接 / Unknown 视为不可用
                curOk = st == "已启动" || st == "OK" || st == "Started";
            } else if (line.StartsWith("类名:")) {
                curIsKeyboard = line.IndexOf("Keyboard", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        Flush(connected, ref disconnected, curId, curIsKeyboard, curOk);
    }

    static void Flush(List<string> connected, ref int disconnected, string id, bool isKb, bool ok) {
        if (string.IsNullOrEmpty(id) || !isKb) return;
        if (ok) connected.Add(id);
        else disconnected++;
    }

    // 执行 pnputil 并回传合并输出(用于分类成功/失败/PS2 预期拒绝)。
    // id 为 null 时不拼设备参数(给 /scan-devices 这类无参动作)。返回 null = 超时。
    static string RunPnputilCapture(string verb, string id) {
        var args = (id == null) ? verb : (verb + " \"" + id + "\"");
        var psi = new ProcessStartInfo("pnputil", args) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default
        };
        using (var p = Process.Start(psi)) {
            string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            bool done = p.WaitForExit(RestartTimeoutMs);
            if (!done) { try { p.Kill(); } catch { } return null; }
            return outp;
        }
    }

    static string ShortId(string id) {
        if (string.IsNullOrEmpty(id)) return "";
        return id.Length <= 48 ? id : id.Substring(0, 48) + "…";
    }

    static bool IsElevated() {
        using (var id = WindowsIdentity.GetCurrent()) {
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

}
