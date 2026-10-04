# -*- coding: utf-8 -*-
"""probe_arrow.py — 「去除快捷方式小箭头」机制探针:Shell Icons\\29 覆盖位在哪生效

背景:改注册表 Explorer\\Shell Icons 的 "29" 值指向全透明图标,可让快捷方式左下角
小箭头消失;但权威教程全走 HKLM(需管理员),HKCU 位置是否被 shell 认账说法不一
(有文章明确称 HKCU 是"常见误区")。本探针不靠人眼看桌面,用 shell 自己的图标解析
当裁判:
  0) 生成全透明 16×16 blank.ico(与 shortcut_arrow.cs BlankIcoBytes() 构造一一对应,
     改一处必须同步另一处),PIL 验证其确为全透明;
  1) 现场编译 probe_arrow.cs(SHGetFileInfo 实拍 .lnk 合成图标,箭头在左下角);
  2) 拍 baseline → 写 29=blank.ico → ie4uinit -show 刷新 → 再拍 → 比对左下角
     16×16 的不透明像素数:显著减少 = 覆盖位被认账;
  3) 恢复原值(有旧值还原旧值、没有则删净整个键),结束不留残留。

用法:python probe_arrow.py             # 实验 HKCU(免管理员)
      python probe_arrow.py --hklm      # 实验 HKLM(需管理员窗口)
判读:baseline 角落不透明像素为 0 → SHGetFileInfo 没合成箭头,探针判据失效,
      结论不可用(此时需要换判据,别硬下结论)。
"""

import os
import subprocess
import sys
import tempfile
import winreg

if sys.stdout and hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
SHELL_ICONS = r"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons"
VALUE_NAME = "29"
CREATE_NO_WINDOW = 0x08000000


def make_blank_ico(path):
    """全透明 16×16 ICO(与 shortcut_arrow.cs BlankIcoBytes() 构造一一对应,改一处改两处):
    ICO 头 6B + 目录项 16B + BITMAPINFOHEADER 40B + 像素 16*16*4B(首像素 alpha=1,
    其余全 0)+ AND 掩码 16 行*4B(全 0xFF)。
    ⚠首像素 alpha=1 是命门(26300 实测变体实验):32bpp ICO alpha 全 0 时 GDI 判
    "无 alpha 通道",按 RGB 渲染成不透明黑方块——AND 掩码全 1 救不了(此路径不读
    掩码),PIL 默认写的 PNG-entry ICO 同样黑;一个像素 alpha=1 即触发 alpha 合成。"""
    pixel = 16 * 16 * 4
    mask = 16 * 4
    bmp_size = 40 + pixel + mask
    ico = bytearray(6 + 16 + bmp_size)
    ico[2] = 1                                  # type=icon
    ico[4] = 1                                  # 图像数量=1
    ico[6] = 16                                 # 宽=16
    ico[7] = 16                                 # 高=16
    ico[10] = 1                                 # planes=1
    ico[12] = 32                                # bpp=32
    ico[14] = bmp_size & 0xFF                   # 数据长度(低字节)
    ico[15] = (bmp_size >> 8) & 0xFF            # 数据长度(高字节)
    ico[18] = 22                                # 数据偏移=6+16
    ico[22] = 40                                # biSize
    ico[26] = 16                                # biWidth
    ico[30] = 32                                # biHeight=XOR+AND 双倍高度
    ico[34] = 1                                 # biPlanes
    ico[36] = 32                                # biBitCount(偏移 36,不是 38——踩过)
    ico[62 + 3] = 1                             # 首像素 alpha=1(见上,全 0=黑方块)
    base = 6 + 16 + 40 + pixel
    for i in range(mask):
        ico[base + i] = 0xFF                    # AND 掩码全 1(保险,渲染路径不读)
    with open(path, "wb") as f:
        f.write(ico)


def compile_probe(workdir):
    exe = os.path.join(workdir, "probe_arrow.exe")
    r = subprocess.run(
        [CSC, "/nologo", "/target:exe", "/codepage:65001", "/out:" + exe,
         "/r:System.Drawing.dll", os.path.join(HERE, "probe_arrow.cs")],
        capture_output=True)
    if r.returncode != 0 or not os.path.isfile(exe):
        print(r.stdout.decode("gbk", "replace"))
        print(r.stderr.decode("gbk", "replace"))
        raise SystemExit("probe 编译失败")
    return exe


def make_test_lnk(workdir):
    lnk = os.path.join(workdir, "arrow_test.lnk")

    def esc(p):
        return p.replace("'", "''")

    cmd = ("$sh = New-Object -ComObject WScript.Shell; "
           "$l = $sh.CreateShortcut('{p}'); "
           "$l.TargetPath='C:\\Windows\\explorer.exe'; "
           "$l.Save()").format(p=esc(lnk))
    r = subprocess.run(["powershell", "-NoProfile", "-Command", cmd], capture_output=True)
    if r.returncode != 0 or not os.path.isfile(lnk):
        print(r.stdout.decode("gbk", "replace"))
        print(r.stderr.decode("gbk", "replace"))
        raise SystemExit("测试 lnk 创建失败")
    return lnk


