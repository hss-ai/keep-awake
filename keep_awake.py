# -*- coding: utf-8 -*-
"""keep_awake.py — Windows 防待机小工具(前台常驻,Ctrl+C / 到时退出即恢复)

原理:调用 Win32 API SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED
[| ES_DISPLAY_REQUIRED]),只对当前进程生效——进程退出(含被杀、直接关控制台窗口)
系统自动撤销请求,不改电源计划、不留系统残留。

用法:
  python keep_awake.py                阻止系统待机/休眠(屏幕允许自动关)——挂机下载/跑任务
  python keep_awake.py --display      连屏幕一起保持常亮——看文档/盯进度
  python keep_awake.py --minutes 90   90 分钟后自动结束恢复默认(0=不限时)
  python keep_awake.py --quiet        不打印心跳行

注意:合盖行为不受本工具影响(默认合盖即睡,由电源计划"合盖时"决定);
要合盖不睡:设置 → 系统 → 电源 → 屏幕和睡眠 → 合盖时不执行任何操作。

验证:管理员终端跑 powercfg /requests,本进程会出现在 SYSTEM(加 --display 还有 DISPLAY)请求里。
"""

import argparse
import ctypes
import sys
import time
from datetime import datetime, timedelta

ES_CONTINUOUS = 0x80000000
ES_SYSTEM_REQUIRED = 0x00000001
ES_DISPLAY_REQUIRED = 0x00000002

_SetExecState = ctypes.windll.kernel32.SetThreadExecutionState
_SetExecState.argtypes = [ctypes.c_uint]
_SetExecState.restype = ctypes.c_uint

TICK_SECONDS = 0.5  # 循环节拍,同时是到时检查粒度


def apply_state(flags: int) -> None:
    """flags=0 表示撤销本线程全部唤醒请求(恢复系统默认)。"""
    if _SetExecState(flags) == 0:
        raise OSError("SetThreadExecutionState 调用失败(返回 0)")


def fmt_delta(td) -> str:
    total = max(0, int(td.total_seconds()))
    hours, rem = divmod(total, 3600)
    mins, secs = divmod(rem, 60)
    if hours:
        return f"{hours}小时{mins:02d}分{secs:02d}秒"
    if mins:
        return f"{mins}分{secs:02d}秒"
    return f"{secs}秒"


def parse_args():
    parser = argparse.ArgumentParser(
        description="Windows 防待机小工具:保持系统唤醒,退出(Ctrl+C/到时)即恢复默认,不改电源计划",
    )
    parser.add_argument("--display", action="store_true",
                        help="同时保持屏幕常亮(默认只阻止待机,屏幕可自动关闭)")
    parser.add_argument("--minutes", type=float, default=0, metavar="N",
                        help="N 分钟后自动结束并恢复默认(0=不限时,Ctrl+C 退出)")
    parser.add_argument("--quiet", action="store_true", help="不打印心跳行")
    args = parser.parse_args()
    if args.minutes < 0:
        parser.error("--minutes 不能为负")
    return args


def main() -> int:
    args = parse_args()
    flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED
    if args.display:
        flags |= ES_DISPLAY_REQUIRED

    deadline = None
    if args.minutes > 0:
        deadline = datetime.now() + timedelta(minutes=args.minutes)

    apply_state(flags)
    mode = "系统保持唤醒 + 屏幕常亮" if args.display else "系统保持唤醒(屏幕可自动关闭)"
    timer_note = f";{args.minutes:g} 分钟后自动结束" if deadline else ";不限时"
    print(f"[keep_awake] 已生效:{mode}{timer_note}")
    print("[keep_awake] Ctrl+C 退出并恢复系统默认;验证可跑 powercfg /requests")

    start = datetime.now()
    try:
        while True:
            now = datetime.now()
            if deadline is not None and now >= deadline:
                print(f"\n[keep_awake] 到达 {args.minutes:g} 分钟,自动结束并恢复默认。")
                break
            if not args.quiet:
                elapsed = fmt_delta(now - start)
                remain = f",剩余 {fmt_delta(deadline - now)}" if deadline else ""
                sys.stdout.write(f"\r  已保持 {elapsed}{remain}   ")
                sys.stdout.flush()
            time.sleep(TICK_SECONDS)
    except KeyboardInterrupt:
        print("\n[keep_awake] 收到 Ctrl+C,退出并恢复默认。")
    finally:
        apply_state(ES_CONTINUOUS)  # 撤销本线程的唤醒请求
    return 0


if __name__ == "__main__":
    if sys.platform != "win32":
        sys.exit("仅支持 Windows(SetThreadExecutionState 是 Win32 API)")
    sys.exit(main())
