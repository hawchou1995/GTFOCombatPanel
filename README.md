# CombatPanel

独立 GTFO 战斗统计面板：局内看战斗数据，成功或失败结算看队伍贡献。表格由 IMGUI 画在窗口像素里，不写入任何原版文字对象。

- **当前状态**：`1.19.12` 为本地候选版本（源码与包内文案已更新，`scripts\Verify.ps1` 本地通过，正式 ZIP 已在本地打出：`artifacts\GTFOCombatPanel-1.19.12.zip`；**未**上传、未发布）。逐版变更、未验证项与验证记录只写在 [`Thunderstore/CHANGELOG.md`](Thunderstore/CHANGELOG.md)；Thunderstore 详情页与包内 `Thunderstore/README.md` 只保留常青的功能/配置/安装文档，不列更新日志。
- **安装**：r2modman / Thunderstore Mod Manager 搜索 `CombatPanel`（自动带上 BepInEx 依赖）；或手动把包内 `BepInEx/plugins/GTFOCombatPanel/` 整个目录放进 GTFO 目录下的同一路径。`CombatPanel-config/scoring.json` 只在缺失时创建一次，手改过的配置不会被覆盖。已有 `.cfg` 在升级时保留，新配置项下次加载自动补齐。
- **兼容**：仅依赖 **BepInEx-BepInExPack_GTFO-3.2.3**；EWC（ExtraWeaponCustomization）是可选集成，公共 DLL 不带它的程序集引用。多人快照协议为 **v8**，与旧协议不兼容（旧协议包会被明确拒收）。需要共享统计的每台机器装同一版本。
- **反馈**：<https://qingju.me>；开黑语音频道（小黑盒语音）<https://chat.xiaoheihe.cn/in1wpsjd>。

## HUD

`F8` 开关，列顺序固定，默认全部显示：

`玩家 | 伤害 占比 | 友伤 | 命中率 | 弱点率 | 总弹丸 命中 弱点`

- 同一列内的多组数据并排显示，字段之间只空一格、不画斜杠：`伤害 占比` 形如 `1,234.6 32.1%`，`总弹丸 命中 弱点` 形如 `120 45 12`（顺序为 总、命中、弱点）。
- “伤害”只统计敌伤；“占比”是**该行武器**的敌伤除以本次远征记录的全队敌伤（结算看板的武器行同理），不包含友伤；玩家自己的“总伤害”含工具（哨戒炮等）造成的敌伤，所以两把枪的占比之和可以小于总伤害的占比。全队总伤为零或非房主拿不到队伍总量时，占比显示 `—`。
- 玩家名最多六个文本字符，保持局内玩家颜色；表头、武器名、所有数值和估计标记均为白色，所有字段右对齐。

## 结算界面：全局影响力看板

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

玩家相关字段**只占一行**：**第一行**名次+姓名，**第二行**综合评分，**第三行**评价，**第四行**说明，其下才是四个装备槽与两把武器的数据。加上分隔行与十三项指标共 28 行（拆开击杀等级时 30 行）。

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
| 单人存活 | 队友全倒、只剩自己存活过（失败页不计） | 1.60 | 值 ÷ 队内最大 |
| 阵亡 | 倒地次数 | 0.55 | 反向：`1 − 值 ÷ 队内最大` |
| 友伤/致倒 | 友伤伤害与友伤致倒分别归一后等权惩罚 | 0.75 | 反向严重度 |
| 近战击杀 | 最后伤害来源为 `DamageKind.Melee`；Push 不算近战击杀。单元格同时展示已采集的 `GearMelee` 累计伤害，但伤害不额外计分 | 0.55 | 击杀数 ÷ 队内最大 |
| 触手击杀 | 击杀对象的 `EnemyAgent.IsScout` 为 true | 0.60 | 击杀数 ÷ 队内最大 |

**默认权重合计 9.68**，顺序与上表一致（`CombatPanel-config/scoring.json` 的 `Weights`；`.cfg` 的 `Scoring/Weights` 只是回退值）：`0.95,1.4,0.7,0.28,0.44,0.28,0.78,0.8,1.6,0.55,0.75,0.55,0.6`。三档击杀权重与整行 `Kills` 权重相乘。手改过的旧 11 项列表保留原值，并补上近战/Scout 两个后缀默认。

