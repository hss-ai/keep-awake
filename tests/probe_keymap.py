# -*- coding: utf-8 -*-
"""probe_keymap.py — 端到端验证全局键映射 F2→Ctrl+W(跑前先用 build_keep_awake.py 装新版)。

判据:编译一个前台探针窗口(把收到的 WM_KEYDOWN/UP 逐条落盘),测试端注入按键后断言:
  A) 基本映射:注入 F2 → 探针只收到 Ctrl↓ W↓ W↑ Ctrl↑,绝不能出现 F2;
  B) 外部按着 Ctrl:先注入 Ctrl↓ 再按 F2 → Ctrl 只按下一次(映射不得重复补按、
     也不得替外部抬键),W 组合照发;
  C) 自动重复:连发多次 F2↓ 再 F2↑ → 只产出一次 W 组合(长按不连环触发)。
注入键对低级钩子完全可见(钩子只放行 KeepAwake 自家魔数),故全自动可验;
物理按键与注入走同一条钩子路径(key_remap.cs 不区分来源、只认魔数)。
跑测期间请勿碰键盘鼠标(探针窗口须保持前台;前台一丢即中止注入,防误伤别的窗口)。
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
CTRL_VKS = (0x11, 0xA2, 0xA3)  # WM_KEYDOWN 的 wParam 可能给 VK_CONTROL 也可能给 L/R 原码

# 探针窗口:拿到前台后写 "READY <hwnd>",把收到的键盘消息逐条落盘,
# 前台丢失(写 LOSTFG)或到时(DONE)退出并补写全部实录。纯 ASCII,避开编码坑。
PROBE_CS = r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class ProbeKeys {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    class ProbeForm : Form {
        internal readonly List<string> Lines = new List<string>();
        private string resultPath;
        internal ProbeForm(string path) { resultPath = path; }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x0100 || m.Msg == 0x0101 || m.Msg == 0x0104 || m.Msg == 0x0105) {
                int vk = m.WParam.ToInt32() & 0xFF;
                bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                bool fg = GetForegroundWindow() == Handle;
                string kind = (m.Msg == 0x0100 || m.Msg == 0x0104) ? "down" : "up";
                string line = kind + " vk=0x" + vk.ToString("X2") + " ctrl=" + (ctrl ? "1" : "0")
                            + " msg=0x" + m.Msg.ToString("X4") + " fg=" + (fg ? "1" : "0")
                            + " t=" + DateTime.Now.ToString("HH:mm:ss.fff");
                Lines.Add(line);
                try { File.AppendAllText(resultPath, line + Environment.NewLine); } catch { }
            }
            base.WndProc(ref m);
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
            // 它随后抢回前台,把测试注入的键吃进别的窗口(实测翻车,见 LOSTFG)。
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
                if (GetForegroundWindow() != form.Handle) return;
                ready.Stop();
                try { File.AppendAllText(resultPath, "READY " + form.Handle.ToInt64() + Environment.NewLine); } catch { }
                Timer life = new Timer();
                life.Interval = 100;
                life.Tick += delegate {
                    if (GetForegroundWindow() != form.Handle) {
                        life.Stop();
                        try { File.AppendAllText(resultPath, "LOSTFG" + Environment.NewLine); } catch { }
                        form.Close();
                    } else if (DateTime.UtcNow > deadline) {
                        life.Stop();
                        form.Close();
                    }
                };
                life.Start();
            };
            ready.Start();
        };
        Application.Run(form);
        try {
            List<string> all = new List<string>(form.Lines);
            all.Add("DONE");
            File.AppendAllLines(resultPath, all.ToArray());
        } catch {
            return 1;
        }
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
    _anonymous_ = ("ki",)


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("union", _INPUTUNION)]
    _anonymous_ = ("union",)  # 挂上才允许 a[0].wVk 直写;不挂时赋值只建 Python 属性、不报错


user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.SendInput.restype = wintypes.UINT
user32.GetForegroundWindow.restype = wintypes.HWND

PROBE_HWND = 0  # 注入前置校验:前台一丢立即中止,绝不把按键送进别的窗口


class FocusLost(Exception):
    pass


def fg_is(hwnd):
    return user32.GetForegroundWindow() == hwnd


def send_key(vk, up=False):
    if PROBE_HWND and not fg_is(PROBE_HWND):
        raise FocusLost("前台已丢失,中止注入")
    arr = (INPUT * 1)()
    arr[0].type = 1  # INPUT_KEYBOARD
    arr[0].wVk = vk
    arr[0].dwFlags = 2 if up else 0
    arr[0].dwExtraInfo = 0x12340000  # 非零且非 KeepAwake 魔数:证明"外部注入"仍会被映射
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


def parse_events(path):
    """[(kind, token, ctrl)]:按 key=value 取 kind/vk/ctrl(行尾可有 msg/fg/t 等扩展字段),
    只保留 F2/W/Ctrl 相关消息(过滤无关键噪音),Ctrl 原码归一为 C。"""
    events = []
    for line in read_text(path).splitlines():
        parts = line.split()
        if len(parts) < 3 or parts[0] not in ("down", "up"):
            continue
        try:
            vk = int(parts[1].split("=")[1], 16)
            ctrl = int(parts[2].split("=")[1])
        except (IndexError, ValueError):
            continue
        if vk == VK_F2:
            events.append((parts[0], "F2", ctrl))
        elif vk == VK_W:
            events.append((parts[0], "W", ctrl))
        elif vk in CTRL_VKS:
            events.append((parts[0], "C", ctrl))
    return events


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
    probe = subprocess.Popen([probe_exe, result_path, "15"])
    try:
        if not wait_marker(result_path, "READY", 10):
            print("探针 10s 内未取得前台,放弃")
        else:
            global PROBE_HWND
            PROBE_HWND = parse_probe_hwnd(result_path)
            scenarios = []
            try:
                if fg_is(PROBE_HWND):
                    tap(VK_F2)                # A) 基本映射
                    scenarios.append("A")
                    time.sleep(0.35)
                if fg_is(PROBE_HWND):
                    send_key(VK_LCONTROL)     # B) 外部按着 Ctrl 再按 F2
                    time.sleep(0.05)
                    send_key(VK_F2)
                    time.sleep(0.05)
                    send_key(VK_F2, up=True)
                    time.sleep(0.05)
                    send_key(VK_LCONTROL, up=True)
                    scenarios.append("B")
                    time.sleep(0.35)
                if fg_is(PROBE_HWND):
                    for _ in range(4):        # C) 自动重复只触发一次
                        send_key(VK_F2)
                        time.sleep(0.06)
                    time.sleep(0.2)
                    send_key(VK_F2, up=True)
                    scenarios.append("C")
                    time.sleep(0.4)
            except FocusLost:
                pass  # 场景计数不满 3,走下面统一报中止
            if len(scenarios) < 3:
                print("前台中途丢失,只完成场景", scenarios, "——中止(未注入的场景)")
            else:
                wait_marker(result_path, "DONE", 15)
                events = parse_events(result_path)
                print("探针实录(已过滤无关键):")
                for kind, token, ctrl in events:
                    print("  %s %-2s ctrl=%d" % (kind, token, ctrl))
                quad = [("down", "C"), ("down", "W"), ("up", "W"), ("up", "C")]
                got = [(k, t) for k, t, _ in events]
                no_f2 = all(t != "F2" for _, t, _ in events)
                w_ctrl_ok = all(c == 1 for _, t, c in events if t == "W")
                # 断言"完整组模式":全部事件恰好由 N 个 Ctrl↓W↓W↑Ctrl↑ 组拼成且 N>=3。
                # 不写死 N(测试注入 3 组,人手在探针上按 F2 也会成组——同一条钩子路径,一并算通过)
                n_groups = len(got) // 4
                seq_ok = len(got) >= 12 and len(got) % 4 == 0 and got == quad * n_groups
                ok = seq_ok and no_f2 and w_ctrl_ok
                print("F2 全被吞=%s | W 期间 Ctrl 全按下=%s | 序列=%s"
                      % (no_f2, w_ctrl_ok,
                         "完整组模式(%d 组 Ctrl+W)" % n_groups if seq_ok else "不符"))
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
