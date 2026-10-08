# -*- coding: utf-8 -*-
"""probe_keymap.py — 端到端验证全局键映射:F2→Ctrl+W、右Win→右Ctrl(含三件套识别)。

架构(v2,钩子录音机):探针 = WH_KEYBOARD_LL 录音钩子 + 一个窗体。
  - 录音钩子先于被测 KeepAwake 装进链(后装在链头),原键与 KeepAwake 注入的键全都录到,
    每条带来源:K=KeepAwake 魔数注入 / T=本测试注入(0x12340000) / U=人物理按键(断言时过滤,
    用户打字不干扰测试——v1 窗口消息版对前台焦点强依赖,实测用户打个字就把场景打断)。
  - 窗体只为「有破坏性的场景」提供安全的目的地:F2→Ctrl+W 的 W 是真按键,前台若是用户
    正在用的窗口会真关人家标签页——A/B/C 注入前守护前台,失焦等待重试、超时跳过;
  - 其余场景(Win 系)输出只有孤立的 Ctrl 单键或弹一下开始菜单(H 后自动 ESC 清场),
    无破坏性,无需前台、直接注入。

场景与判据(录音序列,K/T 按来源精确比对):
  A) F2 基本映射:Tap F2 → [F2↓T, C↓K, W↓K, F2↑T, W↑K, C↑K];
  B) F2 外部按着 Ctrl:→ 只补 W 不补 C,也不替外部抬;
  C) F2 自动重复:4×F2↓ + 1×F2↑ → 只产出一次 W 组合;
  D) 标准右 Win 直映射:Tap 0x5C → [RWIN↓T, C↓K, RWIN↑T, C↑K];
  E) 标准右 Win 外部按着 Ctrl:→ 无任何 K 注入;
  F) 标准右 Win 自动重复:→ 只产出一次 Ctrl 按压对;
  G) 三件套基本映射(用户键盘实测形态:5B↓,+10ms A0↓,+10ms 86↓):
     → [LWIN↓T, SH↓T, C↓K, F23↓T, F23↑T, SH↑T, LWIN↑T, C↑K](三件全吞+右Ctrl);
  H) 纯左 Win:Tap 0x5B → [LWIN↓T, LWIN↓K(20ms 判定超时后原样补发), LWIN↑T]
     (之后注入 ESC 清掉弹出的开始菜单,不在断言内);
  I) 三件套 + 外部按着 Ctrl:→ 坐实但不注入、不替外部抬;
  J) 三件套长按(整体重复形态 ×3):→ 只产出一次 Ctrl 按压对。
清理:杀被测 exe → 恢复已装版本。
"""
import ctypes
import os
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes

CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
VK_F2, VK_W, VK_LCONTROL, VK_CONTROL = 0x71, 0x57, 0xA2, 0x11
VK_RWIN, VK_LWIN, VK_LSHIFT, VK_F23, VK_ESCAPE = 0x5C, 0x5B, 0xA0, 0x86, 0x1B
CTRL_VKS = (0x11, 0xA2, 0xA3)  # WM_KEYDOWN 的 wParam 可能给 VK_CONTROL 也可能给 L/R 原码