评分公式：`100 × Σ(权重 × 角色系数 × 归一值) / Σ(权重 × 角色系数)`，夹到 `[0, 100]`，一位小数。全员并列（`max == min`）时该项不计金、归一值取 0，评分不会炸。

**评价按名次从上往下评，四名玩家一定各不相同。** 每人先取「自己贡献 `权重 × 归一值` 最大、且自己确实是全队第一」的那一项；若这句已被名次更高的人取走，就沿自己的指标列表**往下顺延**到下一句还没被占用的。什么都没领先的玩家也能拿到一句属于自己的，而不是所有人一起退回「团队中坚」。**显示分正好是 `0.0` 的玩家固定拿「战地游客」，这一句可以重复**。称号：总伤害=火力核心、硬直=寸止大师、小怪=清道夫、精英=巨兽猎人、BOSS=屠龙者、弱点率=弱点专精、命中率=弹无虚发、救援=妙手回春、补给=后勤大师、扫描=侦察之眼、单人存活=背水一战、阵亡=铁壁生存、近战击杀=近战强袭、触手击杀=触手猎手。

**支援角色加成**：`scoring.json` 的 `SupportToolNames`（默认 `Bio Tracker,Enemy Scanner,生物追踪仪,Glue Gun,Glue Grenade,C-Foam,胶枪,粘胶,泡沫,Lock Melter,开锁器`，不区分大小写、按子串）匹配玩家时，**总伤害与硬直阻止**乘 `FirepowerBonus`（默认 1.15），支援类指标乘 `UtilityBonus`（默认 1.0）。`ExcludedTools`（默认 `Sentry,哨戒炮,哨戒,Tripmine,Mine,绊雷,地雷`）里的工具永远不算辅助，其余非空工具由 `OtherToolsAreSupport`（默认 true）决定是否算辅助。两个倍数都硬夹到 1.3 上限（下限 1.0）。

## 结算板的落位与字号

两个结算页的板子都在**页面自己的动画真正结束、锚点确实量得到之后**才落位并显示，而且这个条件是硬的：

- 上边缘挂在失败页 `任务失败` 标题的实测底边、或成功页完成区块的实测下沿之下。失败页还要求标题**文字已经完整显示**（它的透明度乘以上层所有 CanvasGroup，达到满不透明）**并且**实测底边连续两次读数一致；只有矩形不动、文字仍在淡入不算就绪。成功页要求完成区块上界同样稳定。
- 下边缘以「返回大厅 / 送所有囚犯回大厅」按钮的上沿为严格下限（`ButtonClearance`），并且要求这条边被真正读到两次一致的值。
- **任何一条真实锚点拿不到时，什么都不画、也不隐藏原版内容**：原版结算画面保持原样，等待期间以有界节流（前两秒逐帧、之后约每半秒一次）复测，不会用窗口比例猜一块微型面板来伪装成功。一旦真实锚点稳定可测，面板自动出现，不需要 `F8` 关开。
- 字号由这段真实条带搜索得到（到条带 92%–96% 或页面可用宽度即停，最高为局内字号的 3.5 倍），可读下限取自该按钮自己的实测高度。
- 板子落位后不会因为一两帧的抖动移动；**页面本身在落位后又明显移动或改变尺寸时（别的模组重排页脚、分辨率变化等），板子自动重新测量并按新条带重新拟合**，每页有次数预算，不需要 F8 关开。
- 结算页仍在持续收快照：数据让表格变大时，会先按落位时真正用到的条带与该帧按钮净空校验；装不下就保留已上板的网格，绝不压按钮或标题。
- 鼠标滚轮在板子上可缩放字号，两页各存一份（`[Results] SuccessFontScale` / `FailureFontScale`）。缩放只乘在页面自己量出的字号上，往上最多夹回该字号。

## 原版元素

看板顶替掉的区块**先挪出页面、再关掉**，并且**每帧重新执行一次**（成功页：红晶/强化剂面板及其说明文字、四张囚犯报告卡及其锚点、检查点消耗；失败页：红晶/强化剂面板及其说明文字）。逐块各自 try/catch，字段为 null 就跳过；成功页用 `Results/ShowSuccessArtifacts`（默认 `false` = 隐藏：红晶/神器栏、总结行、检查点消耗）与 `Results/ShowSuccessPlayerReports`（默认 `false` = 隐藏：四张囚犯报告卡及其锚点），失败页用 `Results/HideVanillaFailBlocks`；两个成功页开关的描述里都带提示**「建议关闭显示以免影响观感」**，改回打开即可原样恢复，不必重编译。看板本身依旧用 IMGUI 画在屏幕上，**不写入任何原版文本对象、不改动任何报告卡里的文字**。

