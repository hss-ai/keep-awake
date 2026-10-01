# -*- coding: utf-8 -*-
"""test_merge.py — 「资源管理器单窗口合并」功能端到端真机测试

用法: python test_merge.py <KeepAwake.exe 路径>

场景:
  1) 杀掉在跑的 KeepAwake(测试需要独占单实例互斥量;测试完会恢复启动已装版本);
  2) 启动被测 exe(合并功能默认开);
  3) 记录基线资源管理器窗口数 n0(CabinetWClass,可见);
  4) explorer 打开临时文件夹 A → 期望窗口数 = n0==0 ? 1 : n0(A 并入既有窗口);
  5) explorer 打开临时文件夹 B → 期望窗口数同上(B 也并入);
  6) 清理:关掉测试开出的窗口/标签,杀掉被测进程,删临时目录;
  PASS 判定:两步计数均达期望且稳定。
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
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.GetForegroundWindow.restype = wintypes.HWND
user32.keybd_event.argtypes = [ctypes.c_byte, ctypes.c_byte, wintypes.DWORD, ctypes.c_size_t]

WM_CLOSE = 0x0010
CABINET = "CabinetWClass"


def class_of(hwnd):
    buf = ctypes.create_unicode_buffer(64)
    if user32.GetClassNameW(hwnd, buf, 64):
        return buf.value
    return ""


def explorer_windows():
    found = []

    @WNDENUMPROC
    def cb(hwnd, lparam):
        if class_of(hwnd) == CABINET and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return found


def wait_count(target, timeout=8.0):
    """等资源管理器窗口数稳定到 target(合并是异步的,允许中间态)。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if len(explorer_windows()) == target:
            time.sleep(0.8)  # 稳定确认
            if len(explorer_windows()) == target:
                return True
        time.sleep(0.4)
    return len(explorer_windows()) == target


def ctrl_w_if_explorer_fg():
    fg = user32.GetForegroundWindow()
    if fg and class_of(fg) == CABINET:
        user32.keybd_event(0x11, 0, 0, 0)   # Ctrl
        user32.keybd_event(0x57, 0, 0, 0)   # W
        user32.keybd_event(0x57, 0, 2, 0)
        user32.keybd_event(0x11, 0, 2, 0)
        return True
    return False


def kill_keepawake():
    subprocess.run(
        ["pwsh", "-NoProfile", "-Command", "Stop-Process -Name KeepAwake -ErrorAction SilentlyContinue"],
        capture_output=True)


def running_keepawake():
    r = subprocess.run(
        ["pwsh", "-NoProfile", "-Command", "@(Get-Process -Name KeepAwake -ErrorAction SilentlyContinue).Count"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    try:
        return int(r.stdout.strip()) > 0
    except ValueError:
        return False


def main():
    if len(sys.argv) < 2:
        print("usage: python test_merge.py <KeepAwake.exe>")
        return 2
    exe = os.path.abspath(sys.argv[1])
    installed = os.path.join(os.environ["USERPROFILE"], "Scripts", "KeepAwake.exe")
    restore_path = installed if os.path.isfile(installed) else exe

    had_running = running_keepawake()
    kill_keepawake()

    proc = subprocess.Popen([exe])
    time.sleep(2.5)  # 等托盘与 WinEvent 钩子就绪
    if proc.poll() is not None:
        print("FAIL: exe 启动即退出(exit=%s)" % proc.returncode)
        return 1

    dir_a = tempfile.mkdtemp(prefix="ka_merge_A_")
    dir_b = tempfile.mkdtemp(prefix="ka_merge_B_")
    ok = False
    try:
        n0 = len(explorer_windows())
        subprocess.Popen(["explorer.exe", dir_a])
        exp1 = n0 + 1 if n0 == 0 else n0
        step1 = wait_count(exp1)
        subprocess.Popen(["explorer.exe", dir_b])
        exp2 = exp1 if n0 == 0 else n0
        step2 = wait_count(exp2)
        print("baseline=%d afterA=%s(expect %d) afterB=%s(expect %d)"
              % (n0, step1, exp1, step2, exp2))
        ok = step1 and step2

        # 清理:把测试引入的窗口/标签关掉
        wins = explorer_windows()
        if n0 == 0 and wins:
            user32.PostMessageW(wins[0], WM_CLOSE, 0, 0)
        elif n0 > 0:
            for _ in range(2):  # 关掉刚合并进去的两个标签(仅当前台是资源管理器时,防误关用户标签)
                if not ctrl_w_if_explorer_fg():
                    print("note: 前台不是资源管理器,跳过一次标签清理")
                time.sleep(0.7)
    finally:
        kill_keepawake()
        time.sleep(0.5)
        if had_running:
            subprocess.Popen([restore_path])
        for d in (dir_a, dir_b):
            shutil.rmtree(d, ignore_errors=True)

    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
