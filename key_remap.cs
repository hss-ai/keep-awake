// key_remap.cs — 全局键映射:F2 → Ctrl+W(低级键盘钩子)
//
// 背景:用户此前装 PowerToys 只为 Keyboard Manager 的一条映射(F2→Ctrl+W),
// 集成进托盘后即可卸掉 PowerToys。
//
// 机制:
//   1) WH_KEYBOARD_LL 低级钩子拦在全系统键盘流最前面,装在独立线程(自有
//      GetMessage 消息循环):UI 线程卡顿不影响按键,钩子慢也不拖 UI;
//   2) F2 按下:吞掉原键 → 若此刻 Ctrl 未按下则补按左 Ctrl → 按下 W;
//      F2 抬起:吞掉 → 抬起 W → 若 Ctrl 是我们补按的才一并抬起
//      (用户自己按着的 Ctrl 绝不替他抬,松开 F2 后仍保持);
//   3) 系统自动重复的 F2 只吞不重发——一次物理按压 = 一次 Ctrl+W,
//      长按 F2 不会连环关标签;
//   4) 自家注入的事件带 dwExtraInfo 魔数("KAWA"),钩子看到直接放行,
//      杜绝任何自反馈;外部程序注入的 F2(无魔数)照常映射——
//      输出是 Ctrl+W 不是 F2,天然不成环;
//   5) 托盘开关只翻 volatile 标志,钩子常驻装着:关闭时全部直通
//      (装/卸钩子要跨线程编排没必要;直通开销可忽略,PowerToys 同样常驻钩子);
//      按住 F2 期间关闭映射或退出程序,keyup 路径/消息循环退出前都会补抬欠下的键,
//      不会粘键;回调整体 try/catch,钩子线程异常绝不带崩进程。
// 语义细节:按住其它修饰键(Shift/Alt/Win)再按 F2,修饰键自然保留
//   (与物理按键模型一致,PowerToys 键映射同语义);F2 本义(重命名)全局被替换。
// 限制:安全桌面(UAC 弹窗)与管理员窗口的按键不经本钩子
//   (未提权进程的固有限制,PowerToys 不提权时同样如此)。
// 注意:C# 5 语法(系统 csc 不认 C#6+),不要用字符串插值 $""、?. 等。

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeepAwake {

internal sealed class KeyRemapper : IDisposable {

    // 映射定义:原键 → 目标组合。要改映射只动这三行 + 菜单文案。
    private const ushort SourceVk = 0x71;     // VK_F2
    private const ushort TargetVk = 0x57;     // 'W'
    private const ushort TargetModVk = 0xA2;  // VK_LCONTROL

    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int VK_CONTROL = 0x11;      // GetAsyncKeyState 查的是"任一 Ctrl"

    private static readonly UIntPtr MagicExtra = new UIntPtr(0x4B415741); // "KAWA"
    private static readonly IntPtr MagicPtr = new IntPtr(0x4B415741);

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mi; [FieldOffset(0)] public KEYBDINPUT Ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion U; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    private readonly LowLevelProc proc;   // 持引用防 GC 回收原生回调
    private readonly Thread thread;
    private IntPtr hook;
    private uint threadId;
    private volatile bool enabled;
    private volatile bool disposed;
    private bool down;           // F2 处于按下态(仅钩子线程访问)
    private bool wePressedCtrl;  // Ctrl 是我们补按的(仅钩子线程访问)

    public KeyRemapper() {
        proc = HookCallback;
        thread = new Thread(RunLoop);
        thread.IsBackground = true;
        thread.Name = "KeyRemapper";
        thread.Start();
    }

    /// <summary>开关映射(UI 线程调用;只翻标志位,钩子常驻,关闭即全直通)。</summary>
    public bool Enabled {
        get { return enabled; }
        set {
            if (enabled == value) return;
            enabled = value;
            AppLog.Write("键映射 F2→Ctrl+W: " + (value ? "开启" : "关闭"));
        }
    }

    private void RunLoop() {
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) {
            AppLog.Write("键映射:WH_KEYBOARD_LL 安装失败 err=" + Marshal.GetLastWin32Error() + ",F2→Ctrl+W 不可用");
            return;
        }
        AppLog.Write("键映射:键盘钩子已安装(F2→Ctrl+W)");
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        try { ReleaseHeld(); } catch { }   // 退出(Dispose/WM_QUIT)时若 F2 还按着,欠的抬起要还
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    // 回调整体包 try/catch:钩子线程里任何未处理异常都会带崩整个进程,
    // 异常时尽力补抬欠下的键、放行事件——映射失效远好于托盘工具暴毙
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        try {
            return Handle(nCode, wParam, lParam);
        } catch {
            try { ReleaseHeld(); } catch { }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }

    private IntPtr Handle(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode < HC_ACTION) return CallNextHookEx(hook, nCode, wParam, lParam);
        KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
        if (k.VkCode != SourceVk || k.ExtraInfo == MagicPtr)
            return CallNextHookEx(hook, nCode, wParam, lParam);
        uint msg = unchecked((uint)wParam.ToInt64());
        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) {
            if (enabled) {
                if (!down) {   // 首次按下才发组合;自动重复只吞不发
                    down = true;
                    // 只补按用户没按着的 Ctrl:他正按着 Ctrl 时只补 W,抬起也不动他的 Ctrl
                    wePressedCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0;
                    if (wePressedCtrl) SendKey(TargetModVk, false);
                    SendKey(TargetVk, false);
                }
                return new IntPtr(1);   // 吞掉 F2(含自动重复)
            }
            // 映射已关:F2 原样放行(但下面 keyup 仍会清欠账)
        } else if (msg == WM_KEYUP || msg == WM_SYSKEYUP) {
            // 按住 F2 期间映射被关掉:keyup 直通可以,但欠下的 W/Ctrl 抬起必须还,
            // 否则它们永远卡在按下态(表现为 W 键粘键)
            ReleaseHeld();
            if (enabled) return new IntPtr(1);   // 吞掉 F2
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// <summary>抬起我们注入且尚未抬起的键(仅钩子线程调用)。</summary>
    private void ReleaseHeld() {
        if (!down) return;
        down = false;
        SendKey(TargetVk, true);
        if (wePressedCtrl) { SendKey(TargetModVk, true); wePressedCtrl = false; }
    }

    private static void SendKey(ushort vk, bool up) {
        INPUT[] one = new INPUT[1];
        one[0].Type = INPUT_KEYBOARD;
        one[0].U.Ki.Vk = vk;
        one[0].U.Ki.Flags = up ? KEYEVENTF_KEYUP : 0;
        one[0].U.Ki.ExtraInfo = MagicExtra;
        SendInput(1, one, Marshal.SizeOf(typeof(INPUT)));
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(500);   // 后台线程,极端情况卸不干净也随进程结束
    }
}

}
