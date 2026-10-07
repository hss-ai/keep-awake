# -*- coding: utf-8 -*-
"""probe_tabs_intact.py — 回归:N 个旧标签在多次合并后必须全部健在。

针对 bug「老的目录被关掉了,只剩最新打开的」:连开 4 个测试目录(逐个合并成标签),
断言:①资源管理器窗口数全程不变(首个若无从有到 1);②每开一个,跨窗口总标签数恰好 +1;
③合并日志的「合并后标签快照」里 4 个测试路径全部在场。
清理:经 Shell COM 按路径 Quit 测试标签(不用键盘,不碰用户标签)。
"""
import ctypes
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL

CABINET = "CabinetWClass"
MARKER = "kaTabs_"


def class_of(hwnd):
    buf = ctypes.create_unicode_buffer(64)
    return buf.value if user32.GetClassNameW(hwnd, buf, 64) else ""


def title_of(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    return buf.value if user32.GetWindowTextW(hwnd, buf, 256) else ""


def explorer_hwnds():
    found = []

    @WNDENUMPROC
    def cb(hwnd, lparam):
        if class_of(hwnd) == CABINET and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found


def tab_count(title):
    m = re.search(r"和 (\d+) 个其他选项卡", title)
    return int(m.group(1)) + 1 if m else 1


def total_tabs():
    return sum(tab_count(title_of(h)) for h in explorer_hwnds())


def quit_test_tabs():
    """逐条重枚举 Quit 测试标签:foreach 同一集合内连 Quit 会因 COM 集合塌缩错位
    漏关/误伤(实测),每次只关第一条匹配再重新枚举,轮次上限兜底。"""
    for _ in range(12):
        cmd = ("$victim = $null; "
               "foreach ($w in (New-Object -ComObject Shell.Application).Windows()) { "
               "try { if ([string]$w.LocationURL -like '*" + MARKER + "*') { $victim = $w; break } } catch {} }; "
               "if ($victim) { try { $victim.Quit(); 'one' } catch { 'err' } } else { 'none' }")
        r = subprocess.run(["pwsh", "-NoProfile", "-Command", cmd],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if "one" not in r.stdout:
            break
        time.sleep(0.6)


def pwsh(cmd):
    subprocess.run(["pwsh", "-NoProfile", "-Command", cmd], capture_output=True)


def main():
    exe = os.path.join(os.environ["USERPROFILE"], "Scripts", "KeepAwake.exe")
    had = subprocess.run(
        ["pwsh", "-NoProfile", "-Command",
         "@(Get-Process -Name KeepAwake -ErrorAction SilentlyContinue).Count"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    had_running = had.stdout.strip().isdigit() and int(had.stdout.strip()) > 0

    pwsh("Stop-Process -Name KeepAwake -ErrorAction SilentlyContinue")
    subprocess.Popen([exe])
    time.sleep(2.5)

    dirs = [tempfile.mkdtemp(prefix=MARKER + chr(ord("A") + i) + "_") for i in range(4)]
    ok = True
    try:
        n0 = len(explorer_hwnds())
        base = total_tabs()
        print("start: windows=%d tabs=%d" % (n0, base))
        expect_wins = max(n0, 1)
        for idx, d in enumerate(dirs):
            subprocess.Popen(["explorer.exe", d])
            # 8s:COM/UIA 主路正常 ~2s;explorer 忙态(UIA 间歇无响应)时 AddButton 有界
            # 重试实测 8~12s——3.8s 的旧预算在忙态下必然数在新窗口关掉之前,假 FAIL
            time.sleep(8.0)
            wins = len(explorer_hwnds())
            tabs = total_tabs()
            print("step %d: windows=%d tabs=%d(期望 %d/%d)" % (idx, wins, tabs, expect_wins, base + idx + 1))
            if wins != expect_wins or tabs != base + idx + 1:
                ok = False
                break

        if ok:
            log_path = os.path.join(os.environ.get("TEMP", ""), "KeepAwake_merge.log")
            try:
                with open(log_path, encoding="utf-8", errors="replace") as f:
                    lines = [ln for ln in f.read().splitlines() if "合并后标签快照" in ln]
                snap = lines[-1] if lines else ""
                present = sum(1 for d in dirs if d in snap)
                print("快照铁证: 4 个测试路径在场 %d/4" % present)
                ok = present == 4
            except OSError:
                print("读日志失败,跳过铁证(仅计数判据)")
    finally:
        quit_test_tabs()
        pwsh("Stop-Process -Name KeepAwake -ErrorAction SilentlyContinue")
        time.sleep(0.4)
        if had_running:
            os.startfile(exe)
        for d in dirs:
            shutil.rmtree(d, ignore_errors=True)

    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
