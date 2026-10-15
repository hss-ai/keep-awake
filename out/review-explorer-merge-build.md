# 审查报告:explorer 合并器 / 副作用基础设施 / 构建发布链路

- 审查对象:keep-awake @ HEAD `18f02dc`(v1.4.10),工作树干净
- 材料来源:编译基线实测 + 三路独立代码地图(合并主流程 / 副作用与基础设施 / 构建与发布链路)+ 12 条报告论断裁决 + 6 条通读新发现裁决 + 6 条未核验低严重观察
- 本报告为合成层:所有结论出自上述材料,未新增、未改判;矛盾或措辞分歧处按裁决原文并陈
- 计数口径:按材料四 18 条原始裁决计数;C1 与新发现·merge-core-2 为同一问题双重命中,合并呈现并注明互证,仍各计一条

---

## 总览

- **裁决分布:实锤 14 条、部分成立 4 条(C3/C6/C7/C11)、误报 0、已修复 0、无法定论 0、未核验低严重观察 6 条**(另有代码地图通读附带的十余条"未逐条核验"观察,见「未核验观察」节 B 部分)。无任何一条裁决为"无法定论"整条,但部分成立与新发现条目内含若干静态断环的子环节,单列于「通读新发现·需真机实测的断环环节」。
- **最要紧的问题:键盘兜底链两个零核实高危缺口。** ① Ctrl+T 路径 `merged=true` 无条件置位即关源窗(merge-core-1,high):`explorer_tab_merge.cs:366` 是全函数唯一无成功核实的路径,Ctrl+T 落空时目标窗无新标签、源窗照关。② 键盘兜底打字前无"新标签到位"守卫(C1,high,与新发现 merge-core-2 互证):Ctrl+T 被吞时 Ctrl+L 原地导航**用户当前标签**顶掉原视图,而成功判据只查"同路径存在"必误判合并成功——Win10 无标签页必现、Win11 Ctrl+T 偶发失效同路径。二者共同构成"关窗丢视图"缺陷,与代码自承的"宁可不合并也不丢用户的文件夹"原则(`explorer_tab_merge.cs:377` 注释)直接冲突。
- **最紧迫的流程问题(非代码缺陷):发布断链**(build-1,medium)——v1.4.8~v1.4.10 全部修复只 commit 未 push 未 tag,远端 Releases 停在 v1.4.7,新代码(含从未跑过 CI 的版本注入新路径)未经任何 CI;Releases 用户缺 v1.4.8 合并闸门与 v1.4.9/10 全部修复。
- **编译基线:`python build_keep_awake.py --build-only` 退出码 0**(编译+版本注入通过,产物落 gitignore 的 build/)。

---

## 报告论断逐条裁决

