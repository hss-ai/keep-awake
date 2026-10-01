// explorer_tab_merge.cs — 资源管理器单窗口合并(参照 ExplorerTabUtility 的思路)
//
// 目标:Windows 全程只有 1 个资源管理器窗口,后开的自动变成既有窗口里的新标签页。
//
// 机制:
//   1) SetWinEventHook(EVENT_OBJECT_CREATE) 监听新窗口,按类名 CabinetWClass 过滤
//      (用 CREATE 而非 SHOW:还原最小化窗口不会再触发误合并);
//   2) 经 Shell COM(IShellWindows/CLSID 9BA05972-...)轮询取新窗口的 LocationURL;
//      文件系统路径 file:///C:/xx → C:\xx;虚拟文件夹(主页/此电脑)URL 为空;
//   3) 若存在另一个资源管理器窗口:置前(AttachThreadInput 解前台锁)→ Ctrl+T 开新标签
//      → Ctrl+L 聚焦地址栏 → SendInput 逐字符输入路径 → 回车 → 关闭新窗口;
//      虚拟文件夹场景退化为只开新标签(新标签默认落在主页,与 Win+E 行为一致);
//   4) 若它是第一个资源管理器窗口:保留,它就是"那一个"。
//
// 限制:依赖 Win11 22H2+ 资源管理器原生标签页;合并瞬间有按键注入,期间请勿抢键盘。
// 线程模型:钩子装在 UI 线程(消息循环所在),合并工作在独立 STA 线程(COM 需要)。
// 注意:C# 5 语法(系统 csc 不认 C#6+);编译需加 /r:Microsoft.CSharp.dll(dynamic COM)。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace KeepAwake {

internal sealed class ExplorerTabMerger : IDisposable {

    private delegate void WinEventDelegate(IntPtr hHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx; public int Dy; public uint MouseData; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mi; [FieldOffset(0)] public KEYBDINPUT Ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion U; }

    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_L = 0x4C;
    private const ushort VK_T = 0x54;

    private const string ExplorerWindowClass = "CabinetWClass";

    private IntPtr hook;
    private readonly WinEventDelegate hookProc;          // 持引用防 GC 回收原生回调
    private readonly Thread worker;
    private readonly AutoResetEvent wakeup = new AutoResetEvent(false);
    private readonly Queue<IntPtr> pending = new Queue<IntPtr>();
    private readonly Dictionary<IntPtr, DateTime> seen = new Dictionary<IntPtr, DateTime>();
    private volatile bool enabled;
    private bool disposed;

    public ExplorerTabMerger() {
        hookProc = OnWinEvent;
        worker = new Thread(WorkerLoop);
        worker.SetApartmentState(ApartmentState.STA);    // Shell COM 走 STA 稳
        worker.IsBackground = true;
        worker.Name = "ExplorerTabMerger";
        worker.Start();
    }

    /// <summary>开关合并(须在 UI 线程调用:WinEvent 钩子要挂在有消息循环的线程上)。</summary>
    public bool Enabled {
        get { return enabled; }
        set {
            enabled = value;
            if (value) InstallHook();
            else UninstallHook();
        }
    }

    private void InstallHook() {
        if (disposed || hook != IntPtr.Zero) return;
        hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE, IntPtr.Zero,
            hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (hook == IntPtr.Zero) System.Diagnostics.Debug.WriteLine("ExplorerTabMerger: SetWinEventHook 失败");
    }

    private void UninstallHook() {
        if (hook == IntPtr.Zero) return;
        UnhookWinEvent(hook);
        hook = IntPtr.Zero;
    }

    // UI 线程回调:只做最便宜的过滤,活儿转给 worker
    private void OnWinEvent(IntPtr hHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime) {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        var sb = new StringBuilder(64);
        if (GetClassName(hwnd, sb, 64) == 0 || sb.ToString() != ExplorerWindowClass) return;
        lock (pending) { pending.Enqueue(hwnd); }
        wakeup.Set();
    }

    private void WorkerLoop() {
        while (!disposed) {
            wakeup.WaitOne();
            if (disposed) break;
            while (true) {
                IntPtr hwnd;
                lock (pending) {
                    if (pending.Count == 0) break;
                    hwnd = pending.Dequeue();
                }
                try {
                    if (enabled) HandleNewWindow(hwnd);
                } catch {
                    // 单个窗口处理异常不拖垮守护
                }
            }
        }
    }

