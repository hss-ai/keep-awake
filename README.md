<p align="center">
  <img src="docs/icon.png" width="120" alt="KeepAwake icon">
</p>

<h1 align="center">KeepAwake</h1>

<p align="center">
  Windows 托盘小工具:防待机 + 资源管理器单窗口合并,双击即生效,退出零残留。<br>
  图标是一杯咖啡:咖啡因撑着系统不打瞌睡;新开的资源管理器,自动并进那唯一的一扇。
</p>

<p align="center">
  <a href="https://github.com/hss-ai/keep-awake/releases">
    <img src="https://img.shields.io/github/v/release/hss-ai/keep-awake" alt="Release">
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/license-MIT-green.svg" alt="License: MIT">
  </a>
</p>

---

## ✨ 特性

- **托盘三态图标,一眼可辨**

  | 图标 | 状态 | 含义 |
  |:---:|---|---|
  | 🟢 绿 | 防待机 | 系统不睡,屏幕允许自动关 |
  | 🔵 蓝 | 防待机 + 屏幕常亮 | 系统不睡,屏幕也不关 |
  | ⚪ 灰 | 关闭 | 恢复系统默认行为 |

- **左键单击托盘图标 = 开 / 关切换**,不用进任何菜单;鼠标悬停显示已保持时长
- **右键菜单**:`防待机开启` / `屏幕常亮` / `资源管理器单窗口合并` / `开机自启` / `退出`,顶部另有一行置灰的实时状态项(同步显示当前状态与已保持时长);勾选「屏幕常亮」会自动连防待机一起开
- **资源管理器单窗口合并**(Win11,默认开启):新开的资源管理器窗口自动变成已有窗口里的新标签页——全 Windows 始终只有一扇资源管理器(详见下文专节)
- **单实例**:Mutex 保证,重复启动静默退出,不会多开图标
- **零残留**:唤醒请求只挂在本进程,正常退出、被杀、注销、重启,系统都会自动撤销请求;**不修改任何电源计划设置**
- **免安装单文件 exe**:数十 KB,无运行时依赖(依赖系统自带的 .NET Framework 运行)

## 📥 下载使用