| 编号 | 论断摘要 | 裁决 | 与修复史关系 | 建议动作 |
|---|---|---|---|---|
| C1 | Win10 键盘兜底"劫持"既有窗口:Ctrl+L 把既有窗口导航走,判据=路径存在→误判成功关新窗 | **实锤(high)** | 新增;v1.4.8 三道防线对此形态全不触发;**与新发现·merge-core-2 互证(同一代码链)** | 修复:键兜底前加新标签守卫;合并前 UIA 探测标签能力,无则整体停用保窗;成功判据改"标签数恰+1" |
| C2 | 标题核验写死中文"- 文件资源管理器 / 和",非 zh-CN 多标签核验必败 | **实锤(low)** | v1.4.10 辩证存档延续,本轮核验存档继续有效(代码地图亦确认 589/610 仍在) | 维持存档(失败退化为开重复标签不丢数据;改 UIA 有挂死前科,zh-CN 部署收益为零) |
| C3 | Shell 枚举序==UIA 子项序的索引映射拖动重排后可能错位,同名文件夹误判通过 | **部分成立(low)** | v1.4.10 已存档"拖动重排同名误激活",本轮为存档延续核验,非新增 | 维持存档;闭环需真机重排探针(比对枚举序与 TabListView 子项序) |
| C4 | NavigateByClipboard 文本+富格式并存时只还原纯文本,富格式丢失 | **实锤(low)** | 新增未覆盖(富格式场景从未处理;**与新发现·merge-side-1 同域互证**) | 低危修复:整 IDataObject 快照还原,或扩白名单——混合内容直接走"不占用,兜底放弃"分支 |
| C5 | taskkill /f 重启 explorer 中断进行中的文件复制/移动/删除,确认框无提示 | **实锤(medium)** | 新增;重启流程自 v1.4.0 引入起即无检测 | 修复:taskkill 前 FindWindow("OperationStatusWindow") 检测,或最低成本确认框文案补一句 |
| C6 | kill explorer 后立即 Process.Start,shell 已被系统拉起时多开主页窗口 | **部分成立(low)** | 新增;重启段自 v1.4.0 后从未被改 | 存档观察;顺手修则 :295 前轮询 FindWindow("Shell_TrayWnd"),已出现即跳过 |
| C7 | WinEvent 钩子挂全系统 EVENT_OBJECT_CREATE,每次事件回 UI 线程 GetClassName 开销大 | **部分成立(low)** | 新增(v1.4.10③恰反向佐证钩子宿主=UI 线程的线程事实) | 无需动作:按报告自己的"先量再改";若改 idProcess 须补 explorer 重启重挂 |
| C8 | ComCall 每次新建线程并重建 ShellWindows,一次合并二三十次调用,应复用 | **实锤(low)** | 新增(不在 v1.4.10 存档清单);"二三十次"量级对虚拟/重试路径准确 | 存档不修:每调用一线程是 :980-983 明示的挂起隔离设计,复用以牺牲挂起恢复能力换延迟收益 |
| C9 | 构建脚本 taskkill /F 强杀旧实例,护栏残留到下次启动才自愈 | **实锤(low)** | 新增;自护栏引入以来一直存在(v1.4.10 GuardPending 后置修的是另一路径) | 轻量修复:copyfile 成功后补跑一次 `[dest,"--restore-power"]`(CLI 通道已存在,加一行);--quit 事件属过度工程不做 |
| C10 | 文档写"三个 .cs"实际 5 个;键映射注释写 20ms 判定窗而常量是 25 | **实锤(low)** | 历史漂移:"三个"自 v1.4.0 起失真;20ms 注释自 v1.4.5 引入时常量即 25 | 修复:纯注释文案更正(含 WM_TIMER 旧注释更新) |
| C11 | 合并模块约 1062 行偏大应拆分(UIA/COM/注入分开,Guard 与两套日志独立) | **部分成立(low)** | 无关(纯结构建议;报告把托盘文件的拆分点错挂在"合并模块"条目下) | 维持存档观察;若继续膨胀再拆,须同步 build 源清单/探针共用编译约定,优先分节/partial 不动临界区 |
| C12 | CI 只编译不跑测试;缺 tag↔版本校验、/warnaserror、代码签名 | **实锤(low)** | 存续项(CI 层从未覆盖;**与未核验观察 build-2 同域互证**) | 低优先级采纳:先落 tag↔版本比对(数行可落);/warnaserror 先本机确认存量零 warning;SignPath 按需申请 |

### 逐条证据

**C1(实锤,high;与 merge-core-2 互证)。** 逐环推演(explorer_tab_merge.cs,HEAD 18f02dc):①Win10 无标签页时 CountEmptyTabs 经 ComCall 正常返回≥0(:430),AddButton 为 UIA 找不到→invoked=false 正常 return(:449,469-471),comDead 仅在 COM 超时路径置位(:433,481)保持 false→键盘兜底照进(:320)。②兜底链 :332-346:Ctrl+T 无效(Win10 无标签页,:465 作者注释自认)→Ctrl+L 有效,路径打进目标窗当前视图(:736-763)顶掉用户原文件夹→TabExistsInWindow 判据=同路径记录存在(:819-821),刚被导航的窗口必命中→merged=true(:346)→WM_CLOSE 关新窗(:382-384)。③v1.4.8 三道防线逐环核验全不拦:MergeGate(:283)只防并发;快照放弃(:295)要求空串/超时,Win10 每窗一条 Shell 记录必非空(空路径也 Add"(虚拟)"占位,:842);comDead(:320)只防 COM 无响应——Win10 explorer 完全健康,防线恰好全部不触发。④keep_awake_tray.cs:326 默认 Enabled=true 无 OS 版本检测;README.md:104 却称 Win10"开关无效,但不报错"与实际矛盾。⑤Win11 Ctrl+T 偶发失效同一代码路径。保留意见:"文件夹丢失"措辞偏重——后退历史尚在,丢的是视图非数据。

**C2(实锤,low)。** explorer_tab_merge.cs:589 `display.IndexOf(" - 文件资源管理器")`、:610 `" 和 "` 两处中文写死,亲读 HEAD 仍如此。非 zh-CN 多标签标题必含本地化连词→三判全不中(:610)→:617 return false→:311 NavigateNewTab 按 LocationURL 判成败(:678-680,语言无关必成功)→开重复标签+:382-384 关新窗,不丢数据;论断限定"多标签时"准确——单标签可经 t==display / " - " 两个语言中性判过关。v1.4.2 引入、v1.4.7 重写 ActivateExistingTab 时原样保留、v1.4.10 辩证存档,本轮核验存档继续有效;改 UIA IsSelected 须新增一路跨进程 UIA 读(UIA 挂死前科,:573-575 注释)。