    private void HandleNewWindow(IntPtr newHwnd) {
        // 同一窗口的重复事件 5 秒内只处理一次
        lock (seen) {
            DateTime last;
            if (seen.TryGetValue(newHwnd, out last) && (DateTime.Now - last).TotalSeconds < 5) return;
            seen[newHwnd] = DateTime.Now;
            if (seen.Count > 64) PruneSeen();
        }

        // 等 explorer 完成导航:LocationURL 出现文件路径即成功;连续多次空 URL = 虚拟文件夹
        string path = null;
        int emptySeen = 0;
        for (int i = 0; i < 20; i++) {
            if (!IsWindow(newHwnd)) return;               // 窗口已被用户关掉
            string probe = GetExplorerLocation(newHwnd);
            if (probe != null) {
                if (probe.Length > 0) { path = probe; break; }
                emptySeen++;
                if (emptySeen >= 3 && IsWindowVisible(newHwnd)) { path = ""; break; }
            }
            Thread.Sleep(200);
        }
        if (path == null) return;                          // 4 秒没等到导航信息,放弃,不动用户窗口

        IntPtr target = FindTargetWindow(newHwnd);
        if (target == IntPtr.Zero) return;                 // 它就是第一个资源管理器窗口,留着

        if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
        ForceForeground(target);
        Thread.Sleep(150);
        SendCtrlCombo(VK_T);                               // 新标签页(默认落在主页)
        Thread.Sleep(300);
        if (!string.IsNullOrEmpty(path)) {
            SendCtrlCombo(VK_L);                           // 聚焦地址栏(全选当前路径)
            Thread.Sleep(130);
            TypeUnicode(path);
            Thread.Sleep(70);
            KeyTap(VK_RETURN);
            Thread.Sleep(180);
        }
        if (IsWindow(newHwnd)) PostMessage(newHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    // ==== 查询 ====

    /// <summary>取某资源管理器窗口当前路径。null=窗口尚未登记;/""=虚拟文件夹;其余=文件系统路径。</summary>
    private static string GetExplorerLocation(IntPtr hwnd) {
        try {
            Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            dynamic shellWindows = Activator.CreateInstance(t);
            try {
                foreach (dynamic w in shellWindows) {
                    try {
                        long wh = Convert.ToInt64(w.HWND);
                        if (new IntPtr(wh) == hwnd) return UrlToPath((string)w.LocationURL);
                    } catch {
                        // 个别条目不是标准浏览器窗口,跳过
                    } finally {
                        try { Marshal.ReleaseComObject(w); } catch { }
                    }
                }
            } finally {
                try { Marshal.ReleaseComObject(shellWindows); } catch { }
            }
        } catch {
        }
        return null;
    }

    private static string UrlToPath(string url) {
        if (string.IsNullOrEmpty(url)) return "";
        if (!url.StartsWith("file:///")) return url;       // 非 file 协议原样返回
        string s = Uri.UnescapeDataString(url.Substring(8)); // file:///C:/xx → C:/xx
        return s.Replace('/', '\\');
    }

    private static IntPtr FindTargetWindow(IntPtr exclude) {
        IntPtr best = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var sb = new StringBuilder(64);
            if (GetClassName(h, sb, 64) != 0 && sb.ToString() == ExplorerWindowClass
                && IsWindowVisible(h) && h != exclude) {
                best = h;
                return false;                              // EnumWindows 按 z 序先见顶层,取最上面的那个
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private void PruneSeen() {
        List<IntPtr> dead = null;
        lock (seen) {
            foreach (KeyValuePair<IntPtr, DateTime> kv in seen) {
                if ((DateTime.Now - kv.Value).TotalMinutes > 1) {
                    if (dead == null) dead = new List<IntPtr>();
                    dead.Add(kv.Key);
                }
            }
            if (dead != null) foreach (IntPtr h in dead) seen.Remove(h);
        }
    }

    // ==== 输入注入 ====

    private static INPUT KeyDown(ushort vk, ushort scan, uint flags) {
        INPUT i = new INPUT();
        i.Type = INPUT_KEYBOARD;
        i.U.Ki.Vk = vk;
        i.U.Ki.Scan = scan;
        i.U.Ki.Flags = flags;
        return i;
    }

    private static void SendKeys(params INPUT[] inputs) {
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendCtrlCombo(ushort vk) {
        SendKeys(
            KeyDown(VK_CONTROL, 0, 0), KeyDown(vk, 0, 0),
            KeyDown(vk, 0, KEYEVENTF_KEYUP), KeyDown(VK_CONTROL, 0, KEYEVENTF_KEYUP));
    }

    private static void KeyTap(ushort vk) {
        SendKeys(KeyDown(vk, 0, 0), KeyDown(vk, 0, KEYEVENTF_KEYUP));
    }

    private static void TypeUnicode(string text) {
        foreach (char ch in text) {
            SendKeys(
                KeyDown(0, ch, KEYEVENTF_UNICODE),
                KeyDown(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
    }

    private static void ForceForeground(IntPtr hwnd) {
        IntPtr fg = GetForegroundWindow();
        uint fgPid;
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out fgPid);
        uint targetPid;
        uint targetThread = GetWindowThreadProcessId(hwnd, out targetPid);
        uint me = GetCurrentThreadId();
        bool attachedFg = false;
        bool attachedTarget = false;
        try {
            if (fgThread != 0 && fgThread != me) attachedFg = AttachThreadInput(me, fgThread, true);
            if (targetThread != me) attachedTarget = AttachThreadInput(me, targetThread, true);
            // 轻点一下 Alt 解 SetForegroundWindow 的前台锁
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
        } finally {
            if (attachedTarget) AttachThreadInput(me, targetThread, false);
            if (attachedFg) AttachThreadInput(me, fgThread, false);
        }
    }

    public void Dispose() {
        disposed = true;
        Enabled = false;
        wakeup.Set();
        worker.Join(500);                                  // worker 是后台线程,合并不中断也就这一回
    }
}

}
