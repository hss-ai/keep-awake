# -*- coding: utf-8 -*-
"""probe_merge_once.py — 用「窗口标题=临时文件夹名」的判据验证合并确实发生。

流程:杀 KeepAwake → 起被测 exe(合并默认开)→ 开一个独有名字的临时文件夹 → 等 4s →
枚举 CabinetWClass 窗口标题:若唯一窗口标题含临时文件夹名,说明该文件夹已成为活动标签(合并实锤)。
清理:前台若是资源管理器则 Ctrl+W 关掉该标签 → 杀被测 exe → 恢复已装版本 → 删临时目录。
"""
import ctypes
import os
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
user32.GetForegroundWindow.restype = wintypes.HWND
user32.keybd_event.argtypes = [ctypes.c_byte, ctypes.c_byte, wintypes.DWORD, ctypes.c_size_t]

CABINET = "CabinetWClass"


def class_of(hwnd):
    buf = ctypes.create_unicode_buffer(64)
    return buf.value if user32.GetClassNameW(hwnd, buf, 64) else ""


def title_of(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    return buf.value if user32.GetWindowTextW(hwnd, buf, 256) else ""


def explorer_titles():
    found = []

    @WNDENUMPROC
    def cb(hwnd, lparam):
        if class_of(hwnd) == CABINET and user32.IsWindowVisible(hwnd):
            found.append(title_of(hwnd))
        return True

    user32.EnumWindows(cb, 0)
    return found


def pwsh(cmd):
    subprocess.run(["pwsh", "-NoProfile", "-Command", cmd], capture_output=True)


def ctrl_w_if_explorer_fg():
    """仅当前台资源管理器的活动标签是本探针目录(标题含 kaProbe)时才发 Ctrl+W——
    绝不碰用户自己的标签。"""
    fg = user32.GetForegroundWindow()
    if not fg or class_of(fg) != CABINET:
        return False
    if "kaProbe" not in title_of(fg):
        return False
    user32.keybd_event(0x11, 0, 0, 0)
    user32.keybd_event(0x57, 0, 0, 0)
    user32.keybd_event(0x57, 0, 2, 0)
    user32.keybd_event(0x11, 0, 2, 0)
    return True


def main():
    exe = os.path.abspath(sys.argv[1])
    installed = os.path.join(os.environ["USERPROFILE"], "Scripts", "KeepAwake.exe")
    restore = installed if os.path.isfile(installed) else exe

    had = subprocess.run(
        ["pwsh", "-NoProfile", "-Command",
         "@(Get-Process -Name KeepAwake -ErrorAction SilentlyContinue).Count"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    had_running = had.stdout.strip().isdigit() and int(had.stdout.strip()) > 0

    pwsh("Stop-Process -Name KeepAwake -ErrorAction SilentlyContinue")
    proc = subprocess.Popen([exe])
    time.sleep(2.5)

    probe_dir = tempfile.mkdtemp(prefix="kaProbe_")
    marker = os.path.basename(probe_dir)
    ok = False
    try:
        before = explorer_titles()
        subprocess.Popen(["explorer.exe", probe_dir])
        time.sleep(15)  # COM 主路 ~2s;explorer COM/UIA 间歇无响应时,有界重试+键盘兜底实测 8~12s
        after = explorer_titles()
        print("before:", before)
        print("after :", after)
        hit = [t for t in after if marker in t]
        ok = len(hit) > 0 and len(after) <= max(1, len(before))
        print("merged-active-tab=%s" % ("yes:" + hit[0] if hit else "no"))
        if ctrl_w_if_explorer_fg():
            time.sleep(0.6)  # 关掉探测标签
    finally:
        pwsh("Stop-Process -Name KeepAwake -ErrorAction SilentlyContinue")
        time.sleep(0.4)
        if had_running:
            # os.startfile(ShellExecute)脱离测试进程组;Popen 会被命令结束连带回收,图标会消失
            os.startfile(restore)
        shutil.rmtree(probe_dir, ignore_errors=True)

    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
