// explorer_tab_merge.cs — 资源管理器单窗口合并(参照 ExplorerTabUtility 的思路)
//
// 目标:Windows 全程只有 1 个资源管理器窗口,后开的自动变成既有窗口里的新标签页。
//
// 机制:
//   1) SetWinEventHook(EVENT_OBJECT_CREATE) 监听新窗口,按类名 CabinetWClass 过滤
//      (用 CREATE 而非 SHOW:还原最小化窗口不会再触发误合并);
//   2) 经 Shell COM(IShellWindows/CLSID 9BA05972-...)轮询取新窗口的 LocationURL;
//      文件系统路径 file:///C:/xx → C:\xx;虚拟文件夹(主页/此电脑)URL 为空;
//      ⚠️ 外部程序唤起的窗口常先落主页再导航,空 URL 是过渡态——须等它稳定 ~3s
//      且标题不再变化才判虚拟,否则合并标签会停在主页(v1.1.0 已修,日志在 %TEMP%);
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
using System.IO;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowText")]
    private static extern int GetWindowTextNative(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
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
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

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
    private const int SW_MINIMIZE = 6;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_L = 0x4C;
    private const ushort VK_T = 0x54;
    private const ushort VK_V = 0x56;

    private const string ExplorerWindowClass = "CabinetWClass";

    // 诊断日志(合并行为出问题时看 %TEMP%\KeepAwake_merge.log;超 512KB 自动重开)
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "KeepAwake_merge.log");

    private static void Log(string msg) {
        try {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 524288) fi.Delete();
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + msg + Environment.NewLine);
        } catch {
        }
    }

    private static string GetTitle(IntPtr hWnd) {
        var sb = new StringBuilder(256);
        return GetWindowTextNative(hWnd, sb, 256) > 0 ? sb.ToString() : "";
    }

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
        Log("新窗口 " + newHwnd + " 标题=" + GetTitle(newHwnd));
        Thread.Sleep(200);                                   // 轻微节流:上一轮合并刚结束 explorer 可能还在忙

        // 等 explorer 完成导航。两个终态:
        //   a) LocationURL 出现 file: 路径 → 文件系统路径;
        //   b) 空 URL 稳定 ~3 秒(标题也不再变)且窗口可见 → 虚拟文件夹(主页/此电脑)。
        // ⚠️ 从其他程序打开的窗口常「先落主页、再导航到目标」——空 URL 是过渡态,
        //    标题一变就重置耐心;提前断定虚拟会把合并出来的标签停在主页(v1.1.0 的 bug)。
        string path = null;
        string lastTitle = null;
        int emptyRun = 0;
        int poll = 0;
        for (; poll < 40; poll++) {                          // 最长 ~8 秒
            if (!IsWindow(newHwnd)) { Log("窗口已被关闭,放弃"); return; }
            string title = GetTitle(newHwnd);
            if (lastTitle != null && title != lastTitle) emptyRun = 0;   // 还在导航
            lastTitle = title;
            string probe = GetExplorerLocation(newHwnd);
            if (probe != null && probe.Length > 0) { path = probe; break; }
            if (probe != null && IsWindowVisible(newHwnd)) emptyRun++;
            if (emptyRun >= 15) { path = ""; break; }        // ~3s 稳定空 URL → 虚拟文件夹
            Thread.Sleep(200);
        }
        if (path == null) {
            Log("等待 " + (poll * 200 / 1000.0) + "s 未见导航信息,跳过合并(窗口保留)");
            return;
        }
        Log("路径判定[" + poll + "轮]: " + (path.Length == 0 ? "(虚拟文件夹→主页标签)" : path));

        IntPtr target = FindTargetWindow(newHwnd);
        if (target == IntPtr.Zero) { Log("无既有窗口,保留为首窗"); return; }

        bool merged = false;
        if (path.Length > 0) {
            // 首选:Shell COM Navigate2(navOpenInNewTab)——零按键:不经前台、不吃输入法、不抢焦点
            if (NavigateNewTab(target, path)) {
                for (int i = 0; i < 10 && !TabExistsInWindow(target, path); i++) Thread.Sleep(200);
                merged = TabExistsInWindow(target, path);
                Log("COM 新标签导航: " + (merged ? "OK" : "已调用但标签未到位"));
            } else {
                Log("COM Navigate2 调用失败");
            }
        }

        if (!merged && path.Length > 0) {
            // 兜底:键盘注入(前台确认→Ctrl+T→Ctrl+L 输入路径;IME 摘除+重试+剪贴板粘贴)
            if (BringToFrontVerified(target)) {
                SendCtrlCombo(VK_T);
                Thread.Sleep(400);
                NavigateTarget(target, path);
                Thread.Sleep(500);
                if (!TabExistsInWindow(target, path)) {
                    Log("键盘首轮未见目标标签(当前标签: " + TabsSnapshot(target) + "),重试一次");
                    NavigateTarget(target, path);
                    Thread.Sleep(500);
                }
                if (!TabExistsInWindow(target, path)) {
                    Log("重试仍未到位(当前标签: " + TabsSnapshot(target) + "),改用剪贴板粘贴兜底");
                    NavigateByClipboard(target, path);
                    Thread.Sleep(600);
                }
                merged = TabExistsInWindow(target, path);
                Log("键盘兜底导航验证: " + (merged ? "OK" : "仍失败"));
            } else {
                Log("无法把目标窗口置前,跳过键盘兜底");
            }
        }

        if (!merged && path.Length == 0) {
            // 虚拟文件夹(主页等):Ctrl+T 新标签默认就落在主页,与被并窗口一致
            if (BringToFrontVerified(target)) {
                SendCtrlCombo(VK_T);
                Thread.Sleep(400);
                merged = true;
                Log("虚拟文件夹:已开主页标签");
            } else {
                Log("虚拟文件夹但无法置前,放弃");
            }
        }

        if (!merged) {
            // 任何一条路都没走通:新窗口必须保留,宁可不合并也不丢用户的文件夹
            Log("并入失败,新窗口保留");
            return;
        }

        if (IsWindow(newHwnd)) {
            PostMessage(newHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        ForceForeground(target);                              // 尽力把目标窗口带到用户眼前,成败不计
        Log("并入完成 " + target + (path.Length == 0 ? "(主页标签)" : " → " + path));
    }

    /// <summary>把目标窗口置前并核实(带最小化/还原强制激活兜底)。失败返回 false——
    /// 此时绝不能注入按键。</summary>
    private static bool BringToFrontVerified(IntPtr target) {
        if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
        for (int i = 0; i < 3; i++) {
            ForceForeground(target);
            Thread.Sleep(150);
            if (GetForegroundWindow() == target) return true;
            ShowWindow(target, SW_MINIMIZE);
            Thread.Sleep(60);
            ShowWindow(target, SW_RESTORE);
            Thread.Sleep(200);
            if (GetForegroundWindow() == target) return true;
        }
        return false;
    }

    /// <summary>在目标窗口里以新标签打开 path:IWebBrowser2.Navigate2 + navOpenInNewTab(0x800)。
    /// 零按键、不吃输入法、不依赖前台。</summary>
    private static bool NavigateNewTab(IntPtr targetHwnd, string path) {
        try {
            Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            dynamic shellWindows = Activator.CreateInstance(t);
            try {
                foreach (dynamic w in shellWindows) {
                    try {
                        if (new IntPtr(Convert.ToInt64(w.HWND)) != targetHwnd) continue;
                        w.Navigate2(path, 0x800);            // navOpenInNewTab
                        return true;
                    } catch {
                    } finally {
                        try { Marshal.ReleaseComObject(w); } catch { }
                    }
                }
            } finally {
                try { Marshal.ReleaseComObject(shellWindows); } catch { }
            }
        } catch {
        }
        return false;
    }

    /// <summary>在目标窗口当前标签导航到 path:Ctrl+L 聚焦地址栏,打字期间临时摘掉
    /// 该编辑框的输入法上下文(否则中文输入法会吞掉 unicode 按键、回车变成确认候选词),
    /// 输入路径回车后恢复。</summary>
    private static void NavigateTarget(IntPtr targetHwnd, string path) {
        SendCtrlCombo(VK_L);
        Thread.Sleep(160);
        uint pid;
        uint targetThread = GetWindowThreadProcessId(targetHwnd, out pid);
        uint me = GetCurrentThreadId();
        bool attached = targetThread != me && AttachThreadInput(me, targetThread, true);
        IntPtr focus = IntPtr.Zero;
        IntPtr oldImc = IntPtr.Zero;
        bool imeOff = false;
        try {
            focus = GetFocus();                              // 附线后读到的是目标线程的焦点编辑框
            if (focus != IntPtr.Zero) {
                oldImc = ImmAssociateContext(focus, IntPtr.Zero);
                imeOff = true;
            }
            if (!attached || focus == IntPtr.Zero)
                Log("注意: 附线=" + attached + " 焦点=" + focus + "(IME 摘除未生效,输入可能被输入法吃掉)");
            TypeUnicode(path);
            Thread.Sleep(80);
            KeyTap(VK_RETURN);
            Thread.Sleep(120);                               // 让导航吃到回车再恢复输入法
        } finally {
            if (imeOff) ImmAssociateContext(focus, oldImc);
            if (attached) AttachThreadInput(me, targetThread, false);
        }
    }

    /// <summary>兜底导航:Ctrl+L 后粘贴路径再回车。粘贴不走键盘注入,天然绕过输入法;
    /// 代价是短暂占用剪贴板(仅文本,用完尽力恢复)。</summary>
    private static void NavigateByClipboard(IntPtr targetHwnd, string path) {
        string prev = null;
        try {
            if (System.Windows.Forms.Clipboard.ContainsText()) prev = System.Windows.Forms.Clipboard.GetText();
        } catch {
        }
        try {
            System.Windows.Forms.Clipboard.SetText(path);
        } catch {
            Log("剪贴板写入失败,兜底放弃");
            return;
        }
        try {
            SendCtrlCombo(VK_L);
            Thread.Sleep(160);
            SendCtrlCombo(VK_V);                             // 'V'
            Thread.Sleep(120);
            KeyTap(VK_RETURN);
            Thread.Sleep(200);
        } finally {
            try {
                if (prev != null) System.Windows.Forms.Clipboard.SetText(prev);
            } catch {
            }
        }
    }

    /// <summary>目标窗口名下(同 HWND 的每个标签各有一条 Shell 记录)是否已有位于 path 的标签。</summary>
    private static bool TabExistsInWindow(IntPtr hwnd, string path) {
        return TabsSnapshotOf(hwnd, path, true).Length > 0;
    }

    /// <summary>诊断用:列出目标窗口当前全部标签的路径("; " 分隔)。</summary>
    private static string TabsSnapshot(IntPtr hwnd) {
        return TabsSnapshotOf(hwnd, null, false);
    }

    /// <summary>一次枚举两用:path 为 null 返回全部标签快照串;否则命中同路径标签时返回该路径。</summary>
    private static string TabsSnapshotOf(IntPtr hwnd, string path, bool matchOnly) {
        var tabs = new List<string>();
        try {
            Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            dynamic shellWindows = Activator.CreateInstance(t);
            try {
                foreach (dynamic w in shellWindows) {
                    try {
                        if (new IntPtr(Convert.ToInt64(w.HWND)) != hwnd) continue;
                        string p = UrlToPath((string)w.LocationURL);
                        if (matchOnly) {
                            if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return p;
                        } else {
                            tabs.Add(p.Length == 0 ? "(虚拟)" : p);
                        }
                    } catch {
                    } finally {
                        try { Marshal.ReleaseComObject(w); } catch { }
                    }
                }
            } finally {
                try { Marshal.ReleaseComObject(shellWindows); } catch { }
            }
        } catch {
        }
        return string.Join("; ", tabs.ToArray());
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
        if (url.StartsWith("file:///"))                     // 本地盘 file:///C:/xx → C:\xx
            return Uri.UnescapeDataString(url.Substring(8)).Replace('/', '\\');
        if (url.StartsWith("file://"))                      // 网络盘 file://server/share → \\server\share
            return Uri.UnescapeDataString(url.Substring(5)).Replace('/', '\\');
        return url;                                         // 非 file 协议原样返回
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
