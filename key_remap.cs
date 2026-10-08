// key_remap.cs — 全局键映射:F2 → Ctrl+W、左 Win → 左 Ctrl(低级键盘钩子)
//
// 背景:用户此前装 PowerToys 只为 Keyboard Manager 的一条映射(F2→Ctrl+W),
// 集成进托盘后即可卸掉 PowerToys;左 Win → 左 Ctrl 是后加的第二条(键盘上
// Win 键易误触开始菜单,改充 Ctrl 物尽其用)。
//
// 机制(两条映射共用一个 WH_KEYBOARD_LL 钩子,各自独立开关):
//   1) 低级钩子拦在全系统键盘流最前面,装在独立线程(自有 GetMessage 消息循环):
//      UI 线程卡顿不影响按键,钩子慢也不拖 UI;
//   2) F2 按下:吞掉原键 → 若此刻 Ctrl 未按下则补按左 Ctrl → 按下 W;
//      F2 抬起:吞掉 → 抬起 W → 若 Ctrl 是我们补按的才一并抬起
//      (用户自己按着的 Ctrl 绝不替他抬,松开后仍保持);
//   3) 左 Win 按下:吞掉原键 → 若此刻 Ctrl 未按下则补按左 Ctrl;抬起:吞掉 →
//      补抬(同样是"自己按着的 Ctrl 不碰")。左 Win 从此等于左 Ctrl:
//      开始菜单、Win+D/E/L 等系统快捷键在左 Win 上失效——这正是映射的
//      预期语义,要开开始菜单用右 Win 或菜单里关掉本映射;
//   4) keyup 只在对应 keydown 是我们吞掉时才吞:按住原键期间开关状态翻转
//      (如映射从关到开:keydown 直通、keyup 却被吞)会让修饰键缺 up 而粘键,
//      系统从此认为 Win 一直按着、所有按键变 Win+ 组合——v1.4.3 修
//      (v1.4.2 及之前 up 只看开关不看 down 归属,理论边界,顺手补上);
//   5) 系统自动重复的按下只吞不重发——一次物理按压 = 一次输出,
//      长按 F2 不会连环关标签、长按 Win 也不会重复注入 Ctrl;
//   6) 自家注入的事件带 dwExtraInfo 魔数("KAWA"),钩子看到直接放行,
//      杜绝任何自反馈;外部程序注入的原键(无魔数)照常映射——
//      输出是目标键不是原键,天然不成环;
//   7) 托盘开关只翻 volatile 标志,钩子常驻装着:关闭时全部直通
//      (装/卸钩子要跨线程编排没必要;直通开销可忽略,PowerToys 同样常驻钩子);
//      按住原键期间关闭映射或退出程序,keyup 路径/消息循环退出前都会补抬欠下的键,
//      不会粘键;回调整体 try/catch,钩子线程异常绝不带崩进程。
// 语义细节:按住其它修饰键(Shift/Alt/另一侧 Win)再按 F2,修饰键自然保留
//   (与物理按键模型一致,PowerToys 键映射同语义);F2 本义(重命名)、
//   左 Win 本义(开始菜单)全局被替换。
// 限制:安全桌面(UAC 弹窗)与管理员窗口的按键不经本钩子
//   (未提权进程的固有限制,PowerToys 不提权时同样如此)。
// 注意:C# 5 语法(系统 csc 不认 C#6+),不要用字符串插值 $""、?. 等。

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeepAwake {

internal sealed class KeyRemapper : IDisposable {

    // 映射定义:原键 → 目标。要改映射只动这些常量 + 菜单文案。
    private const ushort VkF2 = 0x71;      // VK_F2(原键,映射一)
    private const ushort VkW = 0x57;       // 'W'
    private const ushort VkLWin = 0x5B;    // VK_LWIN(原键,映射二)
    private const ushort VkLCtrl = 0xA2;   // VK_LCONTROL(映射一/二共用的补按目标)

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
    private struct MOUSEINPUT { public int Dx; public int Dy; public int MouseData; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
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
    private volatile bool f2Enabled;
    private volatile bool winEnabled;
    private volatile bool disposed;
    // 以下四项仅钩子线程访问
    private bool f2Down;        // F2 的 keydown 被我们吞了(欠一组抬起)
    private bool f2WeCtrl;      // F2 映射补按的 Ctrl 还欠抬起
    private bool winDown;       // 左 Win 的 keydown 被我们吞了
    private bool winWeCtrl;     // Win 映射补按的 Ctrl 还欠抬起

    public KeyRemapper() {
        proc = HookCallback;
        thread = new Thread(RunLoop);
        thread.IsBackground = true;
        thread.Name = "KeyRemapper";
        thread.Start();
    }

    /// <summary>F2→Ctrl+W 开关(UI 线程调用;只翻标志位,钩子常驻,关闭即直通)。</summary>
    public bool F2Enabled {
        get { return f2Enabled; }
        set {
            if (f2Enabled == value) return;
            f2Enabled = value;
            AppLog.Write("键映射 F2→Ctrl+W: " + (value ? "开启" : "关闭"));
        }
    }

    /// <summary>左Win→左Ctrl 开关(同上)。</summary>
    public bool WinEnabled {
        get { return winEnabled; }
        set {
            if (winEnabled == value) return;
            winEnabled = value;
            AppLog.Write("键映射 Win(左)→Ctrl(左): " + (value ? "开启" : "关闭"));
        }
    }

    private void RunLoop() {
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) {
            AppLog.Write("键映射:WH_KEYBOARD_LL 安装失败 err=" + Marshal.GetLastWin32Error() + ",键映射不可用");
            return;
        }
        AppLog.Write("键映射:键盘钩子已安装(F2→Ctrl+W、Win左→Ctrl左)");
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        try { ReleaseF2Held(); ReleaseWinHeld(); } catch { }   // 退出(Dispose/WM_QUIT)时欠下的抬起要还
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    // 回调整体包 try/catch:钩子线程里任何未处理异常都会带崩整个进程,
    // 异常时尽力补抬欠下的键、放行事件——映射失效远好于托盘工具暴毙
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        try {
            return Handle(nCode, wParam, lParam);
        } catch {
            try { ReleaseF2Held(); ReleaseWinHeld(); } catch { }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }

    private IntPtr Handle(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode < HC_ACTION) return CallNextHookEx(hook, nCode, wParam, lParam);
        KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
        if (k.ExtraInfo == MagicPtr)
            return CallNextHookEx(hook, nCode, wParam, lParam);   // 自家注入的一律放行
        uint msg = unchecked((uint)wParam.ToInt64());
        bool downMsg = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool upMsg = msg == WM_KEYUP || msg == WM_SYSKEYUP;
        bool swallow;
        if (k.VkCode == VkF2) swallow = HandleF2(downMsg, upMsg);
        else if (k.VkCode == VkLWin) swallow = HandleLWin(downMsg, upMsg);
        else return CallNextHookEx(hook, nCode, wParam, lParam);
        return swallow ? new IntPtr(1) : CallNextHookEx(hook, nCode, wParam, lParam);
    }

    // F2 → Ctrl+W。返回 true = 吞掉本事件。
    private bool HandleF2(bool downMsg, bool upMsg) {
        if (downMsg) {
            if (!f2Enabled) return false;   // 映射已关:原样放行(keyup 侧仍会认账)
            if (!f2Down) {                  // 首次按下才发组合;自动重复只吞不发
                f2Down = true;
                // 只补按用户没按着的 Ctrl:他正按着 Ctrl 时只补 W,抬起也不动他的 Ctrl
                f2WeCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0;
                if (f2WeCtrl) SendKey(VkLCtrl, false);
                SendKey(VkW, false);
            }
            return true;
        }
        if (upMsg && f2Down) {
            // keydown 是我们吞的,keyup 才吞 + 还欠账;keydown 直通过的
            // (按住期间才开映射)keyup 也直通——否则 F2 永远卡在按下态
            ReleaseF2Held();
            return true;
        }
        return false;
    }

    // 左 Win → 左 Ctrl。返回 true = 吞掉本事件。
    private bool HandleLWin(bool downMsg, bool upMsg) {
        if (downMsg) {
            if (!winEnabled) return false;
            if (!winDown) {
                winDown = true;
                // 只补按用户没按着的 Ctrl(任一侧):他自己的 Ctrl 绝不重复、也绝不替他抬
                winWeCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0;
                if (winWeCtrl) SendKey(VkLCtrl, false);
            }
            return true;   // 吞掉(含自动重复:修饰键没有重复注入的意义)
        }
        if (upMsg && winDown) {
            // 同 F2:keyup 只吞我们吞过 keydown 的;直通过的 up 放行,
            // 否则修饰键缺 up 直接粘死——系统会认为 Win 一直按着
            ReleaseWinHeld();
            return true;
        }
        return false;
    }

    /// <summary>抬起 F2 映射注入且尚未抬起的键(仅钩子线程调用)。</summary>
    private void ReleaseF2Held() {
        if (!f2Down) return;
        f2Down = false;
        SendKey(VkW, true);
        if (f2WeCtrl) { SendKey(VkLCtrl, true); f2WeCtrl = false; }
    }

    /// <summary>抬起 Win 映射注入且尚未抬起的键(仅钩子线程调用)。</summary>
    private void ReleaseWinHeld() {
        if (!winDown) return;
        winDown = false;
        if (winWeCtrl) { SendKey(VkLCtrl, true); winWeCtrl = false; }
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
