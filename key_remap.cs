// key_remap.cs — 全局键映射:F2 → Ctrl+W、键盘右侧 Win 合成键 → 右 Ctrl(低级键盘钩子)
//
// 背景:用户此前装 PowerToys 只为 Keyboard Manager 的一条映射(F2→Ctrl+W),
// 集成进托盘后即可卸掉 PowerToys;"右侧 Win 键 → 右 Ctrl"是后加的第二条。
//
// 映射二的特殊性(2026-10-08 键盘录音实测):用户的无线键盘固件把右侧那个键
// 合成为三件套——0x5B(VK_LWIN) 起手,+10ms 0xA0(VK_LSHIFT),再 +10ms 0x86(VK_F23);
// 键盘上真正的左 Win 只发干净的 0x5B,标准右 Win(0x5C)从未出现。
//   → 识别策略:0x5B down 先暂扣(吞下不发),20ms 判定窗内出现 0xA0/0x86 伴随
//     = 键盘右侧那个键:三件全吞、注入右 Ctrl(与 F2 映射同一套"不碰用户自己的
//     Ctrl/长按不重复/欠抬必还"语义);
//   → 窗口内无伴随 = 真左 Win:原样补发(仅 20ms 不可感知延迟;Win+D 等组合键
//     的人类按压间隔 ≫20ms,不受影响),开始菜单/Win 快捷键照常。
//   → 标准键盘的 0x5C(VK_RWIN)直映射保留:换键盘/别的机器发标准码时直接生效。
//
// 机制(两条映射共用一个 WH_KEYBOARD_LL 钩子,各自独立开关):
//   1) 低级钩子拦在全系统键盘流最前面,装在独立线程(自有 GetMessage 消息循环):
//      UI 线程卡顿不影响按键,钩子慢也不拖 UI;0x5B 的 20ms 判定窗经线程消息
//      循环的 WM_TIMER 实现(SetTimer 无窗口定时器),钩子回调本身零阻塞;
//   2) F2 按下:吞掉原键 → 若此刻 Ctrl 未按下则补按左 Ctrl → 按下 W;
//      F2 抬起:吞掉 → 抬起 W → 若 Ctrl 是我们补按的才一并抬起
//      (用户自己按着的 Ctrl 绝不替他抬,松开后仍保持);
//   3) 右侧 Win 键(三件套)按下:吞掉全部 → 若此刻 Ctrl 未按下则补按右 Ctrl;
//      抬起:吞掉 → 补抬(同样是"自己按着的 Ctrl 不碰");
//   4) keyup 只在对应 keydown 是我们吞掉时才吞:按住原键期间开关状态翻转
//      (如映射从关到开:keydown 直通、keyup 却被吞)会让修饰键缺 up 而粘键,
//      系统从此认为 Win 一直按着、所有按键变 Win+ 组合——v1.4.3 修;
//   5) 系统自动重复的按下只吞不重发——一次物理按压 = 一次输出;
//   6) 自家注入的事件带 dwExtraInfo 魔数("KAWA"),钩子看到直接放行,
//      杜绝任何自反馈;外部程序注入的原键(无魔数)照常映射;
//   7) 托盘开关只翻 volatile 标志,钩子常驻装着:关闭时全部直通;
//      按住原键期间关闭映射或退出程序,keyup 路径/消息循环退出前都会补抬欠下的键,
//      不会粘键;回调整体 try/catch,钩子线程异常绝不带崩进程。
// 语义细节:按住其它修饰键再按 F2,修饰键自然保留(与物理按键模型一致);
//   F2 本义(重命名)、键盘右侧 Win 键的本义(Win 快捷键)全局被替换。
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
    private const ushort VkLCtrl = 0xA2;   // VK_LCONTROL(映射一的补按目标)
    private const ushort VkRWin = 0x5C;    // VK_RWIN(标准右 Win,直映射兜底)
    private const ushort VkRCtrl = 0xA3;   // VK_RCONTROL(映射二目标)
    private const ushort VkLWin = 0x5B;    // VK_LWIN(三件套起手键 = 真·左 Win 共用)
    private const ushort VkLShift = 0xA0;  // VK_LSHIFT(三件套伴随键之一)
    private const ushort VkF23 = 0x86;     // VK_F23(三件套伴随键之二,人手不会按)

    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_APP_DECIDE = 0x8001;  // 判定线程 → 钩子线程:0x5B 判定窗到期
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int VK_CONTROL = 0x11;      // GetAsyncKeyState 查的是"任一 Ctrl"
    private const int PendingWindowMs = 25;       // 三件套伴随键的判定窗(实测 +10ms/+20ms 到达)

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
    private readonly Thread decideThread;  // 0x5B 判定窗计时(见下)
    private readonly AutoResetEvent decideSignal;
    private volatile uint hookThreadId;    // 判定线程跨线程读,钩子线程写
    private IntPtr hook;
    private uint threadId;
    private volatile bool f2Enabled;
    private volatile bool winEnabled;
    private volatile bool disposed;
    // 以下状态仅钩子线程访问
    private bool f2Down;        // F2 的 keydown 被我们吞了(欠一组抬起)
    private bool f2WeCtrl;      // F2 映射补按的 Ctrl 还欠抬起
    private bool rwinDown;      // 标准右 Win(0x5C)的 keydown 被我们吞了
    private bool rwinWeCtrl;    // 标准右 Win 映射补按的 Ctrl 还欠抬起
    // 三件套状态机(键盘右侧 Win 合成键)
    private bool lwinPending;   // 0x5B down 已暂扣,判定窗内(等待伴随键或判定线程到期)
    private bool trioActive;    // 判定为三件套:0x5B 吞着,后续 0xA0/0x86 伴随键也吞
    private bool trioWeCtrl;    // 三件套映射补按的右 Ctrl 还欠抬起

    public KeyRemapper() {
        proc = HookCallback;
        thread = new Thread(RunLoop);
        thread.IsBackground = true;
        thread.Name = "KeyRemapper";
        // 判定窗计时线程:不能用 WM_TIMER——Win11 对后台进程的合成消息(WM_TIMER)
        // 节流,实测 25ms 请求被拖到数百 ms(端到端 H 场景实锤:补发晚于物理 keyup,
        // 左 Win down/up 错序粘键)。改由本线程 Sleep 计时后 PostThreadMessage 投递
        // 决定消息(posted 消息不被节流,GetMessage 立即唤醒)。Sleep 本线程不卡键盘流。
        decideSignal = new AutoResetEvent(false);
        decideThread = new Thread(DecideLoop);
        decideThread.IsBackground = true;
        decideThread.Name = "KeyRemapperDecide";
        decideThread.Start();
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

    /// <summary>键盘右侧 Win 键→右 Ctrl 开关(同上;含三件套识别与标准 0x5C 直映射)。</summary>
    public bool WinEnabled {
        get { return winEnabled; }
        set {
            if (winEnabled == value) return;
            winEnabled = value;
            AppLog.Write("键映射 Win(右)→Ctrl(右): " + (value ? "开启" : "关闭"));
        }
    }

    private void RunLoop() {
        threadId = GetCurrentThreadId();
        hookThreadId = threadId;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) {
            AppLog.Write("键映射:WH_KEYBOARD_LL 安装失败 err=" + Marshal.GetLastWin32Error() + ",键映射不可用");
            return;
        }
        AppLog.Write("键映射:键盘钩子已安装(F2→Ctrl+W、右侧Win合成键→Ctrl右)");
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) {
            // 线程消息(无窗口)Translate/Dispatch 无效,显式处理:
            if (msg.Message == WM_APP_DECIDE && msg.Hwnd == IntPtr.Zero) {
                OnPendingTimeout();
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        try { ReleaseF2Held(); ReleaseRwinHeld(); ReleaseTrioHeld(); } catch { }   // 欠下的抬起要还
        if (lwinPending) { lwinPending = false; SendKey(VkLWin, false); }  // 暂扣中的左 Win 还他
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    // 判定窗计时循环:钩子线程置信号 → 本线程睡满窗口 → 投递决定消息回钩子线程。
    private void DecideLoop() {
        while (!disposed) {
            try {
                if (!decideSignal.WaitOne(500)) continue;   // 空转唤醒节流到 2 次/秒
                Thread.Sleep(PendingWindowMs);
                uint tid = hookThreadId;
                if (tid != 0 && !disposed) PostThreadMessage(tid, WM_APP_DECIDE, IntPtr.Zero, IntPtr.Zero);
            } catch {
            }
        }
    }

    // 回调整体包 try/catch:钩子线程里任何未处理异常都会带崩整个进程,
    // 异常时尽力补抬欠下的键、放行事件——映射失效远好于托盘工具暴毙
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        try {
            return Handle(nCode, wParam, lParam);
        } catch {
            try { ReleaseF2Held(); ReleaseRwinHeld(); ReleaseTrioHeld(); } catch { }
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
        else if (k.VkCode == VkRWin) swallow = HandleRWin(downMsg, upMsg);
        else if (k.VkCode == VkLWin) swallow = HandleTrioLWin(downMsg, upMsg);
        else if (k.VkCode == VkLShift || k.VkCode == VkF23) swallow = HandleTrioSide(k.VkCode, downMsg, upMsg);
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

    // 标准右 Win(0x5C)→ 右 Ctrl 直映射(用户键盘不发此码,换标准键盘时生效)。
    private bool HandleRWin(bool downMsg, bool upMsg) {
        if (downMsg) {
            if (!winEnabled) return false;
            if (!rwinDown) {
                rwinDown = true;
                rwinWeCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0;
                if (rwinWeCtrl) SendKey(VkRCtrl, false);
            }
            return true;   // 吞掉(含自动重复:修饰键没有重复注入的意义)
        }
        if (upMsg && rwinDown) {
            ReleaseRwinHeld();
            return true;
        }
        return false;
    }

    // 三件套起手键 0x5B:键盘右侧那个键(合成 5B+A0+86)与真·左 Win(裸 5B)的岔路口。
    // down 先暂扣,判定窗(~25ms,判定线程计时)内出现伴随键 = 右侧键(转 HandleTrioSide 判定);
    // 窗口超时 = 真左 Win,OnPendingTimeout 里原样补发(魔数),up 直通与之配对。
    private bool HandleTrioLWin(bool downMsg, bool upMsg) {
        if (downMsg) {
            if (!winEnabled) return false;
            if (trioActive) return true;        // 长按右侧键:0x5B 自动重复,直接吞
            if (lwinPending) {
                // 极速连按(上一发的判定还在途):三件套的 5B 不会这么快重复,
                // 坐实上一发是纯左 Win,先补发它的 down,再暂扣本发
                SendKey(VkLWin, false);
            }
            lwinPending = true;                 // 暂扣,等伴随键或判定窗到期
            decideSignal.Set();
            return true;
        }
        if (upMsg) {
            if (trioActive) {                   // 右侧键抬起:吞 + 还右 Ctrl 欠账
                ReleaseTrioHeld();
                return true;
            }
            if (lwinPending) {
                // 判定窗内极速抬起(判定线程还没来得及投递):立即按"真左 Win"收尾——
                // 成对补发 down+up(魔数)再吞掉物理 up。⚠不能只补 down 放行物理 up:
                // 注入的新事件要排在本事件之后走链,系统会先收到 up 后收到 down(错序粘键)
                lwinPending = false;
                SendKey(VkLWin, false);
                SendKey(VkLWin, true);
                return true;
            }
            return false;                       // 真左 Win 的 up:直通(与补发的 down 配对)
        }
        return false;
    }

    // 三件套伴随键 0xA0/0x86:判定窗内到达 = 坐实键盘右侧键(全吞 + 注入右 Ctrl);
    // 三件套激活期间的重复/抬起也吞;其余(用户自己打字的 Shift 等)一律直通。
    private bool HandleTrioSide(uint vk, bool downMsg, bool upMsg) {
        if (trioActive) return true;    // 右侧键按住期间的伴随键 down/up/重复:全吞
        if (downMsg && lwinPending) {   // pending 存活期 ≈ 判定窗,窗内伴随键必属三件套
            // 坐实三件套:0x5B 的暂扣转为吞,注入右 Ctrl(用户自己按着的 Ctrl 不碰)
            lwinPending = false;
            trioActive = true;
            trioWeCtrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0;
            if (trioWeCtrl) SendKey(VkRCtrl, false);
            return true;
        }
        return false;   // 与三件套无关的 Shift/F23(用户打字等):原样直通
    }

    // 0x5B 判定窗到期(判定线程投递的 WM_APP_DECIDE):真·左 Win,原样补发。
    private void OnPendingTimeout() {
        if (!lwinPending) return;
        lwinPending = false;
        SendKey(VkLWin, false);
    }

    /// <summary>抬起 F2 映射注入且尚未抬起的键(仅钩子线程调用)。</summary>
    private void ReleaseF2Held() {
        if (!f2Down) return;
        f2Down = false;
        SendKey(VkW, true);
        if (f2WeCtrl) { SendKey(VkLCtrl, true); f2WeCtrl = false; }
    }

    /// <summary>抬起标准右 Win 映射注入且尚未抬起的键(仅钩子线程调用)。</summary>
    private void ReleaseRwinHeld() {
        if (!rwinDown) return;
        rwinDown = false;
        if (rwinWeCtrl) { SendKey(VkRCtrl, true); rwinWeCtrl = false; }
    }

    /// <summary>收尾三件套状态(仅钩子线程调用):右侧键欠下的右 Ctrl 抬起。</summary>
    private void ReleaseTrioHeld() {
        if (!trioActive) return;
        trioActive = false;
        if (trioWeCtrl) { SendKey(VkRCtrl, true); trioWeCtrl = false; }
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
