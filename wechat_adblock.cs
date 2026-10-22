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
        return OverrideOr("wechat-adblock-path.txt", @"E:\software\wechat-adblock");
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
            Log("mitmdump 持续故障 " + (portFails * 5) + "s,降级纯转发");
            RelayStart();
        }
        else {
            Log("mitmdump 未监听(fail #" + portFails + "),等待重试");
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
                    if (!child.HasExited) child.Kill();
                }
                catch {
                }
                child = null;
            }
        }
    }

    static void KillStrayMitmdump() {
        // taskkill /IM 只认镜像名;venv 里的 mitmdump.exe 是 uv shim,真身进程名是
        // python.exe → 两条路都走:按镜像名杀 + 按命令行特征(our addon)杀
        try {
            var psi = new ProcessStartInfo("taskkill", "/IM mitmdump.exe /F") {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi);
        }
        catch {
        }
        try {
            foreach (int pid in FindPidsByCommandLine("wechat_adblock.py")) {
                try {
                    Process.GetProcessById(pid).Kill();
                }
                catch {
                }
            }
        }
        catch {
        }
        Thread.Sleep(1500);
    }

    // 按命令行子串找 pid(wmic;找不到返回空)
    static List<int> FindPidsByCommandLine(string needle) {
        var hits = new List<int>();
        try {
            var psi = new ProcessStartInfo("wmic",
                "process get ProcessId,CommandLine /format:csv") {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            string outText = Process.Start(psi).StandardOutput.ReadToEnd();
            foreach (string line in outText.Split('\n')) {
                if (line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var cells = line.Split(',');
                for (int i = 0; i < cells.Length; i++) {
                    int pid;
                    if (int.TryParse(cells[i].Trim(), out pid) && pid > 0) {
                        if (!hits.Contains(pid)) hits.Add(pid);
                        break;
                    }
                }
            }
        }
        catch {
        }
        return hits;
    }

    // 443 的监听者是不是 mitmdump:按 TCP 连接的对端本机端口反查 owning pid 的进程名。
    // 走 netstat 文本解析(免 WMI 依赖);拿不到就返回 false(保守=重新拉起,最多一次抖动)
    static bool MitmdumpOwnsPort() {
        try {
            var psi = new ProcessStartInfo("netstat", "-ano") {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            string outText = Process.Start(psi).StandardOutput.ReadToEnd();
            foreach (string line in outText.Split('\n')) {
                if (line.IndexOf("127.0.0.1:443") < 0) continue;
                if (line.IndexOf("LISTENING") < 0 && line.IndexOf("ESTABLISHED") < 0) continue;
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                int pid;
                if (!int.TryParse(parts[parts.Length - 1], out pid)) continue;
                // 进程名不可靠:venv 里的 mitmdump.exe 是 uv 的 shim,真身进程名是
                // python.exe(实测 3.11),只能按命令行特征认:mitmdump + 我们的 addon
                if (LooksLikeMitmdump(pid)) return true;
            }
        }
        catch {
        }
        return false;
    }

    // 是不是我们的 mitmdump:命令行里同时出现 mitmdump 与 wechat_adblock.py
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
        try {
            var psi = new ProcessStartInfo("wmic", "process where processid=" + pid +
                " get CommandLine /value") {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            string cmd = Process.Start(psi).StandardOutput.ReadToEnd();
            if (cmd.IndexOf("mitmdump", StringComparison.OrdinalIgnoreCase) >= 0 &&
                cmd.IndexOf("wechat_adblock", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch {
        }
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
                if (html.IndexOf(PayloadMarker) >= 0) continue;
                string injected;
                int at = html.IndexOf("<head>");
                if (at >= 0) {
                    at += 6;
                    injected = html.Insert(at, payload);
                }
                else {
                    int close = html.IndexOf("</head>");
                    if (close < 0) continue;
                    injected = html.Insert(close, payload);
                }
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