# 探针(v2):窗体 + WH_KEYBOARD_LL 录音钩子(装在窗体线程)。窗体拿到前台后写 READY;
# 失焦不自杀(deadline 到才退)——A/B/C 靠测试端守护,D/E/F 与前台无关。纯 ASCII 避开编码坑。
PROBE_CS = r'''
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class ProbeKeys {
    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLL { public uint Vk; public uint Scan; public uint Flags; public uint Time; public IntPtr Extra; }

    const int WH_KEYBOARD_LL = 13;

    class ProbeForm : Form {
        internal IntPtr Hook = IntPtr.Zero;
        private LowLevelProc proc;      // 持引用防 GC 回收原生回调
        private string path;
        internal ProbeForm(string p) { path = p; }
        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            proc = Record;
            Hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            try { File.AppendAllText(path, "HOOK " + (Hook != IntPtr.Zero) + Environment.NewLine); } catch { }
        }
        protected override void OnFormClosed(FormClosedEventArgs e) {
            if (Hook != IntPtr.Zero) { UnhookWindowsHookEx(Hook); Hook = IntPtr.Zero; }
            base.OnFormClosed(e);
        }
        // 录音钩子:只录不吞,原样传递。K=KeepAwake 魔数注入 T=测试注入 U=人物理键
        private IntPtr Record(int nCode, IntPtr wParam, IntPtr lParam) {
            try {
                if (nCode >= 0) {
                    KBDLL k = (KBDLL)Marshal.PtrToStructure(lParam, typeof(KBDLL));
                    long extra = k.Extra.ToInt64();
                    string origin = extra == 0x4B415741L ? "K" : (extra == 0x12340000L ? "T" : "U");
                    uint msg = unchecked((uint)wParam.ToInt64());
                    string kind = (msg == 0x0100 || msg == 0x0104) ? "down" : "up";
                    string line = kind + " vk=0x" + k.Vk.ToString("X2") + " o=" + origin
                                + " t=" + DateTime.Now.ToString("HH:mm:ss.fff");
                    try { File.AppendAllText(path, line + Environment.NewLine); } catch { }
                }
            } catch { }
            return CallNextHookEx(Hook, nCode, wParam, lParam);
        }
    }

    [STAThread]
    static int Main(string[] args) {
        string resultPath = args[0];
        int lifeSeconds = int.Parse(args[1]);
        Application.EnableVisualStyles();
        ProbeForm form = new ProbeForm(resultPath);
        form.Text = "KeepAwake keymap probe";
        form.Width = 460;
        form.Height = 160;
        DateTime deadline = DateTime.UtcNow.AddSeconds(lifeSeconds);
        form.Shown += delegate {
            // 后台进程直调 SetForegroundWindow 会被前台锁拒。用 AttachThreadInput 解锁:
            // 不注入任何按键——Alt 轻点法会把前一前台应用的菜单栏"武装"起来,
            // 它随后抢回前台,把测试注入的键吃进别的窗口(实测翻车)。
            uint probePid;
            uint me = GetCurrentThreadId();
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out probePid);
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try { SetForegroundWindow(form.Handle); }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
            Timer ready = new Timer();
            ready.Interval = 50;
            ready.Tick += delegate {
                if (GetForegroundWindow() != form.Handle) return;   // 没抢到就继续等抢
                ready.Stop();
                try { File.AppendAllText(resultPath, "READY " + form.Handle.ToInt64() + Environment.NewLine); } catch { }
                Timer life = new Timer();
                life.Interval = 100;
                life.Tick += delegate {
                    // 失焦不退出(v1 版 LOSTFG 自杀让等待重试无从谈起),只到时收摊
                    if (DateTime.UtcNow > deadline) {
                        life.Stop();
                        form.Close();
                    }
                };
                life.Start();
            };
            ready.Start();
        };
        Application.Run(form);
        try { File.AppendAllText(resultPath, "DONE" + Environment.NewLine); } catch { }
        return 0;
    }
}
'''


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.c_ulonglong)]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long),
                ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_ulonglong)]


class _INPUTUNION(ctypes.Union):  # 必须是 Union:Structure 会把 ki 排在 mi 后面,sizeof 变 64
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("union", _INPUTUNION)]
    _anonymous_ = ("union",)


user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.SendInput.restype = wintypes.UINT
user32.GetForegroundWindow.restype = wintypes.HWND

PROBE_HWND = 0  # 仅对破坏性场景(A/B/C)做前置校验:前台一丢就等待/跳过,绝不误伤别的窗口


class FocusLost(Exception):
    pass


def fg_is(hwnd):
    return user32.GetForegroundWindow() == hwnd