**C3(部分成立,low)。** 半真半不可定:①同名盲区=真——display 仅为叶名/新窗标题去后缀(:583,588-591),核实只比标题串(:608-613),错位选中同名异路径文件夹时标题照常命中→误判通过→关新窗;后果无数据丢失(目标路径仍在另一标签),异名错位则 ~2s 核实拦下退回新开标签(:616-617)。②错位前提静态定不了:TabIndexOf 按 IShellWindows 同 HWND 记录出现序计 idx(:672-688),UiaSelectTab 取 TabListView 第 idx 子项(:710-722),拖动重排是否同步注册序属 OS 行为。③"疑似未测"属实:probe_reuse_tab.py:141-143 仅按创建序建标签,全仓 grep 重排/reorder/drag 零命中。

**C4(实锤,low;与 merge-side-1 同域互证)。** explorer_tab_merge.cs:777-778 ContainsText() 为首查分支,Word/Excel 复制必含 CF_UNICODETEXT 走此支,prev=GetText() 仅存纯文本;:788 SetText(path) 内部 SetDataObject 整体替换剪贴板,富格式(HTML/RTF/Bitmap)此刻被毁;:810 SetText(prev) 仅还原文本。grep 全部 .cs 无 SetDataObject/GetDataObject/IDataObject,整对象快照未实现。设计注释(:771-773"非文本内容恢复不了就不动它")被违反——文本分支先于 ContainsImage 守卫(:779-783),Excel 复制所含 CF_BITMAP 混在文本里照毁。与 v1.4.10"剪贴板兜底空基线用后 Clear"非同一处(git show 18f02dc 该方法仅增一行 :811 else Clear(),只覆盖 prev==null);本条是 prev!=null 的文本+富格式并存场景,git log -L 该方法仅 v1.1.0/1.1.1/1.4.0/1.4.10 改动,富格式场景从未处理。

**C5(实锤,medium)。** 重启 explorer 唯一路径=小箭头切换:keep_awake_tray.cs:439-442 确认框文案只写"任务栏与桌面会闪几秒…文件夹窗口自动还原成标签页",无文件操作中断提示;点"是"→:443-449 直接 Task.Run 调 RestartExplorerAndRestoreFolders,中间无检测。shortcut_arrow.cs:271-304:快照仅文件夹窗口路径(:272-284)→:287-291 无条件 taskkill /f /im explorer.exe(按映像名杀全部实例)→:294-295 睡 800ms 重启→:297-303 逐个重开。全仓 .cs grep FindWindow|OperationStatus|文件操作|复制|进度 零命中。文件操作进度窗宿主 explorer.exe、强杀即中止属公开 Windows 行为;代码侧无任何缓冲设计,码内各环全实证。

**C6(部分成立,low)。** 代码前提实锤:shortcut_arrow.cs:287-291 taskkill→:294 固定 Sleep(800) 无状态检测→:295 无条件 Process.Start("explorer.exe") 无参;全仓 5 个 .cs 搜 Shell_TrayWnd/TrayWnd/FindWindow 零命中。本机注册表 Winlogon AutoRestartShell=1(只读实测),winlogon 自动拉起这路必然发生,两路无互斥,竞态结构确凿。静态断环:①800ms 时点 winlogon 拉起的新 shell 是否已就绪;②代码 :295 先行成为 shell 后 winlogon 是否仍无条件 CreateProcess 第二个——均需真机探针。即便触发,多开窗口被默认开启的合并器并入,收敛为多一个主页标签页,不丢数据,low 相称。

**C7(部分成立,low)。** explorer_tab_merge.cs:175-176 SetWinEventHook(EVENT_OBJECT_CREATE,…,idProcess:0)=全系统无进程过滤✓;GetClassName 确在 UI 线程执行(WINEVENT_OUTOFCONTEXT 投递装钩线程;keep_awake_tray.cs:74/:324-326 同一 UI 线程;v1.4.10 提交信息"UI 线程挂=钩子宿主全堵"佐证)✓。但"每次事件都做 GetClassName"过述::189 先早退(idObject!=OBJID_WINDOW||hwnd==0),仅窗口级创建事件才 :191 GetClassName(非跨进程文本往返);重活已转 worker(:192-193 入队)。"开销大"量级静态无法定,需真机探针量化;重挂逻辑是 idProcess 方案的正确前提✓。

**C8(实锤,low)。** ComCall(:984-993)每次 new STA 线程+Join 超时即弃;被包 7 函数(:497/:523/:625/:670/:833/:863/:897)各自 Activator.CreateInstance 重建 ShellWindows,全文件零复用——机制属实。单次合并计数:顺利文件路径≈10 次、虚拟位置≈24-28、重试 30+;"二三十次"对虚拟/重试路径准确。但复用建议与 :980-983 明示的挂起隔离设计冲突:每调用一线程正是超时弃置单元(explorer COM 实测无征兆挂起,:323-325/:436-439),共享 STA 一次挂起即毒化该作业后续全部调用;MergeGate(:283)只串行闸内,轮询在闸前仍跨作业并发,串行未使复用更可行。开销仅延迟、无正确性影响。