只 `SetActive(false)` 是不够的：成功页与失败页都会用几秒把内容**动画进场**，动画过程中会把区块**重新打开**。所以区块同时被挪到页面外（局部坐标 `y = 6000`），无论页面怎么动它都回不来。

## 位置：IMGUI，零视差

面板在 **窗口像素**里绘制（`OnGUI` 的 `EventType.Repaint`）：

- IMGUI 由 Unity 在**所有相机和所有 canvas 之后**合成，并且只相对窗口定位。因此它**不继承任何视角运动、HUD 运动或 HUD 视差**：游戏里没有任何东西能让它动，只有改配置（热重载）才会重新定位。
- 位置基准是**原生手电筒图标**（右侧装备栏那个）：用 **HUD 自己那台相机**量出它的屏幕矩形，**每 0.25 秒采样一次直到连续 1 秒不动**（进关时 HUD 有入场动画，只采第一帧会偏几百像素），然后**锁死**。面板挂在图标正下方、右边缘与图标对齐。
- 之后不再采样，所以也没有视差；整张表还会被**夹进窗口内**，所有列一定完整可见。
- 只在改配置（热重载）或换分辨率时重新采样。`AnchorToFlashlight = false` 或量不到图标时，退回 `PositionX/PositionY`（负值表示**从右边/下边**量）。
- 绘制从不处理输入，所以它不会吃掉鼠标点击。

## 字体、列宽与间距

- 字体优先复用 GTFO `TMP_Settings` 默认/回退 TMP 资产的 CJK 字体 `sourceFontFile`；没有源 Font 时按资产 `faceInfo.familyName` 动态创建。只有 GTFO TMP 字体不可用才回退 Windows CJK 字体。局内 HUD 与结算板共用同一 renderer。
- 字号 = `FontSize`（1080p 基准）× 紧凑度字号系数 × `窗口高度/1080`，所以任何分辨率看起来一致。
- 表头和数值都拆成**独立字段**，每个字段有自己的槽位、各自右对齐，所以合并列里的数字在行与行之间是竖着对齐的；字段之间不画斜杠，只留一段字号倍数的间距（`compact` 档还会再收窄），`伤害 占比`、`总弹丸 命中 弱点` 两列因此更紧凑；弹丸表头只保留“总弹丸”这一处修饰，行数少时只显示一个 `—` 并右对齐到最后一个槽位。
- 列间留白基线 `2 × 内边距 + 列间距` = **0.625em**（`PaddingFactor` 0.27、`GapFactor` 0.30，所有紧凑度档一起生效）；`友伤`、`命中率`、`弱点率`、`伤害/占比` 四条列下限分别为 2.65 / 3.05 / 3.05 / 6.0em。下限只是**最小值**，列宽仍取「下限」与「该列最宽单元实量值」中的较大者，所以收紧下限只减少表头在自己槽位里留下的空白，**不可能截断任何数值**。`总弹丸 命中 弱点` 三字段的间距是精确的 `FieldGapFactor = 0.60`，弹丸列下限 6.8em。
- 列宽用**IMGUI 自己那套字体图集**实量最宽单元（`GUIStyle.CalcSize`），再与内置 em 估算取较大者，并不低于该列以 em 表示的最小宽度。因此：表头与数值共用同一个单元格盒、右边缘必然对齐；列间不可能重叠；不会截断。
- 相邻文字的净留白（单位 em，表头行文字右边缘到下一个文字左边缘，由测试的 `SPACING:` 一行给出，可随 `.\scripts\Verify.ps1` 复跑）：伤害→占比 0.620、占比→友伤 1.255、友伤→命中率 0.675、命中率→弱点率 0.675、弱点率→总弹丸 0.625、弹丸三字段 0.567 / 0.567。

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
- 弱点率 = 弱点命中弹丸数 / 命中敌人的弹丸数。弱点读取 `eLimbDamageType.Weakspot`，不以伤害倍率猜测。
- 房主端的人类远端玩家：总弹丸由原版同步开火次数与武器弹丸数估计；命中/弱点由主机收到的敌方伤害事件估计。因为原版伤害包没有唯一弹丸 ID，穿透和延迟可能造成偏差，这些字段统一带白色 `~`。非房主不伪造其他玩家数据。
- 伤害/友伤按相应伤害回调前后的实际生命值损失记录，不计自伤和过量伤害。覆盖有来源的子弹、近战、火焰及推击；哨戒炮可观测伤害归属其工具槽，不计入手持枪的弹丸命中率。
- 每次事件同时记入玩家综合数据和具体武器（装备槽 + 武器 ID）。主机对未装本模组的远端玩家只能按收到事件时的装备推断武器，快速切枪会有归属误差。
- 未采集字段显示 `—`；已有采集但没有开火时，三个计数为 `0`，百分比仍为 `—`，不会显示 NaN/Infinity。
- 没有可验证玩家来源的爆炸包不归属某把武器；不把未知值算成已知玩家伤害。

