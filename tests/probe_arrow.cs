// probe_arrow.cs — 探针:SHGetFileInfo 实拍 .lnk 的合成图标(含快捷方式小箭头),存 PNG
// 用法:probe_arrow.exe <目标文件> <输出.png>
// 由 tests/probe_arrow.py 现场编译调用:箭头若在,左下角有一团白色不透明像素;
// 覆盖位生效后同一位置应只剩透明——用像素当裁判,不靠人眼。
// 注意:C# 5 语法(系统 csc 不认 C#6+)。

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

static class ProbeArrowApp {

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    const uint SHGFI_ICON = 0x00000100;
    const uint SHGFI_LARGEICON = 0x00000000;

    static int Main(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("usage: probe_arrow.exe <file> <out.png>");
            return 2;
        }
        var shfi = new SHFILEINFO();
        SHGetFileInfo(args[0], 0, ref shfi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
            SHGFI_ICON | SHGFI_LARGEICON);
        if (shfi.hIcon == IntPtr.Zero) {
            Console.Error.WriteLine("SHGetFileInfo 未取到图标");
            return 1;
        }
        using (var icon = Icon.FromHandle(shfi.hIcon))
        using (var bmp = icon.ToBitmap()) {
            bmp.Save(args[1], ImageFormat.Png);
            Console.WriteLine("saved {0} ({1}x{2})", args[1], bmp.Width, bmp.Height);
        }
        return 0;
    }
}
