// audio_mute.cs — 启动时静音:Core Audio COM 直调 IAudioEndpointVolume.SetMute
//
// 语义(与用户既有的开机静音方案一致,见记忆库 #45):
//   - SetMute(1) 是幂等的「设置」而非静音键的「切换」——已静音再设无副作用,
//     不存在"本想静音反而放出声"的翻车面;音量值不动,取消静音即恢复原音量;
//   - 只作用于当前默认播放设备(eRender/eConsole):静音按设备记忆,会话中途
//     切默认设备(如插 HDMI)新设备按自身状态走,下次启动会被重新静音;
//   - COM 互操作直调,免管理员、无任何运行时依赖(pycaw 方案的 Python 依赖就此退场)。
//
// 登录竞态:随系统自启时音频服务可能尚未就绪(IMMDeviceEnumerator/Activate 抛异常)。
// 只在「没设成功」时后台重试(5s/15s/30s 各一次),成功即止——不会跟用户随后
// 手动开声音打架。(另一台机器的教训:一次性静音可能被端点晚初始化冲掉,守护式
// 复压需 180s;本机无此病史,失败重试已够,真遇到再升级。)
//
// 注意:C# 5 语法(csc 4.0.30319);本文件被 build_keep_awake.py 与 tests/probe_mute.py
// 共用编译——探针侧不带 keep_awake_tray.cs,AppLog 由探针自己提供同签名替身。

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace KeepAwake {

// ==== Core Audio COM 互操作(方法序即 vtable 序,不可乱) ====

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    void RegisterControlChangeNotify(IntPtr notify);          // 不用,占 vtable 位
    void UnregisterControlChangeNotify(IntPtr notify);        // 同上
    void GetChannelCount(out uint channels);
    void SetMasterVolumeLevel(float level, ref Guid ctx);
    void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    void GetMasterVolumeLevel(out float level);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(uint channel, float level, ref Guid ctx);
    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
    void GetChannelVolumeLevel(uint channel, out float level);
    void GetChannelVolumeLevelScalar(uint channel, out float level);
    void SetMute(bool mute, ref Guid ctx);                     // 本文件的主角
    void GetMute(out bool mute);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IntPtr props);      // 不用,占 vtable 位
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out uint state);
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    void EnumAudioEndpoints(int flow, int state, out IntPtr devices);  // 不用,占 vtable 位
    void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device); // eRender=0, eConsole=0
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr notify);
    void UnregisterEndpointNotificationCallback(IntPtr notify);
}

// CoCreateInstance 的 coclass 占位:名字无所谓,GUID 是 CLSID_MMDeviceEnumerator
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorComObject { }

static class SystemMute {
    static readonly Guid IidAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
    const int ClsctxAll = 0x17;

    static IAudioEndpointVolume Endpoint() {
        var enum0 = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice device;
        enum0.GetDefaultAudioEndpoint(0, 0, out device);        // 当前默认播放设备
        object activated;
        Guid iid = IidAudioEndpointVolume;
        device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out activated);
        return (IAudioEndpointVolume)activated;
    }

    /// <summary>把当前默认播放设备设为静音/取消静音(幂等设置,非切换;音量值不动)。</summary>
    public static bool SetMuted(bool mute) {
        try {
            IAudioEndpointVolume ep = Endpoint();
            Guid ctx = Guid.Empty;
            ep.SetMute(mute, ref ctx);
            return true;
        } catch (Exception ex) {
            AppLog.Write("静音设置失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>查询当前默认播放设备的静音态;无音频设备等异常返回 null。</summary>
    public static bool? GetMuted() {
        try {
            IAudioEndpointVolume ep = Endpoint();
            bool muted;
            ep.GetMute(out muted);
            return muted;
        } catch {
            return null;
        }
    }
}

// 「启动时静音」开关持久化:刻意独立于电源护栏的 Software\KeepAwake 键——
// 那处 Restore 完会把整个键删掉(DeleteSubKey),混放会被顺手清掉。
static class MuteAtStartupPref {
    const string RegPath = "Software\\KeepAwakePrefs";
    const string ValueName = "MuteAtStartup";

    public static bool Get() {
        try {
            using (var key = Registry.CurrentUser.OpenSubKey(RegPath)) {
                if (key == null) return true;                    // 未写过 = 默认开
                return Convert.ToInt32(key.GetValue(ValueName, 1)) != 0;
            }
        } catch {
            return true;
        }
    }

    public static void Set(bool on) {
        try {
            using (var key = Registry.CurrentUser.CreateSubKey(RegPath)) {
                key.SetValue(ValueName, on ? 1 : 0, RegistryValueKind.DWord);
            }
        } catch (Exception ex) {
            AppLog.Write("启动时静音开关写入失败: " + ex.Message);
        }
    }
}

// 启动静音入口:立刻设一次;失败(登录竞态音频服务未就绪)后台 5/15/30s 重试。
// 重试只在「尚未成功」时发生,成功后不再碰音频——用户手动开声音不会被压回去。
static class StartupMute {
    public static void Apply() {
        if (SystemMute.SetMuted(true)) {
            AppLog.Write("启动静音:已生效(默认播放设备)");
            return;
        }
        System.Threading.Tasks.Task.Run((Action)delegate {
            int[] waits = { 5000, 15000, 30000 };
            foreach (int ms in waits) {
                Thread.Sleep(ms);
                if (SystemMute.SetMuted(true)) {
                    AppLog.Write("启动静音:重试后已生效");
                    return;
                }
            }
            AppLog.Write("启动静音:多次重试仍失败(无音频设备?)");
        });
    }
}

}