1. 打开 [Releases](https://github.com/hss-ai/keep-awake/releases) 页,下载最新版的 **`KeepAwake.exe`** —— 单文件、免安装;**若 Releases 页暂无版本(二进制尚未发布),直接用下一节「从源码构建」,一条命令即可得到同样的 exe**;
2. 双击运行,托盘出现绿杯图标并弹气泡,防待机立即生效;
3. 左键托盘图标 = 开 / 关;右键菜单 = 屏幕常亮 / 退出。

> **遇到 SmartScreen 提示?** exe 未做代码签名,首次运行可能弹出「Windows 已保护你的电脑」——点 **「更多信息」→「仍要运行」** 即可,后续不再提示。
>
> Win11 默认把新托盘图标收进任务栏右下角 `^` 溢出区,拖到任务栏上即可常驻。

## 🛠 从源码构建

```console
python build_keep_awake.py
```

一条命令完成:**生成图标 → csc 编译 → 安装到 `%USERPROFILE%\Scripts\KeepAwake.exe` → 桌面创建「防待机」快捷方式**。重复执行 = 重建覆盖(会自动停掉在跑的旧实例)。

> **卸载**:退出托盘程序,删除 `%USERPROFILE%\Scripts\KeepAwake.exe` 与桌面「防待机」快捷方式即可,无其他残留。

构建环境:

| 依赖 | 说明 |
|---|---|
| Windows 10/11 | 编译器用系统自带的 `csc.exe`(.NET Framework 4.x),**无需安装 .NET SDK / Visual Studio / MSBuild** |
| Python 3.8+ + Pillow | 仅构建期用于生成图标(`pip install pillow`) |
| PowerShell 7(`pwsh`) | 装机两步(停旧实例、建桌面快捷方式)通过 `pwsh` 调用;Windows 自带的只有 5.1 |

> 未装 `pwsh` 时,构建在编译完成后、装机步骤处中断——exe 已产出于 `build\KeepAwake.exe`,可手动取用。

## 💻 命令行版用法

`keep_awake.py` 是零依赖的命令行版本(纯 Python 标准库),适合挂机下载、跑长任务、写进脚本:

```console
python keep_awake.py                # 阻止系统待机/休眠(屏幕允许自动关),Ctrl+C 退出
python keep_awake.py --display      # 连屏幕一起保持常亮
python keep_awake.py --minutes 90   # 90 分钟后自动结束并恢复默认
python keep_awake.py --quiet        # 不打印心跳行
```

| 参数 | 说明 |
|---|---|
| `--display` | 同时保持屏幕常亮(默认只阻止待机,屏幕可自动关闭) |
| `--minutes N` | `N` 分钟后自动结束并恢复默认;`0` = 不限时,Ctrl+C 退出 |
| `--quiet` | 不打印心跳行(默认每 0.5 秒刷新显示已保持 / 剩余时长) |

## 🗂 资源管理器单窗口合并

第二个托盘功能(默认开启,右键菜单 `资源管理器单窗口合并` 随时关)。参照 ExplorerTabUtility 的思路,让全 Windows 只保留 **1 个资源管理器窗口**:之后新开的窗口——双击文件夹、Win+E、其他程序唤起——都会自动并进这扇窗口,变成一个新标签页。

工作方式:

- 监听**新建**的资源管理器窗口(`SetWinEventHook` + `EVENT_OBJECT_CREATE`,还原最小化不会误触发);
- 经 Shell COM 读取新窗口的路径,再经 `Navigate2` 在既有窗口里**直接开出指向该路径的新标签**——零按键,不受中文输入法与前台焦点影响,然后关掉多余的新窗口;此路不通时自动退回键盘注入兜底(`Ctrl+T` + 地址栏输入,含输入法隔离与剪贴板粘贴两级后备);
- 第一个资源管理器窗口保留不合并——它就是「那一个」;
- 虚拟位置(主页/此电脑类)拿不到文件路径,退化为直接开一个主页新标签(与 Win+E 的默认落点一致);
- 从其他程序「打开所在文件夹」(带文件选中)合并后定位到所在文件夹,但文件高亮不保留;
- 仅键盘兜底触发时才有约半秒按键注入;依赖 Win11 22H2+ 的资源管理器原生标签页,Win10 无此能力(开关无效,但不报错);
- 合并行为异常时可看 `%TEMP%\KeepAwake_merge.log` 诊断日志。

真机测试脚本在 [`tests/`](tests/):`test_merge.py`(窗口计数判据)+ `probe_merge_once.py`(窗口标题=临时文件夹名判据,证明合并真实发生)。

## ⚙️ 工作原理

调用 Win32 API `SetThreadExecutionState`,向系统挂一个**只属于当前进程**的唤醒请求:

```c
ES_CONTINUOUS | ES_SYSTEM_REQUIRED    // 阻止系统待机 / 休眠
             | ES_DISPLAY_REQUIRED    // (可选)同时保持屏幕常亮
```

- **进程级,不碰全局**:不改电源计划、不改系统设置。这是它比 `powercfg` 改全局睡眠超时干净的地方 —— 进程退出(正常退出、被杀、直接关窗口、注销、重启),系统自动撤销请求,不留任何残留。
- **退出即恢复**:托盘版在退出时显式撤销请求;命令行版在 Ctrl+C / 到时后于 `finally` 中撤销。

**验证方法**:管理员终端运行 `powercfg /requests`,本进程会出现在 `SYSTEM` 请求列表里(开启屏幕常亮时还会出现在 `DISPLAY` 里);退出后条目随之消失。

## 📌 说明与限制

- **合盖行为不受本工具控制** —— 合盖睡不睡由电源计划的「合盖时」设置决定(默认睡眠)。需要合盖不睡:
  - Win11:设置 → 系统 → 电源 → 屏幕和睡眠 → 合盖时不执行任何操作;
  - Win10:在控制面板 → 硬件和声音 → 电源选项 →「选择电源按钮的功能」中设置。
- **普通用户权限即可** —— 需要管理员的只有 `powercfg /requests` 这条验证命令,工具本身不需要。
- **系统要求**:Windows 10 / 11(托盘版依赖系统自带的 .NET Framework 4.x;命令行版仅支持 Windows,零依赖);**资源管理器单窗口合并**进一步要求 Win11 22H2+。
- 托盘版与命令行版**可同时使用**:两个进程各挂各的请求,互不干扰,都退出后才完全恢复。
- **开机自启**:右键菜单勾选「开机自启」即可——写当前用户注册表 Run 键(免管理员),exe 挪窝后下次启动会自动指向新位置;取消勾选即撤销。

## License

[MIT](LICENSE)