**C9(实锤,low)。** ①--build-only 编译完直接 return 0 不触 taskkill——CI 路径干净,强杀仅本地装机路径 build_keep_awake.py:198(taskkill ×6 循环,忽略退出码)。②/F=TerminateProcess 不发 WM_QUERYENDSESSION;进程内 Disengage 仅两调用点(TurnOff=UI 菜单触发、OnSessionEnding=注销/关机),强杀时均不可达→护栏残留成立。③全仓 grep 无命名事件/管道优雅退出通道;Mutex(:70-71)是"新实例静默退出"。④kill 后仅 copyfile+快捷方式不启动新实例,残留持续到下次启动 :266 SelfHealIfPending 或手动 --restore-power(:55-58 已存在)——"下次启动才自愈"属实。

**C10(实锤,low)。** ①build_keep_awake.py:2 docstring 列 5 个 .cs,:13 写"三个 .cs 源文件",SRC_FILES(:35-41)恰 5 项,同文件自相矛盾。②key_remap.cs:9/12/24 与 keep_awake_tray.cs:17 写"20ms 判定窗",常量 PendingWindowMs=25(:78),推演链 DecideLoop(:230 Sleep(25))→补发,窗口与延迟实际均 25ms;:15 又写"判定窗 25ms",同一头注 20/25 并存;附带 :24-25"WM_TIMER 实现"与现行判定线程实现(:168-171 注释明说弃用 WM_TIMER)矛盾。均早于 v1.4.7~10 且未被顺带修。

**C11(部分成立,low)。** 属实半边:行数精确吻合(1062,仓库最大);UIA/COM 查询与输入注入确实混在同一文件同一类(:35-1060),仅两条分节注释轻量分区。不符半边:PowerHibernateGuard 与 AppLog 都在 keep_awake_tray.cs(:103/:81)不在合并模块;两套日志本就分居两文件;"重启 explorer"在 shortcut_arrow.cs:287-295——报告把针对托盘文件的拆分点错挂在"合并模块"条目下,该半句归属错误。拆分本身可行但连带 build_keep_awake.py:36-40 源清单、csc 等价命令注释、探针共用编译约定,且该文件刚经 v1.4.8/v1.4.10 时序敏感修复(MergeGate:283、stateLock:142),机械拆分有行为回归风险,收益纯组织性。

**C12(实锤,low)。** ①build.yml 全 7 步无任何测试或校验步骤✓。②tag↔版本一致性校验缺失——版本唯一源 keep_awake_tray.cs:265"启动(v1.4.10)",build_keep_awake.py:110-123 只读源码、全脚本不感知 git tag,而 build.yml:6 以 tags:['v*'] 触发、:48-54 按 tag 挂 release,tag 漂移时挂错版本不拦✓。③build_keep_awake.py:172-179 无 /warnaserror✓。限定:tests/ 7 个文件全是真机端到端探针(会动系统静音态/全局键盘钩子),不能原样搬进 CI runner;零成本可落的只有版本比对类静态检查;SignPath Foundation 开源免费已查证存在(需申请审核)。

---

## 通读新发现(已裁决)

### 新发现·merge-core-1 —— Ctrl+T 路径 `merged=true` 零核实即关源窗(实锤,high)

explorer_tab_merge.cs:363-367 亲读:BringToFrontVerified 通过→SendCtrlCombo(VK_T)→Sleep(400)→merged=true 无条件,全段无任何核实——:366 是同函数四条路径中唯一零核实路径(其余 :303 经 TryActivateTab 标题核实 :608-617、:313 与 :346 经 TabExistsInWindow)。merged=true→:382-384 IsWindow 即 PostMessage(WM_CLOSE) 关源窗,Ctrl+T 落空时目标窗无新标签、源窗照关。加重因素:SendKeys(:1010-1012)不查 SendInput 返回值;SendCtrlCombo 前无 StillForeground 复查(NavigateTarget :753 有);且 path="" 常因 GetExplorerShellPath 3000ms 超时(:262)=explorer 忙,explorer 忙又使 400ms 固定等待更易不够——两失败条件同源正相关。:377 注释自承"宁可不合并也不丢用户的文件夹"原则,本分支是唯一违反点。后果措辞修正:是"虚拟位置窗口消失无替代"(无实体数据损失,可重开),比"原文件夹丢失"轻一档。git blame 证实 :366 自 v1.4.0 引入后从未改动;v1.4.8 仅给该分支加 KeyboardGate 包装,未加核实。修法材料现成:CountEmptyTabs(:493-514)+NavigateNewTab 基线+1 核实模式(:430/:476-487)可平移。Ctrl+T 落空频率与主页 Self.Path 可读性需真机探针,不影响缺陷存在性判定。

