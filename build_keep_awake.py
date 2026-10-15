# -*- coding: utf-8 -*-
"""build_keep_awake.py — 防待机托盘工具(keep_awake_tray.cs + explorer_tab_merge.cs + key_remap.cs + shortcut_arrow.cs + audio_mute.cs → KeepAwake.exe)一键构建

流程:
  1) PIL 生成 keep_awake.ico(绿圆底+白咖啡杯,与托盘三态图标同款设计);
  2) 用 Windows 自带的 .NET Framework csc 编译(免装任何构建链,exe 仅几十 KB);
  3) 安装到 %USERPROFILE%\\Scripts\\KeepAwake.exe(C 盘用户工具目录);
  4) 桌面建「防待机.lnk」快捷方式(桌面路径从注册表 User Shell Folders 读,防 OneDrive 重定向)。

用法:python build_keep_awake.py                (默认全流程:构建+装机+桌面快捷方式)
      python build_keep_awake.py --build-only  (仅生成图标并编译到 build/KeepAwake.exe,
                                                CI 用:跳过停旧进程/装机/建快捷方式)
      (在本仓库根目录跑,五个 .cs 源文件须同目录)
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
    os.path.join(HERE, "key_remap.cs"),             # 全局键映射 F2→Ctrl+W、Win右→Ctrl右(低级键盘钩子)
    os.path.join(HERE, "shortcut_arrow.cs"),        # 去除快捷方式小箭头(HKLM Shell Icons\29)
    os.path.join(HERE, "audio_mute.cs"),            # 启动时静音(Core Audio COM 直调 IAudioEndpointVolume)
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


def source_version() -> str:
    """版本号单一源头:keep_awake_tray.cs 启动日志行「启动(vX.Y.Z)」。

    产物版本三处对齐全靠它——exe 的 AssemblyVersion/FileVersion(生成的
    AssemblyInfo.cs)与内嵌 manifest 的 assemblyIdentity version(生成的副本,
    仓库里的 app.manifest 保持 1.4.0.0 占位不动)。以前 manifest 静态 1.4.0.0
    又没有 AssemblyInfo,exe 属性查不到版本、与启动日志各说各话。
    """
    import re
    with open(os.path.join(HERE, "keep_awake_tray.cs"), encoding="utf-8") as f:
        # v1.4.11:正则锚定 AppLog.Write 的启动行——此前对全文取首个匹配,文件前部将来
        # 出现含『启动(vX.Y.Z)』的注释/变更日志就会静默取错版本(审查 build-3)
        m = re.search(r'AppLog\.Write\("启动\(v(\d+\.\d+\.\d+)\)"\)', f.read())
    if not m:
        raise SystemExit("keep_awake_tray.cs 里没找到 AppLog.Write 启动版本行,版本号无从注入")
    return m.group(1)


def check_tag() -> int:
    """CI tag 构建用(build.yml 调 --check-tag):校验 git tag 与源码版本一致。

    v1.4.11 新增(审查 C12①/build-2):版本四处表面(启动日志行/AssemblyInfo/manifest
    副本/git tag)此前只有前三处对齐,tag 无 gate——忘 bump 启动行就打 tag 会发布
    tag↔exe 版本错位的产物。tag 名取 CI 注入的 GITHUB_REF_NAME。"""
    tag = os.environ.get("GITHUB_REF_NAME", "")
    version = source_version()
    if tag == "v" + version:
        print(f"tag/版本一致:{tag}")
        return 0
    print(f"tag {tag} 与源码版本 v{version} 不一致——先改 keep_awake_tray.cs 启动日志行再打 tag")
    return 1


def write_version_sources(version: str, asm_path: str, manifest_copy: str) -> None:
    quad = version + ".0"
    with open(asm_path, "w", encoding="utf-8", newline="\n") as f:
        # 文件里有中文(AssemblyDescription);csc 走 /codepage:65001 编译,UTF-8 落盘即可
        f.write(
            "// 构建时生成(build_keep_awake.py):版本源头在 keep_awake_tray.cs 启动日志行\n"
            'using System.Reflection;\n'
            f'[assembly: AssemblyVersion("{quad}")]\n'
            f'[assembly: AssemblyFileVersion("{quad}")]\n'
            '[assembly: AssemblyTitle("KeepAwake")]\n'
            '[assembly: AssemblyProduct("KeepAwake")]\n'
            '[assembly: AssemblyDescription("防待机托盘工具")]\n'
        )
    with open(MANIFEST, encoding="utf-8") as f:
        manifest = f.read()
    import re
    manifest, n = re.subn(r'version="\d+\.\d+\.\d+\.\d+"', f'version="{quad}"', manifest, count=1)
    if n != 1:
        raise SystemExit("app.manifest 里没找到 assemblyIdentity 的 version 属性,无法注入版本")
    with open(manifest_copy, "w", encoding="utf-8", newline="\n") as f:
        f.write(manifest)


def main() -> int:
    if "--check-tag" in sys.argv:
        return check_tag()
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

    version = source_version()
    asm_info = os.path.join(BUILD_DIR, "AssemblyInfo.cs")
    manifest_out = os.path.join(BUILD_DIR, "app.manifest")
    write_version_sources(version, asm_info, manifest_out)
    print(f"[1.5/4] 版本注入:{version}(AssemblyInfo + manifest 副本)")

    compile_cmd = [
        CSC, "/nologo", "/target:winexe", "/codepage:65001", "/optimize+",
        "/warnaserror+",  # v1.4.11(审查 C12②):存量零 warning 实证后开启,警告即编译失败
        "/win32icon:" + ico, "/win32manifest:" + manifest_out, "/out:" + out_exe,
        "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:Microsoft.CSharp.dll",
        "/r:" + find_gac_assembly("UIAutomationClient"),
        "/r:" + find_gac_assembly("UIAutomationTypes"),
        *SRC_FILES, asm_info,
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
        except OSError as e:   # v1.4.11(审查 build-4):磁盘满/路径非法等非占用类错误直接报清原因
            raise SystemExit(f"装机拷贝失败(非占用类 IO 错误):{e}")
    else:
        raise SystemExit("旧实例未能停止,KeepAwake.exe 覆盖失败")
    print(f"[3/4] 已安装:{dest}")
    # v1.4.11(审查 C9):上面的 taskkill /F 是强杀,在跑实例没机会 Disengage(撤销电源
    # 护栏),残留要等下次启动才自愈。装机后立刻用新 exe 的维护通道补一刀恢复,闭环不留
    # 窗口;失败不阻断装机(新实例启动仍会无条件自愈)
    try:
        subprocess.run([dest, "--restore-power"], capture_output=True, timeout=15)
        print("[3.5/4] 已补跑 --restore-power(清理被强杀旧实例的电源护栏残留)")
    except Exception as e:
        print(f"[3.5/4] --restore-power 补跑未成({e});不影响装机,下次启动会自愈")

    lnk = os.path.join(desktop_dir(), SHORTCUT_NAME)
    make_shortcut(dest, lnk)
    print(f"[4/4] 桌面快捷方式:{lnk}")
    print("完成。双击「防待机」即启动并开始防待机;左键托盘图标开关,右键菜单可退出。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
