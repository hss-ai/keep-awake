// wechat_adblock.cs — 公众号文章去广告守护(v1.5.0,KeepAwake 托管)
//
// 接管原独立 pythonw guard 的全部职责,微信去广告从「多一个常驻进程」变成
// KeepAwake 的一个功能开关(知识库 #196 三层防线的守卫层):
//   1. 保活 mitmdump 反代(127.0.0.1:443,hosts 已把 mp.weixin.qq.com 指到本机);
//   2. 每分钟补丁微信 xworker 文章模板(载荷 = 后端 payload.htm,单一真源);
//   3. mitmdump 持续故障 30s 后降级纯 TCP 中继(文章可用、广告回来——短暂抖动
//      宁可慢几秒也不能让未注入的原始 HTML 灌进 webview 缓存,#196 踩过);
//   4. 开关关闭 = 杀 mitmdump + 中继直通(文章可用、有广告),不动 hosts/CA。
//
// 后端(E:\software\wechat-adake 之类)装着 mitmproxy venv + mitm CA + addon +
// payload.htm;本文件只做监督,零新增依赖,后端缺失时功能整体停用(菜单置灰)。
// 后端路径可用 exe 同目录 wechat-adblock-path.txt 覆盖;模板扫描根目录可用
// wechat-adblock-tmpl-roots.txt 追加(每行一个,默认 %APPDATA% + E:\home 两处)。
//
// 语言约束:系统 csc(v4.0.30319)= C# 5,禁用 $"插值"/?./表达式体。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace KeepAwake {

// 开关持久化:与合并/键映射/静音同键(独立于会被电源护栏 Restore 删净的
// Software\KeepAwake 键)。未写过 = 默认开。
static class WechatAdblockPref {
    const string RegPath = "Software\\KeepAwakePrefs";
    const string ValueName = "WechatAdblockEnabled";

    public static bool Get() {
        try {
            using (var key = Registry.CurrentUser.OpenSubKey(RegPath)) {
                if (key == null) return true;                    // 未写过 = 默认开
                return Convert.ToInt32(key.GetValue(ValueName, 1)) != 0;
            }
        }
        catch {
            return true;
        }
    }