### 新发现·merge-core-2 —— 键盘兜底打字前无"新标签到位"守卫,原地顶掉用户标签(实锤,high;与论断 C1 互证)

explorer_tab_merge.cs:331-334 前台验证过后直接 Ctrl+T+Sleep(400)+NavigateTarget,无新标签核实;:736 NavigateTarget 首动作 Ctrl+L 作用于当前活动标签(:757 打字/:763 回车),Ctrl+T 被吞即原地导航用户原标签;:336/:341/:346 三段验证只调 TabExistsInWindow(:819-821→:829-855 仅查同路径存在性),被顶掉的原标签同样命中→:382-384 关新窗。不对称自证:COM 路径有守卫(:430 取 CountEmptyTabs 基线,:476-486 等 ≥基线+1 才导航)而键盘路径无;:423-427 注释自认同形态是 v1.2.2(git show 1511b00)修"老标签被顶掉"时必修过的实锤——当时只给 COM 路径加了守卫,键盘路径同形态漏修。keep_awake_tray.cs:324-326 默认 Enabled=true 且 grep 全 .cs 无 OS 版本闸→Win10(:465 自认无标签页,AddButton 必缺失)必走键盘兜底必现,全链静态闭合;Win11 需 AddButton 三连败叠加 Ctrl+T 失败,发生率需真机探针(不影响缺陷成立)。本条与 C1 为同一问题在"报告论断"与"通读新发现"两个来源的双重命中,互证合并:修法=打字前加"新标签到位"守卫(未开出即中止保窗,与 COM 路径对称),顺带评估 Win10 直接禁用合并的 OS 闸。

### 新发现·merge-core-3 —— WM_CLOSE 前对新窗零复核,竞态窗口内用户操作被关(实锤,medium)

explorer_tab_merge.cs:250-267 轮询 40×200ms≈8s 读到非空即 break,path 固化后全程不再读新窗;:283 MergeGate 排队可再等数十秒;操控段(TabsSnapshot 2s + NavigateNewTab 最坏~22s + 标签到位轮询 + 键盘兜底多轮 Sleep)合计常态 3~10s、最坏 30s+;:303/:312/:336/:346 的 merged 判定全部只看 target 窗口的 TabExistsInWindow,与 newHwnd 当前内容无关;:382-384 唯一关窗点仅 IsWindow(newHwnd) 存在性检查后 PostMessage WM_CLOSE(通读全文 1063 行确认无其它复核点)。COM 首选路径(:310-318)不碰新窗前台,用户在竞态窗口内自由操作新窗无阻;WM_CLOSE 关闭新窗及全部标签,用户导航位置/手动标签丢失。修复:WM_CLOSE 前加 ComCall 限时复核 GetExplorerLocation(newHwnd) 仍等于当初判定的 path,已变化则保窗不关。

### 新发现·merge-side-1 —— 剪贴板前置检查契约三处违反(实锤,medium;与论断 C4 同域互证)

explorer_tab_merge.cs:775-786 前置检查整段 try/catch{} 空吞,ContainsText 抛 ExternalException(剪贴板被瞬时占用)时 prev=null 继续走;:779-781 白名单仅 ContainsImage/FileDropList/Audio,RTF-only/HTML Format/私有格式(无 CF_UNICODETEXT)时四项全 false 不 return;:788 SetText(path)(SetDataObject 内部 EmptyClipboard 语义)覆盖全部旧格式;:808-813 finally 里恢复失败也 catch{} 无日志——与 :771-773 注释契约"非文本内容不动它,恢复不了就毁了→直接放弃"直接矛盾。触发链 :341-343 属实(:320 拦 comDead,需 COM 活着但导航+键盘两轮全败,边缘但静态成立)。补充同源洞=文本+图元混合(Excel 复制)走 ContainsText 分支,恢复只回纯文本——即论断 C4,两来源互证:merge-side-1 覆盖"纯富格式整体被毁+检查空吞+恢复无日志",C4 覆盖"混合内容富格式丢失",修法一致(检查抛异常即 return、白名单改全格式清单、恢复失败补 Log,或整 IDataObject 快照)。全由 WinForms/Win32 公开 API 语义可证,无需真机探针。

### 新发现·merge-side-2 —— 重启快照裸 COM 无限时,explorer 挂死即空快照照杀+承诺失实(实锤,medium)

