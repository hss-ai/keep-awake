# -*- coding: utf-8 -*-
"""build_keep_awake.py — 防待机托盘工具(keep_awake_tray.cs + explorer_tab_merge.cs + key_remap.cs + shortcut_arrow.cs → KeepAwake.exe)一键构建

流程:
  1) PIL 生成 keep_awake.ico(绿圆底+白咖啡杯,与托盘三态图标同款设计);
  2) 用 Windows 自带的 .NET Framework csc 编译(免装任何构建链,exe 仅几十 KB);
  3) 安装到 %USERPROFILE%\\Scripts\\KeepAwake.exe(C 盘用户工具目录);
  4) 桌面建「防待机.lnk」快捷方式(桌面路径从注册表 User Shell Folders 读,防 OneDrive 重定向)。

用法:python build_keep_awake.py                (默认全流程:构建+装机+桌面快捷方式)
      python build_keep_awake.py --build-only  (仅生成图标并编译到 build/KeepAwake.exe,
                                                CI 用:跳过停旧进程/装机/建快捷方式)
      (在本仓库根目录跑,三个 .cs 源文件须同目录)
依赖:Pillow(pip install pillow,仅构建期);csc 用系统自带 Framework64 v4.0.30319;
      装机步骤用系统自带 powershell.exe 与 taskkill,无 pwsh 依赖。
重复跑 = 重建覆盖(覆盖前自动停掉在跑的旧实例)。
"""

import os
import shutil
import subprocess
import sys
import time
import winreg

# 输出编码防护:非 GBK 环境(如 GitHub Actions runner 的 Python 默认 cp1252)打中文会
# UnicodeEncodeError 直接崩构建;errors="replace" 只把编不了的字符换成 "?",不改默认
# 编码,本机 GBK 控制台显示不变。stdout 为 None(如 pythonw)时跳过。
if sys.stdout and hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(errors="replace")
if sys.stderr and hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
SRC_FILES = [
    os.path.join(HERE, "keep_awake_tray.cs"),       # 托盘主程序(防待机 + 菜单)
    os.path.join(HERE, "explorer_tab_merge.cs"),    # 资源管理器单窗口合并(dynamic COM 需 Microsoft.CSharp)
    os.path.join(HERE, "key_remap.cs"),             # 全局键映射 F2→Ctrl+W、Win左→Ctrl左(低级键盘钩子)
    os.path.join(HERE, "shortcut_arrow.cs"),        # 去除快捷方式小箭头(HKLM Shell Icons\29)
]
MANIFEST = os.path.join(HERE, "app.manifest")       # dpiAware=true:进程从头 system DPI aware
BUILD_DIR = os.path.join(HERE, "build")
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

SHORTCUT_NAME = "防待机.lnk"
GREEN = (39, 174, 96, 255)  # 与 cs 里 iconOn 同色


def make_ico(path: str) -> None:
    from PIL import Image, ImageDraw

    img = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([8, 8, 248, 248], fill=GREEN)
    white = (255, 255, 255, 255)
    d.rectangle([76, 104, 180, 124], fill=white)   # 杯口沿
    d.rectangle([88, 120, 168, 208], fill=white)   # 杯身
    d.arc([168, 128, 232, 184], -80, 110, fill=white, width=14)  # 杯柄
    d.line([108, 56, 108, 92], fill=white, width=14)             # 蒸汽×2
    d.line([148, 56, 148, 92], fill=white, width=14)
    img.save(path, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


def find_gac_assembly(name: str) -> str:
    """csc 不探测 GAC,UIAutomation 系程序集只存在于 GAC;按名解析绝对路径。"""
    import glob
    for arch in ("GAC_MSIL", "GAC_64", "GAC_32"):
        hits = glob.glob(rf"C:\Windows\Microsoft.NET\assembly\{arch}\{name}\v4.0_*\{name}.dll")
        if hits:
            return hits[0]
    raise SystemExit(f"未在 GAC 找到 {name}.dll(这台机器 .NET Framework 不完整?)")


def desktop_dir() -> str:
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
        ) as key:
            val, _ = winreg.QueryValueEx(key, "Desktop")
        return os.path.expandvars(val)
    except OSError:
        return os.path.join(os.environ["USERPROFILE"], "Desktop")