    public static void Set(bool on) {
        try {
            using (var key = Registry.CurrentUser.CreateSubKey(RegPath)) {
                key.SetValue(ValueName, on ? 1 : 0, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex) {
            AppLog.Write("公众号去广告开关写入失败: " + ex.Message);
        }
    }
}

internal sealed class WechatAdblockSupervisor : IDisposable {
    // ---- 路径解析:exe 同目录可放覆盖文件,默认值匹配 hary2 的装机布局 ----
    static string ExeDir() {
        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    }

    static string OverrideOr(string fileName, string fallback) {
        try {
            string p = Path.Combine(ExeDir(), fileName);
            if (File.Exists(p)) {
                string v = File.ReadAllText(p, Encoding.UTF8).Trim();
                if (v.Length > 0) return v;
            }
        }
        catch {
        }
        return fallback;
    }

    public static string BackendDir() {
        return OverrideOr("wechat-adblock-path.txt", @"E:\java\workspace_own\wechat-adblock");
    }

    public static bool BackendPresent() {
        return File.Exists(Path.Combine(BackendDir(), "venv", "Scripts", "mitmdump.exe"));
    }

    // ------------------------------------------------------------------

    const string Host = "mp.weixin.qq.com";
    const string UpstreamCName = "mpv6.weixin.qq.com";   // 不在 hosts,系统解析即真实 IP
    const string PayloadMarker = "wechat-adblock";

    readonly object gate = new object();
    readonly List<IPAddress> realIps = new List<IPAddress>();
    Thread loopThread;
    TcpListener relay;
    Thread relayAcceptThread;
    Process child;
    bool enabled;
    bool disposed;
    int portFails;
    DateTime lastIpRefresh = DateTime.MinValue;
    DateTime lastPatch = DateTime.MinValue;
    DateTime takeoverAt = DateTime.MinValue;

    public WechatAdblockSupervisor(bool startEnabled) {
        enabled = startEnabled;
        // 清掉上一个实例(或旧 python guard)留下的 mitmdump 孤儿:它占着 443,
        // 既会挡住本实例拉起新的,也会让"关"态下广告其实没关
        KillStrayMitmdump();
        if (startEnabled && BackendPresent()) {
            takeoverAt = DateTime.Now;
        }
        loopThread = new Thread(Loop);
        loopThread.IsBackground = true;
        loopThread.Start();
    }

    public bool Enabled {
        get {
            lock (gate) {
                return enabled;
            }
        }
        set {
            lock (gate) {
                if (enabled == value) return;
                enabled = value;
            }
            if (!value) {
                KillChild();
                Log("公众号去广告:关闭(降级纯转发,文章可用、广告回来)");
            }
            else {
                Log("公众号去广告:开启");
                // 关闭期间中继占着 443,开启时必须先让路并清旧子进程——否则监督
                // 循环看到「端口在监听」会误判健康,mitmdump 永远起不来(等于停在纯转发)
                RelayStop();
                KillChild();
                if (BackendPresent() && takeoverAt == DateTime.MinValue) {
                    KillStrayMitmdump();
                    takeoverAt = DateTime.Now;
                }
            }
        }
    }

    void Loop() {
        while (true) {
            try {
                lock (gate) {
                    if (disposed) return;
                }
                if (DateTime.UtcNow - lastIpRefresh > TimeSpan.FromMinutes(30)) {
                    RefreshRealIps();
                    lastIpRefresh = DateTime.UtcNow;
                }
                if (enabled && BackendPresent()) {
                    SuperviseMitmdump();
                    if (DateTime.UtcNow - lastPatch > TimeSpan.FromMinutes(1)) {
                        PatchTemplates();
                        lastPatch = DateTime.UtcNow;
                    }
                }
                else if (!enabled) {
                    KillChild();
                    // 中继只在 mitmdump 真的不在时接管(否则两个抢 443)
                    if (!MitmdumpOwnsPort() && !PortListening() && realIps.Count > 0) {
                        RelayStart();
                    }
                }
            }
            catch (Exception ex) {
                Log("守护循环异常: " + ex.Message);
            }
            Thread.Sleep(5000);
        }
    }

    void SuperviseMitmdump() {
        // 载荷改了必须重启 mitmdump:addon 在启动时把 payload.htm 读进内存,
        // 之后改文件不重启就一直用旧载荷(2026-10-23 实锤:加了底部广告选择器,
        // 实抓文章页仍是旧 CSS,只剩留言区选择器)。
        if (PayloadChanged()) {
            Log("检测到 payload.htm 变更,重启 mitmdump 以加载新载荷");
            RelayStop();
            KillChild();
            Thread.Sleep(1500);
        }
        // 僵死清场(2026-10-30 实锤,广告因此复发一上午):
        // 探活(TcpClient 连不上)=false 但 netstat 里 443 仍挂着 LISTENING
        // = 监听进程僵死(socket 在、不 accept)。此时降级中继也 bind 不上
        // (端口被占),监督循环会「故障→降级→bind 失败」死循环 6120s+。
        // 对策:连续 2 轮探活失败后,若 LISTENING 者是我们家族的 mitmdump →
        // 杀整棵进程树,让下一轮重新拉起;不是我们家族的只记日志(不乱杀)。
        if (!PortListening() && portFails >= 2) {
            int listenerPid = FindPort443ListenerPid();
            if (listenerPid > 0) {
                if (LooksLikeMitmdumpFresh(listenerPid)) {
                    Log("443 探活失败但 PID " + listenerPid +
                        " 仍挂 LISTENING(僵死),强制清场杀树");
                    KillProcessTree(listenerPid);
                    Thread.Sleep(1500);
                }
                else {
                    Log("443 探活失败且被无关进程 PID " + listenerPid +
                        " 占用,无法自动清场");
                }
            }
        }
        // 端口在监听还不够:关闭期间起的中继也是"在监听",但它不做注入。
        // 所以要求监听者确实是 mitmdump(否则先让路再拉新的)
        if (PortListening() && MitmdumpOwnsPort()) {
            portFails = 0;
            return;
        }
        if (PortListening()) {
            Log("443 由降级中继占用,让路给 mitmdump");
        }
        // 端口死了:先让路给 mitmdump(中继停掉),再(重)拉子进程
        RelayStop();
        lock (gate) {
            if (child == null || child.HasExited) {
                child = StartMitmdump();
                Log("mitmdump (re)started");
            }
        }
        Thread.Sleep(4000);
        if (PortListening()) {
            portFails = 0;
            return;
        }
        portFails++;
        if (portFails >= 6 && realIps.Count > 0) {
            // 日志节流:降级状态下曾每 5s 刷一条,102 分钟刷了几百行
            // 且毫无信息量;改为每 60s 最多记一次
            if ((DateTime.Now - lastDegradeLog).TotalSeconds >= 60) {
                Log("mitmdump 持续故障 " + (portFails * 5) + "s,降级纯转发");
                lastDegradeLog = DateTime.Now;
            }
            RelayStart();
        }
        else if ((DateTime.Now - lastDegradeLog).TotalSeconds >= 60) {
            // 探活失败重试期间同样节流,避免无限刷屏
            Log("mitmdump 未监听(fail #" + portFails + "),等待重试");
            lastDegradeLog = DateTime.Now;
        }
    }

    DateTime lastDegradeLog = DateTime.MinValue;

    // payload.htm 的内容指纹(长度 + 修改时间);变了就要重启 mitmdump
    string payloadStamp = "";
    bool payloadStampInit = false;

    bool PayloadChanged() {
        try {
            string path = Path.Combine(BackendDir(), "payload.htm");
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            string stamp = fi.Length.ToString() + "@" + fi.LastWriteTimeUtc.Ticks.ToString();
            if (!payloadStampInit) {
                payloadStamp = stamp;
                payloadStampInit = true;
                return false;
            }
            if (stamp == payloadStamp) return false;
            payloadStamp = stamp;
            return true;
        }
        catch {
            return false;
        }
    }

    Process StartMitmdump() {
        string backend = BackendDir();
        var psi = new ProcessStartInfo {
            FileName = Path.Combine(backend, "venv", "Scripts", "mitmdump.exe"),
            Arguments = "--mode reverse:https://" + UpstreamCName +
                        " --set confdir=\"" + Path.Combine(backend, "mitm") + "\"" +
                        " --set keep_host_header=true" +
                        " --listen-host 127.0.0.1 --listen-port 443" +
                        " -s \"" + Path.Combine(backend, "wechat_adblock.py") + "\"" +
                        " -q",
            WorkingDirectory = backend,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try {
            return Process.Start(psi);
        }
        catch (Exception ex) {
            Log("mitmdump 启动失败: " + ex.Message);
            return null;
        }
    }

    void KillChild() {
        lock (gate) {
            if (child != null) {
                try {
                    if (!child.HasExited) KillProcessTree(child.Id);
                }
                catch {
                }
                child = null;
            }
        }
    }

    // 杀整棵进程树:venv 里的 mitmdump.exe 是 uv shim,进程链是
    //   mitmdump.exe(shim) → venv\Scripts\python.exe → uv\...\python.exe(真正监听 443 的)
    // 只 Kill() shim 会留下真身继续占着 443(就是 v1.5.1 说的孤儿,只是没杀干净)。
    static void KillProcessTree(int pid) {
        try {
            var psi = new ProcessStartInfo("taskkill", "/PID " + pid + " /T /F") {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi).WaitForExit(5000);
        }
        catch {
            try {
                Process.GetProcessById(pid).Kill();
            }
            catch {
            }
        }
    }

    static void KillStrayMitmdump() {
        // 只杀「跑着我们 addon」的 mitmdump,不按镜像名无差别杀:
        // venv 里的 mitmdump.exe 是 uv shim,真身进程名是 python.exe,
        // 按命令行特征(wechat_adblock)认才准;也避免误杀别的项目的 mitmdump。
        // 判定要同时含 mitmdump 与 wechat_adblock——只匹配后者会误伤
        // 「命令行里恰好出现该路径」的无关进程(编辑器、shell 都会中招)。
        try {
            foreach (int pid in FindPidsByCommandLine("wechat_adblock")) {
                if (!LooksLikeMitmdumpFresh(pid)) continue;
                KillProcessTree(pid);
            }
        }
        catch {
        }
        Thread.Sleep(1500);
    }

    // 按命令行子串找 pid
    // 注意:Windows 11 已移除 wmic(实测本机 C:\Windows\System32\wbem\WMIC.exe 不存在),
    // 依赖 wmic 会静默返回空 → 端口归属判断永远失败 → 监督循环误判「443 被降级中继占用」
    // 反复让路(v1.5.1 实测刷屏)。改用 PowerShell CIM(系统自带,Win11 仍在)。
    static List<int> FindPidsByCommandLine(string needle) {
        var hits = new List<int>();
        try {
            // needle 里的单引号需转义,否则会破坏 PowerShell 字符串字面量
            string safeNeedle = needle.Replace("'", "''");
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command " +
                "Get-CimInstance Win32_Process | Where-Object { " +
                "$_.CommandLine -like '*" + safeNeedle + "*' } | " +
                "ForEach-Object { $_.ProcessId }") {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            string outText = Process.Start(psi).StandardOutput.ReadToEnd();
            foreach (string line in outText.Split('\n')) {
                int pid;
                if (int.TryParse(line.Trim(), out pid) && pid > 0) {
                    if (!hits.Contains(pid)) hits.Add(pid);
                }
            }
        }
        catch {
        }
        return hits;
    }

    // 取 pid 的命令行(wmic 已被 Win11 移除,同 FindPidsByCommandLine 改走 CIM)
    // CIM 查询每次要起一个 PowerShell(~1s),监督循环 5s 一轮,故按 pid 缓存 30s。
    static readonly Dictionary<int, string> cmdLineCache = new Dictionary<int, string>();
    static readonly Dictionary<int, DateTime> cmdLineAt = new Dictionary<int, DateTime>();

    static string CommandLineOf(int pid) {
        lock (cmdLineCache) {
            DateTime at;
            string cached;
            if (cmdLineCache.TryGetValue(pid, out cached) &&
                cmdLineAt.TryGetValue(pid, out at) &&
                (DateTime.Now - at).TotalSeconds < 30) {
                return cached;
            }
        }
        string cmd = CommandLineOfFresh(pid);
        lock (cmdLineCache) {
            cmdLineCache[pid] = cmd;
            cmdLineAt[pid] = DateTime.Now;
        }
        return cmd;
    }

    // 不走缓存的版本:杀进程前必须拿最新命令行
    static string CommandLineOfFresh(int pid) {
        string cmd = "";
        try {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command " +
                "$p = Get-CimInstance Win32_Process -Filter 'ProcessId=" + pid + "'; " +
                "if ($p) { $p.CommandLine }") {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            cmd = Process.Start(psi).StandardOutput.ReadToEnd();
        }
        catch {
        }
        return cmd;
    }

    // 443 的监听者是不是 mitmdump:按 TCP 连接的对端本机端口反查 owning pid 的进程名。
    // 走 netstat 文本解析(免 WMI 依赖);拿不到就返回 false(保守=重新拉起,最多一次抖动)
    static bool MitmdumpOwnsPort() {
        // 只认 LISTENING 那一条:ESTABLISHED 行是同端口的客户端连接,
        // 会重复触发昂贵的命令行查询(CIM 每次 ~1s)
        int pid = FindPort443ListenerPid();
        if (pid <= 0) return false;
        // 进程名不可靠:venv 里的 mitmdump.exe 是 uv 的 shim,真身进程名是
        // python.exe(实测 3.11),只能按命令行特征认:mitmdump + 我们的 addon
        return LooksLikeMitmdump(pid);
    }

    // netstat 里 127.0.0.1:443 的 LISTENING 者 pid(拿不到返回 -1)。
    // 用途:①端口归属判定;②僵死清场——探活失败但 LISTENING 仍挂着,
    // 说明监听进程僵死(socket 在、不 accept),得按 pid 直接杀。
    static int FindPort443ListenerPid() {
        try {
            var psi = new ProcessStartInfo("netstat", "-ano") {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            string outText = Process.Start(psi).StandardOutput.ReadToEnd();
            foreach (string line in outText.Split('\n')) {
                if (line.IndexOf("127.0.0.1:443") < 0) continue;
                if (line.IndexOf("LISTENING") < 0) continue;
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                int pid;
                if (int.TryParse(parts[parts.Length - 1], out pid) && pid > 0) return pid;
            }
        }
        catch {
        }
        return -1;
    }

    // 是不是我们的 mitmdump:命令行里同时出现 mitmdump 与 wechat_adblock
    static bool LooksLikeMitmdump(int pid) {
        try {
            using (var p = Process.GetProcessById(pid)) {
                string name = p.ProcessName;
                if (name != null && name.IndexOf("mitmdump", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch {
        }
        // 进程名不可靠:venv 里的 mitmdump.exe 是 uv shim,真身进程名是 python.exe,
        // 只能按命令行特征认:mitmdump + 我们的 addon(wmic 已被 Win11 移除,走 CIM)
        string cmd = CommandLineOf(pid);
        if (cmd.Length > 0 &&
            cmd.IndexOf("mitmdump", StringComparison.OrdinalIgnoreCase) >= 0 &&
            cmd.IndexOf("wechat_adblock", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    // 同 LooksLikeMitmdump,但绕过命令行缓存(杀进程前必须拿最新命令行)
    static bool LooksLikeMitmdumpFresh(int pid) {
        try {
            using (var p = Process.GetProcessById(pid)) {
                string name = p.ProcessName;
                if (name != null && name.IndexOf("mitmdump", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch {
        }
        string cmd = CommandLineOfFresh(pid);
        if (cmd.Length > 0 &&
            cmd.IndexOf("mitmdump", StringComparison.OrdinalIgnoreCase) >= 0 &&
            cmd.IndexOf("wechat_adblock", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    static string ProcessNameOf(int pid) {
        try {
            using (var p = Process.GetProcessById(pid))
                return p.ProcessName;
        }
        catch {
            return null;
        }
    }

    static bool PortListening() {
        try {
            using (var c = new TcpClient()) {
                var ar = c.BeginConnect("127.0.0.1", 443, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(1500);
                if (ok && c.Connected) {
                    c.Close();
                    return true;
                }
                c.Close();
                return false;
            }
        }
        catch {
            return false;
        }
    }

    void RefreshRealIps() {
        try {
            var ips = Dns.GetHostAddresses(UpstreamCName);
            lock (gate) {
                realIps.Clear();
                foreach (var ip in ips) {
                    if (ip.AddressFamily == AddressFamily.InterNetwork) realIps.Add(ip);
                }
            }
        }
        catch (Exception ex) {
            Log("真实 IP 解析失败: " + ex.Message);
        }
    }

    // ---- 降级中继:127.0.0.1:443 → 真实 IP:443 纯管道(真证书直通) ----

    void RelayStart() {
        lock (gate) {
            if (relay != null) return;
            var l = new TcpListener(IPAddress.Parse("127.0.0.1"), 443);
            try {
                l.Start(16);
            }
            catch (Exception ex) {
                Log("中继监听失败: " + ex.Message);
                l = null;
            }
            if (l == null) return;
            relay = l;
            Log("降级中继已上线(mp.weixin → " + RealIpString() + ")");
        }
        relayAcceptThread = new Thread(RelayAccept);
        relayAcceptThread.IsBackground = true;
        relayAcceptThread.Start();
    }

    void RelayAccept() {
        TcpListener l;
        lock (gate) {
            l = relay;
        }
        if (l == null) return;
        while (true) {
            TcpClient client;
            try {
                client = l.AcceptTcpClient();
            }
            catch {
                break;   // listener 被停
            }
            IPAddress ip = FirstRealIp();
            if (ip == null) {
                try {
                    client.Close();
                }
                catch {
                }
                continue;
            }
            var relayClient = new RelayConn {
                Client = client,
                UpstreamIp = ip,
            };
            var t = new Thread(RelayConn.Run);
            t.IsBackground = true;
            t.Start(relayClient);
        }
    }

    class RelayConn {
        public TcpClient Client;
        public IPAddress UpstreamIp;

        public static void Run(object state) {
            var rc = (RelayConn)state;
            TcpClient up = null;
            try {
                up = new TcpClient();
                up.Connect(rc.UpstreamIp, 443);
                var t1 = new Thread(delegate() {
                    Pipe(rc.Client, up);
                });
                var t2 = new Thread(delegate() {
                    Pipe(up, rc.Client);
                });
                t1.IsBackground = true;
                t2.IsBackground = true;
                t1.Start();
                t2.Start();
                t1.Join();
                t2.Join();
            }
            catch {
            }
            finally {
                try {
                    rc.Client.Close();
                }
                catch {
                }
                try {
                    if (up != null) up.Close();
                }
                catch {
                }
            }
        }

        static void Pipe(TcpClient a, TcpClient b) {
            try {
                var src = a.GetStream();
                var dst = b.GetStream();
                var buf = new byte[65536];
                while (true) {
                    int n = src.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    dst.Write(buf, 0, n);
                }
            }
            catch {
            }
            try {
                a.Close();
            }
            catch {
            }
            try {
                b.Close();
            }
            catch {
            }
        }
    }

    void RelayStop() {
        lock (gate) {
            if (relay != null) {
                try {
                    relay.Stop();
                }
                catch {
                }
                relay = null;
                Log("降级中继已停止");
            }
        }
    }

    IPAddress FirstRealIp() {
        lock (gate) {
            return realIps.Count > 0 ? realIps[0] : null;
        }
    }

    string RealIpString() {
        lock (gate) {
            return realIps.Count > 0 ? realIps[0].ToString() : "?";
        }
    }

    // ---- 模板补丁:把 payload.htm 载荷写进 xworker 文章模板的 <head> ----

    static List<string> TemplateRoots() {
        // C# 5:yield 不能出现在带 catch 的 try 块里,改为整表收集
        var roots = new List<string>();
        string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        roots.Add(Path.Combine(appdata, @"Tencent\xwechat\radium\users"));
        roots.Add(@"E:\home\Tencent\xwechat\radium\users");
        try {
            string extra = Path.Combine(ExeDir(), "wechat-adblock-tmpl-roots.txt");
            if (File.Exists(extra)) {
                foreach (string line in File.ReadAllLines(extra, Encoding.UTF8)) {
                    string t = line.Trim();
                    if (t.Length > 0) roots.Add(t);
                }
            }
        }
        catch {
        }
        return roots;
    }

    void PatchTemplates() {
        string backend = BackendDir();
        string payloadFile = Path.Combine(backend, "payload.htm");
        string payload;
        try {
            payload = File.ReadAllText(payloadFile, Encoding.UTF8);
        }
        catch (Exception ex) {
            Log("载荷缺失(" + payloadFile + "): " + ex.Message);
            return;
        }
        if (payload.IndexOf(PayloadMarker) < 0) {
            Log("载荷文件无标记,拒绝注入");
            return;
        }
        int patched = 0;
        foreach (string root in TemplateRoots()) {
            if (!Directory.Exists(root)) continue;
            string tmplRoot;
            try {
                tmplRoot = Path.Combine(root, @"xworker\mpxworker\local\tmpl");
            }
            catch {
                continue;
            }
            // root 本身就是 ...\users\<hash>;枚举其下 xworker
            PatchDir(new DirectoryInfo(root), payload, ref patched);
        }
        if (patched > 0) Log("模板补丁: 本次写入 " + patched + " 份");
    }

    static void PatchDir(DirectoryInfo dir, string payload, ref int patched) {
        DirectoryInfo[] subs;
        try {
            subs = dir.GetDirectories();
        }
        catch {
            return;
        }
        foreach (DirectoryInfo sub in subs) {
            if (string.Equals(sub.Name, "xworker", StringComparison.OrdinalIgnoreCase)) {
                PatchXworker(sub, payload, ref patched);
            }
            else if (sub.Name.IndexOf('.') < 0) {
                PatchDir(sub, payload, ref patched);   // users\<hash> 一层
            }
        }
    }

    static void PatchXworker(DirectoryInfo xworker, string payload, ref int patched) {
        string tmpl = Path.Combine(xworker.FullName, "mpxworker\\local\\tmpl");
        if (!Directory.Exists(tmpl)) return;
        string[] files;
        try {
            files = Directory.GetFiles(tmpl, "*.html", SearchOption.AllDirectories);
        }
        catch {
            return;
        }
        foreach (string file in files) {
            try {
                string html = File.ReadAllText(file, Encoding.UTF8);
                // 载荷更新后模板必须跟着更新:先前只判「含标记就跳过」,导致改了
                // payload.htm(新增留言区选择器)模板永远停在旧载荷——2026-10-23 实锤。
                // 改成:先剥掉旧载荷整段,再按当前载荷注入。
                // ⚠️ StripPayload 返回 null = 「不含我们的 marker」= 全新模板
                // (微信升级会下载新版本号模板,2026-10-30 实锤 8499560)——
                // 这种必须当原文注入!v1.5.2 曾误写成 continue 跳过,导致
                // 微信升级后的新模板永远裸奔、模板通道广告全回来。
                string stripped = StripPayload(html);
                if (stripped == null) stripped = html;
                // 载荷首尾空白裁掉,保证「剥→注」幂等(否则每次重注多一个换行)
                string payloadTrimmed = payload.Trim();
                string injected;
                int at = stripped.IndexOf("<head>");
                if (at >= 0) {
                    at += 6;
                    injected = stripped.Insert(at, payloadTrimmed);
                }
                else {
                    int close = stripped.IndexOf("</head>");
                    if (close < 0) continue;
                    injected = stripped.Insert(close, payloadTrimmed);
                }
                // 内容已等价就别写盘:模板是 12MB 级的文件,每分钟重写 18 份
                // 会把磁盘和 IO 拖垮(且内容没变,纯浪费)
                if (injected == html) continue;
                string tmp = file + ".adblock.tmp";
                File.WriteAllText(tmp, injected, new UTF8Encoding(false));
                File.Replace(tmp, file, null);
                patched++;
            }
            catch (Exception ex) {
                Log("模板补丁失败 " + file + ": " + ex.Message);
            }
        }
    }

    // 剥掉已注入的旧载荷块;不是我们打的补丁则返回 null
    // 载荷结构固定:<style id="wechat-adblock">…</style> + <script id="wechat-adblock-beacon">…</script>
    static string StripPayload(string html) {
        if (html.IndexOf(PayloadMarker) < 0) return null;
        int styleAt = html.IndexOf("<style id=\"" + PayloadMarker + "\">");
        if (styleAt < 0) return null;
        int styleEnd = html.IndexOf("</style>", styleAt);
        if (styleEnd < 0) return null;
        styleEnd += 8;
        string result = html.Substring(0, styleAt) + html.Substring(styleEnd);

        int scriptAt = result.IndexOf("<script id=\"" + PayloadMarker + "-beacon\">");
        if (scriptAt >= 0) {
            int scriptEnd = result.IndexOf("</script>", scriptAt);
            if (scriptEnd >= 0) {
                scriptEnd += 9;
                result = result.Substring(0, scriptAt) + result.Substring(scriptEnd);
            }
        }
        // 与 python 侧 payload.strip_payload 保持逐字节一致(否则两侧会互相
        // 「改写」模板:每轮各写 36 份 12MB 文件,纯 IO 浪费):
        //   ① 3 个以上连续换行合并成 2 个;
        //   ② 注入点(</head> 前)残留的空白行收成 1 个换行。
        result = CollapseBlankLines(result, 3, 2);
        result = Regex.Replace(result, @"[ \t]*\n[ \t]*\n(?=</head>)", "\n");
        return result;
    }

    // 把 n 个及以上连续换行压成 m 个
    static string CollapseBlankLines(string text, int n, int m) {
        var rx = new Regex(@"\n{" + n + ",}");
        return rx.Replace(text, new string('\n', m));
    }

    // ---- 日志:写后端 logs\guard.log(与 python 工具同约定,#196 文档指向它) ----

    static void Log(string msg) {
        try {
            string dir = Path.Combine(BackendDir(), "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "guard.log"),
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg +
                Environment.NewLine, Encoding.UTF8);
        }
        catch {
        }
    }

    public void Dispose() {
        lock (gate) {
            if (disposed) return;
            disposed = true;
        }
        KillChild();
        RelayStop();
    }
}

}  // namespace KeepAwake