shortcut_arrow.cs:274-281 逐窗口裸 COM 读 w.Document.Folder.Self.Path,无任何限时(全文件无 ComCall/Join,仅 SendMessageTimeout 与 taskkill WaitForExit(3000)@291 有超时,均不护快照);:282-284 快照异常仅记日志;:286-291 taskkill /f 无条件照杀→空快照时已开标签全丢,与 keep_awake_tray.cs:441 弹窗承诺"已打开的文件夹窗口会自动还原成标签页"矛盾;挂死时 :285 行起全不执行且无任何日志,:443-449 Task.Run fire-and-forget 静默泄漏线程。挂死机制有本库实测背书:explorer_tab_merge.cs:980-983"同步调用可能无限挂起(2026-10-04 实测…枚举都挂过)"、:660-663 Self.Path"跨进程慢调用(实测能把整个枚举拖到超时)";ComCall 限时纪律仅用于合并路径。代码地图"副作用"路亦独立记录此观察(⑥重启快照 Self.Path 慢调用,自注已并入本发现)——双重命中互证。v1.4.8 的快照超时纪律只焊在合并路径,重启快照从未套限时。修复:快照枚举套限时线程(超时按快照失败处理),失败/超时回用户确认是否仍重启。

### 新发现·build-1 —— 发布断链:v1.4.8~v1.4.10 未推送未打 tag,Releases 停在 v1.4.7(实锤,medium)

逐项独立复核全中:①git ls-remote origin main→35eb5e5,本地 HEAD=18f02dc、"ahead 2";②远端 tag 止于 v1.4.7(本地连 tag 都没打);③gh release list→Latest=v1.4.7;④最后一次 CI run(37906477291)headSha=35eb5e5,18f02dc 提交于 2026-10-10 此后零 run=新代码未经 CI;⑤build.yml:3-7(tags 触发)+47-48(挂 release 限 refs/tags/),v1.4.8 走 main push 非故未成 release;⑥README.md:45 引导用户从 Releases 下载;⑦18f02dc 新增的版本注入(source_version/write_version_sources)在 main() 编译前无条件执行,CI 路径覆盖该新逻辑但从未跑过。非代码缺陷,是发布流程断链:Releases 用户拿到的 v1.4.7 缺 v1.4.8 合并闸门(explorer 0xC00000FD/AppHang 根治)与 v1.4.9/10 全部修复;本地装机用户(已装 v1.4.10)不受影响。动作:推送 18f02dc 至 origin/main 并打 v1.4.10 tag 触发 release(动远端状态,由主流程/用户执行);长期把"打 tag 发版"纳入发布 checklist。

### 需真机实测的断环环节(无整条裁决为"无法定论",以下为部分成立/实锤条目内静态无法闭合的子环节)

- C6 两断环:800ms 时点 winlogon 拉起的新 shell 是否已就绪;代码 :295 先行成为 shell 后 winlogon 是否仍无条件 CreateProcess 第二个(探针:taskkill 后轮询 Shell_TrayWnd 出现时刻 vs 800ms,或直跑该函数数窗口)。
- C3:拖动标签重排是否同步 IShellWindows 注册序(探针:开多标签→拖动重排→比对枚举序与 TabListView 子项序)。
- C7:回调频率+GetClassName 次数+CPU 开销量级(报告自提"先量再改")。
- C2:非 zh-CN 多标签标题必含本地化连词(结构同构推断,未真机验证 en-US 标题)。
- merge-core-1:Ctrl+T 落空频率与主页 Self.Path 可读性。
- merge-core-2:Win11 上 AddButton 三连败+Ctrl+T 失败叠加的发生率。
- merge-side-2:explorer 挂死触发频率。
- C5:文件操作进度窗被强杀中止的真机演示(只读规则下无法演示;代码侧无缓冲已实证)。
- C12②:/warnaserror 开启前须本机编译确认存量零 warning(本轮只读未跑)。

---

## 未核验观察

### A. 材料五清单(6 条,均**未核验**,低严重,不应当实锤引用)