## 成功与失败结算

成功页与失败页都改用上面那张深色影响力看板，由 IMGUI 画在页面之上（窗口像素），不写入任何原版文字对象。

- `Results/ShowImpactBoard = false` 时影响板**整块不画**，页面保持游戏原样——**没有旧统计表回落**。
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
SuccessFontScale = 1
FailureFontScale = 1
ShowImpactBoard = true
ShowSuccessArtifacts = false
ShowSuccessPlayerReports = false
HideVanillaFailBlocks = true

[Scoring]
ScoringFile = true
Weights = 0.95,1.4,0.7,0.28,0.44,0.28,0.78,0.8,1.6,0.55,0.75,0.55,0.6
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
- `PanelOpacity`（默认 0.28）：**局内面板底板的浓淡**，0 = 完全透出游戏画面，1 = 实心板。底板本身是深色，为了在亮背景下也能看清数字；嫌它像块黑板就往下调。每一帧都重新读，所以改文件或热重载下一帧就生效。**结算影响板不受这个键影响**（保留它自己的底板值）。
- `[Results] ShowResultOverlay`（默认 true）：关掉后结算页保持游戏原样，**不再回退旧统计表**。`ShowImpactBoard`（默认 true）：影响板整块开关。`ShowSuccessArtifacts`（默认 false = 隐藏成功页红晶/神器栏、总结行、检查点消耗）、`ShowSuccessPlayerReports`（默认 false = 隐藏四张囚犯报告卡及其锚点）、`HideVanillaFailBlocks`（默认 true = 隐藏失败页红晶面板）：打开即精确还原；两条成功页开关的描述里都带提示「建议关闭显示以免影响观感」。
- `[Results] SuccessFontScale` / `FailureFontScale`（默认 1.0，范围 0.5–2.0，一格 0.1）：结算板字号的滚轮缩放，成功页与失败页各存一份（两页条带高度不同，同一个倍率是两张不同大小的表）。1.0 = 页面条带自己量出来的字号；滚轮只在这个字号上做乘算，往上被夹回 1.0，往下到 0.5 为止，并且仍按真实条带与页面宽度回退到画得下、且不低于可读下限。指针落在板内才吃滚轮；改文件或热重载同样下一帧生效。
- `ToggleKey`（默认 F8）：局内与结算页同一个 overlay，按下后写回 `ShowPanel` + `ShowResultOverlay` 并 `Config.Save()`，即**永久保存到 `.cfg`**，下一帧生效。结算页的板子跟随同一个开关，不需要额外按键。
- `[Scoring] ScoringFile`（默认 true）：是否读取 `CombatPanel-config/scoring.json`；`Weights` 只是回退值（关闭或文件不可读时生效）。
- 以上全部支持热重载（`.cfg` 与 `scoring.json` 都在主线程重载）。位置不顺手就改 `PositionX/PositionY`，大小不合适就改 `FontSize`，保存即生效。