def snapshot(probe_exe, target, png):
    r = subprocess.run([probe_exe, target, png], capture_output=True,
                       creationflags=CREATE_NO_WINDOW)
    out = (r.stdout or b"").decode("gbk", "replace").strip()
    if r.returncode != 0:
        raise SystemExit("探针拍照失败:" + out + (r.stderr or b"").decode("gbk", "replace"))
    print("   " + out)


def corner_opaque(png):
    """左下角 16×16(小箭头所在)的不透明像素数。"""
    from PIL import Image
    img = Image.open(png).convert("RGBA")
    box = img.crop((0, img.height - 16, 16, img.height))
    return sum(1 for p in box.getdata() if p[3] > 8)


def read_value(root):
    try:
        with winreg.OpenKey(root, SHELL_ICONS) as k:
            v, _ = winreg.QueryValueEx(k, VALUE_NAME)
            return v
    except OSError:
        return None


def write_value(root, data):
    with winreg.CreateKey(root, SHELL_ICONS) as k:
        winreg.SetValueEx(k, VALUE_NAME, 0, winreg.REG_SZ, data)


def clear_value(root):
    """删 29 值;Shell Icons 键里没别的值就把整键删掉,不留空壳。"""
    try:
        with winreg.OpenKey(root, SHELL_ICONS, 0, winreg.KEY_SET_VALUE) as k:
            winreg.DeleteValue(k, VALUE_NAME)
    except FileNotFoundError:
        return
    except PermissionError:
        print("   (删值被拒:权限不足)")
        return
    try:
        with winreg.OpenKey(root, SHELL_ICONS) as k:
            if winreg.QueryInfoKey(k)[1] == 0:
                winreg.DeleteKey(root, SHELL_ICONS)
    except OSError:
        pass


def refresh_icons():
    # 与 shortcut_arrow.cs RefreshIcons() 三件套一致(改一处改两处):
    # SHChangeNotify(ASSOCCHANGED) 才打得穿系统图标缓存(实测光 ie4uinit+广播不够)
    import ctypes
    ctypes.windll.shell32.SHChangeNotify(0x08000000, 0, None, None)
    subprocess.run(["ie4uinit.exe", "-show"], capture_output=True,
                   creationflags=CREATE_NO_WINDOW)


def main():
    use_hklm = "--hklm" in sys.argv
    root = winreg.HKEY_LOCAL_MACHINE if use_hklm else winreg.HKEY_CURRENT_USER
    tag = "HKLM" if use_hklm else "HKCU"
    workdir = os.path.join(tempfile.gettempdir(), "KeepAwake_probe_arrow")
    os.makedirs(workdir, exist_ok=True)

    blank = os.path.join(workdir, "blank.ico")
    make_blank_ico(blank)
    from PIL import Image
    bi = Image.open(blank).convert("RGBA")
    if bi.size != (16, 16) or bi.getchannel("A").getextrema() != (0, 1):
        raise SystemExit("blank.ico 自检失败:不是 16x16、仅一像素 alpha=1(%r,%r)"
                         % (bi.size, bi.getchannel("A").getextrema()))
    print("[0] blank.ico 合法:16x16 全透明,%d 字节" % os.path.getsize(blank))

    # 成品 exe 手写的 ICO 与探针构造必须逐字节一致(防 C#/Python 两份构造漂移)
    exe = os.path.join(HERE, "..", "build", "KeepAwake.exe")
    if os.path.isfile(exe):
        exe_blank = os.path.join(workdir, "blank_from_exe.ico")
        subprocess.run([exe, "--write-blank-ico", exe_blank], capture_output=True,
                       creationflags=CREATE_NO_WINDOW)
        with open(blank, "rb") as f1, open(exe_blank, "rb") as f2:
            same = f1.read() == f2.read()
        print("[0b] 成品 exe 手写的 blank.ico 与探针构造逐字节%s" % ("一致" if same else "不一致!!"))
        if not same:
            return 1

    probe_exe = compile_probe(workdir)
    lnk = make_test_lnk(workdir)
    before_png = os.path.join(workdir, "before.png")
    snapshot(probe_exe, lnk, before_png)
    n0 = corner_opaque(before_png)
    print("[1] baseline 左下角 16x16 不透明像素:%d" % n0)
    if n0 == 0:
        print("!! baseline 角落全透明——SHGetFileInfo 没把箭头合成进图标,本探针判据失效,结论不可用")
        return 1

    old = read_value(root)
    print("[2] %s Shell Icons\\29 原值:%r → 写 %s" % (tag, old, blank))
    try:
        write_value(root, blank)
    except PermissionError:
        print("!! 写 %s 被拒:需要管理员窗口跑 --hklm" % tag)
        return 1
    try:
        refresh_icons()
        after_png = os.path.join(workdir, "after.png")
        snapshot(probe_exe, lnk, after_png)
        n1 = corner_opaque(after_png)
        print("[3] 覆盖后左下角 16x16 不透明像素:%d" % n1)
        verdict = "生效(箭头消失)" if n1 <= n0 // 2 else "未生效(角落像素没减少)"
        print("[4] 结论:%s 的 Shell Icons\\29 覆盖位 → %s" % (tag, verdict))
    finally:
        if old is None:
            clear_value(root)
            print("[5] 已恢复原状:29 值删净")
        else:
            write_value(root, old)
            print("[5] 已恢复原状:29 还原为 %r" % old)
        refresh_icons()
    return 0


if __name__ == "__main__":
    sys.exit(main())