1. **merge-core-4(未核验,low)**:NavigateNewTab 的成功验证可被合并前就存在的同路径旧标签点亮——ActivateExistingTab 因 UIA Select 失败或标题核验失败让位后旧标签仍在,TabExistsInWindow 存在性命中≠新标签到位→重复标签/滞留主页空标签(数据无损)。位置:explorer_tab_merge.cs:311-314(验证仅存在性 :819-821;让位路径 :604-617;:588-590 display 截取写死中文即 C2 已存档退化)。建议:验证改"目标窗标签总数较 CountEmptyTabs 之前基线 +1 且新标签路径==path"。
2. **merge-side-3(未核验,low)**:重启确认问句承诺"自动还原成标签页"但不检查合并器开关——OnToggleArrow(keep_awake_tray.cs:419-458)全程不引用 merger.Enabled,合并器可经菜单独立关闭(:384-387,默认开 :324-326);关闭态重启后还原成一堆散窗口,文案失实、单窗布局被毁。建议:弹问句前检查 merger.Enabled,关闭态改文案或临时开启完成并回后再回滚。
3. **merge-side-4(未核验,low)**:键盘兜底里 Ctrl+L 组合键在最后一次前台复查之前就注入——:736 NavigateTarget 先 SendCtrlCombo(VK_L) 后 :753-756 才 StillForeground;:794 剪贴板路径同构;BringToFrontVerified(:331/:363)到此间隔可达 2-4s,期间用户切窗即把按键打进当前应用,与 :773 注释承诺的复查时序不符。建议:两处 SendCtrlCombo(VK_L) 前各补一次 StillForeground,失败即中止保窗。
4. **build-2(未核验,low)**:release 附产物只认 ref 前缀 v*,无 tag↔注入版本一致性校验,忘 bump 启动行即发布错位产物(build.yml:47-54 无比对;版本唯一源 build_keep_awake.py:110-123)。**与 C12 裁决②同域互证**——C12 裁决已独立确认"tag↔版本校验缺失"为实锤的一部分,本条属未核验重述,修法同 C12①。
5. **build-3(未核验,low)**:source_version 正则对整个 .cs 全文取首个匹配且无 AppLog.Write 锚定(build_keep_awake.py:119-120),文件前部将来出现含"启动(vX.Y.Z)"的注释/变更日志即静默取错版本;当前恰好唯一(grep -c=1),闭环靠文本模式巧合而非结构锚定。建议:正则锚定 AppLog.Write 形态或断言全文匹配唯一。
6. **build-4(未核验,low)**:装机覆盖循环只捕 PermissionError(build_keep_awake.py:197-205),其余 OSError(磁盘满/路径非法)裸 traceback;csc/powershell 失败输出硬编码 GBK 解码(:105-106/:182-183),非中文系统诊断乱码(errors=replace 保证不崩)。建议:except 扩到 OSError 并区分文案;解码改按 locale.getpreferredencoding(False)。

### B. 代码地图通读附带观察(均标注"未逐条核验",仅记录)

合并主流程(explorer_tab_merge.cs):
- AddButton 超时线程迟到可能多点标签(:474 已知);
- Ctrl+T 成功但打字全败时目标窗残留空标签(:332 后无回滚);
- job.Start 前无 try,Start 抛异常则 hwnd 永留 inFlight(:210-224);
- Dispose 不等在飞合并,退出可留半合并态(剪贴板可能含路径);
- NavigateEmptyTab 取"最后空标签"依赖枚举序假设,拖动重排后可能选中用户主页标签;
- v1.4.10 已存档项(标题核验写死中文 :589/:610、本地化目录名、拖动重排同名误激活)确认仍在——与 C2/C3 裁决互证。

副作用与基础设施:
- Log 并发 AppendAllText 撞文件锁被空 catch 吞,丢日志行(:120-128);
- dynamic COM 子对象(w.Document/Folder/Self/SelectedItems)RCW 不释放靠 GC(:277,633,904);
- ComCall 超时线程弃置无上限,explorer 挂死时多作业各 ~40 轮可累积上百弃置 STA 线程(:984-993+:250-267);
- InstallHook 失败仅 Debug.WriteLine,release 无诊断无重试(:177);
- UIA 线程未设 STA(:443,:703),与全库 STA 纪律不一致;
- (⑥重启快照 Self.Path 慢调用已并入裁决 merge-side-2,不重复计。)

构建与发布链路:
- Pillow/python-version 均未钉死(build.yml:22,25),依赖升级改图标字节→exe 跨环境不可复现(sha256 仅单次运行内自洽);
- write_version_sources 注入正则取首个 4 段 version=,当前 XML 声明 version="1.0" 不匹配故安全,manifest 将来加带 4 段 version 的前置节点会注错处;
- push 同时命中 branches 与 tags 过滤会跑两次 workflow,冗余无害;
- build/ 内 9 个部署探针 ps1 与 probe_keys.cs 均未入库,部署知识单机存放;
- 非 GBK 系统上 csc/powershell 失败输出按 GBK 解码仅乱码不崩(**与材料五 build-4 重叠,互证**);
- desktop_dir 注册表读取失败回退 %USERPROFILE%\Desktop 在重定向机器上可能落非显示桌面(双重边缘)。

---

## 建议动作清单

### 本轮修(代码/流程,按严重度排序)

