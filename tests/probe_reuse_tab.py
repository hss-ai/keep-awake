# -*- coding: utf-8 -*-
"""probe_reuse_tab.py — 回归:目标路径已在既有窗口某隐藏标签中时,外部打开必须
激活那条既有标签,而不是新开一条重复标签。

针对 bug(v1.4.2 之前):目录已在 tab1、当前显示 tab3 时,从其他程序「在资源管理器
打开」该目录 → 合并逻辑照样 AddButton+Navigate2 开出第二条重复标签,活动的还是
新开的重复条,用户要的老标签永远切不过去。

流程:确保 KeepAwake 在跑 → 开 3 个测试目录成标签(D1 即"已在 tab1"的目标)→
触发 A `explorer /select,D1\\a.txt`(带文件选中,外部程序唤起的典型形态)→
触发 B `explorer D1`(直接打开目录)→ 每步断言:窗口数=1、D1 的 LocationURL 计数
仍为 1(无重复标签)、目标窗口标题含 D1(活动标签已切到既有 D1)、合并日志新增
「既有标签激活: OK」。清理:Quit 测试标签、删临时目录。
"""
import os
import shutil
import subprocess
import sys
import tempfile
import time
import ctypes
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL

CABINET = "CabinetWClass"
MARKER = "kaReuse_"
LOG = os.path.join(os.environ.get("TEMP", ""), "KeepAwake_merge.log")


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


def url_of(path):
    return "file:///" + path.replace("\\", "/")


def tab_urls():
    """全部 explorer 窗口名下的 LocationURL 列表(平铺)。"""
    cmd = ("$out = @(); foreach ($w in (New-Object -ComObject Shell.Application).Windows()) { "
           "try { $out += [string]$w.LocationURL } catch {} }; $out -join [char]10")
    r = subprocess.run(["pwsh", "-NoProfile", "-Command", cmd],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return [u for u in r.stdout.splitlines() if u]


def log_lines():
    try:
        with open(LOG, encoding="utf-8", errors="replace") as f:
            return f.read().splitlines()
    except OSError:
        return []


def check_step(tag, d1_url, log_base):
    """返回 (ok, 描述)。断言:窗口数=1、D1 计数=1、活动标签=D1、日志有既有标签激活 OK。"""
    time.sleep(2.0)                                        # 关新窗+ShowTargetToUser 尘埃落定后再核对
    hwnds = explorer_hwnds()
    urls = tab_urls()
    d1_count = sum(1 for u in urls if u.lower() == d1_url.lower())
    title = title_of(hwnds[0]) if hwnds else "(none)"
    new_log = " / ".join(log_lines()[log_base:])
    ok = (len(hwnds) == 1
          and d1_count == 1
          and os.path.basename(os.path.dirname(d1_url)) and "D1" in title
          and "既有标签激活: OK" in new_log)
    print("  [%s] windows=%d d1_tabs=%d title=%r 激活日志=%s" % (
        tag, len(hwnds), d1_count, title, "有" if "既有标签激活: OK" in new_log else "无"))
    if len(hwnds) != 1:
        print("    ✗ 窗口数应为 1")
    if d1_count != 1:
        print("    ✗ D1 标签计数应为 1(出现了重复标签或标签丢失)")
    if "D1" not in title:
        print("    ✗ 活动标签未切到既有 D1")
    if "既有标签激活: OK" not in new_log:
        print("    ✗ 日志未见既有标签激活记录")
    return ok


def quit_test_tabs():
    """逐条重枚举 Quit 测试标签。不能 foreach 同一集合内连 Quit(COM 集合塌缩错位,
    实测会漏关甚至误伤他人标签),也不能先删目录再 Quit(指向已删目录的标签变僵尸,
    Quit 假成功)——每次枚举只关第一条匹配,关完重枚举,轮次上限兜底。"""
    for _ in range(10):
        cmd = ("$victim = $null; "
               "foreach ($w in (New-Object -ComObject Shell.Application).Windows()) { "
               "try { if ([string]$w.LocationURL -like '*" + MARKER + "*') { $victim = $w; break } } catch {} }; "
               "if ($victim) { try { $victim.Quit(); 'one' } catch { 'err' } } else { 'none' }")
        r = subprocess.run(["pwsh", "-NoProfile", "-Command", cmd],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        if "one" not in r.stdout:
            break
        time.sleep(0.6)


def main():
    running = subprocess.run(
        ["pwsh", "-NoProfile", "-Command",
         "@(Get-Process -Name KeepAwake -ErrorAction SilentlyContinue).Count -gt 0"],
        capture_output=True).returncode == 0
    if not running:
        print("KeepAwake 未在运行,先启动它再跑本探针")
        return 2

    base = tempfile.mkdtemp(prefix=MARKER)
    d1, d2, d3 = (os.path.join(base, x) for x in ("D1", "D2", "D3"))
    for d in (d1, d2, d3):
        os.makedirs(d)
    marker_file = os.path.join(d1, "a.txt")
    open(marker_file, "w").close()
    d1_url = url_of(d1)

    ok_a = ok_b = False
    try:
        for d in (d1, d2, d3):
            subprocess.Popen(["explorer.exe", d])
            time.sleep(4.0)
        n_before = len(tab_urls())
        if sum(1 for u in tab_urls() if u.lower() == d1_url.lower()) != 1:
            print("场景搭建失败:D1 标签未就位(总标签 %d)" % n_before)
            return 2
        print("场景就绪: windows=%d D1 已是隐藏标签" % len(explorer_hwnds()))

        log_base = len(log_lines())
        subprocess.Popen(["explorer.exe", "/select," + marker_file])
        time.sleep(12.0)
        ok_a = check_step("触发A /select 文件", d1_url, log_base)

        log_base = len(log_lines())
        subprocess.Popen(["explorer.exe", d1])
        time.sleep(12.0)
        ok_b = check_step("触发B 打开目录", d1_url, log_base)
    finally:
        quit_test_tabs()          # 先关标签再删目录(顺序反了会造出关不掉的僵尸标签)
        shutil.rmtree(base, ignore_errors=True)

    print("PASS" if (ok_a and ok_b) else "FAIL")
    return 0 if (ok_a and ok_b) else 1


if __name__ == "__main__":
    sys.exit(main())
