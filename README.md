# KeepAwake · Windows 防待机小工具

让 Windows 笔记本不待机的托盘常驻小工具:**双击即生效,左键一点就开关,退出零残留**。

单文件 C# 源码 + 一键构建脚本,用 Windows 系统自带的 .NET Framework `csc` 编译——**不需要安装任何构建链**,产物是一个 28 KB 的原生 exe,秒启动、无运行时依赖。

## 功能

- **托盘常驻**:启动即开启「系统防待机」(屏幕允许自动关闭),并弹气泡提示
- **左键单击托盘图标 = 开/关切换**
- **图标三态一眼可辨**:🟢 绿 = 防待机 / 🔵 蓝 = 防待机 + 屏幕常亮 / ⚪ 灰 = 已关闭;鼠标悬停显示已保持时长
- **右键菜单**:`防待机开启` / `屏幕常亮`(勾上自动连防待机一起开)/ `退出`
- **单实例**:重复启动自动静默退出,不会多开图标
- **零残留**:基于 `SetThreadExecutionState`,唤醒请求只挂在本进程——正常退出、被杀、注销、重启,系统都自动撤销请求;**不修改任何电源计划设置**

## 快速开始

### 方式一:托盘版(推荐)

构建环境:Windows 10/11(自带 .NET Framework 4.x)+ Python 3.8+(构建期用 Pillow 画图标,`pip install pillow`)。

```
python build_keep_awake.py
```

一条命令完成:生成图标 → 编译 → 安装到 `%USERPROFILE%\Scripts\KeepAwake.exe` → 桌面建「防待机」快捷方式。

之后的使用方式:双击桌面「防待机」→ 托盘出现绿杯图标并弹气泡 → 左键开关 / 右键菜单退出。

> Win11 默认把新托盘图标收进任务栏右下角 `^` 溢出区,点开可见;拖到任务栏上即可常驻。

### 方式二:命令行版(挂机跑任务/定时)

`keep_awake.py` 是零依赖的命令行版本,适合挂着下载、跑长任务时用:

```
python keep_awake.py                # 阻止系统待机(屏幕允许自动关),Ctrl+C 退出
python keep_awake.py --display      # 连屏幕一起保持常亮
python keep_awake.py --minutes 90   # 90 分钟后自动结束并恢复默认
python keep_awake.py --quiet        # 不打印心跳行
```

## 工作原理

调用 Win32 API `SetThreadExecutionState`:

```
ES_CONTINUOUS | ES_SYSTEM_REQUIRED           # 阻止系统待机/休眠
             | ES_DISPLAY_REQUIRED           # (可选)同时保持屏幕常亮
```

唤醒请求**只对当前进程生效**,进程退出(含被杀、直接关窗口)系统自动撤销——这是它比 `powercfg` 改全局睡眠超时干净的地方:不改系统设置、不留任何残留。

**验证方法**:管理员终端运行 `powercfg /requests`,本进程会出现在 `SYSTEM`(开启屏幕常亮时还有 `DISPLAY`)请求列表里;退出后条目消失。

## 说明与限制

- **合盖行为不受本工具控制**——合盖睡不睡由电源计划的「合盖时」设置决定(默认睡眠),与本工具无关
- 托盘版与命令行版可同时使用(两个进程各挂各的请求,互不干扰;都退出后才完全恢复)
- 需要管理员权限的只有 `powercfg /requests` 验证命令,工具本身普通权限即可

## 目录结构

| 文件 | 说明 |
|---|---|
| `keep_awake_tray.cs` | 托盘版 C# 源码(WinForms NotifyIcon,限 C#5 语法以兼容系统自带 csc) |
| `build_keep_awake.py` | 一键构建:画图标 → csc 编译 → 安装 → 桌面快捷方式 |
| `keep_awake.py` | 命令行版(纯标准库,零依赖) |

## License

[MIT](LICENSE)