1. **[high] merge-core-1**:explorer_tab_merge.cs:366 的 Ctrl+T 路径——注入前取 CountEmptyTabs 基线,注入后轮询核实空标签数≥基线+1 才置 merged(平移 :476-487 同法),计数超时按未见处理保窗。
2. **[high] C1/merge-core-2(互证合并)**:键盘兜底打字前加"新标签到位"守卫(未开出即中止保窗,与 COM 路径 :430/:476-486 对称);合并前(或键兜底前)UIA 探测目标窗口 TabListView/AddButton 能力,没有即整体跳过合并保窗(Win10 直接受益,可弹气泡);成功判据从"路径存在"改"合并前快照对比标签数恰+1"。
3. **[medium] merge-core-3**:WM_CLOSE 前加 ComCall 限时复核 GetExplorerLocation(newHwnd)(虚拟位置辅以标题比对)仍==当初判定的 path,已变化则保窗不关。
4. **[medium] merge-side-1/C4(互证合并)**:剪贴板前置检查抛异常即 return、白名单改全格式清单(纯文本才可占用,顺带护住文本+图元混合)、恢复失败补 Log——三处均一两行;或整 IDataObject 快照还原(注意延迟渲染格式跨进程重设的丢失风险)。
5. **[medium] merge-side-2**:重启快照枚举套限时线程(超时按快照失败处理),文件系统标签优先 LocationURL、仅虚拟路径读 Self.Path;快照失败/超时回用户确认是否仍重启。
6. **[medium] C5**:taskkill 前加 FindWindow("OperationStatusWindow") 检测,检出即提示或延后;或最低成本在确认框文案补"进行中的文件复制/移动会被中断"。
7. **[medium] build-1**:推送 18f02dc 至 origin/main 并打 v1.4.10 tag 触发 release(**动远端状态,由主流程/用户执行**);长期把"打 tag 发版"纳入发布 checklist 或加 CI 自动比对提醒。
8. **[low] C12**:①先落 tag↔启动日志版本比对(tag 钩子已在+source_version() 可 import,数行可落);②/warnaserror 先本机确认存量零 warning 再开;③SignPath 按需申请。
9. **[low] C9**:build_keep_awake.py 装机路径 copyfile 成功后补跑一次 [dest,"--restore-power"](一行);--quit 命名事件不做。
10. **[low] C10**:纯注释更正(build_keep_awake.py:13"三个"→"五个";key_remap.cs:9/12/24 与 keep_awake_tray.cs:17 的 20ms→25ms;key_remap.cs:24-25 WM_TIMER 注释更新为判定线程实现)。

### 维持存档(不动)

- C2(标题核验中文写死:失败退回新开标签不丢数据,改 UIA/路径级风险大于收益);
- C3(拖动重排错位:受标题核实兜底,闭环需真机重排探针);
- C8(ComCall 不复用:挂起隔离设计,复用牺牲挂起恢复能力,开销仅延迟);
- C11(模块拆分:收益纯组织性,机械拆分有时序回归风险,继续膨胀再拆);
- C7(WinEvent 开销:按"先量再改",量前不动);
- C6(存档观察为主;若顺手修,Shell_TrayWnd 轮询数百 ms 即可)。

### 需真机探针验证(改码前/后量化)

- C3:多标签拖动重排→比对 IShellWindows 枚举序与 UIA 子项序;
- C7:回调频率+GetClassName 次数+CPU;
- C6:taskkill 后轮询 Shell_TrayWnd 出现时刻 vs 800ms 固定等待;
- merge-core-1/merge-core-2:Ctrl+T 落空频率、Win11 AddButton 三连败+Ctrl+T 失败叠加发生率;
- merge-side-2:explorer 挂死触发频率;
- C12②:/warnaserror 开启前本机编译确认存量零 warning;
- 未核验观察 A/B 两节各条的坐实(尤其 merge-core-4 验证判据、merge-side-3 合并器开关检查、merge-side-4 前台复查补位)。

---

## 静态审查边界

1. **只读静态推演**:本轮全部裁决基于对 HEAD 18f02dc(工作树干净)的读码推演,禁跑探针/禁真机操作。凡依赖 OS 运行时行为的环节——winlogon 拉起时序(C6)、拖动重排注册序(C3)、Ctrl+T 失败率(merge-core-1/2)、非 zh-CN 标题结构(C2)、剪贴板瞬时占用(merge-side-1)、文件进度窗中止演示(C5)——只能静态断环,已在「需真机实测的断环环节」逐条写明缺什么实测。
2. **编译基线的覆盖面**:`--build-only` 退出码 0 仅证明"编译+版本注入通过",不含任何运行时行为;tests/ 8 个探针脚本不参与构建,本轮均未执行。
3. **未核验观察的定级来源**:材料五 6 条与代码地图"其他观察"十余条仅经通读记录,未做逐条独立裁决,严重度为标注者自标,发布决策前不应直接当实锤引用。
4. **多来源分歧的处理**:C1"文件夹丢失"措辞在裁决中被修正为"丢的是视图非数据"(后退历史尚在)、merge-core-1 后果修正为"虚拟位置窗口消失无替代(轻一档)"——本报告按裁决原文并陈措辞修正,未改判裁决等级;merge-side-2 中审查员引用的"661-663"行号经核实微偏但内容属实。
5. **行号基准**:全部行号以 HEAD 18f02dc 为准;若后续提交移动代码,行号需重新对位。