def send_key(vk, up=False):
    arr = (INPUT * 1)()
    arr[0].type = 1  # INPUT_KEYBOARD
    # ⚠必须走完全限定路径:_anonymous_ 只把 union 摊平到 ki/mi 这一层,
    # wVk/dwFlags 仍在 ki 内——顶层直写 arr[0].wVk 只挂 Python 属性、内存全零,
    # 注入退化成 vk=0 的裸 keydown(2026-10-08 分段严格断言下实锤;此前
    # v1.4.0~v1.4.2 的"通过"靠宽容断言把人手按键也算数,注入路径从未真正生效)
    arr[0].ki.wVk = vk
    arr[0].ki.dwFlags = 2 if up else 0
    arr[0].ki.dwExtraInfo = 0x12340000  # 非零且非 KeepAwake 魔数:既证"外部注入仍被映射",又是断言来源标记
    if user32.SendInput(1, arr, ctypes.sizeof(INPUT)) != 1:
        raise OSError("SendInput 失败")


def tap(vk):
    send_key(vk)
    time.sleep(0.05)
    send_key(vk, up=True)


def pwsh(cmd):
    subprocess.run(["pwsh", "-NoProfile", "-Command", cmd], capture_output=True)


def keep_awake_count():
    r = subprocess.run(["pwsh", "-NoProfile", "-Command",
                        "@(Get-Process -Name KeepAwake -ErrorAction SilentlyContinue).Count"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    s = r.stdout.strip()
    return int(s) if s.isdigit() else 0


def compile_probe(out_exe):
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    src = os.path.join(repo, "build", "probe_keys.cs")
    os.makedirs(os.path.dirname(src), exist_ok=True)
    with open(src, "w", encoding="utf-8") as f:
        f.write(PROBE_CS)
    r = subprocess.run([CSC, "/nologo", "/target:winexe", "/codepage:65001", "/optimize+",
                        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
                        "/out:" + out_exe, src], capture_output=True)
    if r.returncode != 0 or not os.path.isfile(out_exe):
        print(r.stdout.decode("gbk", "replace"))
        print(r.stderr.decode("gbk", "replace"))
        raise SystemExit("探针编译失败")


def read_text(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return ""


def wait_marker(path, marker, timeout):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if marker in read_text(path):
            return True
        time.sleep(0.1)
    return False


def parse_probe_hwnd(path):
    for line in read_text(path).splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[0] == "READY":
            return int(parts[1])
    return 0


def parse_sections(path):
    """{mark: [(kind, token, origin)]}:按测试端写入的 "MARK <tag>" 行分段;
    只保留 F2/W/Ctrl/Win 相关消息(过滤无关键),Ctrl 原码归一为 C,
    人物理键(o=U)一并丢弃——用户打字不干扰断言。"""
    sections = {}
    cur = "PRE"
    for line in read_text(path).splitlines():
        parts = line.split()
        if parts and parts[0] == "MARK" and len(parts) == 2:
            cur = parts[1]
            sections.setdefault(cur, [])
            continue
        if len(parts) < 3 or parts[0] not in ("down", "up"):
            continue
        try:
            vk = int(parts[1].split("=")[1], 16)
            origin = parts[2].split("=")[1]
        except (IndexError, ValueError):
            continue
        if origin == "U":
            continue
        if vk == VK_F2:
            token = "F2"
        elif vk == VK_W:
            token = "W"
        elif vk in CTRL_VKS:
            token = "C"
        elif vk == VK_RWIN:
            token = "RWIN"
        elif vk == VK_LWIN:
            token = "LWIN"
        elif vk == VK_LSHIFT:
            token = "SH"
        elif vk == VK_F23:
            token = "F23"
        else:
            continue
        sections.setdefault(cur, []).append((parts[0], token, origin))
    return sections


def trio_tap(repeats=1):
    """模拟用户键盘的右侧 Win 合成键:5B↓ →+10ms A0↓ →+10ms 86↓(整体重复 repeats 次),
    再逆序抬起。间隔照实测采样(10ms/10ms)。"""
    for _ in range(repeats):
        send_key(VK_LWIN)
        time.sleep(0.01)
        send_key(VK_LSHIFT)
        time.sleep(0.01)
        send_key(VK_F23)
        time.sleep(0.05)
    send_key(VK_F23, up=True)
    time.sleep(0.01)
    send_key(VK_LSHIFT, up=True)
    time.sleep(0.01)
    send_key(VK_LWIN, up=True)


# 每场景的注入脚本。B/E/I 用外部 Ctrl 的按下/抬起包住原键按压,验证"不重复补按、不替外部抬"。
SCENARIOS = [
    ("A", lambda: tap(VK_F2)),
    ("B", lambda: (
        send_key(VK_LCONTROL), time.sleep(0.05),
        send_key(VK_F2), time.sleep(0.05),
        send_key(VK_F2, up=True), time.sleep(0.05),
        send_key(VK_LCONTROL, up=True))),
    ("C", lambda: (
        [send_key(VK_F2) or time.sleep(0.06) for _ in range(4)],
        time.sleep(0.2),
        send_key(VK_F2, up=True))),
    ("D", lambda: tap(VK_RWIN)),
    ("E", lambda: (
        send_key(VK_LCONTROL), time.sleep(0.05),
        send_key(VK_RWIN), time.sleep(0.05),
        send_key(VK_RWIN, up=True), time.sleep(0.05),
        send_key(VK_LCONTROL, up=True))),
    ("F", lambda: (
        [send_key(VK_RWIN) or time.sleep(0.06) for _ in range(4)],
        time.sleep(0.2),
        send_key(VK_RWIN, up=True))),
    ("G", lambda: trio_tap(1)),
    ("H", lambda: (
        send_key(VK_LWIN), time.sleep(0.06), send_key(VK_LWIN, up=True),
        time.sleep(0.35),                       # 等开始菜单弹出
        tap(VK_ESCAPE))),                       # 清场,ESC 不在断言 token 集
    ("I", lambda: (
        send_key(VK_LCONTROL), time.sleep(0.05),
        trio_tap(1),
        time.sleep(0.05),
        send_key(VK_LCONTROL, up=True))),
    ("J", lambda: trio_tap(3)),
]

# 录音序列期望。T=测试注入的原键(录音钩子在链头先于 KeepAwake,原键可见);
# K=KeepAwake 注入的目标键。原键被吞的效果 = 目标键恰好成组出现、无多余注入。
_QUAD_MID = [("down", "C", "K"), ("down", "W", "K")]           # F2↓ 触发的注入
_QUAD_END = [("up", "W", "K"), ("up", "C", "K")]               # F2↑ 触发的补抬
_TRIO_HEAD = [("down", "LWIN", "T"), ("down", "SH", "T")]      # 三件套头两件(5B↓,A0↓)
_TRIO_TAIL = [("up", "F23", "T"), ("up", "SH", "T"), ("up", "LWIN", "T")]  # 抬起三连
_TRIO_REP = [("down", "LWIN", "T"), ("down", "SH", "T"), ("down", "F23", "T")]  # 一组重复
EXPECTED = {
    "A": [("down", "F2", "T")] + _QUAD_MID + [("up", "F2", "T")] + _QUAD_END,
    "B": [("down", "C", "T"), ("down", "F2", "T"), ("down", "W", "K"),
          ("up", "F2", "T"), ("up", "W", "K"), ("up", "C", "T")],
    "C": [("down", "F2", "T")] + _QUAD_MID + [("down", "F2", "T")] * 3
         + [("up", "F2", "T")] + _QUAD_END,
    "D": [("down", "RWIN", "T"), ("down", "C", "K"),
          ("up", "RWIN", "T"), ("up", "C", "K")],
    "E": [("down", "C", "T"), ("down", "RWIN", "T"),
          ("up", "RWIN", "T"), ("up", "C", "T")],
    "F": [("down", "RWIN", "T"), ("down", "C", "K")] + [("down", "RWIN", "T")] * 3
         + [("up", "RWIN", "T"), ("up", "C", "K")],
    # G:5B↓ 暂扣 → A0↓ 坐实(此刻注入 C↓K)→ 86↓/抬起全吞 → 5B↑ 还账 C↑K
    "G": _TRIO_HEAD + [("down", "C", "K"), ("down", "F23", "T")] + _TRIO_TAIL
         + [("up", "C", "K")],
    # H:纯左 Win——20ms 判定超时后 KeepAwake 原样补发(魔数 K);up 直通配对
    "H": [("down", "LWIN", "T"), ("down", "LWIN", "K"), ("up", "LWIN", "T")],
    # I:外部 Ctrl 按着 → 坐实三件套但零注入
    "I": [("down", "C", "T")] + _TRIO_HEAD + [("down", "F23", "T")] + _TRIO_TAIL
         + [("up", "C", "T")],
    # J:整体重复 ×3 → 只第一组注入一次 Ctrl 按压
    "J": _TRIO_HEAD + [("down", "C", "K"), ("down", "F23", "T")] + _TRIO_REP * 2
         + _TRIO_TAIL + [("up", "C", "K")],
}

# 破坏性场景:输出含真实 W 键,必须由探针窗体持有前台(安全目的地)才允许注入。
# 其余场景输出仅孤立 Ctrl 单键(H 弹一次开始菜单随即 ESC 清场),无破坏性。
HARMFUL = {"A", "B", "C"}


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ["USERPROFILE"], "Scripts", "KeepAwake.exe")
    installed = os.path.join(os.environ["USERPROFILE"], "Scripts", "KeepAwake.exe")
    restore = installed if os.path.isfile(installed) else exe

    had_running = keep_awake_count() > 0
    pwsh("Stop-Process -Name KeepAwake -ErrorAction SilentlyContinue")
    time.sleep(0.5)
    subprocess.Popen([exe])
    time.sleep(2.5)
    if keep_awake_count() == 0:
        print("被测进程未存活:", exe)
        return 1

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    probe_exe = os.path.join(repo, "build", "probe_keys.exe")
    compile_probe(probe_exe)
    result_path = os.path.join(tempfile.gettempdir(), "KeepAwake_probe_keys.log")
    if os.path.isfile(result_path):
        os.remove(result_path)

    ok = False
    probe = subprocess.Popen([probe_exe, result_path, "70"])
    try:
        if not wait_marker(result_path, "READY", 10):
            print("探针 10s 内未取得前台,放弃")
        else:
            global PROBE_HWND
            PROBE_HWND = parse_probe_hwnd(result_path)
            done_tags, skipped_tags = [], []
            for tag, inject in SCENARIOS:
                if tag in HARMFUL:
                    waited = 0.0
                    while PROBE_HWND and not fg_is(PROBE_HWND) and waited < 6.0:
                        time.sleep(0.3)
                        waited += 0.3
                    if not fg_is(PROBE_HWND):
                        skipped_tags.append(tag)   # 用户正占着前台,跳过破坏性场景
                        continue
                with open(result_path, "a", encoding="utf-8") as f:
                    f.write("MARK %s\n" % tag)
                inject()
                done_tags.append(tag)
                time.sleep(0.35)
            time.sleep(0.5)
            sections = parse_sections(result_path)
            print("探针实录(按场景分段,K=KeepAwake注入 T=测试注入,人手键已滤):")
            failures = []
            for tag, _ in SCENARIOS:
                if tag in skipped_tags:
                    print("  [%s] SKIP(前台被占用,破坏性场景未注入)" % tag)
                    continue
                got = sections.get(tag, [])
                want = EXPECTED[tag]
                show = " ".join("%s:%s:%s" % g for g in got) or "(空)"
                print("  [%s] %s" % (tag, show))
                if got != want:
                    failures.append("%s 序列不符:期望 %s" % (tag, want))
            # 核心场景(A=F2 本体 / D=标准右Win / G=三件套,用户键盘的实际形态)
            # 至少其一真实跑过,才算测到了映射本体(全 skip = 白跑)
            if not ({"A", "D", "G"} & set(done_tags)):
                failures.append("A/D/G 均未实跑(前台全程被占用),映射本体未验证")
            ok = not failures
            for msg in failures:
                print("  ✗", msg)
    finally:
        probe.kill()
        pwsh("Stop-Process -Name KeepAwake -ErrorAction SilentlyContinue")
        time.sleep(0.4)
        if had_running:
            os.startfile(restore)  # os.startfile 脱离测试进程组,命令结束图标不消失

    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
