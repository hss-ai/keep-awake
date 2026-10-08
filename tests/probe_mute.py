# -*- coding: utf-8 -*-
"""probe_mute.py — 端到端验证「启动时静音」:KeepAwake 一启动(手动双击/开机自启同一条路)
就把当前默认播放设备 SetMute;开关关掉后启动完全不碰音频;已静音时启动幂等不炸。

判据走独立探针 exe(probe_volume.cs + 生产同一份 audio_mute.cs 编译,直查
IAudioEndpointVolume),不依赖 pycaw;探针侧自带 AppLog 同签名替身(audio_mute.cs
的日志落点),不编 keep_awake_tray.cs。

场景与判据:
  A) 开关开(出厂默认):先取消静音 → 启动 KeepAwake → 默认设备变 MUTED;
  B) 开关关(HKCU 写 0):先取消静音 → 启动 KeepAwake → 保持 UNMUTED(未被静音);
  C) 幂等:保持 MUTED → 启动 KeepAwake → 仍 MUTED 且进程存活(重复设置无副作用)。

清理:杀被测 exe → 恢复原静音态与原开关值;测试前 KeepAwake 在跑的重新拉起。
注意:场景会短暂动到系统静音态,跑的时候别指望出声。
"""
import os
import subprocess
import sys
import tempfile
import time
import winreg

CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
PREF_PATH = r"Software\KeepAwakePrefs"
PREF_NAME = "MuteAtStartup"

# 纯 ASCII(C# 源经 UTF-8 落盘 + /codepage:65001 编译,控制台输出只打英文单词防 mojibake)
PROBE_CS = r'''
using System;

namespace KeepAwake {

// audio_mute.cs 里 SystemMute 的日志落点:探针不带 keep_awake_tray.cs,给同签名替身
static class AppLog {
    public static void Write(string msg) {
        Console.Error.WriteLine("[probe-applog] " + msg);
    }
}

static class ProbeVolume {
    static int Main(string[] args) {
        if (args.Length >= 1 && args[0] == "get") {
            bool? m = SystemMute.GetMuted();
            Console.WriteLine(m == null ? "UNKNOWN" : (m.Value ? "MUTED" : "UNMUTED"));
            return m == null ? 3 : 0;
        }
        if (args.Length >= 2 && args[0] == "set" && (args[1] == "on" || args[1] == "off")) {
            bool ok = SystemMute.SetMuted(args[1] == "on");
            Console.WriteLine(ok ? "OK" : "FAIL");
            return ok ? 0 : 1;
        }
        Console.WriteLine("usage: probe_volume get | set on|off");
        return 2;
    }
}

}
'''


def pwsh(cmd):
    subprocess.run(["pwsh", "-NoProfile", "-Command", cmd], capture_output=True)


def keep_awake_count():
    r = subprocess.run(["pwsh", "-NoProfile", "-Command",
                        "@(Get-Process -Name KeepAwake -ErrorAction SilentlyContinue).Count"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    s = r.stdout.strip()
    return int(s) if s.isdigit() else 0


def stop_keep_awake():
    pwsh("Stop-Process -Name KeepAwake -ErrorAction SilentlyContinue")
    time.sleep(0.5)


def compile_probe(out_exe):
    src = os.path.join(tempfile.gettempdir(), "KeepAwake_probe_volume.cs")
    with open(src, "w", encoding="utf-8") as f:
        f.write(PROBE_CS)
    audio_cs = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "audio_mute.cs")
    result = subprocess.run([CSC, "/nologo", "/target:exe", "/codepage:65001",
                             "/out:" + out_exe, src, audio_cs], capture_output=True)
    if result.returncode != 0 or not os.path.isfile(out_exe):
        print(result.stdout.decode("gbk", "replace"))
        print(result.stderr.decode("gbk", "replace"))
        raise SystemExit("探针编译失败")


def probe(exe, *args):
    """返回 stdout 一行(MUTED/UNMUTED/UNKNOWN/OK/FAIL)。"""
    r = subprocess.run([exe] + list(args), capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    return (r.stdout or "").strip()


def wait_mute(exe, target, timeout):
    deadline = time.time() + timeout
    state = ""
    while time.time() < deadline:
        state = probe(exe, "get")
        if state == target:
            return True, state
        time.sleep(0.3)
    return False, state


def pref_get():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, PREF_PATH) as key:
            return winreg.QueryValueEx(key, PREF_NAME)[0] != 0
    except OSError:
        return True  # 未写过 = 默认开,与 C# 侧 Get() 同口径


def pref_set(on):
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, PREF_PATH) as key:
        winreg.SetValueEx(key, PREF_NAME, 0, winreg.REG_DWORD, 1 if on else 0)


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.environ["USERPROFILE"], "Scripts", "KeepAwake.exe")
    if not os.path.isfile(exe):
        print("被测 exe 不存在:", exe)
        return 1

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    probe_exe = os.path.join(repo, "build", "probe_volume.exe")
    compile_probe(probe_exe)

    initial = probe(probe_exe, "get")
    if initial != "MUTED" and initial != "UNMUTED":
        print("本机无音频设备(端点查询返回 %s),SKIP" % initial)
        return 0
    orig_pref = pref_get()
    orig_muted = initial == "MUTED"
    had_running = keep_awake_count() > 0
    print("初始态:%s,开关=%s" % (initial, "开" if orig_pref else "关"))

    failures = []
    try:
        # A) 开关开:取消静音 → 启动 → 必须变 MUTED
        pref_set(True)
        if probe(probe_exe, "set", "off") != "OK":
            failures.append("前置:取消静音失败(音频端点不可写?)")
        else:
            stop_keep_awake()
            subprocess.Popen([exe])
            ok, state = wait_mute(probe_exe, "MUTED", 10)
            print("  [A] 开关开→启动: %s" % state)
            if not ok:
                failures.append("A:启动后未静音(状态 %s)" % state)

        # B) 开关关:取消静音 → 启动 → 必须保持 UNMUTED(完全不碰音频)
        pref_set(False)
        if probe(probe_exe, "set", "off") == "OK":
            stop_keep_awake()
            subprocess.Popen([exe])
            time.sleep(3.5)  # 启动静音在构造函数里同步做,3.5s 足够暴露"错误地静音"
            state = probe(probe_exe, "get")
            print("  [B] 开关关→启动: %s" % state)
            if state != "UNMUTED":
                failures.append("B:开关关着仍被静音(状态 %s)" % state)
        else:
            failures.append("前置:取消静音失败")

        # C) 幂等:保持 MUTED → 启动 → 仍 MUTED 且进程存活(重复设置不炸不翻)
        pref_set(True)
        if probe(probe_exe, "set", "on") == "OK":
            stop_keep_awake()
            subprocess.Popen([exe])
            time.sleep(3.5)
            state = probe(probe_exe, "get")
            alive = keep_awake_count() > 0
            print("  [C] 已静音→启动: %s,进程存活=%s" % (state, alive))
            if state != "MUTED" or not alive:
                failures.append("C:幂等场景异常(状态 %s,存活 %s)" % (state, alive))
        else:
            failures.append("前置:设静音失败")
    finally:
        stop_keep_awake()
        probe(probe_exe, "set", "on" if orig_muted else "off")  # 恢复原静音态
        pref_set(orig_pref)
        if had_running:
            os.startfile(exe)  # 脱离测试进程组,命令结束图标不消失

    for msg in failures:
        print("  ✗", msg)
    ok = not failures
    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
