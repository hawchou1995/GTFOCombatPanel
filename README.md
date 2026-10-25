# CombatPanel 1.19.7

源码：[hawchou1995/GTFOCombatPanel](https://github.com/hawchou1995/GTFOCombatPanel)。当前功能、JSON 称号示例和多人协议要求以 `Thunderstore/README.md` 为准；下方旧版本记录仅作历史说明。

**1.19.7 已于 2026-10-25 发布至 Thunderstore GTFO（`HawChow-CombatPanel`，Client Mods，NSFW=否）；尚未在游戏内实机验证**：

- 结算板下边界锚定「送所有囚犯回大厅 / 返回大厅」按钮本身，失败页再与「重开检查点」按钮取更高者，板子压不住这两个已命名按钮中的任何一个；页面上更高但未命名的控件不再削减板子高度。
- 字号与行高按该按钮上方的真实间隙搜索：最高到条带的 96%，到达条带的 92% 或撞到页面可用宽度即停止；测量不可用或连可读下限都装不下时保留原字号，由既有 fit 兜底。
- 恢复结算板铺满页面可用宽度：`PanelGeometry.FillWidth` 重新作为最后一步，多余宽度只摊给右对齐的数值列，标签列宽度、行高、字号与表格高度都不变。

本版同时包含未发布的 1.19.6 两项（客机伤害兜底、rundown 名称解析），以及两项用户报告的缺陷修复：扫描「累计」不再与「首次」印成同一个数（累计改为每次原生标记确认记一次，面板只在两者不同时才打印该段），胶枪「命中」不再把敌人砸胶门的挥击计入、三格改为整组取源（此前归档里出现过「射击 435 / 命中 516」）。协议仍为 v8，伤害归因、计分权重与存档格式未改。发布包的 ZIP SHA-256 为 `EDF2D61B55273EE4B0DF0E2A3A18567488956AEF65D3EFC20C055E832858FA36`（336126 字节、6 成员），线上下载包与之逐字节一致，包内 DLL 与本地构建同为 `0F07793B070C969A5EA0F3E4999429B638A0B0C6352435A19A16157C98ECEEA6`。

验证（2026-10-25）：`scripts/Verify.ps1` 输出 `VERIFY: PASS`，插件构建 0 警告 0 错误，`PASS: 1857 combat-panel assertions`（另 9 配置监听 / 50 补给归属 / 38 扫描重放）。这只是编译与自动化（托管重放）验证，不是实机验证。

1.19.6 修复“主机未装模组时客机自己那行永远没有伤害”：`BulletDamage` / `MeleeDamage` 两个肢体钩子的 finalizer 原本只把调用前后的肢体血量交给 `CombatTracker.NoteLocalOutgoing`，而这类对局里伤害由主机结算，本机同一次调用前后血量不变，`amount <= 0` 静默返回。现在把武器带入的原始 `dam`（护甲与精准倍率之前）一并传入，只在血量差读不到时作为兜底，并按肢体剩余血量封顶；能读到血损时仍以实际血损为准，估算标记 `~` 与显示不变。游戏方法自己抛异常时不再据 `dam` 估算，同一个异常原样返回；一次性诊断日志补 `healthBefore / healthAfter / dam / amount`，武器名读不到时写 `unknown weapon`。结算写出的 rundown key 补上本地档解析：`Local_<id>` 能读到本机已加载的 `RundownDataBlock.name` 就写该名字，读不到（无此 block、读取失败、名字为空）或本来不是这个形状的 key 一律原样保留。主机与“主机装模组”的客机仍不本地记账，协议 v8、显示布局、总量与占比公式均未改。验证（2026-10-24）：以 SDK 6.0.428 对本机 r2modman 的 Survival profile 里的真实 BepInEx / Interop 程序集重跑当前 `scripts/Verify.ps1`，得到 `VERIFY: PASS`：插件构建 0 警告 0 错误，`PASS: 1778 combat-panel assertions`（含本次新增的兜底规则与估算标记用例），文件监听 9 条、补给归属 50 条、扫描管线重放 38 条断言全部通过，EWC 可选桥接、显示投影、结算工具行投影与 GTFO TMP 字体等 wiring checks 亦为 PASS。这只是编译与自动化（托管重放）验证，不是实机验证：未进游戏复核 `Local_<id>` 到本机 `RundownDataBlock.name` 的实际解析，主机未装模组时客机的实际数值也仍待实测；未安装 profile、未发布。

1.19.5 修复扫描获取链：观察扫描器实际提交的原生标记请求与 `ToolSyncManager.DoTagEnemy` 执行结果；客机晚到确认匹配已提交请求，主机在验证作用域保存原始发送者，不再依赖远端第一人称扫描器实例。确认排队至主线程结算；候选列表、被动雷达、距离都不用于猜归属。客机配对结果标 `~`，因为广播不携带扫描者字段。

更正 1.19.4：其候选快照兜底未解决实机扫描缺失，不能视为修复成功。双方安装 StatDisplay 与 CombatPanel 仍可能存在伤害差额，原因尚未逐事件证明；撤回“差值必是主机未装 StatDisplay、不是漏记”的断言。本轮伤害、布局、评分和协议代码未改，协议仍为 **v8**。双 profile Verify 通过；Survival Test 简单实测反馈确认两个人机均有扫描数据，真人 Steam 多人、客机晚到确认与远端代测仍待专项实测。变更记录见 `Thunderstore/CHANGELOG.md`。

## 历史开发记录（1.17.x 及更早，仅作历史说明）
**1.17.18 fixes missing settlement tool data**: result rows now carry the equipped GearClass name into the tool and Tool Efficiency formatter, so observed scanner/sentry/mine/glue values can render. Both overlays use the GTFO TMP CJK font source when available, with a shared renderer and explicit system-font fallback. Published to Thunderstore GTFO / Client Mods as `HawChow-CombatPanel`; gameplay rendering remains to be confirmed in a new session. See `reports/release-1.17.18.md`.

**1.17.16** introduced scanner-only Tool Efficiency details, item/core aliases for tripmines, and settlement alignment; its local candidate remains in `reports/release-1.17.16.md`.

**1.17.12 让主机把每一个座位的行都发给客机，包括人机。** `HostRows` 一直用 `IsHuman` 过滤它要描述的行——那个问题问的是"这个 peer 能不能被寻址"，是接收方那一层该问的，不是"谁的行值得发"。人机不会应答、也不装插件，但主机量它的伤害、硬直、击杀和救援，跟量真人一模一样；客机自己一样都量不到，所以人机那行根本没有任何来源。这就是客机只看得到主机和自己的原因。

**1.17.11 restores the optional EWC bridge when building against a profile that contains EWC; Verify now fails if it silently compiles out, and the bridge is now armed inside the same guard as the plugin-list check, so an EWC that is installed but older than the accuracy API leaves the mod running exactly as if EWC were absent instead of breaking its type initialiser. It also colors the unique rescue-count leader without changing the two-rescue scoring floor.** The Gale turbo-plasma API callbacks still need a fresh in-game confirmation; see `reports/release-1.17.11.md`.


**1.17.10 repairs teammate-rescue attribution.** It pairs the replicated reviver/target state with `PLOC_Downed.OnPlayerRevived`, and credits only a confirmed successful recovery; unresolved owners stay unassigned. The host's tracking policy includes slotted bot owners when GTFO resolves them to `SNet_Player`. New smoke assertions cover host/client roster boundaries. A new Survival Test gameplay run is still required to confirm real human and bot rescue counts. See `reports/release-1.17.10.md`.


独立 GTFO BepInEx IL2CPP 战斗面板，仅依赖 `BepInEx-BepInExPack_GTFO-3.2.3`。无需 Hikaria Core、GTFO-API、TheArchive、AccuracyTracker、AccuracyShow 或 DamageIndicator。

**EWC（ExtraWeaponCustomization）是可选集成，不是依赖。** 装了 EWC 才会启用额外的命中/伤害桥接：它读的是 `EWC.API.Accuracy`，加上 `FireShotAPI` / `ProjectileAPI` / `DamageAPI` 三条补充流，用于 EWC 自己接管的武器。这些接口是按 **EWC 4.13.4** 的反编译签名编写的，**4.13.4 之前的旧版本很可能不兼容**；那种情况下桥接不会启用，插件本身照常加载，行为完全等同于没装 EWC——EWC 武器改由面板自己看得见的 ray 计量。没装 EWC 也是同一套行为。

![当前局内战斗数据面板：伤害、占比、友伤与弹丸统计](https://raw.githubusercontent.com/hawchou1995/HawChow-Mod-Assets/main/GTFOCombatPanel-panel-screenshot.png)
![任务成功的结算看板：评价下面那一行「说明」写出称号背后的真实数字](https://raw.githubusercontent.com/hawchou1995/HawChow-Mod-Assets/main/GTFOCombatPanel-settlement-success.png)

**1.17.9 修复客机收包后丢弃全队快照的根因**：网络层已给出发送者 ID，但旧版队列却使用解码器中的 `Sender`（快照固定为 0），所以握手成功、快照全部被权限检查拒绝。现在保留底层发送者，与快照 `Owner` 分开；协议仍为 v3、评分仍在本机。新增测试直接链接生产 `SquadNet`，原多机测试也不再绕过收包队列。1080 条统计断言及 9 条 watcher 断言通过；尚未完成新版真人双端验证，不把自动化结果当联机实测。旧版客机必须更新；新版客机可读取 1.17.8 主机发来的 v3 快照。本版未发布。详见 `reports/release-1.17.9.md`。
**更正历史表述**：下文 1.17.4 关于握手与前缀的解释是当时的假设/健壮性改动，不是已验证的联机根因；此次根因由真实客机日志与修前失败的生产入口回归共同定位。

**1.17.8 只改文档，代码与行为跟 1.17.7 完全一致**：把 1.17.7 段落里的"未发布"更正为已发布，并补上发布记录（线上包与本地构建逐字节一致；`1.17.2`–`1.17.6` 从未单独发布，内容都在 1.17.7 里）。本机已装 1.17.7 的话不必为这个版本更新。下面仍是 1.17.7 的实质改动。
**1.17.7 修掉弹丸类武器命中率恒为 100%。** `ProjectileAPI.OnProjectileHit` 的第二个参数（这发弹丸打到了什么）此前被忽略，于是打墙、打地板、打天花板都算一发命中：实机上 #1 的涡轮电浆枪是 `529 发 / 529 命中`、#2 是 `92 / 92`，两人都精确 100%，并把本人的总命中率抬到 `~82.5%`。现在只有落在**敌人**身上的弹丸算命中，并沿用伤害路径那套"景物不算目标"的规则（容器锁、门锁不算）；命中仍有伤害记录这条独立来源，所以只去掉误计、不会丢数。1023 条断言通过；记录见 `reports/release-1.17.7.md`。本版**已发布**到 Thunderstore（`HawChow-CombatPanel-1.17.7`，线上包与本地构建字节一致）。其下是 1.17.6 的修复。
**1.17.6 让 EWC 武器不再"整段让位"，并把武器名钉住。** 上一版补齐了 EWC 的四套流，可是 `BeginFire` 对 EWC 管理的武器仍然**无条件**跳过，于是那些行的唯一来源只剩 EWC 自己：它一旦对某个 (玩家, 槽位) 不报数，那行**只能**是 `0/0/0`，与"有没有开枪"无关（实机那局 #2 的 HEL枪、#3 的沙鹰、#4 的涡轮电浆枪就是这样）。现在改为**条件让位**：EWC 报过才让位，从未报过就按本机看得见的 ray 自计，并交给桥上用 `max` 与 EWC 后来的总量对账（`Applied` 记住已交出的部分，不翻倍）；**看不见 ray 就不估**——不按弹药消耗造数字，只留一条日志说明这一行是"无观测"而不是 0。同时 `FireShotAPI`/`ProjectileAPI`/伤害记录带来的观测现在真的会落表（EWC 沉默时，第二发之后交，标 `~`），爆炸归属的"最近一次攻击"登记也扩到每一次计入的开火（上一局那条被丢掉的 19.3 点爆炸伤害就是为了这个）。另修显示层：武器名只由真正命名它的来源设定，被动读背包不再改名——归档写 `HEL左轮手枪`、板写 `手枪` 的分歧不再发生。1019 条断言通过；记录见 `reports/release-1.17.6.md`。本版**未发布**。其下是 1.17.5 的修复。
**1.17.5 补齐 EWC 的四套数据流，并让同一会话的第二局也留下一份存档。** ExtraWeaponCustomization 的精度 API 并不覆盖它管理的每一把武器：StatDisplay 读的是**四套**（`AccuracyAPI` + `FireShotAPI` + `ProjectileAPI` + `DamageAPI`），本模组此前只订了其中一套，于是像**涡轮电浆枪**这种只走补充流的武器整行空着（弹丸 `0/0/0`、伤害 `0.0`）。现在四套全订并按 `max` 对账（同一发被两套都报到也只算一次），原版爆炸伤害 `ReceiveExplosionDamage` 也补上了钩子——爆炸载荷没有 owner 字段，所以归属走"最近一次攻击"缓存，没有可归属的攻击就**不计**而不是猜给谁。另修一个由日志与磁盘同时证明的真缺陷：结算存档的"每屏一份"闩锁只认 **page 指针**，而游戏把结算页实例留满整个会话，同一会话的第二局失败因此**既不写文件、也不打日志**（`Survival new` 那局两块失败屏只落下一个 json，且与截图那局不是同一局）；键改为 page + 本机局次计数器。1009 条断言通过；记录见 `reports/release-1.17.5.md`。本版**未发布**。其下是 1.17.4 的修复。
**1.17.4 修掉「客机局内只看到自己」的两个真因，并让结算页不再压按钮**。朋友的客机日志给出了确凿事实：roster 认得主机，但整局**一个包都没收到**（全是零收包记录），局内表只有表头+自己。两个真因都在我自造的同步层：**(1) 发送曾依赖对方的应答**——门槛是"只发给应答过的 peer"，那条反向链路一死数据就永远不来；现在取消门槛，每个 peer 每秒都收，这与现成实现（StatDisplay 的 `SenderMode.OtherPlayers`）的不问直接发一致。**(2) 接收前缀可能被别的模组挡掉**——朋友机上 `RecieveBytes` 有 4 个前缀，现在我用 `Priority.High` 先读。另外修了一个真缺陷：`HandshakePolicy` 有"对端坚持旧序号即采信"的自愈，而**快照缓存是第二道序号锁、没有这条规则**，于是对端重启/热重载后整局不再共享（因果对照实验证实）。结算页方面：主机那个「送所有囚犯回大厅」是原版按钮，本模组此前**量不到它**就退回"铺到屏幕底部"，现在改为遍历页面**所有按钮**取最高边，且量不到时也不超过窗口的 0.66。1002 条断言通过；记录见 `reports/release-1.17.4.md`。本版**未发布**。其下是 1.17.3/1.17.2 的修复。

**1.17.3 修复「主机装了模组、结算能共享、局内看不到别人」**：根因不在显示、也不在传输，而在我上一版自己加的**握手层**——招呼有次数上限（每 2 秒一次、最多 5 次），用完就整局不再理该 peer；那 10 秒里对端若还在读条（一人在关卡、另一人在加载界面）或招呼丢包，两台机器此后一个包都不交换，屏幕上看与「这模组不能共享」无从区分。现在招呼**只降速不停止**（前 10 秒 2 秒一次，之后 10 秒一次）；「是否发数据」也改为看 peer **本局是否证明过**自己装了模组（`EverAnswered`），不再因为对端静默 12 秒就悄悄停发；同时放宽主客判定，避免「只看到自己和主机」。另加 `squad sync outbound:` / `squad sync inbound:` 两条每局各一次的定性日志。1001 条断言通过（含可区分场景 B7）。本版**未发布**；记录见 `reports/release-1.17.3.md`。1.17.2 的 EWC 命中率修复与其下的底板开关见后续小节。
**1.17.1 局内底板可调**：局内表格自己那块深色底板现在由 `[Layout] PanelOpacity` 控制，默认 **0.28**（旧值是写死在代码里的 0.48，看起来像一块黑板）。`0` = 完全透出游戏画面，`1` = 实心板；边框描边按同一比例缩放，所以底板变淡时不会只剩一圈轮廓。每一帧重读，改文件或热重载下一帧生效，启动与热重载日志都带 `panelOpacity=` 便于确认。结算影响板不受这个键影响。其下是 1.17.0 的多人同步重写（无头 `SquadExchange`、每台机器都打招呼、应答按节奏重发、序号回退自愈）；981 条面板断言通过，**已发布到 Thunderstore**，记录见 `reports/release-1.17.1.md`。

**1.17.2 修复 EWC 武器命中率恒为空**：ExtraWeaponCustomization 开火时发一次精度回调、弹丸命中时**另发一次**（那次 `Count = 0`），旧实现按「每次回调 = 一发完整结果」读并 `fired <= 0` 提前返回，命中增量被整段丢弃——于是枪数在涨、命中永远是 0，整行显示 `—`。本版改读 EWC 自身的**累计**统计（回调触发前已累加），开火与命中都拿到，取消射击的负增量也不再被吞。同时补齐无模组主机下客机自己那行的伤害（本地输出端钩子，`min(这一击伤害, 剩余生命)`，标 `~`），小队占比在该拓扑下不再恒为 `—`；995 条面板断言通过。本版**未发布**；1.17.1 的底板开关与 1.17.0 的多人同步重写见 `reports/release-1.17.2.md` 与后续小节。

扫描数据的 `~` 表示当前采集路径是估算：真人扫描使用扫描仪实例的标记状态；机器人虽可从主机上的扫描动作确定归属，当前计次仍按 `BotTag` 回调时间窗口合并，所以保留 `~`。这不是主机权限不足，也不能只去掉标记就称为精确数据。

## HUD

`F8` 开关，列顺序固定，默认全部显示：

`玩家 | 伤害 占比 | 友伤 | 命中率 | 弱点率 | 总弹丸 命中 弱点`

- 同一列内的多组数据并排显示，字段之间只空一格、不画斜杠：`伤害 占比` 形如 `1,234.6 32.1%`，`总弹丸 命中 弱点` 形如 `120 45 12`（顺序为 总、命中、弱点）。
- “伤害”只统计敌伤；“占比”是**该行武器**的敌伤除以本次远征记录的全队敌伤（结算看板的武器行同理），不包含友伤；玩家自己的“总伤害”含工具（哨戒炮等）造成的敌伤，所以两把枪的占比之和可以小于总伤害的占比。全队总伤为零或非房主拿不到队伍总量时，占比显示 `—`。
- 玩家名最多六个文本字符，保持局内玩家颜色；表头、武器名、所有数值和估计标记均为白色，所有字段右对齐。

## 结算界面：全局影响力看板（1.13.0）

成功/失败结算使用一张深色影响力矩阵：**行是字段，列是玩家**，玩家按综合评分（0–100，一位小数）降序排列。名称、评分、装备和指标以字重及细线分组，数值右对齐；多值用 ` / ` 分隔，并测量完整文字避免裁切。新配置默认拆开击杀类别；已有 `Scoring/SplitKillTiers` 不被覆盖，合并模式也标注小/精/首。

| 行 | 内容 |
|---|---|
| `#1 玩家` | 名次 + 玩家名（保持局内玩家颜色） |
| 综合评分 | 只显示 0–100 的数值；按名次染金/银/铜/白，不追加名次词 |
| 评价 | 一句 3–6 字评价 |
| 说明 | 这句评价是拿什么换来的，带真实数字（如 `伤害 95.4，占全队 52.2%`、`扫描 6 次，首次标记 2 只`）；量不到的项按全表约定写 `—` |
| 主武器 / 副武器 / 近战 / 工具 | 四个装备槽的名称；支援工具后面标 `支援` |
| 主武器 伤害 占比 | 该武器的对敌伤害，以及它占全队敌伤的百分比 |
| 主武器 命中 弱命 | 该武器的命中率与弱点命中率 |
| 主武器 弹丸 | 该武器的 发射 / 命中 / 弱点 弹丸数 |
| （副武器三行同上） | 同上，换成副武器 |
| —— 以下按权重降序 —— | 分隔行 |
| 十三项指标 | 见下表 |

玩家相关字段**只占一行**（1.9.0 曾把名字印成「名次条 + 玩家」两行，冗余，已去掉）：**第一行**名次+姓名，**第二行**综合评分，**第三行**评价，**第四行**说明——这句评价是拿什么换来的，带真实数字（`伤害 95.4，占全队 52.2%`、`扫描 6 次，首次标记 2 只`），其下才是四个装备槽与两把武器的数据。加上分隔行与十三项指标共 28 行（拆开击杀等级时 30 行）。

十三项指标按权重降序排列，**每项指标的队内唯一最高者染金**，并列则不染金。工具动作和数值只属于本结算看板；局内工具 UI 不改：

| 指标 | 口径 | 权重 | 归一化 |
|---|---|---:|---|
| 总伤害 | 对敌累计伤害 | 0.95 | 值 ÷ 队内最大 |
| 硬直阻止 | 被打断的攻击次数；攻击已开始且未结束、`Light`/`Heavy`；Push 可计打断但不属于近战击杀 | 1.40 | 值 ÷ 队内最大 |
| 击杀 | 小怪 `<120` / 精英 `120–999` / BOSS `≥1000`，按 `HealthMax`；小怪/精英/BOSS 相对价值 0.5/1/1.5 | 0.70 | 加权值 ÷ 队内最大 |
| 救援队友 | 成功救援者 | 0.28 | 值 ÷ 队内最大 |
| 弱点命中率 | 弱点弹丸 ÷ 命中弹丸 | 0.44 | 原比率 0–1 |
| 命中率 | 命中弹丸 ÷ 发射弹丸；综合分保留样本置信度缩放 | 0.28 | 原比率 0–1 |
| 补给效率 | 每次有效量 ÷ 应有量的算术平均 | 0.78 | 原比率 0–1 |
| 工具效率 | 首次标记数；其他工具动作只作结算详情 | 0.80 | 值 ÷ 队内最大 |
| 单人存活 | 队友全倒、只剩自己存活过（失败页不计） | 0.47 | 值 ÷ 队内最大 |
| 阵亡 | 倒地次数 | 0.55 | 反向：`1 − 值 ÷ 队内最大` |
| 友伤/致倒 | 友伤伤害与友伤致倒分别归一后等权惩罚 | 0.75 | 反向严重度 |
| 近战击杀 | 最后伤害来源为 `DamageKind.Melee`；Push 不算近战击杀。单元格同时展示现有 `GearMelee` 累计伤害，但伤害不额外计分 | 0.55 | 击杀数 ÷ 队内最大 |
| 触手击杀 | 击杀对象的 `EnemyAgent.IsScout` 为 true | 0.60 | 击杀数 ÷ 队内最大 |

**默认权重合计 8.55**，顺序与上表一致（`CombatPanel-config/scoring.json` 的 `Weights`；`.cfg` 的 `Scoring/Weights` 只是回退值）：`0.95,1.4,0.7,0.28,0.44,0.28,0.78,0.8,0.47,0.55,0.75,0.55,0.6`。三档击杀权重与整行 `Kills` 权重相乘。旧版已发布默认值会迁移到新默认；手改的旧 11 项列表保留原值，并补上近战/Scout 两个后缀默认。有效分仍为 `100 × Σ(权重 × 角色系数 × 归一值) / Σ(权重 × 角色系数)`，夹到 `[0, 100]`，一位小数。

评分公式：`100 × Σ(权重 × 角色系数 × 归一值) / Σ(权重 × 角色系数)`，夹到 `[0, 100]`，一位小数。全员并列（`max == min`）时该项不计金、归一值取 0，评分不会炸。

**评价按名次从上往下评，四名玩家一定各不相同。** 每人先取「自己贡献 `权重 × 归一值` 最大、且自己确实是全队第一」的那一项；若这句已被名次更高的人取走，就沿自己的指标列表**往下顺延**到下一句还没被占用的。什么都没领先的玩家也能拿到一句属于自己的，而不是所有人一起退回「团队中坚」。**显示分正好是 `0.0` 的玩家固定拿「战地游客」，这一句可以重复**。称号表新增近战击杀=近战强袭、触手击杀=触手猎手（均四字）；原有总伤害=火力核心、硬直=寸止大师、小怪=清道夫、精英=巨兽猎人、BOSS=屠龙者、弱点率=弱点专精、命中率=弹无虚发、救援=妙手回春、补给=后勤大师、扫描=侦察之眼、单人存活=背水一战、阵亡=铁壁生存。

**支援角色加成**：`CombatPanel-config/scoring.json` 的 `SupportToolNames`（默认 `Bio Tracker,Enemy Scanner,生物追踪仪,Glue Gun,Glue Grenade,C-Foam,胶枪,粘胶,泡沫,Lock Melter,开锁器`，不区分大小写、按子串）匹配玩家时，**总伤害与硬直阻止**乘 `FirepowerBonus`（默认 1.15），支援类指标乘 `UtilityBonus`（默认 1.0）。`ExcludedTools`（默认 `Sentry,哨戒炮,哨戒,Tripmine,Mine,绊雷,地雷`）里的工具永远不算辅助（哨戒炮、绊雷排除），其余非空工具由 `OtherToolsAreSupport`（默认 true）决定是否算辅助。两个倍数都硬夹到 1.3 上限（下限 1.0）。

**原版元素**：看板顶替掉的区块**先挪出页面、再关掉**，并且**每帧重新执行一次**（成功页：红晶/强化剂面板及其说明文字、四张囚犯报告卡及其锚点、检查点消耗；失败页：红晶/强化剂面板及其说明文字）。逐块各自 try/catch，字段为 null 就跳过；成功页用 `Results/ShowSuccessArtifacts`（默认 `false` = 隐藏：红晶/神器栏、总结行、检查点消耗）与 `Results/ShowSuccessPlayerReports`（默认 `false` = 隐藏：四张囚犯报告卡及其锚点），失败页用 `Results/HideVanillaFailBlocks`；两个成功页开关的描述里都带提示**「建议关闭显示以免影响观感」**，改回打开即可原样恢复，不必重编译。看板本身依旧用 IMGUI 画在屏幕上，**不写入任何原版文本对象、不改动任何报告卡里的文字**。

只 `SetActive(false)` 是不够的：成功页与失败页都会用几秒把内容**动画进场**，动画过程中会把区块**重新打开**，这正是红晶面板一直回来的原因。所以区块同时被挪到页面外（局部坐标 `y = 6000`），无论页面怎么动它都回不来。这个坑和这个绕法在已发布模组 **StatDisplay** 的 `Patches/PagePatches.cs` 里有原文注释：`// HACK - Want to set inactive, but it blinks in...`，随后就是 `m_artifactInfo_text.transform.localPosition = new Vector3(0, 2000, 0);`。

**失败页与成功页共用同一条安全区，`任务失败` 底部对齐成功页完成块底部（1.16.0）**：成功页量到的完成块底边被 `FailureAlignment.RememberCompletion` 记住（同窗口尺寸才复用），失败页把自己的 `m_missionFailed_text` 底边移到那条线上（`AlignHeading`，世界空间 TMPro 按菜单相机换算像素→世界单位，读不到相机就不动），随后**用与成功页完全相同的** `ResultsRegion.Resolve` / `ResultsPlacement.TryFitBetween` 规则放板子，底部取 `min(strip.Bottom, 屏幕高 − 8)`，即永远在 `m_btnGoToLobby` / `m_btnRestartCheckpoint` 之上，塞不下就**不画**。运行期一次性打印 `messageBottom / alignedMessageBottom / completionAnchor / board 区间 / lobbyButtonTop / boardOverlapsLobbyButton` 六个数供实机核对。

`Results/ShowImpactBoard = false` 时影响板**整块不画**，页面回到原版样子——**没有旧统计表回落**（`PrepareStatistics` 已删除）。是否绘制由纯函数 `OverlayPolicy.Draws(showResults, showOverlay, showBoard, toggled)` 决定：三个开关加 F8 状态必须同时成立。

### 必须在实机确认的三点（均有降级保护 + 首次触发日志）

- 人类扫描以 `m_tagging` 的上升沿记标记尝试；**每个 scanner 带自己的揭示账本**，一次揭示本身就记 1 次首标并补记 1 次扫描次数，所以该行不会再出现「有首标、分母是 0」或「两个人都在扫却都是 0」。机器人扫描改钩机器人自己的 `Player.PlayerBotActionUseEnemyScanner.UpdateStateScan()` 实例方法取归属（反编译确认它是静态 `BotTag` 的唯一调用者，`m_agent` 就是该机器人），只有读不到时才退回「中心一米内唯一持追踪仪」的近似规则；回调按游戏标记时长合并，结果标 `~`（静态接口本身没有 owner）。未归属时日志打印候选数与最近距离，不再静默。跨玩家去重与新的归属钩子在实战中的命中仍待确认。
- 近战推击走游戏自己的链路（`Dam_SyncedDamageBase.PushDamage` → `Dam_EnemyDamageBase.ReceivePushDamage`），既不经过近战伤害入口也不造成伤害，所以钩子挂在推击入口、在处理推击之前读敌人的攻击状态：打断正在进行的攻击才记一次硬直，且同一帧同一敌人只记一次。实战中的实际触发仍需进游戏确认。
- 补给效率按每次补给事件的「实际有效量 ÷ 应有量」实测取平均：弹药在 `PlayerAmmoStorage.PickupAmmo` 前后读包内储量，医疗在 `ReceiveAddHealth` 前后读血量，远程发送者由 `SNet.Replication.TryGetLastSender` 解析；自己给自己用同样计入，拾取不计入分母。任何一次读数失败只跳过那一次，该项显示 `—`。

## 位置：贴着手电筒图标，零视差（1.7.0）

面板改用 **IMGUI（`OnGUI`）**在**窗口像素**里绘制。

- IMGUI 由 Unity 在**所有相机和所有 canvas 之后**合成，并且只相对窗口定位。因此它**不继承任何视角运动、HUD 运动或 HUD 视差**：游戏里没有任何东西能让它动，只有改配置（热重载）才会重新定位。
- 位置基准是**原生手电筒图标**（右侧装备栏那个）：用 **HUD 自己那台相机**量出它的屏幕矩形，**每 0.25 秒采样一次直到连续 1 秒不动**（进关时 HUD 有入场动画，只采第一帧会偏几百像素——1.2.0/1.3.0 就是这么偏的），然后**锁死**。面板挂在图标正下方、右边缘与图标对齐。
- 之后不再采样，所以也没有视差；整张表还会被**夹进窗口内**，所有列一定完整可见。
- 只在改配置（热重载）或换分辨率时重新采样。`AnchorToFlashlight = false` 或量不到图标时，退回 `PositionX/PositionY`（负值表示**从右边/下边**量）。
- 绘制只在 `EventType.Repaint` 上做，且从不处理输入，所以它不会吃掉鼠标点击。

### 合并列按字段右对齐，且不再画斜杠（1.8.0 / 1.8.1）

表头和数值都拆成**独立字段**，每个字段有自己的槽位、各自右对齐，所以合并列里的数字在行与行之间是竖着对齐的，而不是整串被最长的那个值推着走。**1.8.1 起字段之间不再画斜杠**，只留一段字号倍数的间距（`紧凑` 档还会按紧凑度再收窄），所以 `伤害 占比`、`总弹丸 命中 弱点` 两列都变窄了；弹丸表头只保留“总弹丸”这一处修饰，行数少时只显示一个 `—` 并右对齐到最后一个槽位。

### 列间距收窄（1.8.2）

1.8.1 的实机反馈是「列之间太开」，只有弹丸那三个数字被明确说「比较合适」。这一版只动两处几何常量与四条列下限，单元格里的文字、列集合、表头文案、占比算法、名称着色与右对齐全部原样：

- 列间留白基线 `2 × 内边距 + 列间距` 从 **1.067em 收到 0.625em**：`PaddingFactor` 0.45 → 0.27、`GapFactor` 0.55 → 0.30（单位都是字号，所有紧凑度档一起生效）。同一张表跟着整体变窄。
- `友伤`（3.4 → 2.65em）、`命中率`（4.6 → 3.05em）、`弱点率`（4.6 → 3.05em）和 `伤害/占比`（7.0 → 6.0em，即每字段 3.5 → 3.0em）四条列下限同步收紧。下限只是**最小值**，列宽仍取「下限」与「该列最宽单元实量值」中的较大者，所以收紧下限只减少表头在自己槽位里留下的空白，**不可能截断任何数值**。
- `总弹丸 命中 弱点` 三字段的间距**刻意没动**：`FieldGapFactor` 仍是精确的 `0.60`，弹丸列下限仍是 `6.8em`。

无头断言里用真实列集合、真实格式化结果加表头行搭了一张代表性表格，逐对量的净留白（单位 em，表头行文字右边缘到下一个文字左边缘）：

| 相邻文字 | 1.8.1 | 1.8.2 |
|---|---|---|
| 伤害 → 占比 | 1.120 | 0.620 |
| 占比 → 友伤 | 2.447 | 1.255 |
| 友伤 → 命中率 | 2.667 | 0.675 |
| 命中率 → 弱点率 | 2.667 | 0.675 |
| 弱点率 → 总弹丸 | 1.067 | 0.625 |
| 总弹丸 → 命中 | 0.567 | 0.567 |
| 命中 → 弱点 | 0.567 | 0.567 |

前五处每处都至少窄四分之一，最后两处逐位相同（差值 < 1e-4）。这些数字由测试打印的 `SPACING:` 一行给出，可随 `.\scripts\Verify.ps1` 复跑。

### 字体与列宽

- 字体优先复用 GTFO `TMP_Settings` 默认/回退 TMP 资产的 CJK 字体 `sourceFontFile`；没有源 Font 时按资产 `faceInfo.familyName` 动态创建。只有 GTFO TMP 字体不可用才回退 Windows CJK 字体。局内 HUD 与结算板共用同一 renderer。
- 字号 = `FontSize`（1080p 基准）× 紧凑度字号系数 × `窗口高度/1080`，所以任何分辨率看起来一致。
- 列宽用**IMGUI 自己那套字体图集**实量最宽单元（`GUIStyle.CalcSize`），再与内置 em 估算取较大者，并不低于该列以 em 表示的最小宽度。因此：表头与数值共用同一个单元格盒、右边缘必然对齐；列间不可能重叠；不会截断。

### 历史：四次尝试，前三次都没同时满足“画得出来”和“不动”

| 版本 | 做法 | 结果 |
|---|---|---|
| 1.2.0 | 挂在原生 HUD 层级，却用 `ScreenPointToWorldPointInRectangle(父级, 屏幕点, 相机, …)` 做屏幕↔局部换算 | 相机不是渲染该 canvas 的那台，整表位移 + 每列偏斜不同 + 随视角变化（日志：请求 `(1451,610)`，实际画在左上角） |
| 1.3.0 | 自建 Screen Space - Overlay canvas，像素级定位 | canvas 尺寸正确（`2560x1440 px, scale 1`）但**一个像素都没画出来** |
| 1.4.0 | 照 `AccuracyShow`/`DamageIndicator` 的做法，活在原生层级局部坐标 | **能画出来**，但继承 HUD 视差（会跟着镜头动），且右边两列被屏幕边缘裁掉 |
| 1.7.0 | IMGUI + 以手电筒图标为基准（采样到稳定后锁死） | 贴着 logo，且之后一动不动 |
| 1.8.0 | 合并列按字段拆槽、各自右对齐 | 弹丸 / 伤害占比 的数字竖着对齐，弹丸表头缩短 |
| 1.8.1 | 合并列不再画斜杠，字段之间只留字号倍数的间距 | 两列都变窄；面板与结算文字里不再出现 `/` |
| 1.8.2 | 收窄列间留白基线（1.067 → 0.625em）与四条列下限 | 截图反馈的五处留白每处至少窄四分之一；弹丸三字段间距与 1.8.1 完全一致 |

参考模组（含反编译的 `catrice.AccuracyShow`、`catrice.DamageIndicator`）里**没有任何一个自建 canvas**，大家都是挂在原生层级里，因此都带视差——没人解决过这个问题。

## 语言

`General/Language` 支持 `zh`（默认）与 `en`。也接受 `zh-CN`、`cn`、`English` 等拼写，无法识别时回退中文。表头、结算页标题与统计标签全部跟随该设置。

## 紧凑度

`General/Density` 支持 `compact`（默认）、`normal`、`spacious`，调整行高、单元格内边距与列间距，并作为字号系数参与：

| 值 | 行高 | 单元格内边距 | 列间距 | 字号 |
|---|---|---|---|---|
| `compact`（默认） | ×0.80 | ×0.88 | ×0.50 | ×0.90 |
| `normal` | ×1.00 | ×1.00 | ×1.00 | ×1.00 |
| `spacious` | ×1.30 | ×1.12 | ×1.25 | ×1.10 |

紧凑度不会把列压到比文字更窄，所以任何一档都不会截断。

## 显示哪些数据

`[Columns]` 段可逐列开关：`ShowDamage`、`ShowFriendlyFire`、`ShowAccuracy`、`ShowWeakspot`、`ShowProjectiles`。玩家名是行标签，始终显示。关掉某列后该列既不占宽度也不占表头，表格立即变窄并用剩余列重新排布。

## 配置热重载

插件自带配置文件监听。**游戏中直接编辑** `BepInEx/config/HawChow.GTFOCombatPanel.cfg` 或与 DLL 同目录的 `CombatPanel-config/scoring.json` 并保存，约 0.3 秒后自动生效：语言、紧凑度、列开关、字号与位置、十三项权重与辅助工具规则全部立即重算，无需重启游戏。生效时日志会输出：

```
[HOTRELOAD] config reloaded; language=...; density=...; columns=...; the table is rebuilt and repositioned from the new settings.
```

BepInEx 6 自身不监听配置文件，本插件用 `FileSystemWatcher` 监听：`ConfigWatcher.Watch` 同时开两个 watcher（`.cfg` 一个、评分文件所在目录一个），共用一个 300 ms 去抖标志，并且只在主线程执行重载与 Unity 对象操作。Windows 的 `FileSystemWatcher` 偶尔会漏事件，若改了配置没反应，再存一次即可。

## 统计口径

> 逐项的数据来源、准确度与限制（尤其是「房主装了本模组」与「房主没装」两种情况），见 [`DATA-AVAILABILITY.md`](DATA-AVAILABILITY.md)。

- 本机玩家和房主控制的机器人：钩住实际普通枪、霰弹枪及同步武器 `Fire`，在同一次开火内统计各颗初始弹丸射线；穿透续射只升级同一颗弹丸的命中/弱点标记。
- 命中率 = 命中敌人的弹丸数 / 总发射弹丸数。击中队友只产生友伤，不提高对敌命中率。
- 弱点率 = 弱点命中弹丸数 / 命中敌人的弹丸数，与 Hikaria 的口径一致。弱点读取 `eLimbDamageType.Weakspot`，不以伤害倍率猜测。
- 房主端的人类远端玩家：总弹丸由原版同步开火次数与武器弹丸数估计；命中/弱点由主机收到的敌方伤害事件估计。因为原版伤害包没有唯一弹丸 ID，穿透和延迟可能造成偏差，这些字段统一带白色 `~`。非房主不伪造其他玩家数据。
- 伤害/友伤按相应伤害回调前后的实际生命值损失记录，不计自伤和过量伤害。覆盖有来源的子弹、近战、火焰及推击；哨戒炮可观测伤害归属其工具槽，不计入手持枪的弹丸命中率。
- 每次事件同时记入玩家综合数据和具体武器（装备槽 + 武器 ID）。主机对未装本模组的远端玩家只能按收到事件时的装备推断武器，快速切枪会有归属误差。
- 未采集字段显示 `—`；已有采集但没有开火时，三个计数为 `0`，百分比仍为 `—`，不会显示 NaN/Infinity。
- 没有可验证玩家来源的爆炸包不归属某把武器；不把未知值算成已知玩家伤害。

## 成功与失败结算

成功页与失败页都改用上面那张深色影响力看板，由 IMGUI 画在页面之上（窗口像素），不写入任何原版文字对象。

- `Results/ShowImpactBoard = false` 时影响板**整块不画**，页面保持游戏原样——**没有旧统计表回落**（旧的逐武器统计表 `PrepareStatistics` 已删除）。
- 原版报告卡里的**文字从不改写**；被顶替的区块按上面「原版元素」那节的方式整块挪出页面并关掉。
- `[Results] OffsetX/OffsetY`（像素）调整看板位置；超出窗口时整体等比缩小并夹回屏内。

成功、失败、回到大厅及检查点回滚不会清除统计。只在下一次新远征生成时清空（检查点重试按本次远征累计）。

## 配置

文件：`BepInEx/config/HawChow.GTFOCombatPanel.cfg`。升级时保留已有文件，新配置项在下一次游戏加载时自动补齐。

```
[General]
Language = zh
Density = compact

[Panel]
ShowPanel = true
ToggleKey = F8

[Columns]
ShowDamage = true
ShowFriendlyFire = true
ShowAccuracy = true
ShowWeakspot = true
ShowProjectiles = true

[Layout]
AnchorToFlashlight = true
PositionX = -0.03
PositionY = 0.365
OffsetX = 0
OffsetY = 0
FontSize = 17
PanelOpacity = 0.28

[Results]
ShowResults = true
ShowResultOverlay = true
OffsetX = 0
OffsetY = 0
ShowImpactBoard = true
ShowSuccessArtifacts = false
ShowSuccessPlayerReports = false
HideVanillaFailBlocks = true

[Scoring]
ScoringFile = true
Weights = 0.95,1.4,0.7,0.28,0.44,0.28,0.78,0.8,0.47,0.55,0.75,0.55,0.6
SupportToolNames = Bio Tracker,Enemy Scanner,生物追踪仪,Glue Gun,Glue Grenade,C-Foam,胶枪,粘胶,泡沫,Lock Melter,开锁器
SupportExcludedTools = Sentry,哨戒炮,哨戒,Tripmine,Mine,绊雷,地雷
SupportOtherTools = true
SupportRoleFirepowerBonus = 1.15
SupportRoleUtilityBonus = 1.0
SplitKillTiers = true
KillMinorMaxHealth = 120
KillEliteMaxHealth = 1000
StaggerNeedsAttack = true
```

- `AnchorToFlashlight = true`：以原生手电筒图标的屏幕矩形为基准，挂在它下方并与右边缘对齐；该矩形**采样到不动之后才锁死**，所以面板贴在装备栏旁边但从不跟随移动。
- `FontSize`：1080p 基准字号，随窗口高度与紧凑度缩放。
- `PanelOpacity`（默认 0.28）：**局内面板底板的浓淡**，0 = 完全透出游戏画面，1 = 实心板。底板本身是深色，为了在亮背景下也能看清数字；嫌它像块黑板就往下调。每一帧都重新读，所以改文件或热重载下一帧就生效。**结算影响板不受这个键影响**（保留它自己已确认的底板值）。1.17.1 之前局内底板是写死的 0.48。
- `[Results] ShowResultOverlay`（默认 true）：关掉后结算页保持游戏原样，**不再回退旧统计表**。`ShowImpactBoard`（默认 true）：影响板整块开关。`ShowSuccessArtifacts`（默认 false = 隐藏成功页红晶体/神器栏、总结行、检查点消耗）、`ShowSuccessPlayerReports`（默认 false = 隐藏四张囚犯报告卡及其锚点）、`HideVanillaFailBlocks`（默认 true = 隐藏失败页红晶面板）：打开即精确还原；两条成功页开关的描述里都带提示「建议关闭显示以免影响观感」。
- `ToggleKey`（默认 F8）：局内与结算页同一个 overlay，按下后写回 `ShowPanel` + `ShowResultOverlay` 并 `Config.Save()`，即**永久保存到 `.cfg`**，下一帧生效。
- `[Scoring] ScoringFile`（默认 true）：是否读取 `CombatPanel-config/scoring.json`；`Weights` 只是回退值（关闭或文件不可读时生效）。评分文件字段见下。
- 以上全部支持热重载（`.cfg` 与 `scoring.json` 都在主线程重载）。位置不顺手就改 `PositionX/PositionY`，大小不合适就改 `FontSize`，保存即生效。

**评分文件 `CombatPanel-config/scoring.json`**（与 DLL 同目录，绝不裸放；只在缺失时创建一次，非法时保留上一次有效配置并回落 `.cfg`，绝不覆盖用户文件，写入原子）字段：`Guidance`、`Weights`（十三项，顺序同上看板；默认合计 8.55）、`EnabledMetrics`（`null` = 全开，权重设 0 即不计分）、`MinorHealth`（默认 120）、`EliteHealth`（默认 1000）、`MinorKillWeight` / `EliteKillWeight` / `BossKillWeight`（三档击杀的相对价值，默认 0.5 / 1 / 1.5，必须单调不减；与整行 `Kills` 权重相乘）、`FirepowerBonus`（默认 1.15，≤1.3）、`UtilityBonus`（默认 1.0，≤1.3）、`SupportToolNames[]`、`ExcludedTools[]`、`OtherToolsAreSupport`（默认 true）、`RescueFloor`（默认 2）、`AccuracyConfidencePellets`（默认 20）。

## 构建/安装

```powershell
.\scripts\Verify.ps1 -DotnetExe dotnet -GTFO_DIR 'C:\Program Files (x86)\Steam\steamapps\common\GTFO'
.\scripts\Build-Package.ps1 -DotnetExe dotnet -GTFO_DIR 'C:\Program Files (x86)\Steam\steamapps\common\GTFO'
```

需要带 SDK 的 .NET；目标为 net6.0。DLL 默认位于 `bin/Release/GTFOCombatPanel.dll`，也可用 `-BuildOutputDirectory` 将 Verify/包构建输出到独立目录。候选包位于 `artifacts/GTFOCombatPanel-1.17.18.zip`。安装后核对 DLL 哈希；已有 `.cfg` 与 `CombatPanel-config/scoring.json` 保留。

## 验证与测试重点

1.17.18 对 `Test` 与 `Survival Test` 两套 interop 的完整 Verify 均通过：每套 1203 条面板断言、9 条文件监听断言，Release 0 警告 / 0 错误。新增覆盖 GearClass 工具名到扫描效率文本的投影；结算数据与评分来源未改。

1.17.18 已发布到 Thunderstore GTFO / Client Mods（`HawChow-CombatPanel`），UI/API/社区页与下载哈希均核验通过。Test profile 新实机日志确认 GTFO TMP 字体为 `Noto Sans SC`，4 人结算板绘制且 Sentry、C-Foam、Mine、Bio Tracker 工具行/效率文本已生成；Survival Test 尚未启动。Test cfg 在安装后启动时写为 v1.17.18 header（18:49:58）；安装时哈希未变，安装器只覆盖 DLL。安装收据：`%PI_SCRATCH_DIR%\CombatPanel-1.17.18-install-20261018-183920.json`。

实机游玩仍需你确认：落点是否顺手、字号是否合适、结算页排版、远端玩家 `~` 估算的贴近程度。回报异常时附截图及 `BepInEx/LogOutput.log`；日志里这几条可用于定位：

- `[DIAG] table font: ...; shared by the in-level HUD and settlement board.`：实际字体来源（GTFO TMP 资产/source Font、GTFO face family，或 Windows CJK fallback）；两块面板共用该字体。
- `[DIAG] table 6 columns x 5 rows, … px at (x,y) of a 2560x1440 window, font …px …`：实际列数/行数、像素尺寸、落点、字号。**这一条同时证明 `OnGUI` 在被调用。**
- `[DIAG] result table …`：结算表的行列数与落点。
- `[HOTRELOAD]`：改配置后是否生效。

## 反馈

问题反馈、功能建议与其他：**https://qingju.me**

- 开黑语音频道（小黑盒语音）：**https://chat.xiaoheihe.cn/in1wpsjd**
- 已发布版本：**1.13.0**，Thunderstore 团队 `HawChow` / 社区 `GTFO`（分类 Client Mods）—— <https://thunderstore.io/c/gtfo/p/HawChow/CombatPanel/>；发布与核验记录见 `reports/release-1.13.0.md`。
- **1.15.13 已发布**（2026-10-15 23:31 UTC+8）：线上 `HawChow/CombatPanel` 最新版就是 1.15.13，商店下载包的 SHA-256 `6BA6CEF8F56189867A76E0C76950B4A8352C53BE09964B084FB9D6D5279F95FA` 与本机 `artifacts/GTFOCombatPanel-1.15.13.zip` **逐字节一致**（2,933,233 字节）。1.13.0 的归档 SHA-256 为 `D31420DF678000E3E8C9FD936E2178BE8A098BD6C1E4700CC5E8F98F7F5BDEA6`。

---

## 1.17.2（**本地，未发布**）

主线是 EWC 武器命中率的真实缺陷修复，外加无模组主机下客机自己那行的伤害兜底。构建 0 警告 / 0 错误、995 条面板断言（新增 14 条覆盖 EWC 的累计读法、层级锁定、`max` 对账不翻倍、负增量撤销与计数契约），记录见 `reports/release-1.17.2.md`。

**验证边界**：EWC 的修复由「EWC 自身反编译语义（`InitShot` / `AddHit` / `CancelShot` / `StatInfo.Add`）+ 无头断言」支撑，**尚未**在装了 EWC 的 Survival 武器（沙鹰）上做实机复现；无模组主机的客机伤害兜底也**尚未**实机联机确认。日志里新增 `[DIAG] EWC update carrying no shot: …`（零开火增量一定会出现）与 `[DIAG] local damage fallback: …`（兜底被启用的那一次）可用于下一次实机核对。

## 1.17.1（**已发布**）

局内面板底板改为可配置（`[Layout] PanelOpacity`，默认 0.28）。构建 0 警告 / 0 错误、981 条面板断言，`Build-Package.ps1` 校验 `VERIFY: PASS`。作者实机初步测试通过（观感与底板开关正常）；**多人联机反馈待补**，记录见 `reports/release-1.17.1.md`。

### 同步（A/B）

`CombatSync` 收到快照后**只**放进独立的 `SnapshotCache`，本机实测账本 `CombatTracker.Store` 一个字段都不动；显示走 `CombatSync.DisplayRows()`，把本机行克隆后按字段合并远端数据（`CombatMerge.Project` / `ApplyRemote`）。主机的实测伤害不会被客机估算顶掉，客机的实测命中率不被主机估算覆盖，未观测字段绝不清零已测量值，重复注入同一份快照不会把伤害二次累加（合并是覆盖）。`PanelBehaviour.Refresh` 与 `ResultsDisplay.BuildReport` 都改走 `DisplayRows()`。客机也不再用「看到的掉血」估全队总量：`EnemySetHealthPatch`、`ObserveSetHealth`、`_damageFromHealth` 已整体删除；主机没装模组时客机伤害/份额显示 `—`（不再是带 `~` 的半精度近似值），装模组的主机一开口它的实测总数立刻接管（`CombatSync.SquadTotal`）。

### 扫描（C）

每个 scanner 带**自己**的揭示账本；`ScanSession.Observe` 改成「一次揭示本身就是一次扫描」——新揭示一定记 1 次首标并补记 1 次扫描次数，所以该行不会再出现「有首标、分母是 0」或「两个人都在扫却都是 0」。

### 评分（D/E/F）

十三项默认权重 `0.95,1.4,0.7,0.28,0.44,0.28,0.78,0.8,0.47,0.55,0.75,0.55,0.6`（合计 8.55）；近战击杀/Scout 击杀独立计分，结算近战行复用 `GearMelee` 累计伤害作上下文。工具动作/数值只在局外结算显示，局内工具 UI 不变。火力补偿只作用于**总伤害与硬直阻止**，效用默认 1.0，两者硬夹 1.3；辅助判定仍按 `SupportToolNames` / `ExcludedTools` / `OtherToolsAreSupport`。
可编辑评分文件 `CombatPanel-config/scoring.json`（与 DLL 同目录，绝不裸放）承载十三项权重、启用指标、血量阈值、`RescueFloor`、`AccuracyConfidencePellets`、两个倍率与辅助工具规则：缺失时只创建一次，非法时保留上一次有效配置并回落 `.cfg`，绝不覆盖用户文件，写入原子。**每个客户端用自己的 JSON 算自己的分，主机只同步原始计数。** `.cfg` 的 `Weights` 降级为回退值。

### 热重载（G）

`ConfigWatcher.Watch(ConfigFile, extraFile)` 同时开两个 watcher（`.cfg` 与评分文件所在目录），共用一个 300 ms 去抖标志，重载在主线程执行；配置变化作废看板缓存并重测，普通计数更新保持已锁定字号与位置。

### 结算页开关（H/I/J）

两个成功页开关改成肯定式、**默认关闭**：`Results/ShowSuccessArtifacts`（默认 false = 隐藏红晶体/神器栏、总结行、检查点消耗）与 `Results/ShowSuccessPlayerReports`（默认 false = 隐藏四张囚犯报告卡及其锚点）。旧的两个反方向开关已删除。两个 `.cfg` 描述都带提示「建议关闭显示以免影响观感」；打开时每一块都精确还原（记录原始 localPosition + activeSelf）。

`Results/ShowImpactBoard = false` 时影响板**整块不画**，页面回到原版样子，**没有旧统计表回落**（`PrepareStatistics` 已删除）；是否绘制集中在纯函数 `OverlayPolicy.Draws(showResults, showOverlay, showBoard, toggled)`。F8（`ToggleKey`）同时翻转局内表与结算板，经纯函数 `TogglePolicy.Apply` 决定写回 `ShowPanel` + `ShowResultOverlay` 两个值并 `Config.Save()`，**永久保存到 `.cfg`**，下一帧生效；局内与结算页行为一致。

### 失败页对齐（K）

失败页与成功页共用同一条安全区，`任务失败` 底部对齐成功页完成块底部（`FailureAlignment.RememberCompletion` / `AlignHeading`），随后用与成功页完全相同的 `ResultsRegion.Resolve` / `ResultsPlacement.TryFitBetween` 规则放板，底部永远在返回大厅 / 重开检查点按钮之上，塞不下就不画。运行期一次性打印六个定位数字供实机核对。

### 总伤害带占比（L）

指标区「总伤害」行现在写成 `1234.5 (23.4%)`（占比量不到时只写伤害本身），占比仍沿用 `~` 估算标记。

### 已知未验证

**当前验证**：920 条面板回归、9 条真实文件监听回归、隔离原生 Blink 及配置切换保留完成状态通过。**待实机确认**：成功页完整展示顺序、失败页实体 F8、四人联机主客机数据。若配置 `ShowResultOverlay=false`，先按 F8 打开或热重载改为 true。本版不强改已保存的开关。