**评分文件 `CombatPanel-config/scoring.json`**（与 DLL 同目录；只在缺失时创建一次，非法时保留上一次有效配置并回落 `.cfg`，绝不覆盖用户文件，写入原子）字段：`Guidance`、`Weights`（十三项，顺序同上看板；默认合计 9.68）、`EnabledMetrics`（`null` = 全开，权重设 0 即不计分）、`MinorHealth`（默认 120）、`EliteHealth`（默认 1000）、`MinorKillWeight` / `EliteKillWeight` / `BossKillWeight`（三档击杀的相对价值，默认 0.5 / 1 / 1.5，必须单调不减；与整行 `Kills` 权重相乘）、`SleepingMeleeKillMultiplier` / `SleepingScoutKillMultiplier`（默认 2，范围 1–10）、`FirepowerBonus`（默认 1.15，≤1.3）、`UtilityBonus`（默认 1.0，≤1.3）、`SupportToolNames[]`、`ExcludedTools[]`、`OtherToolsAreSupport`（默认 true）、`RescueFloor`（默认 2）、`AccuracyConfidencePellets`（默认 20）、`Titles`、`Reasons`。

## 实机待确认

以下口径有降级保护并在首次触发时写日志，但仍需要实机复核（断言只覆盖托管策略，不覆盖 Unity 原生回调时序与观感）：

- 人类扫描以 `m_tagging` 的上升沿记标记尝试；每个 scanner 带自己的揭示账本。机器人扫描钩机器人自己的 `Player.PlayerBotActionUseEnemyScanner.UpdateStateScan()` 取归属，只有读不到时才退回近似规则，结果标 `~`。跨玩家去重与归属钩子在实战中的命中仍待确认。
- 近战推击走游戏自己的链路（`Dam_SyncedDamageBase.PushDamage` → `Dam_EnemyDamageBase.ReceivePushDamage`），钩子挂在推击入口、在处理前读敌人的攻击状态：打断正在进行的攻击才记一次硬直，且同一帧同一敌人只记一次。
- 补给效率按每次补给事件的「实际有效量 ÷ 应有量」实测取平均：弹药在 `PlayerAmmoStorage.PickupAmmo` 前后读包内储量，医疗在 `ReceiveAddHealth` 前后读血量，远程发送者由 `SNet.Replication.TryGetLastSender` 解析；自己给自己用同样计入，拾取不计入分母。任何一次读数失败只跳过那一次，该项显示 `—`。
- 两个结算页的板子落位时机与字号观感、结算板滚动缩放手感，都还没有实机证据。

## 构建 / 验证

```powershell
.\scripts\Verify.ps1 -DotnetExe dotnet -GTFO_DIR '<GTFO 目录>'
.\scripts\Build-Package.ps1 -DotnetExe dotnet -GTFO_DIR '<GTFO 目录>'
```

需要带 SDK 的 .NET；目标为 net6.0。`Verify.ps1` 用真实 BepInEx / Interop 程序集构建插件，跑面板断言、文件监听、补给归属、扫描重放、EWC 契约与 Scout 挂点安全检查，并做显示投影 / 结算上界 / 页面稳定门 / 结算工具行 / 失血目标 / hello roster / GTFO TMP 字体等接线检查。`Build-Package.ps1` 先跑 Verify，再由 `Thunderstore/manifest.json` 的版本号生成 `artifacts/GTFOCombatPanel-<版本>.zip`（版本号只有一个来源：`PluginInfo.cs` + 同一个 manifest）。

DLL 默认位于 `bin/Release/GTFOCombatPanel.dll`，也可用 `-BuildOutputDirectory` 输出到独立目录。安装后核对 DLL 哈希；已有 `.cfg` 与 `CombatPanel-config/scoring.json` 保留。

## 验证边界

结论分两类，不混用：**编译与托管重放断言**（`scripts\Verify.ps1`，可复现）；**实机观察**（截图、日志、手感，只在明确记录时才算证据）。断言覆盖不到 Unity 原生回调时序、页面动画的实际轨迹和屏幕前的观感，这类结论一律需要实机复核才能成立。逐版的验证记录、断言数与未验证项写在 [`Thunderstore/CHANGELOG.md`](Thunderstore/CHANGELOG.md)。

## 仓库结构

- `Thunderstore/`：包内文档（`manifest.json`、常青 README、CHANGELOG、图标与截图），`Build-Package.ps1` 的打包源。
- `tests/`：无 Unity 的断言套件（Smoke 面板/结算/同步/扫描、ConfigWatcher、Supply、Scan）。
- `scripts/`：`Verify.ps1`、`Build-Package.ps1` 与 EWC/Scout 契约检查。
- `reports/`：本地验证报告（不随包发布）。
- `defaults/scoring.json`：包内随附的评分文件，由 Smoke 套件自身生成，保证与运行时默认值逐字节一致。
