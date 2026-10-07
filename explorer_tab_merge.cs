// explorer_tab_merge.cs — 资源管理器单窗口合并(参照 ExplorerTabUtility 的思路)
//
// 目标:Windows 全程只有 1 个资源管理器窗口,后开的自动变成既有窗口里的新标签页。
//
// 机制:
//   1) SetWinEventHook(EVENT_OBJECT_CREATE) 监听新窗口,按类名 CabinetWClass 过滤
//      (用 CREATE 而非 SHOW:还原最小化窗口不会再触发误合并);
//   2) 经 Shell COM(IShellWindows/CLSID 9BA05972-...)轮询取新窗口的 LocationURL;
//      文件系统路径 file:///C:/xx → C:\xx;虚拟位置(回收站/此电脑)URL 为空,
//      改读 Document.Folder.Self.Path 得 ::{GUID} 命名空间路径(v1.4.1);
//      ⚠️ 外部程序唤起的窗口常先落主页再导航,空 URL 是过渡态——须等它稳定 ~3s
//      且标题不再变化才判虚拟,否则合并标签会停在主页(v1.1.0 已修,日志在 %TEMP%);
//   3) 若存在另一个资源管理器窗口(排除仍在待处理队列里的新窗口与其它虚拟桌面
//      上被 DWM cloak 的窗口):目标路径已是既有窗口里的某条标签时,UIA 直接激活
//      那条标签(免开重复标签,v1.4.2);否则 UIA 调用标签栏 AddButton 开真新标签 →
//      对新增的空标签原地 Navigate2 到目标路径(实测本机 Win11 的 Navigate2 新标签标志
//      navOpenInNewTab 无效,见 NavigateNewTab 注释);此路不通时退回键盘注入
//      (Ctrl+T/Ctrl+L+SendInput)兜底,打字/回车前复查前台,丢了就放弃合并;
//   4) 若它是第一个资源管理器窗口:保留,它就是"那一个"。
//
// 限制:依赖 Win11 22H2+ 资源管理器原生标签页;合并瞬间有按键注入,期间请勿抢键盘。
// 线程模型:WinEvent 钩子装在 UI 线程(消息循环所在);合并每窗口一个工作线程
//   (explorer 的 COM/UIA 会间歇性无响应,独立线程保证一个挂住不堵后续窗口)。
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
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int cb);
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
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int DWMWA_CLOAKED = 13;   // >0 表示窗口被 DWM 隐藏(典型:在其它虚拟桌面上)
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
    private readonly HashSet<IntPtr> inFlight = new HashSet<IntPtr>();   // 正在合并中的窗口
    private readonly Dictionary<IntPtr, DateTime> seen = new Dictionary<IntPtr, DateTime>();
    private volatile bool enabled;
    private bool disposed;
    // 键盘注入互斥门:多个合并线程同时进键盘兜底会互相打乱对方的按键序列
    private static readonly object KeyboardGate = new object();

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
                // 每个窗口的合并跑在独立线程:explorer 的 COM/UIA 都会间歇性无响应,
                // 单线程串行时一个调用挂住就堵死整条队列(2026-10-04 实测挂过 3 分钟);
                // 独立线程顶多这一个窗口不合并,后续窗口照常
                lock (inFlight) { inFlight.Add(hwnd); }
                Thread job = new Thread(delegate() {
                    try {
                        if (enabled) HandleNewWindow(hwnd);
                    } catch {
                        // 单个窗口处理异常不拖垮守护
                    } finally {
                        lock (inFlight) { inFlight.Remove(hwnd); }
                    }
                });
                job.IsBackground = true;
                job.Name = "ExplorerMergeJob";
                job.SetApartmentState(ApartmentState.STA);   // 原设计:Shell COM 走 STA 稳(老 worker 就是 STA)
                job.Start();
                Thread.Sleep(250);   // 轻微错峰:连开多窗时别让后到的被前一个抢先选为目标
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
            string probe = ComCall(1500, delegate() { return GetExplorerLocation(newHwnd); }, null);
            if (probe != null && probe.Length > 0) { path = probe; break; }
            if (probe != null && IsWindowVisible(newHwnd)) emptyRun++;
            if (emptyRun >= 15) {                            // ~3s 稳定空 URL → 虚拟位置
                // 虚拟位置(回收站/此电脑等)LocationURL 为空,但 Document.Folder.Self.Path
                // 能拿到 ::{GUID}(2026-10-04 本机实测)——用它导航,新标签落在用户真想去的
                // 位置;读不到才退回主页标签(与 Win+E 默认落点一致)
                string shellPath = ComCall(3000, delegate() { return GetExplorerShellPath(newHwnd); }, null);
                path = (shellPath != null && shellPath.StartsWith("::")) ? shellPath : "";
                break;
            }
            Thread.Sleep(200);
        }
        if (path == null) {
            Log("等待 " + (poll * 200 / 1000.0) + "s 未见导航信息,跳过合并(窗口保留)");
            return;
        }
        Log("路径判定[" + poll + "轮]: " + (path.Length == 0 ? "(虚拟位置不可解析→主页标签)" : path));

        IntPtr target = FindTargetWindow(newHwnd);
        if (target == IntPtr.Zero) { Log("无既有窗口,保留为首窗"); return; }
        Log("合并前标签快照: " + TabsSnapshot(target));

        bool merged = false;
        if (path.Length > 0 && ActivateExistingTab(target, path, newHwnd)) {
            // 目标路径已是既有窗口里的某条标签:直接激活它,不再开重复标签
            //(v1.4.2 之前这里无判断,目录已在隐藏标签里时照样开出第二条重复标签,
            // 活动的还是新开的重复条,用户要找的那条老标签永远切不过去)
            merged = true;
        }
        if (!merged && path.Length > 0) {
            // 首选:Shell COM Navigate2(navOpenInNewTab)——零按键:不经前台、不吃输入法、不抢焦点
            if (NavigateNewTab(target, path)) {
                for (int i = 0; i < 10 && !TabExistsInWindow(target, path); i++) Thread.Sleep(200);
                merged = TabExistsInWindow(target, path);
                Log("COM 新标签导航: " + (merged ? "OK" : "已调用但标签未到位"));
            } else {
                Log("COM Navigate2 调用失败");
            }
        }

        if (!merged && path.Length > 0 && !path.StartsWith("::")) {
            // 兜底:键盘注入(前台确认→Ctrl+T→Ctrl+L 输入路径;IME 摘除+重试+剪贴板粘贴)。
            // 虚拟位置(::{GUID})不走键盘:实测打 GUID 进地址栏不生效,还会给目标窗口
            // 堆一堆默认标签;COM 的 Navigate2 在本进程内又会间歇性无限挂起(2026-10-04,
            // pwsh 里同调用正常,疑 C# dynamic/套间交互,待挂起线程抓栈定位)——
            // GUID 合并只赌 COM 一把,失败保窗不丢位置(v1.4.0 一刀切主页的严格改进)。
            // 整段过 KeyboardGate:两个合并线程同时打字会互相打断对方的按键序列
            bool gate = Monitor.TryEnter(KeyboardGate, 5000);
            try {
                if (!gate) {
                    Log("键盘兜底:互斥门等待超时(另一合并正在注入),放弃");
                } else if (BringToFrontVerified(target)) {
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
            } finally {
                if (gate) Monitor.Exit(KeyboardGate);
            }
        }

        if (!merged && path.Length == 0) {
            // 虚拟位置读不到命名空间路径:Ctrl+T 新标签默认就落在主页,与被并窗口一致。
            // 同样要注入按键,过 KeyboardGate
            bool gate = Monitor.TryEnter(KeyboardGate, 5000);
            try {
                if (!gate) {
                    Log("主页标签:键盘互斥门等待超时,放弃");
                } else if (BringToFrontVerified(target)) {
                    SendCtrlCombo(VK_T);
                    Thread.Sleep(400);
                    merged = true;
                    Log("虚拟文件夹:已开主页标签");
                } else {
                    Log("虚拟文件夹但无法置前,放弃");
                }
            } finally {
                if (gate) Monitor.Exit(KeyboardGate);
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
        Thread.Sleep(300);                       // 等关窗后的焦点归还尘埃落定
        ShowTargetToUser(target);                // 还原最小化 + 尽力带到眼前(不注入按键)
        Log("并入完成 " + target + (path.Length == 0 ? "(主页标签)" : " → " + path)
            + " 最终最小化=" + IsIconic(target) + " 合并后标签快照: " + TabsSnapshot(target));
    }

    /// <summary>合并完成后把目标窗口带回用户眼前:最小化则还原(还原自带激活);
    /// 置前被前台锁拒绝时,至少把 z 序抬到顶层(SWP_NOACTIVATE 不抢焦点、无需前台权限)。
    /// 这里刻意不注入 Alt 键——那会打搅用户正在用的应用,只留给键盘兜底路径用。</summary>
    private static void ShowTargetToUser(IntPtr target) {
        if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
        SetForegroundWindow(target);
        if (GetForegroundWindow() != target) {
            SetWindowPos(target, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
        }
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

    /// <summary>在目标窗口里开出指向 path 的新标签。实测(PowerShell 对照实验,2026-10-01):
    /// 本机 Win11 的 IWebBrowser2.Navigate2 对 navOpenInNewTab(0x800)/InBackgroundTab(0x1000)
    /// 一律原地导航——会把既有标签顶掉(v1.2.1 之前「老目录被关掉」的真凶)。
    /// 可靠组合:UIA 调用标签栏 AddButton 开出真新标签(落在主页,LocationURL 为空),
    /// 再对这条新增的空标签原地 Navigate2 到目标路径。全程零键盘:不吃输入法、不依赖前台。</summary>
    private static bool NavigateNewTab(IntPtr targetHwnd, string path) {
        int emptyBefore = ComCall(2000, delegate() { return CountEmptyTabs(targetHwnd); }, -1);
        if (emptyBefore < 0) {
            Log("COM 无响应(空标签基线计数超时),放弃 COM 新标签");
            return false;
        }
        // AddButton 的 UIA 查找/调用会间歇性失败,两种形态(2026-10-03/04 实测,v1.3.3 起就有):
        //   轻症=FindFirst 返回 null;重症=explorer 的 UIA provider 无响应时 FindFirst 无限挂起
        //   (实测挂过 ~3 分钟)。挪到独立线程 + Join 超时兜底,重试 3 次再判死;
        //   超时后线程弃置(后台线程,极端情况泄漏一个,好过合并线程陪葬)
        bool invoked = false;
        for (int attempt = 1; attempt <= 3 && !invoked; attempt++) {
            Exception uiaErr = null;
            Thread uia = new Thread(delegate() {
                try {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(targetHwnd);
                    var cond = new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.AutomationIdProperty, "AddButton");
                    var btn = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, cond);
                    if (btn == null) return;                     // 找不到:线程正常结束,invoked 仍 false
                    var inv = (System.Windows.Automation.InvokePattern)btn.GetCurrentPattern(
                        System.Windows.Automation.InvokePattern.Pattern);
                    inv.Invoke();
                    invoked = true;
                } catch (Exception ex) {
                    uiaErr = ex;
                }
            });
            uia.IsBackground = true;
            uia.Start();
            if (!uia.Join(4000)) {
                Log("UIA AddButton 查找/调用超时(第 " + attempt + " 次)——explorer UIA 无响应");
            } else if (uiaErr != null) {
                Log("UIA 新标签异常(第 " + attempt + " 次): " + uiaErr.Message);
            } else if (!invoked) {
                Log("未找到标签栏 AddButton(第 " + attempt + " 次;Win10 无标签页或 UIA 未就绪)");
            }
            if (!invoked) Thread.Sleep(300);
        }
        if (!invoked) {
            Log("AddButton 3 次尝试均失败,放弃 COM 新标签");
            return false;
        }
        // 等新增的空标签出现(空标签计数至少 +1 才动手;超时线程迟到完成时可能多点出
        // 两个,>= 都放行,NavigateEmptyTab 取最后一条空标签即最新点出的那个)。
        // 计数每次都限时——COM 挂起时立即按未见处理,不再无限等
        for (int i = 0; i < 12; i++) {
            Thread.Sleep(150);
            int nowEmpty = ComCall(1500, delegate() { return CountEmptyTabs(targetHwnd); }, -1);
            if (nowEmpty < 0) {
                Log("COM 无响应(空标签复点超时),放弃 COM 新标签");
                return false;
            }
            if (nowEmpty >= emptyBefore + 1) {
                return ComCall(5000, delegate() { return NavigateEmptyTab(targetHwnd, path); }, false);
            }
        }
        Log("AddButton 调用后未见新增空标签");
        return false;
    }

    /// <summary>目标窗口下 LocationURL 为空(主页/虚拟位置)的标签条数。</summary>
    private static int CountEmptyTabs(IntPtr hwnd) {
        int n = 0;
        try {
            Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            dynamic shellWindows = Activator.CreateInstance(t);
            try {
                foreach (dynamic w in shellWindows) {
                    try {
                        if (new IntPtr(Convert.ToInt64(w.HWND)) != hwnd) continue;
                        if (string.IsNullOrEmpty((string)w.LocationURL)) n++;
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
        return n;
    }

    /// <summary>把目标窗口下【最后一条】空 URL 标签原地导航到 path(只应在"刚新增了空标签"
    /// 后调用)。用"最后"而非"第一":ShellWindows 按注册顺序枚举,UIA 刚点出来的空标签
    /// 排在后;若用户自己原本开着主页标签,取第一个会把用户的主页标签导航走,
    /// 新空标签反而留在主页。</summary>
    private static bool NavigateEmptyTab(IntPtr targetHwnd, string path) {
        try {
            Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            dynamic shellWindows = Activator.CreateInstance(t);
            dynamic last = null;
            try {
                foreach (dynamic w in shellWindows) {
                    bool keep = false;
                    try {
                        keep = new IntPtr(Convert.ToInt64(w.HWND)) == targetHwnd
                            && string.IsNullOrEmpty((string)w.LocationURL);
                    } catch {
                    }
                    if (keep) {
                        if (last != null) { try { Marshal.ReleaseComObject(last); } catch { } }
                        last = w;
                    } else {
                        try { Marshal.ReleaseComObject(w); } catch { }
                    }
                }
                if (last == null) {
                    Log("NavigateEmptyTab: 枚举未见空标签(目标=" + targetHwnd + ")");
                    return false;
                }
                try {
                    // 实测(2026-10-04 隔离探针):Navigate2 对裸 "::{GUID}" 抛
                    // "Value does not fall within the expected range",加 shell::: 前缀即可;
                    // 文件系统路径原样传
                    string navPath = path.StartsWith("::") ? "shell:::" + path : path;
                    long t0 = DateTime.Now.Ticks;
                    last.Navigate2(navPath, 0);        // 原地导航这条新开的主页标签
                    Log("NavigateEmptyTab: Navigate2 OK " + ((DateTime.Now.Ticks - t0) / 10000) + "ms → " + navPath);
                    return true;
                } catch (Exception ex) {
                    Log("NavigateEmptyTab: Navigate2 异常: " + ex.Message + " (path=" + path + ")");
                    return false;
                } finally {
                    try { Marshal.ReleaseComObject(last); } catch { }
                }
            } finally {
                try { Marshal.ReleaseComObject(shellWindows); } catch { }
            }
        } catch (Exception ex) {
            Log("NavigateEmptyTab: 枚举阶段异常: " + ex.Message);
        }
        return false;
    }

    /// <summary>目标窗口名下已有位于 path 的标签时,激活那条标签(UIA SelectionItem.Select),
    /// 免开重复标签。索引映射:Shell 枚举同 HWND 记录序 == UIA TabListView 子项序
    /// (2026-10-07 探针实测一一对应,TabItem 的 Name 是本地化显示名无法按路径匹配,
    /// 只能靠索引);UIA 走独立线程+Join 超时(与 AddButton 同源的挂死风险);激活以
    /// 「目标窗口标题变为新窗口的显示名」核实(新窗口此刻已导航到目标,标题即显示名,
    /// 虚拟位置同样成立)。失败返回 false,落回「新开标签」的既有路径,行为不劣化。</summary>
    private static bool ActivateExistingTab(IntPtr target, string path, IntPtr newHwnd) {
        int idx = ComCall(2000, delegate() { return TabIndexOf(target, path); }, -1);
        if (idx < 0) return false;                       // 无同路径标签:该走新开标签
        string display = GetTitle(newHwnd);
        int dash = display.IndexOf(" - 文件资源管理器");
        if (dash > 0) display = display.Substring(0, dash);
        if (display.Length == 0) return false;
        bool selected = false;
        for (int attempt = 1; attempt <= 2 && !selected; attempt++) {
            selected = UiaSelectTab(target, idx);
            if (!selected) Thread.Sleep(200);
        }
        if (!selected) {
            Log("既有标签激活: UIA Select 失败(tab#" + idx + "),转新开标签");
            return false;
        }
        for (int i = 0; i < 10; i++) {                   // 标题核实,~2s
            string t = GetTitle(target);
            if (t == display || t.StartsWith(display + " 和 ") || t.StartsWith(display + " - ")) {
                Log("既有标签激活: OK(tab#" + idx + " → " + path + ")");
                return true;
            }
            Thread.Sleep(200);
        }
        Log("既有标签激活: Select 已调用但标题未切换(当前: " + GetTitle(target) + "),转新开标签");
        return false;
    }

    /// <summary>hwnd 名下 tab 序列中,比对路径 == path 的第一条的索引(0-based);无则 -1。
    /// 提速关键:文件系统路径只比 LocationURL(便宜)——空 URL 标签(主页等)读
    /// Document.Folder.Self.Path 是跨进程慢调用(实测能把整个枚举拖到超时),仅当目标
    /// 本身是虚拟路径(::{GUID})才对空 URL 标签做那次慢读。</summary>
    private static int TabIndexOf(IntPtr hwnd, string path) {
        bool virtualPath = path.StartsWith("::");
        int idx = -1;
        int i = 0;
        try {
            Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            dynamic shellWindows = Activator.CreateInstance(t);
            try {
                foreach (dynamic w in shellWindows) {
                    bool mine = false;
                    bool hit = false;
                    try {
                        mine = new IntPtr(Convert.ToInt64(w.HWND)) == hwnd;
                        if (mine) {
                            string cmp = UrlToPath((string)w.LocationURL);
                            if (cmp.Length == 0 && virtualPath) cmp = TabComparePath(w);
                            hit = string.Equals(cmp, path, StringComparison.OrdinalIgnoreCase);
                        }
                    } catch {
                    } finally {
                        try { Marshal.ReleaseComObject(w); } catch { }
                    }
                    if (!mine) continue;
                    if (hit && idx < 0) idx = i;
                    i++;
                }
            } finally {
                try { Marshal.ReleaseComObject(shellWindows); } catch { }
            }
        } catch {
        }
        return idx;
    }

    /// <summary>UIA 激活 TabListView 第 idx 个子项(独立线程限时;返回成功与否,不抛)。
    /// tab 项不是标准 TabItem 挂在窗口树下能 FindAll 到的——须先找 AutomationId=
    /// "TabListView" 的列表,再经 ControlViewWalker 逐个走子项(2026-10-07 探针实测)。</summary>
    private static bool UiaSelectTab(IntPtr hwnd, int idx) {
        bool ok = false;
        Thread uia = new Thread(delegate() {
            try {
                var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                var cond = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.AutomationIdProperty, "TabListView");
                var list = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, cond);
                if (list == null) return;
                var walker = System.Windows.Automation.TreeWalker.ControlViewWalker;
                var child = walker.GetFirstChild(list);
                int i = 0;
                System.Windows.Automation.AutomationElement item = null;
                while (child != null) {
                    if (i == idx) { item = child; break; }
                    child = walker.GetNextSibling(child);
                    i++;
                }
                if (item == null) return;
                var sel = (System.Windows.Automation.SelectionItemPattern)item.GetCurrentPattern(
                    System.Windows.Automation.SelectionItemPattern.Pattern);
                sel.Select();
                ok = true;
            } catch {
            }
        });
        uia.IsBackground = true;
        uia.Start();
        return uia.Join(4000) && ok;
    }

    /// <summary>在目标窗口当前标签导航到 path:Ctrl+L 聚焦地址栏,打字期间临时摘掉
    /// 该编辑框的输入法上下文(否则中文输入法会吞掉 unicode 按键、回车变成确认候选词),
    /// 输入路径回车后恢复。打字前与回车前各复查一次前台。</summary>
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
            if (!StillForeground(targetHwnd)) {
                Log("键盘兜底:打字前前台已丢失,中止(新窗口保留)");
                return;
            }
            TypeUnicode(path);
            Thread.Sleep(80);
            if (!StillForeground(targetHwnd)) {
                Log("键盘兜底:回车前前台已丢失,中止(新窗口保留)");
                return;
            }
            KeyTap(VK_RETURN);
            Thread.Sleep(120);                               // 让导航吃到回车再恢复输入法
        } finally {
            if (imeOff) ImmAssociateContext(focus, oldImc);
            if (attached) AttachThreadInput(me, targetThread, false);
        }
    }

    /// <summary>兜底导航:Ctrl+L 后粘贴路径再回车。粘贴不走键盘注入,天然绕过输入法;
    /// 代价是短暂占用剪贴板——剪贴板里有图片/文件等非文本内容时不动它(恢复不了就毁了),
    /// 直接放弃本兜底;文本则用完尽力恢复。粘贴前与回车前各复查一次前台。</summary>
    private static void NavigateByClipboard(IntPtr targetHwnd, string path) {
        string prev = null;
        try {
            if (System.Windows.Forms.Clipboard.ContainsText()) {
                prev = System.Windows.Forms.Clipboard.GetText();
            } else if (System.Windows.Forms.Clipboard.ContainsImage()
                    || System.Windows.Forms.Clipboard.ContainsFileDropList()
                    || System.Windows.Forms.Clipboard.ContainsAudio()) {
                Log("剪贴板里有非文本内容(图片/文件),不占用,兜底放弃");
                return;
            }
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
            if (!StillForeground(targetHwnd)) {
                Log("剪贴板兜底:粘贴前前台已丢失,中止(新窗口保留)");
                return;
            }
            SendCtrlCombo(VK_V);                             // 'V'
            Thread.Sleep(120);
            if (!StillForeground(targetHwnd)) {
                Log("剪贴板兜底:回车前前台已丢失,中止(新窗口保留)");
                return;
            }
            KeyTap(VK_RETURN);
            Thread.Sleep(200);
        } finally {
            try {
                if (prev != null) System.Windows.Forms.Clipboard.SetText(prev);
            } catch {
            }
        }
    }

    /// <summary>目标窗口名下(同 HWND 的每个标签各有一条 Shell 记录)是否已有位于 path 的标签。
    /// COM 限时:超时按不存在处理。</summary>
    private static bool TabExistsInWindow(IntPtr hwnd, string path) {
        return ComCall(2000, delegate() { return TabsSnapshotOf(hwnd, path, true).Length > 0; }, false);
    }

    /// <summary>诊断用:列出目标窗口当前全部标签的路径("; " 分隔)。COM 限时。</summary>
    private static string TabsSnapshot(IntPtr hwnd) {
        return ComCall(2000, delegate() { return TabsSnapshotOf(hwnd, null, false); }, "(COM超时)");
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
                        string p = TabComparePath(w);      // 虚拟标签用 ::{GUID} 参与比对
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

    /// <summary>取某资源管理器窗口当前标签的 Shell 命名空间路径。虚拟位置(回收站/此电脑)
    /// LocationURL 为空,但这里能拿到 ::{GUID} 形式。null=窗口尚未登记;""=登记了但读不到。</summary>
    private static string GetExplorerShellPath(IntPtr hwnd) {
        try {
            Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            dynamic shellWindows = Activator.CreateInstance(t);
            try {
                foreach (dynamic w in shellWindows) {
                    bool mine = false;
                    string p = null;
                    try {
                        mine = new IntPtr(Convert.ToInt64(w.HWND)) == hwnd;
                        if (mine) p = w.Document.Folder.Self.Path;
                    } catch {
                    } finally {
                        try { Marshal.ReleaseComObject(w); } catch { }
                    }
                    if (mine) return p == null ? "" : p;
                }
            } finally {
                try { Marshal.ReleaseComObject(shellWindows); } catch { }
            }
        } catch {
        }
        return null;
    }

    /// <summary>某标签的参与比对路径:文件系统标签用 LocationURL;空 URL(虚拟标签)
    /// 退读 Self.Path(::{GUID}),让 TabExistsInWindow 也能验证虚拟位置。</summary>
    private static string TabComparePath(dynamic w) {
        string p = UrlToPath((string)w.LocationURL);
        if (p.Length == 0) {
            try {
                p = w.Document.Folder.Self.Path;
                if (p == null) p = "";
            } catch {
                p = "";
            }
        }
        return p;
    }

    private IntPtr FindTargetWindow(IntPtr exclude) {
        // 排除仍在待处理队列/正在合并中的窗口:登录还原/快速连开时,A 若并进排队中的 B,
        // B 随后又被当新窗口合并关掉,B 里 A 的标签就一起没了
        List<IntPtr> skip = new List<IntPtr>();
        skip.Add(exclude);
        lock (pending) { foreach (IntPtr h in pending) skip.Add(h); }
        lock (inFlight) { foreach (IntPtr h in inFlight) skip.Add(h); }
        IntPtr best = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            var sb = new StringBuilder(64);
            if (GetClassName(h, sb, 64) != 0 && sb.ToString() == ExplorerWindowClass
                && !skip.Contains(h) && IsMergeTargetVisible(h)) {
                best = h;
                return false;                              // EnumWindows 按 z 序先见顶层,取最上面的那个
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>可见且不在别的虚拟桌面——IsWindowVisible 对其它桌面的窗口也返回 true
    /// (DWM 只是把它 cloak 掉),不滤的话标签会并进一个当前桌面看不见的窗口。</summary>
    private static bool IsMergeTargetVisible(IntPtr h) {
        if (!IsWindowVisible(h)) return false;
        int cloaked;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return false;
        return true;
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

    /// <summary>限时跑一段 Shell COM 操作。explorer 的 COM 通道会间歇性无响应,同步调用
    /// 可能无限挂起(2026-10-04 实测 CountEmptyTabs/枚举都挂过,且挂时无任何征兆)——
    /// 合并路径上的 COM 调用一律经此包装:超时返回 fallback(调用方按失败处理,窗口保留),
    /// 被弃置的后台线程随进程终结。UIA 同理见 NavigateNewTab 的线程+Join 包装。</summary>
    private static T ComCall<T>(int timeoutMs, System.Func<T> fn, T fallback) {
        T result = fallback;
        Thread t = new Thread(delegate() {
            try { result = fn(); } catch { }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);   // Shell COM 走 STA 稳(与合并线程同语义)
        t.Start();
        return t.Join(timeoutMs) ? result : fallback;
    }

    /// <summary>键盘注入前的前台复查:置前验证与打字之间隔着几百毫秒,用户此刻切走
    /// 窗口的话,后续按键会整段打进别的程序——丢了就放弃,宁可不合并。</summary>
    private static bool StillForeground(IntPtr hwnd) {
        return GetForegroundWindow() == hwnd;
    }

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