def make_shortcut(exe_path: str, lnk_path: str) -> None:
    # .lnk 走 WScript.Shell COM,经系统自带 powershell.exe(5.1)落地——不依赖 pwsh;
    # subprocess 列表参数走 CreateProcessW,中文无损;路径里的 ' 转义成 '' 防 PS 引号断裂
    def esc(p: str) -> str:
        return p.replace("'", "''")

    cmd = (
        "$sh = New-Object -ComObject WScript.Shell; "
        "$lnk = $sh.CreateShortcut('{lnk}'); "
        "$lnk.TargetPath='{exe}'; "
        "$lnk.WorkingDirectory='{wdir}'; "
        "$lnk.Description='{desc}'; "
        "$lnk.IconLocation='{exe},0'; "
        "$lnk.Save()"
    ).format(lnk=esc(lnk_path), exe=esc(exe_path), wdir=esc(os.path.dirname(exe_path)),
             desc=esc("防待机托盘开关(左键开关,右键退出)"))
    result = subprocess.run(["powershell", "-NoProfile", "-Command", cmd], capture_output=True)
    if result.returncode != 0:
        print(result.stdout.decode("gbk", "replace"))
        print(result.stderr.decode("gbk", "replace"))
        raise SystemExit("快捷方式创建失败")


def main() -> int:
    build_only = "--build-only" in sys.argv
    for src_file in SRC_FILES:
        if not os.path.isfile(src_file):
            raise SystemExit(f"源码不存在:{src_file}")
    if not os.path.isfile(MANIFEST):
        raise SystemExit(f"清单不存在:{MANIFEST}")
    if not os.path.isfile(CSC):
        raise SystemExit(f"未找到系统 csc(这台机器没装 .NET Framework?):{CSC}")

    os.makedirs(BUILD_DIR, exist_ok=True)
    ico = os.path.join(BUILD_DIR, "keep_awake.ico")
    out_exe = os.path.join(BUILD_DIR, "KeepAwake.exe")

    make_ico(ico)
    print(f"[1/4] 图标已生成:{ico}")

    compile_cmd = [
        CSC, "/nologo", "/target:winexe", "/codepage:65001", "/optimize+",
        "/win32icon:" + ico, "/win32manifest:" + MANIFEST, "/out:" + out_exe,
        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:Microsoft.CSharp.dll",
        "/r:" + find_gac_assembly("UIAutomationClient"),
        "/r:" + find_gac_assembly("UIAutomationTypes"),
        *SRC_FILES,
    ]
    result = subprocess.run(compile_cmd, capture_output=True)
    if result.returncode != 0 or not os.path.isfile(out_exe):
        print(result.stdout.decode("gbk", "replace"))
        print(result.stderr.decode("gbk", "replace"))
        raise SystemExit("csc 编译失败")
    size_kb = os.path.getsize(out_exe) / 1024
    print(f"[2/4] 编译完成:{out_exe}({size_kb:.0f} KB)")

    if build_only:
        print("完成(--build-only:仅构建,未停旧进程/未装机/未建快捷方式)。")
        return 0

    dest_dir = os.path.join(os.environ["USERPROFILE"], "Scripts")
    os.makedirs(dest_dir, exist_ok=True)
    dest = os.path.join(dest_dir, "KeepAwake.exe")
    # 若旧实例在跑,先停掉再覆盖(taskkill 系统自带,目标不存在时非零退出,忽略;
    # 进程终止后句柄释放有几拍延迟,拷贝撞锁就等半秒重试)
    for _ in range(6):
        subprocess.run(["taskkill", "/IM", "KeepAwake.exe", "/F"], capture_output=True)
        try:
            shutil.copyfile(out_exe, dest)
            break
        except PermissionError:
            time.sleep(0.5)
    else:
        raise SystemExit("旧实例未能停止,KeepAwake.exe 覆盖失败")
    print(f"[3/4] 已安装:{dest}")

    lnk = os.path.join(desktop_dir(), SHORTCUT_NAME)
    make_shortcut(dest, lnk)
    print(f"[4/4] 桌面快捷方式:{lnk}")
    print("完成。双击「防待机」即启动并开始防待机;左键托盘图标开关,右键菜单可退出。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
