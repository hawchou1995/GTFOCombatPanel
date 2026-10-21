# Changelog

## 1.18.0
- Settlement titles are JSON-configurable, each with its own description: total damage, minor/elite/BOSS kills, melee and Scout kills. A player takes only a metric they lead the squad in outright, that is enabled, weighted above zero and has real data; the choice is the candidate with the largest actual contribution to the final score, negative metrics by their real severity. A tie or an all-equal squad awards nothing, and no qualifying candidate falls back to the neutral title. The old `ZeroScore` bypass is gone, so a displayed zero no longer overrides the top rule.
- Kill cells gain a sleeping / awake / unknown split of the same count, in the existing cell, with no new metric and no new row. A sleeping kill is worth twice an awake one of the same class by default (`SleepingKillMultiplier`, 1-10). The state is read from the enemy before the killing blow; a state that cannot be read stays unknown and never counts as awake or asleep.
- Supply quality and downs cells now carry this player's own figures: the supply uses whose benefit actually reached that player and how many were ammo packs, and the health actually lost over the run. Giving a pack away is not a use by the giver, a full-health pack with no benefit is not counted, healing never offsets the loss, and a maximum-health change is not a loss.
- Default weights: LoneSurvivor 1.60 and Stagger 1.40, the rest unchanged; thirteen defaults sum to 9.68. Kill tier values stay 0.5 / 1 / 1.5.
- Kill attribution: a death notified before the damage postfix is settled from the health that was actually lost, so a melee killing blow keeps its cause and a second death callback cannot count the same corpse twice. An enemy pooled before its damage component existed no longer carries its predecessor's credited state.
- Sentries: a round is one `FireBullet` scope, and a native `OnBulletFired` raised inside it is that same round; outside every scope the notification is the only evidence and counts. `max(cumulative calls, cumulative events)` is gone, so switching stream or losing one no longer eats rounds.
- Tripmines: reset clears the counted-mine set too, a consumed mine recovered from the field no longer reports a tool recovery, and a tracked placer beats a conflicting detonation core. Glue projectiles credit the owner from `Fire` and reset per pooled projectile, so a reused projectile cannot inherit the previous hit.
- Scans: a taggable list is not a completed scan. A first mark needs the native tagged state or a rising tag timer, bot windows close with their action, and a throttled bot scan keeps the window it actually had.
- Sync is protocol v7 with a 352-byte snapshot: the kill-state split, the self-consumption pair and health lost travel with the row. A kill total and its split are one block and are taken from one machine, so a cell can never print a subset larger than its count. The squad total is held once with the run counter it came from instead of being re-read from whichever cached row was visited last. Older peers are refused by version, not silently half-read.
- Verification: 1514 combat-panel assertions and 9 config-watcher assertions pass on both profiles, Release 0 warnings / 0 errors, and the EWC 4.13.4 API contract is checked against the installed assembly. **No in-game verification this round**: native callbacks, Steam co-op and rendering are not covered by these results, and the reconnect "otherworld" report is not proven or excluded.
## 1.17.31
- Settlement board sizing: the failure and success boards measure the buttons they must clear only when those buttons are actually shown. The failure page keeps an unshown restart control above the lobby button, and reading it cost 143 px of height at 2560x1440; both screens now share the same rule.
- Settlement board keeps a stable size: the first placement waits for the footer to stop moving, so the table is measured once against the settled page and never resizes on a redraw or F5/F8 toggle.
- Column spacing tightened (0.625em to 0.436em between neighbouring columns); rows are at least as tall as the font's own line, so glyphs are no longer clipped top and bottom.
- Sentry hits no longer collapse to one: the turret reports damageSearchID = 0 for a whole run, and keying on it recorded 33 shots as 1 hit. A zero id now counts each recorded damage event; a real id still deduplicates.
- Player titles: a reverse metric (friendly fire, downs) never becomes a consolation title, and only the squad's outright worst earns it. Friendly-fire title is now "敌我不分"; the neutral fallback is "平平无奇".
- EWC projectile weapons (涡轮电浆枪): hits were lost in 1.17.18 because the whole EWC bridge was compiled out of the shared DLL. A dependency-free runtime bridge now binds ExtraWeaponCustomization's public spawn/hit/post-hit/destroy events by name; original owner and weapon are frozen at spawn, and weakspots come from the actual limb.
- Tripmines: a bot's deployer exposes no first-person face, so every bot mine was refused and the deployment column read zero. Mines are now attributed by gear category, and a carried-in (consumed) mine credits its placer's damage without touching deployment or trigger counts.
- Melee kills: the kill cause came from whatever last touched the enemy, so a console kill or a recycled pointer could report dozens of melee kills against zero melee damage. Only the blow that emptied the health bar names the cause now.
- Verification: 1279 combat-panel assertions and 9 config-watcher assertions pass on both test profiles, Release 0 warnings/errors, and the EWC 4.13.4 API contract is checked against the installed assembly. Live gameplay on success screens and in multiplayer is still unverified.
## 1.17.18
- Fixes settlement tool rows rendering as dashes: `ResultsDisplay.BuildReport` now carries the equipped `GearClass` name into `ImpactInput.ToolName`, allowing observed scan, sentry, tripmine and glue details to reach their existing formatter. No counters or scoring sources were changed.
- In-level and settlement panels now borrow a CJK-capable source font from GTFO's TMP default/fallback assets when available; both surfaces use the same renderer. Dynamic GTFO face-family and Windows CJK font fallbacks are logged if the TMP asset has no source font.
- Both profile Verify runs pass: 1203 combat-panel assertions, 9 config-watcher assertions, Release 0 warnings/errors. GTFO Client Mods release; live font selection and result-screen rendering still need a fresh gameplay session.

## 1.17.17
- In-level Friendly Fire now shows damage only; friendly-fire-caused down counts remain in settlement scoring and JSON.
- Accuracy gold and the “Dead eye” title now follow the displayed raw hit rate, while the existing sample-confidence factor still affects composite score. A lower displayed rate cannot take that title via fallback.
- Tool Efficiency details now show actions and values without repeating Sentry / Tripmine / Glue labels already present in the gear row.
- Adds `MineDeployerInstance.OnSpawn` equipment-mine registration, requiring a non-consumable equipped deployer and matching nonzero gear category. Re-registering an existing item can attach its late detonation-core alias without double-counting deployment.
- Adds MeleeKills and ScoutKills as separate settlement metrics; melee kills use the last melee-damage cause, Scout identity uses `EnemyAgent.IsScout`, and both retain death-hook de-duplication.
- Settlement melee cells display the existing `GearMelee` cumulative damage as context; it is not scored again. Push interruptions do not count as melee kills.
- Adds two four-character awards (`近战强袭`, `触手猎手`), thirteen default weights summing to 8.55, legacy custom-weight migration and snapshot protocol v5.
- Tool action text/data remain settlement-only; the in-level tool UI is unchanged. The shared local candidate is built without the optional EWC bridge because the `Test` profile has no EWC; no package was published.
- Local candidate; test/build/install verification and remaining live-run boundary are recorded in `reports/release-1.17.17.md`. Not published.

## 1.17.16
- Shows scan attempts and first-mark counts only for an equipped scanner; other tools no longer display scan placeholders. Tool Efficiency scoring continues to use scan first marks.
- Resolves reusable mine identity through both the spawned item and detonation core. Mine blast damage uses the game's core owner, falls back to a registered equipment-mine ledger owner when needed, and never falls back to a recent unrelated attack when unresolved. Consumable pickups remain excluded from equipment-tool counters.
- Settlement labels are left-aligned and every player/data column shares a right edge; the in-level table layout is unchanged.
- Profile-specific Verify passes: 1174 combat-panel assertions, 9 config-watcher assertions, Release build 0 warnings/errors, with the optional EWC bridge absent on `Test` and present on `Survival Test`. Live mine/blast and settlement-render verification remains pending. Local test candidate; not published.

## 1.17.15
- Adds eleven weighted settlement metrics, including separate friendly-fire damage and friendly-fire-caused down penalties. Down attribution requires a confirmed alive-to-downed transition.
- Renames the scan-derived scoring row to Tool Efficiency without changing its first-time scanner-tag source. Adds sentry, reusable tool-slot mine and glue-gun statistics; picked-up consumable mines are excluded.
- Records Rundown and level name as separate settlement JSON metadata fields. Snapshot protocol v4 carries friendly-fire downs and tool statistics.
- Verification against the `Survival Test` profile assemblies: Release build 0 warnings/errors, optional EWC bridge PASS, 1165 combat-panel assertions and 9 config-watcher assertions. This is a local test build, not published; live multiplayer verification remains pending.

## 1.17.14
- Fixes client damage/share flicker: `SnapshotCache` now keeps the latest host and player snapshots independently, then recombines them from their original sources. Repeated equal-rank client rows can no longer erase the host's damage field.
- Keeps the settlement board and its one JSON file current as accepted rows arrive after the result page opens; unchanged data does not cause repeated writes or duplicate files.
- Adds B10 for interleaved equal-rank host/client snapshots and archive refresh assertions. Full Verify: 1121 combat-panel assertions, 9 config-watcher assertions, Release build 0 warnings / 0 errors.
## 1.17.13
- Removes local combat rows for owners no longer present in the live `SlottedPlayers` roster, so a departed human cannot survive as a duplicate slot row after a bot takes that seat.
- Removes departed owners' remote snapshots and handshake state; snapshots arriving after the owner leaves are refused before they can recreate the stale display row.
- Adds B9 roster-turnover regressions for local-store pruning, same-slot human-to-bot replacement, late snapshot rejection, and retention of the other guest's row. Verify passes with 1118 combat-panel assertions and 9 config-watcher assertions; the Release build has zero warnings/errors.
- Published via the Thunderstore UI on 2026-10-17 to `HawChow/CombatPanel`, GTFO / Client Mods. UI returned `Success!`; the read-only API reports `HawChow-CombatPanel-1.17.13`, GTFO listing, and the downloaded ZIP matches SHA-256 `32A93D5C734FBFD077F97EB2638257388A2B011732919EE010427199E72712DE`. The targeted human-to-bot live handoff test remains pending.

## 1.17.12
- Fixes a client that could see only the host and itself. `SquadExchange.HostRows` filtered the rows it described with `IsHuman`, which answers whether a peer can be *addressed* on this channel - the right question for the recipient loop, the wrong one for whose row is worth sending. A bot never answers and runs no plugin, but the host measures its damage, staggers, kills and revives exactly as it measures a human's, and a client can measure none of that for itself, so a bot's row had no source anywhere. Every slotted player with a lookup is now described to every human peer; the recipient filter in `Tick` is unchanged, so nothing is ever addressed to a bot.
- Regression under the shipped parser and queue: a headless host with one client and one bot now asserts the client receives the bot's row carrying the host's figure. It fails on the previous rule with `FAIL: B8: and it sees the bot's row as the host measured it - a seat no client can measure for itself, and the row the host never used to put on the wire`, and passes now. 1109 -> 1112 assertions.
- The client log behind this build shows the shape of it: `squad sync outbound: 1 row(s) put on the wire; firstTarget=76561199011390103; 1 of 1 human peer(s) have answered`, and inbound rows whose owner is only the host or the client itself - 134 rows each over a whole run, never a third owner.
- Not published.


## 1.17.11
- Fixes an EWC integration build regression: `EWC_DIR` now follows the selected `GTFO_DIR` profile. When EWC is present, Verify requires the optional bridge reference so the API callbacks cannot silently compile out; EWC remains optional at runtime and is not added to package dependencies.
- Highlights a unique highest rescue count with the existing gold tone even below `RescueFloor`; the scoring threshold and score calculation are unchanged. A tie remains unhighlighted.
- Regression tests cover a one-rescue leader below the scoring floor, rendered cell tint, tied maximums, and the target-profile EWC assembly reference.
- An EWC that is installed but older than the accuracy API used to be fatal: arming the bridge fails when the JIT resolves EWC's own types, and that failure landed on the caller, leaving `EwcBridge` a broken type initialiser so every later call into it threw as well. Arming now happens inside the same guard as the plugin-list check, so an unusable EWC is only an unarmed bridge: `Present` goes false and the mod behaves exactly as it does when EWC is absent, with their weapons measured from this machine's own rays again.
- EWC support is documented as optional and version-bound: the bridge is written against the decompiled signatures of **EWC 4.13.4**, and builds below 4.13.4 will most likely not work. That case is now harmless - the bridge stays unarmed and the mod runs as if EWC were absent - and both READMEs say so.
- The package no longer ships the two PNG screenshots; both READMEs already reference them from the `HawChow-Mod-Assets` GitHub host, so the archive is down to six members: `manifest.json`, `README.md`, `CHANGELOG.md`, `icon.png`, `scoring.json` and the plugin DLL.
- Live confirmation on the Gale profile, host run under 1.17.11: the turbo plasma gun is recognised as EWC-owned (`firing window: class=BulletWeapon, ewcOwned=true, weapon=涡轮电浆枪`), its accuracy row opens with `EWC accuracy levels at first sight` and then climbs through `EWC row measured from EWC's own cumulative totals`, and the settlement reads `副武器 · 弹丸 378 / 197 / 34` where 1.17.10 recorded 164 fired / 0 hit. Rescue attribution on the same run matches the log exactly - Dauda credited twice, Bishop once, the other two zero.
- **Published to Thunderstore** as `HawChow-CombatPanel-1.17.11` (team `HawChow`, community `GTFO`, category `客户端模组`) on 2026-10-17 09:57:16 UTC. The uploaded archive is byte-identical to the local build: SHA-256 `5AF81A7ABAAC2996F983AC78E55B938EE2396369E089B7F169FFCB943A17F4F9`, 281,382 bytes, six members, single dependency `BepInEx-BepInExPack_GTFO-3.2.3`. Note: the `CHANGELOG.md` inside the uploaded archive still ends with `Not published.` - that text was fixed when the ZIP was built and cannot be edited afterwards; this local file carries the correction, exactly as it did for 1.16.2.


## 1.17.10
- Rescue counts are now attributed from the replicated reviver/target state and credited only on the downed player's successful-recovery callback; unsuccessful or unresolved attempts are not guessed.
- Host-side tracking includes slotted bot owners when the game exposes their PlayerAgent/SNet_Player mapping. A fresh gameplay run is still needed to confirm bot attribution on this build.
- Uses positional Harmony binding for the single SyncReviverState payload. Added host/client roster-boundary regression assertions.
- Verification: 1104 combat-panel assertions and 9 production watcher assertions passed; Release build had zero warnings/errors. Not published.


## 1.17.9
- 修复收包队列丢弃网络发送者 ID：快照解码的 Sender 为 0，旧版却把它当成底层发送者，导致握手正常而全队统计全部被拒绝。队列现保存 RecieveBytes 的 messagerID，快照 Owner 仍只表示统计对象，不用于猜测发送者。
- 保持协议 v3、来源权限检查、幂等显示投影及本地评分；握手声明不得覆盖网络身份。快照日志增加 owner，发送日志不再声称只有应答者收到数据。
- 原多机拓扑回归改为经过生产收包队列；新增入口回归取得修前失败、修后通过证据，覆盖独立局次、旧包拒绝、主机重启恢复、双向统计、越权拒绝以及成功/失败结算归档。1080 条统计断言 + 9 条 watcher 断言通过。
- 更正历史 1.17.4 的确定性措辞：握手/前缀属于假设和健壮性改动，不是本次已证实根因。新版真人双端联机尚未验证。本版未发布。

## 1.17.8
- **仅文档修正，模组行为与 1.17.7 完全相同**：已发布版本的页面文字没法单独改（`/edit/` 路由在新旧两站都是 404，改 listing 只能走 API token），所以把 1.17.7 段落里的"未发布"更正为已发布，并补上发布记录（线上包 SHA256 `1A2F954A3C966FA218E1C26236A6B4822583E70AEBC42BCA70A9D5517F103DA2`，与本地构建逐字节一致；`1.17.2`–`1.17.6` 从未单独发布，内容都在 1.17.7 里）。
- 代码差异：**零**。唯一变化是 `PluginInfo.Version` 这个字符串本身（它会出现在启动日志与结算 JSON 的 `version` 字段），逻辑与断言数（1023）不变。若你本机已装 1.17.7，可以不必更新。
## 1.17.7
- **弹丸类武器的命中率不再恒为 100%**：`ProjectileAPI.OnProjectileHit` 的第二个参数（这发弹丸打到了什么）此前被丢掉，于是打墙、打地板、打天花板都算一发命中——实机上 #1 的涡轮电浆枪是 `529 发 / 529 命中`、#2 是 `92 / 92`，两人都精确 100%，还把本人的总命中率一起抬到 `~82.5%`。现在只有落在**敌人**身上的弹丸算命中，并沿用伤害路径同一套"景物不算目标"的规则（容器锁、门锁一律不算），规则落在 `EwcReconcile.ProjectileHitCounts`，无头断言覆盖。
- 命中另有第二条独立来源：伤害记录（`Dam_EnemyDamageBase`）本来就只认敌人，所以这次收紧**不会**让 EWC 弹丸武器丢命中，只去掉误计。口径上与 StatDisplay 不同（它的 `EWCWrapper.AddHitCallback_Unsafe` 同样忽略该参数），这是刻意做得更准。
- 断言 1019 → **1023**。**已发布到 Thunderstore**（1.17.7，线上包 SHA256 `1A2F954A3C966FA218E1C26236A6B4822583E70AEBC42BCA70A9D5517F103DA2`，与本地构建字节一致；1.17.2–1.17.6 未单独发布，内容都包含在本版里）。

## 1.17.6
- **EWC 武器不再"整段让位"（本轮主线）**：`CombatTracker.BeginFire` 原先是**无条件**跳过 EWC 管理的武器（`if (EwcBridge.IsEwcWeapon(weapon)) return null;`），于是这些行的唯一来源只有 EWC 自己；一旦 EWC 对某个 (玩家, 槽位) 从不报数，那行就**只能**是 `0/0/0`，与"有没有开枪"无关。现在改为**条件让位**：EWC 报过该槽位才让位（它的累计总量是基准），从未报过则按本机看得见的 ray 自己计（`EwcBridge.NoteObserved`），再由 `EwcReconcile` 的 `max` 与 EWC 后来的总量对账——`Applied` 记住已经交出去的部分，同一发不会数两次。**看得见才算**：EWC 武器常常不走本模组的 ray 路径，这种情形**不**按弹药消耗估造数字（那会写出假的"0% 命中率"），只留一条 `EWC weapon fired without a ray this build can see` 并**不计**。
- **EWC 的补充流现在真的会落表**：`FireShotAPI` 的开火、`ProjectileAPI` 与伤害记录带来的命中，以前只是"记在桥上"、等 EWC 的精度回调来交表；EWC 不回调就永远不落表。现在只要 EWC 没报过该行，这些观测在**第二次**开火之后交表（等一拍是为了不与"EWC 先报开火、再报同一发的命中"的时序打架；观测是累计的，所以一发都不会丢），并标 `~`。
- **爆炸归属的登记面扩大**：此前只有 EWC 的开火会登记"最近一次攻击"，于是实机日志里出现过 `explosive damage of 19.3 arrived with no attack in the last 3s` 这种被丢弃的爆炸伤害。现在**每一次被计入的开火**都登记（本机、机器人、远端玩家的 `PlayerInventorySynced` 同步计数），3 秒窗口与"没有可归属对象就不计"的规则不变。
- **结算板与存档的武器名不再漂移**：同一个 `(槽位, 武器 id)` 行，被动读背包会**改写**名字——那一局归档写 `HEL左轮手枪`、稍后重画的板写 `手枪`，数字一样名字不一样。现在名字只由**真正命名了这把武器**的来源设定（真正开火的那把武器，或对该行有权威的机器/快照），一旦锁定就不再被被动探针改写；占位名（`GearStandard`）仍会被第一个真名替换。归档与屏幕因此一致。
- 断言 1009 → **1019**（5 条 `EwcReconcile.FallbackHandover`：EWC 报过就不兜底、沉默行交出自己的观测、同一批观测不重复交、第二批只交增量、命中不超过开火；5 条武器名锁定：占位不锁、真名锁、锁后被动不改、换枪仍可改名、`Clone()` 投影带着锁）。本版**未发布**。
## 1.17.5

- **补齐 EWC 的三套补充流**（本轮主线）：EWC 的 `AccuracyAPI` 并不覆盖它管理的每一把武器，而 StatDisplay 读的是**四套**。现在按他们的口径全部订阅：`FireShotAPI.PreShotFired`（开火，取 `data.owner.Owner.Lookup` 与 `data.shotInfo.CWC.Weapon.InventorySlot`）、`ProjectileAPI.OnProjectileHit`（命中，取 `cwc.Owner.Player.Owner.Lookup`）、`DamageAPI.PreLocalDOTDamage` / `PreLocalExplosiveDamage` / `PreLocalShrapnelDamage`（伤害，取 `cwc.slot`，并用 `MasterHasMod` 同款守卫：只有"主机没装本模组"的本机才自记）。
- **原版爆炸伤害此前完全没有钩子**（一个真实的覆盖缺口）：`ReceiveExplosionDamage` 补上（此前只有 Bullet/Melee/Fire/Push）。爆炸载荷**没有 owner 字段**（编译器证明了：`pExplosionDamageData` 没有 `source`），所以归属走"最近一次攻击"缓存（StatDisplay 的 `_cache` 同款做法），缓存里没有可归属的攻击就**不计**而不是猜给某个人。涡轮电浆枪那一行为什么空，本版给的是**定性手段**（见下条判别日志），不是结论。
- **防重复计数**：`EwcReconcile.Resolve` 现在把"观测到的开火数"也纳入 `max` 对账——同一发被 EWC 精度 API 和 `FireShotAPI` 都报到时只算一次，只被其中一方报到时仍然入表。新增两条断言覆盖（纯流来源的武器能报数与命中；被精度 API 覆盖的武器不会被第二来源翻倍）。
- **判别日志**：`BeginFire` 首次遇到每个武器类时打印 `class=…, ewcOwned=…, weapon=…`。这条是为了把"这把武器的开火数为什么没有"一次定性：要么它的 `Fire` 根本不在本模组钩的四个类里（覆写 `Fire` 的子类），要么进了窗口而数据缺在别处。
- **同一会话的第二局失败此前不落盘（真实缺陷，日志与磁盘同时证明）**：`ResultsSaver` 的"每块结算屏一份"闩锁只认 **page 指针**，而游戏把结算页实例留满整个会话——同一会话的第二局失败拿到的是**同一个 page 对象**，于是被判成重复而**静默跳过**：不写文件，也不打日志。证据在 `Survival new` 那局的日志里：两块失败屏（`failure strip` / `failure board placed` 各两次、`New expedition: counters reset` 两次），磁盘上却只有一个 `CombatPanel-results-20261017-091527.json`，并且它与截图那局根本不是同一局（截图里 #1 A1pha 总伤害 1,939.7，那个文件里是 146.0）。现在键改为 **page 指针 + 本机局次计数器**（`CombatSync.Generation`，每局开局自增）：同一块屏反复重测仍然只写一次，同一会话的第二局拿到自己的文件，`settlement saved` 日志同时带上 `run N`。顺带修掉 `_loggedDraw` 的整会话闩锁（`influence board …` 那行因此在本会话第二局里没出现），现在按每块结算屏各报一次。
- 断言 1002 → **1009**（新增 `SettlementArchive` 的 5 条：首屏写、同屏重测不重写、同页换局写、换页写、`Forget` 后可写）。本版**未发布**。（1.17.4 的传输与结算页修复已在你 `Survival new` 的主机实测通过：局内 5 行、结算 4 人、`boardOverlapsLobbyButton=false`。）

## 1.17.4

- **修复「局内客机只看到自己」的第二个根因：数据发送曾经依赖对方的应答。** 朋友的客机日志（1.17.1，`D:\R2mod\GTFO\profiles\tian2`）给出了确凿事实：`authority` 行说明它**认得主机**，但整局**没有任何一条收包记录**（无 `accepted/refused`、无 `direction` 行），局内表 `3 columns x 2 rows`（只有自己），结算板 `(1 players)`。上版的发送门槛是「只发给应答过的 peer」——于是只要那条**反向链路**不通（招呼丢包、对端读条中被错过、或别的模组的前缀吃掉包），数据就永远不来。现在**不再有任何门槛**：每个人类 peer 每秒都收到行，应答交换只保留为诊断与「对方装了模组」的证据。这与现成实现（StatDisplay 的 `SenderMode.OtherPlayers`）一致：它们也是不问、直接发。
- **`RecieveBytes` 前缀提高优先级**：朋友机上这个方法有 **4 个前缀**（GTFO-API、AWO replicator、AddEvents、DamageSync）。任何一个前缀返回 false 或抛异常，都会把它后面的人挡掉，而我此前可能排在最后。现在 `[HarmonyPriority(Priority.High)]`，先读自己的包。
- **快照缓存的第二道锁不再永久拒绝重启的对端**（真实缺陷）：`HandshakePolicy` 有「对端坚持旧序号就采信」的自愈规则，但 `SnapshotCache` 是另一道独立的序号锁、**没有**这条规则——于是对端重开游戏/热重载后，握手已采信新序号，行仍被缓存**永久拒收**，两台机器此后整局不再共享。现在缓存接受握手的判定（`recovered`），并且恢复时**不与上一局的行做字段合并**（那是两次不同的远征，合并会把上一局的数字带进来）。B5 场景正是在新发送规则下暴露了这一点，现已修复并有断言。
- **结算页不再压住主机那个「送所有囚犯回大厅」按钮**：该按钮是**原版** `m_btnGoToLobby`（只是主机才有），而本模组此前在此页**量不到它**时就退回「铺到屏幕底部 − 8」，于是盖住按钮。现在 (1) 板子的下边界改为**量不到按钮时也不超过窗口的 0.66**（实测最靠下的页面页脚是窗口的 0.705，720 高窗口的那个是 0.83，0.66 对它们全部安全）；(2) 不再只量两个已知字段，而是**遍历页面上所有按钮**取最高的一条边，因此自定义/主客不同的结算页也自适应；(3) `BuildGrid` 本来就会按给定高度缩放，量到更短的条带后板子会自己缩小而不是越界。
- 断言 1001 → **1002**（含 B2/B3 按新发送规则改写、B5 恢复断言现在真正生效、B6 旧规则回放的口径说明）。本版**未发布**；真实联机仍需再跑一次核对（日志见下）。

## 1.17.3

- **修复「主机装了模组、结算能共享、局内看不到别人」的根因（本版主线）**：上一版的交换层是我自己加的一层，规则是「对没应答过的 peer 每 2 秒打一次招呼，**最多 5 次**，然后整局不再理它」。那 10 秒里如果对端还在读条（一人在关卡、另一人在加载界面，正是这个场景），或者招呼丢包，该 peer 就被**永久写死**——两台机器整局一个包都不交换，屏幕上看和「这模组根本不能共享」一模一样。改为**招呼只降速、不停止**：前 10 秒走 2 秒节奏，之后每 10 秒一次，同一局内仍然找得到对方。
- **「是否发数据」不再挂在「最近有没有声音」上**：一个 peer 只要本局**证明过**自己装了模组（`EverAnswered`），就持续收到行，即使它自己的上行静默超过 12 秒（`SilenceSeconds`）。旧规则会在静默超时后清掉应答标记，主机于是**悄悄停止**转发——而那台客机明明还能收。新的场景 B7 覆盖这一点，并且是可区分的：客机静默 30 秒后，主机**新产生**的数值必须到达（断言 500；旧规则下停发，缓存里只剩 400，会失败）。
- **主客判定放宽**：收到「关于第三名玩家的行」时，除了 peer 自身的 `IsMaster`，也接受「本机 roster 认定的主机」。主机标志读不到时，旧逻辑会把关于别人的行全部拒掉，表现为「只看得见自己和主机」。
- **两条可定性日志（每局各一条）**：`squad sync outbound:` 说明本机到底有没有在发、发给谁、几个 peer 已证明装了模组；`squad sync inbound:` 在「关卡内 20 秒、roster 里有人类 peer、却一个包都没收到」时告警。下一次联机不必再靠猜。
- **与现成实现的关系（本轮补读）**：StatDisplay 走 `Dinorush.OptionalNetworking` 的 `SenderMode.OtherPlayers`，是**点对点的事件增量**，没有握手；本模组的底层传输与 GTFO-API 的 `NetworkAPI` 一致（`SendBytes(quality, channel, player)` + `RecieveBytes` 前缀，逐行核对过）。差别在我上面自己加的「握手 / 转发 / 每秒整行」这层——本版修的就是这层的病。约束未变：本模组仍只依赖 BepInEx，不能引入 OptionalNetworking 这类前置。
- 断言 998 → **1001**。本版**未发布**；真实联机复现仍未做（等客机日志）。

## 1.17.2

- **修复 EWC 武器命中率恒为空（本版主线）**：ExtraWeaponCustomization 的 `AccuracyManager.InitShot` 在开火时发一次回调（带开火数），`AddHit` 在弹丸命中时**另发一次**回调，而那次 `Count = 0`。旧实现按「每次回调 = 一发完整结果」读，`fired <= 0` 直接 return，于是命中增量被整段丢弃——开出枪数、永远 0 命中，这正是「EWC 武器命中率显示 `—`」的原因。本版改读 EWC 自身的**累计**统计（`WeaponAccuracy.Shots` / `FullShots` 的 `Count/Hits/Crits`，回调触发前已累加），不再依赖增量的形状；`CancelShot` 发的负增量同样被消费（撤销是一笔更正，不是噪声）。规则抽成不引用 Unity 的 `EwcReconcile`，逐字段无头断言。
- **计数契约补一条能吃负增量的入口**：`PlayerCombatStats.AddAccuracy` 会拒绝 `(0,1,1)`（命中数大于开火数），这正是这条流量此前根本无法落表的原因；新增 `AdjustAccuracy` 走有符号增量，累计值夹取到非负、弱点 ≤ 命中。
- **EWC 三层锁定**：EWC 同时维护 pellet / group / 整发三层，旧实现每次回调取「最大的那层」，可能把 A 层的开火数与 B 层的命中数拼在一起。现在每把武器在第一次回调时选定一层（开火数更宽的那层）并**保持**，分子分母永远同源。
- **本地观测不再重复计数**：主机没装本模组时，客机看不到自己打出的命中（游戏不会把本机的伤害回送本机）。此类观测改为按 (玩家, 槽位) 记账，用 `max` 与 EWC 的累计值对账：只补 EWC 没数到的，且不超过 EWC 报的开火数——**只填缺，不会翻倍**。顺带修掉两处：弱点此前用 `Dam_EnemyDamageBase` 转 `Dam_EnemyDamageLimb` 读取（类型根本不对，永远读不到），现按记录里的 `limbID` 查 `DamageLimbs[id].m_type`；补记此前只按玩家存放，换枪会错配到下一把武器，现按槽位归属。
- **无模组主机的客机重新看到自己的伤害**：新增本地输出端钩子（`Dam_EnemyDamageLimb.BulletDamage` / `MeleeDamage` 前缀），取 `min(这一击的伤害, 剩余生命)` 作为本机这一击实际打出的伤害，标 `~`（估算）。主机侧、以及主机已装本模组的客机都**不**走这条路（伤害记录路径已经覆盖），所以不会重复计数；由此客机在自己那行重新拿到伤害、占比与击杀归属。
- **无模组主机时的小队占比不再恒为 `—`**：主机从不提供总数时，改用本机行 + 所有已应答 peer 的自己那行求和，并标 `~`（没应答的对端不在里面，所以是估算而非测量）。
- **可诊断性**：EWC 回调在任何提前返回之前都会打印三层的累计值与本次增量，`Count = 0` 的回调一定会出现在日志里，不再无声丢弃。
- 断言 981 → **995**。本版**未发布**到 Thunderstore。验证边界写清楚：EWC 修复由「EWC 自身反编译语义 + 无头断言」支撑，**尚未**在装了 EWC 的 Survival 武器（沙鹰）上做实机复现；无模组主机的客机伤害兜底同样待实机联机确认。

## 1.17.1

- **局内面板底板可调**：新增 `[Layout] PanelOpacity`（默认 **0.28**），控制局内表格自己那块深色底板有多浓：`0` = 完全透出游戏画面，`1` = 实心板。旧的 0.48 写死在代码里，看起来像一块黑板，所以默认值下调并交给配置。边框描边按同一比例缩放（沿用 0.22/0.48 的旧比例），所以底板变淡时不会只剩一圈轮廓。改文件或热重载**下一帧**生效（每帧重读，不缓存）。
- 结算影响板**不受**这个键影响，继续用它自己已确认的底板值；启动与热重载日志都带 `panelOpacity=` 便于确认改没改到。
- 981 条面板断言通过（新增 7 条覆盖默认值、0..1 夹取、非数字回落、结算板不受影响、边框比例）。**已发布到 Thunderstore**；作者实机初步测试通过，多人联机反馈待补。

## 1.17.0

- **多人交换重写**：交换逻辑从 SNet 里拆出来，成为不引用 Unity/IL2CPP 的 `SquadExchange`，无头测试用内存伪传输真实驱动它。收到的包永不写入采集账本：显示行 = 本机行克隆 + 缓存快照投影。
- **每台机器都向每个 peer 打招呼**（每 2 秒一次，未应答者每局最多 5 次）。因此「无模组主机 + 多台模组客机」时客机之间能直接互相看见；无模组主机不会被塞行，也只在有界次数内被打招呼。
- **应答从「只答一次」改为按节奏反复答**：第一个应答被网络丢掉不再等于整局沉默。证据：B6 场景下旧规则应答 1 次（应答丢失即无法修复），本版应答 2 次并在 1.25 秒内完成配对（≈ 0.6 个重试间隔）。
- **运行序号自愈**：对端重开游戏/热重载后序号回退时，旧版永久拒绝（Test 配置日志的最后一行就是 `refused a stale snapshot`）。现在同一个更旧序号被拒 3 次或持续 3 秒后即采信，并把两侧序号写进日志。证据：旧规则 30 个 tick 全部拒绝；本版第 3 个包接受。
- **同序号双源按字段合并**：主机行是伤害权威，本人行是命中/供给/扫描权威；估计值永不覆盖实测值；本机自己那一行的精确计数不会被对端回声改写（与远端快照逐字节比对断言）。
- **可诊断性**：每个接受/拒绝都输出 `squad sync <hello|snapshot>: accepted|refused; peer=…; reason=…; peerGeneration=…; localGeneration=…`，并另有方向可用性行（「对端能被听到」/「本机被对端听到」）；接收前缀、队列排水与每个 handler 都有异常护栏。
- **上一轮遗留开关**：成功页红水晶（artifact）块与囚犯报告块的显示开关默认关闭，配置说明含「建议关闭显示以免影响观感」；`ShowImpactBoard=false` 时结算影响板完全不渲染；F8 在结算页切换影响板。
- 974 条面板断言通过（含 B1–B6 三种多人拓扑与自愈/抗丢包场景）。本版**未发布**到 Thunderstore。

## 1.16.2

- 修复成功页永久等待：1.16.1 的 TMP alpha、renderer 可见性与预制体枚举门槛不匹配游戏实际的启停式 Blink。现在从 `CM_ExpeditionSectorIcon.BlinkIn` 登记游戏真正展示的节点，再观察 `CoroutineManager.SetActiveBlink` 的 `MoveNext` 自然结束；图标背景、骷髅及可见名称全部完成后放出看板，无超时提前放行。
- 旧动画回调不能完成新动画；同页 F8 不清掉完成状态，换页/重新启用成功页会清理。本轮不改失败页布局，不改评分或扫描。
- 增加 `success native reveal: completed=N/M` 与 `F8 visibility` 状态日志，避免仅记录首次等待而无法判断后续阻塞条件。
- 920 条面板回归、9 条真实文件监听回归、隔离 IL2CPP 原生 Blink 探针通过（先 ready=False，后 completed=2/2，配置切换保留完成状态）。完整成功结算画面和实体 F8 尚未实测。
- 1.16.2 已发布到 Thunderstore；保留已有配置与历史包。

## 1.16.1

- **失败页显示锁存**：原日志先记录 850×517 的板子放在 483..1000，随后按钮边界变化使条带变为 483..998，逐帧重新判定又把板子隐藏。现在首次放置后保留本页布局与不透明度，不再随标题透明度或悬浮几何消失；若按钮确实上移，只上提板子避让。
- **恢复成功页出现时机**：删除超时与“已停止变化”提前放行；不再把 `m_sectorHeader` 当成分区名称。读取每个已清理分区的 `m_title` 与实际 SpriteRenderer，要求文字网格已生成且可见，图标达到游戏 `Setup` / `SetupIcon` 传入的目标透明度，再锁存本页就绪状态。F8 重开不会重新等待。
- **F8 即时响应**：下一 Repaint 即刷新显隐，不再等待 0.25 秒统计刷新；以当前配置为切换起点，不再在离开关卡时强制重新显示。F8 两项配置合并保存且不触发布局重建，文件监听按实际内容过滤自身保存回声，仍接收外部 cfg/JSON 改动。
- **局内介绍截图**：替换为本次提供的 754×671 图片；公共图片资源已更新，包内也保留副本。未发布新模组版本。
- **扫描 `~` 的口径未变**：当前机器人扫描按 `BotTag` 时间窗口合并回调，因此保留估算标记；能确定扫描机器人是谁，不等于计次路径已精确。
- 验证：本机 interop Release 构建 0 警告 / 0 错误，907 条面板回归 + 9 条真实文件监听回归。游戏内淡入顺序、各分区显示状态与 F8 实际手感仍待实测。

## 1.16.0

- **同步层不再回写采集账本。** `CombatSync` 收到快照后**只**放进独立的 `SnapshotCache`，本机实测账本 `CombatTracker.Store` 一个字段都不动；显示路径改为 `CombatSync.DisplayRows()`，把本机行**克隆**后按字段合并远端数据（`CombatMerge.Project` + `CombatMerge.ApplyRemote`）。主机的实测伤害不会被客机的估算顶掉；客机自己实测的命中率不会被主机的估算覆盖；未观测的字段绝不会把已测量值清零；**重复注入同一份快照不会把伤害二次累加**（合并是覆盖）。`PanelBehaviour.Refresh` 与 `ResultsDisplay.BuildReport` 都改走 `DisplayRows()`。
- **客机不再用「看到的掉血」估全队总量。** `EnemySetHealthPatch`、`ObserveSetHealth`、`_damageFromHealth` 已整体删除。主机没装本模组时，客机的伤害/份额显示 `—`（而不是一个带 `~` 的半精度近似值）；一旦装了模组的主机开口，它的实测总数立刻接管（`CombatSync.SquadTotal`）。**本机行不会被自己的快照覆盖**：主机发来的本机行只补上只有主机能给的权威伤害，本机精确命中率原样保留。
- **扫描效率对两条生物追踪器都计数。** 每个 scanner 带**自己**的揭示账本（`CombatTracker.Ledger` 里 `new ScanReveals()`），队伍共用账本降级为诊断；`ScanSession.Observe` 改成「一次揭示本身就是一次扫描」——新揭示一定记 1 次首标，且一定补记 1 次扫描次数，所以该行再也不会出现「有首标、分母是 0」或者「两个人都在扫却都是 0」。
- **权重：硬直 > 救援，火力类上调但不过分。** 十项默认权重 `1.2,1.8,0.9,0.35,0.55,0.35,1.0,1.1,0.6,0.7`（顺序：总伤害、硬直阻止、击杀、救援、弱点率、命中率、补给效率、扫描效率、单人存活、阵亡）：`Stagger 1.2 → 1.8`、`TotalDamage 1.1 → 1.2`（火力类现高于其余每一项权重）、`Revives 1.0 → 0.35`、`SupplyEfficiency 1.3 → 1.0`。救援下限仍为 2 次。`SupportRoleFirepowerBonus` 默认 `1.25 → 1.15` 且只作用于**总伤害与硬直阻止**，`SupportRoleUtilityBonus` 默认 `1.0`，两者都硬夹在 `1.3` 上限。
- **辅助武器判定：胶枪 / C-Foam / 生物追踪仪 / 开锁器算辅助；哨戒炮与绊雷排除。** `SupportExcludedTools`（`Sentry,哨戒炮,哨戒,Tripmine,Mine,绊雷,地雷`）优先，`SupportOtherTools` 默认 true（其余非空工具算辅助）。
- **可编辑评分文件 `CombatPanel-config/scoring.json`（与 DLL 同目录，绝不裸放）。** 十项权重、启用的指标、小怪/精英血量上限、`RescueFloor`、`AccuracyConfidencePellets`、两个倍率、辅助工具规则都在里面。只在缺失时创建一次；文件非法时**保留上一次有效配置并回落 `.cfg`**，**绝不覆盖用户文件**；写入是原子的。**每个客户端用自己的 JSON 算自己的分，主机只同步原始计数。** `.cfg` 的 `Weights` 降级为回退值。
- **热重载覆盖 .cfg 与 scoring.json。** `ConfigWatcher.Watch(ConfigFile, string extraFile)` 现在开两个 watcher（`.cfg` 一个、评分文件所在目录一个），共用一个 300 ms 去抖标志，重载在主线程执行；配置变化作废看板缓存并重测，普通计数更新保持已锁定字号与位置。
- **成功页两个开关改成肯定式、默认关闭。** 旧的两个反方向开关已删除，替换为 `Results/ShowSuccessArtifacts`（默认 **false = 隐藏**：红晶体/神器栏、总结行、检查点消耗）与 `Results/ShowSuccessPlayerReports`（默认 **false = 隐藏**：四张囚犯报告卡及其锚点）。两个 `.cfg` 描述里都含提示 **「建议关闭显示以免影响观感」**。开关打开时每一块都精确还原（记录原始 localPosition + activeSelf）。
- **结算影响板开关 `Results/ShowImpactBoard`。** 关掉后影响板**整块不画**，页面回到原版样子——**没有旧统计表回落**（`PrepareStatistics` 已删除）。判定集中在纯函数 `OverlayPolicy.Draws(showResults, showOverlay, showBoard, toggled)`：三个开关加 F8 状态必须同时成立。
- **F8 切换后写回 `.cfg` 永久保存。** `ToggleKey`（默认 F8）按下时同时翻转局内表与结算板（同一个 overlay），并经纯函数 `TogglePolicy.Apply(bool)` 决定写 `ShowPanel` + `ShowResultOverlay` 两个值，然后 `Config.Save()`；下一帧生效。局内与结算页行为一致。
- **失败页与成功页共用同一条安全区，且失败页看板绝不因亚像素误差消失。** 成功页测到的完成块底边被 `FailureAlignment.RememberCompletion` 记住（同窗口尺寸才复用），失败页把自己的 `m_missionFailed_text` 底边移到那条线上（`AlignHeading`），然后用与成功页完全相同的 `ResultsRegion.Resolve` / `ResultsPlacement.TryFitBetween` 规则放板子，底部取 `min(strip.Bottom, Screen.height-8)`，即永远在 `m_btnGoToLobby` / `m_btnRestartCheckpoint` 之上。`TryFitBetween` 新增 `slack`（`BoardSlack = 2f`，远小于条带留出的 16px 间隙）；差一点点时**把板子上提到底边**而不是丢弃；`HudTable.BuildGrid` 末尾加有界循环，保证返回的网格真的不超过被要求的高度（旧代码靠 `FitFactor` 比例 + 字号取整，会溢出零点几像素，正是"看板被判没地方"的成因）。只有条带真的塌缩才不画，并写日志。
- **指标区「总伤害」行现在带百分比占比**：单元格写成 `1234.5 (23.4%)`（占比量不到时只写伤害本身）；占比仍沿用 `~` 估算标记。
- **三档击杀权重可配置。** 击杀档位（小怪 / 精英 / boss）的相对价值原本硬编码在代码里（`ImpactCounters.WeightedKills` 的 `0.5 * 小怪 + 1.0 * 精英 + 1.5 * boss`，即 1 : 2 : 3）；现在搬到 `scoring.json` 的 `MinorKillWeight` / `EliteKillWeight` / `BossKillWeight`（默认 0.5 / 1 / 1.5，必须单调不减，即 小怪 ≤ 精英 ≤ boss，否则文件判非法）。`Weights` 里的 `Kills` 仍是**整行权重**，三档值是其**内部**的相对价值，两者相乘；默认比例与旧构建逐位一致，既有分数不变。评分文件关闭或不可读时回落到 `.cfg` 的同名三个条目，再缺失则用发布默认。
- **本机已验证**：主工程构建 0 警告 / 0 错误；无头断言 704 → **879**（新增逐字段合并不累加 / 不清零 / 本机精确率存活、双 scanner 首标各自计数、成功页完成等待不被落位重试清零、失败页条带 2px 容差与上提而非丢弃、`PanelGeometry.Build`→`FitFactor`→重建后高度不超预算、开关默认关与 `OverlayPolicy.Draws` 四门与门、F8 写回、`FailureAlignment` 的锚点复用与对齐、板体与大厅按钮不相交、`ScoringFiles` 目录名与文件名）；打包 `VERIFY: PASS`（`artifacts/GTFOCombatPanel-1.16.0.zip`）；新 DLL 与 `CombatPanel-config/scoring.json` 已装入 r2modman `Test` 配置且 DLL 哈希与构建产物一致。
- **实机回报后修掉的两个显示 bug（2026-10-16 晚，按现场日志定位）。** ① 成功页看板完全不显示、F8 也无效：`ResetPlacement()` 每次都把 `_successHoldPage` 清零，而 `_placed` 为 false 时 `Refresh` 每帧重试，于是 `CompletionReady` 的 4 秒兜底与 0.5 秒"已停止变化"两条退路永远无法到期（该页分区元素带非全不透明 Graphic，`opaque` 恒 false）→ 死锁。现在 `ResetPlacement()` 不再清 `_successHoldPage`，新增 `ResetCompletionHold()` 只在页面真的换了或离开结算页时调用，且"藏原版区块"移到所有关卡判断之后、只在 `_placed` 为真时执行（没画板子的帧不再藏原版内容）。② 失败页看板只在鼠标划过 `任务失败` 时出现：实测条带 518 高、网格 518.5 高，`Draw()` 的 `top+height > bottom` 判只差不到 1 像素就把整块丢掉，鼠标移动让按钮 Renderer 边界动一两像素才凑够。详见 `reports/release-1.16.0.md` 第四节。

- **待实机确认**：4 人联机下「主机装模组 / 主机未装模组 / 装了模组的客机」三条路径；成功页看板是否真在分区图标淡入后出现；失败页看板是否稳定出现且不再依赖鼠标悬浮；失败页与成功页完成块底边对齐、板子不遮挡按钮的真机观感。本机没有本次多人场景日志可依据。

## 1.15.13

- **主机→客机的队伍数据同步真的通了。** 上一次失败的真因：`SNet.SetupCustomData<A>` 只认 IL2CPP 元数据里已经存在的类型，而模组自定义 struct 没有指针存储（`Il2CppClassPointerStore`1` 抛 `TypeInitializationException`），所以一条也发不出去。现在改走游戏自己的**裸字节通道**——也就是 GTFO-API 0.5.1 的 `NetworkAPI` 与 Hikaria Core 背后那套做法：发送用 `SNet.Core.SendBytes(字节, 质量, 频道, 目标)`；接收在 `SNet_Replication.RecieveBytes` 上挂一个 Harmony 前缀，前两字节是复制层分发的 key，本模组用一个没人占用的 key（`0xFFFE`）加魔数 `CMPL` 认出自己的包并就地吞掉，别的包原样放行。**依然只依赖 BepInEx**，没有引入任何第三方库。每个包带协议版本（v2），版本不同的两端保持沉默，而不是互解字段。
- **客机也把自己的精确数据回传主机。** 主机看别人那一行只能估，客机对自己那一行是实测：现在客机把自己那一行发回去，谁的实测谁说话（`Accepts` 拒绝针对本机 lookup 的快照，本机行永远不会被覆盖）。
- **主机没装模组时，客机给全队伤害一个近似值。** 主机是唯一施加伤害的机器，客机只能看到敌人被同步后的新血量；于是客机把「看到的掉血」累加为全队总伤害，自己那一行的占比不再是 `—`，而是带 `~` 的近似值（`~52.2%`）。一旦装了模组的主机开口，它的实测总数立刻接管。血量是半精度相对值（`SFloat16`），按该敌人最大血量还原；掉血大于该敌人整条血量（重置/复活）不计入。
- 回归断言 695 → **704**（新增线路往返、包长与近似标记的断言）。

## 1.15.12

- **评价下面多了一行说明，把称号背后的数字写出来。** 此前「评价」只有四个字，看不出凭什么给的。现在紧贴「评价」下面新增一行「说明」，逐人写出这句称号是拿什么换来的，例如 `伤害 95.4，占全队 52.2%`、`扫描 6 次，首次标记 2 只`、`打断敌人攻击 3 次`、`补给 1 次，有效 62.0%`、`阵亡 1 次`；零分玩家写 `本局没有任何计分贡献`。这一行跟着**称号**走，而不是跟着权重最高的指标走，所以它永远不会解释一个此人并没有戴上的称号；量不到的项按本表约定写 `—`，估算项沿用 `~`。整块因此由 24 行变 25 行（拆开击杀等级时 27 行）。
- **看板出现的时机对齐页面自己的淡入。** 失败页的看板一直是等自己的「任务失败」标题起来之后才渲染；成功页此前只等「量得到分区」，而对象一存在就能量到，于是表格仍在页面淡入过程中先出现。现在成功页也等「已清理分区」与它首个分区图标**真正可见**（已激活且不透明度到 1）才落位，与图标同时出现。等待有上限（4 秒）；期间若这一块已停止变化（例如某个说明文字本来就是暗的），也按就绪处理，所以不会把看板拖住。
- **看板出现后不再缩放。** 结果页的轮询每 0.25 秒重刷一次，看板因此此前每帧都重新测量、重建网格，玩家正读着表格时字号会跟着页面动画跳。现在一个结算页只测量一次：位置与字号定下来就冻结，只有换页（成功↔失败）才重新测量。
- 回归断言 **695**（本次新增 15 条：紧跟「评价」的那一行本身，以及它对伤害、扫描、硬直、三种击杀、补给、阵亡、零分与「量不到」各自写出的句子）。

## 1.15.11

- **成功页看板不再抢在分区图标之前出现。** 此前分区那几项还量不到时，看板会先按「历史比例」的兜底位置画出来，等图标加载完再跳到位——看起来就是表格先冒出来、页面随后才齐。现在**量到分区之前先不画**，量到的那一帧直接落在最终位置，与图标同时出现且全程不移动。等待是有上限的（2.5 秒）：万一某个版本永远给不出可测量的分区，看板也会在片刻后按兜底位置出现，而不是干脆不显示。
- 回归断言保持 **678**。

## 1.15.10

- **结算看板用满剩余空间，字号不再偏小。** 此前表格只按「能塞进那条带子」缩，从不长大，四周空着一大片。现在先在常规字号下建一次，若高度只占了带子的九成以下，就按剩余高度重建更大的字号（上限 2.5 倍），左右各留 24 px 边距；网格自身的缩放兜底仍在，所以「长大」永远不会把表格顶到图标或按钮上。
- 回归断言保持 **678**。

## 1.15.9

- **成功页落位：地标改量「连同子物体」。** 1.15.8 的相机兜底让测量真的成了（实机 `completion=373, lobbyButton=1309, boardTop=389`），但 373 比肉眼所见浅了一百多像素——`已清理分区` 的图标**下面的文字**是子物体，只量本体取不到它的底边，看板于是仍然压住图标与那行字。现在 `Bottom()` 把该元素**及其所有子渲染器**都投影一遍取最深底边，子物体走同一套合理性校验（投到画面外、大得离谱的照样丢弃）。
- **对局档案收进一个文件夹**：`…\plugins\GTFOCombatPanel\CombatPanel-results\CombatPanel-results-<时间戳>.json`，不再直接散在插件目录里。
- 回归断言保持 **678**。

## 1.15.8

- **成功页地标测不到的根因修掉了：缺相机时的兜底。** 1.15.7 已经让成功页那段测量真的跑起来了（实机日志终于出现 `success region: …`），但两个地标都报 `unmeasurable` —— `已清理分区` 的分区白框/标题/四个分区图标是**世界空间物体、上方没有 Canvas**，而 `CM_PageBase.Camera` 在页面刚接上的那几帧还是 null，`TryScreenRect` 于是直接判「测不到」，看板退回屏高比例（`boardTop=230`），正好压住那排图标。现在相机缺失会去**找**：先 `Camera.main`，再取任意启用中的相机；仍然没有才放弃。矩形是否合理的校验一条没放宽，投到画面外或大得离谱的测量照旧被丢弃。
- 回归断言保持 **678**。

## 1.15.7

- **新增：每局结算数据自动存到本地，就在本模组 DLL 的同目录。** 每张结算页（成功／失败）写一份 `CombatPanel-results-<yyyyMMdd-HHmmss>.json`：版本、写入时间、结果、每位玩家的名次／名字／分数，以及看板上**逐行原样**的内容（含 `—` 与 `~` 标记）——不重算、不筛选，免得过些时候翻文件时把「没测到」误当成 0。
- 落盘目录取**程序集真实加载路径**：手装是 `BepInEx\plugins\GTFOCombatPanel\`，r2modman 管理下就是 `…\HawChow-CombatPanel\GTFOCombatPanel\`；都读不到才退回 `BepInEx\plugins\<模组名>`。目录与文件名各打一条日志。
- 一张结算页只写一次（以页面实例为键，重复落位、重复绘制都不会重复写）；写失败只打一条 warning，面板照常绘制。
- 回归断言保持 **678**。

## 1.15.6

- **成功结算页的看板落位修好了（根因，不是调坐标）。** `NeedsAttach()` 此前只看「页面在不在」，不区分**成功页还是失败页**；而成功页的位置只在准备阶段量一次，失败页却是每帧重算的。于是「先失败、后成功」时，看板带着失败页的坐标（实机日志 `boardTop=506`）画在成功页上，正好压住 `已清理分区` 的分区图标。现在两处一起改：**换一种结果页必定重挂**（记住上次落位是为哪种页面算的），并且**成功页的可用带子每帧重新测量**——页面本身会把完成分区与按钮动画淡入，某一帧量到的位置不该被带到另一帧。
- 落位规则不变：看板顶边在完成分区实测底边之下、底边不超过送大厅按钮顶边，空间不够时缩字号，绝不向上钳制去压图标。
- 回归断言保持 **678**。

## 1.15.5

- **打容器锁、门锁这类环境物件不再算成命中（也不再算成伤害）。** 实测：沙鹰的命中已经计上了，但 `Dam_EnemyDamageBase` 的子类里有一个 `Dam_StaticEnemyDamage` / `Dam_StaticEnemyDamageLimb`——容器锁、门锁这类静态可破坏物走的正是它，于是朝锁开枪被当成了「打中敌人」。现在两条同时判定：目标若是静态破坏物类型，或它的 `EnemyAgent Owner` 为空（场景物件身后没有敌人），这条记录整条跳过，伤害与命中一起不计。
- **非模组武器的命中计算不受影响。** 补命中那条路径的前提仍是「这次子弹伤害没有被任何射击窗口覆盖」——原版武器走 `Fire` → 射线 → 命中，永远有窗口，因此一次都不会进入新分支；它们仍旧只由射线路径计数。
- 回归断言保持 **678**。

## 1.15.4

- **沙鹰的命中补齐：普通命中与弱点命中分开。** 弱点不靠算法猜——伤害钩子拿到的就是那一根肢体（`Dam_EnemyDamageLimb` 派生自 `Dam_EnemyDamageBase`），`m_type == eLimbDamageType.Weakspot` 直接给出答案，本模组的 `OnEnemyLimbHit` 早就在用同一字段。现在从伤害事件补命中时顺手读出这个标志，普通命中与弱点命中分别入账，折进下一次 accuracy 更新时各自按「这一次实际用掉的命中数」夹住，装不下的留到下一次。弱点命中永远不会多于命中数。
- 回归断言保持 **678**。

## 1.15.3

- **沙鹰的命中改从游戏自己的伤害管线取。** 实测：EWC 公开的两条流（`AccuracyAPI` 三层、`ProjectileAPI`）对这把枪都不吐命中（`shots=1 fired/0 hit, groups=1/0, full=1/0`），所以任何只依赖 EWC 公开接口的做法都拿不到它，StatDisplay 的写法同理。但它的伤害本模组测得到（`副武器·伤害` 492.0），说明伤害走的是游戏自己的 `ReceiveBulletDamage`。现在：EWC 只负责「打了几发」（它对沙鹰的发射数是准的），**命中由伤害事件补**——没有被射击窗口覆盖的那次子弹伤害记一次命中，折进下一次 accuracy 更新；每次更新按 EWC 报的发射数夹住，命中永远不会多于发射。
- **队伍同步这条通道干净关停。** 实测拿到真因：`SNet.SetupCustomData<A>` 会为 A 构造 IL2CPP 指针存储，而托管 struct 没有——抛 `TypeInitializationException: Il2CppClassPointerStore`1`。即模组自定义类型**根本不可能**从这个入口发出（要另做裸字节通道）。既然做不到，就不再注册，也不再在日志与文档里留一句打不通的承诺；日志改为一行说明，所有行仍是本机观测。
- 回归断言保持 **678**。

## 1.15.2

- **沙鹰（EWC 弹丸类武器）的命中终于有数了。** 1.15.1 只在 EWC 的 `AccuracyAPI` 上取数，实测这把枪三层全为 0（`shots=1 fired/0 hit, groups=1/0, full=1/0`），说明 EWC 的精度系统没有覆盖它——Dinorush 的 StatDisplay 对这种情况另挂了 `ProjectileAPI.OnProjectileHit`。现在照做：EWC 精度不计的命中改从它自己的弹丸回调取，折进该玩家的下一次 accuracy 更新，按「这一次更新打出的弹丸数」夹住，装不下的留到下一次。数字仍是 EWC 自己的，不编造。
- **队伍同步那条改成「失败必须说清原因」。** 实测日志里一直有 `squad sync is unavailable on this build … Exception has been thrown by the target of an invocation.`，即 1.14.0 的客机同步**从未生效**（它按设计降级，不污染任何数字）。现在注册失败会把异常类型、消息与内部异常一并打进日志；查明之前这项能力**仍是关闭的**，`DATA-AVAILABILITY.md` 第六节已按此标注。
- 回归断言保持 **678**。

## 1.15.1

- **扫描行的名字改回「扫描效率」。** 值仍然是「首次标记数/扫描次数」两个数（如 `~2/6`），只是指标名按你的要求回到「扫描效率」。
- **EWC 武器的命中按 EWC 的三层计数取最宽那一层。** 上一版只读了 `FullShots`（逐次射击）这一层，实测沙鹰仍是 `9/0/0`；EWC 实际同时维护逐弹丸 `Shots`、逐组 `Groups`、逐次 `FullShots` 三层，命中记在哪一层取决于武器怎么做的。现在按「哪层有内容就用哪层」取值，并把三层原始数字打一条日志（`[DIAG] EWC accuracy levels: …`），下次现场能直接看出沙鹰的命中落在哪一层。
- 回归断言保持 **678**。


## 1.15.0

- **名字显示全名，不再截断到六个字符。** 只剥掉游戏自己会解释的 TextMeshPro 标记（与 1.13.1 同一条规则），其余全部保留；列宽按实测文字自适应，长名字不再被切掉半截。
- **扫描行改为「首次标记数/扫描次数」。** 两个数一起显示，例如 `5/2`：斜杠前是这个人的扫描仪**首次揭示**的敌人数，斜杠后是他总共扫了几次。机器人扫描取不到归属时整对前面带 `~`；一次都没扫过仍然显示 `—`。
- **模组／EWC 武器的命中拿不到，按 Dinorush 的 StatDisplay 那套接好了。** 原版武器每次开火的射线会经过 `Weapon.CastWeaponRay`，插件从那里逐弹丸计数；EWC 接管的武器不走这条路径，于是「发射数」靠弹药消耗兜底算了出来，「命中数」却因为**没有被射线开出的弹丸编号而被整条丢弃**（`ShotSample.Contact` 开头那句 `if (_pellet < 0) return;`），表现就是截图上的 `37 / 0 / 0` → `0.0%`。现在照 StatDisplay 的三步走：① 运行时用插件列表字符串判断 EWC 在不在（`IL2CPPChainloader.Instance.Plugins.ContainsKey("Dinorush.ExtraWeaponCustomization")`）；② 用 `weapon.GetComponent<CustomWeaponComponent>() != null` 认出 EWC 武器，本模组**主动不碰**它们（`BeginFire` 直接返回），避免和 EWC 自己重复计数；③ 订阅 EWC 自己的 `EWC.API.Accuracy.AccuracyAPI.OnAccuracyUpdate`，它每次射击直接给出「打了几发弹丸 / 命中几个肢体 / 其中几个弱点」（`WeaponDelta.FullShots`），落到和原版武器同一行，**精确、不带 `~`**。所有碰 EWC 类型的成员都标了 `[MethodImpl(NoInlining)]`：EWC 不在时它的程序集根本不会被解析，`EwcBridge.cs` 整个用 `EWC_PRESENT` 编译掉，行为与之前完全一致。运行时依赖仍只有 BepInEx。
- 兜底仍然保留：万一某把枪的射线本机看不到而 EWC 又没接管它，改由**射击窗口**补开第一个弹丸（窗口本身证明这一枪打了），这一行会带 `~`，不伪装成实测值。
- `[DIAG] Fire captured` 现在带上武器名、武器类、射线数、命中数、弹药消耗，以及是否走了兜底估算，下次现场一眼可定位。
- 回归断言 665 → **678**。


## 1.14.0

- **客机现在能看到全队的真实数据——前提是两端都装本模组。** 主机是唯一同时看得到每个人开火、背包与伤害的机器，它把每位玩家的一份快照发给回应过握手的对端；客机照旧精确测自己那一行，并用主机发来的数字填满其余各行。**只让主机单方面装模组做不到**：快照是模组自定义数据，没装模组的一端既没有解析器也注册不了这个类型，收不到、也解不开。
- **握手由主机发起、客机只应答。** 主机每 2 秒给尚未表态的槽位各发一次 `Hello`（带协议版本号），应答过的对端才进入收件名单，那之后不会再被打扰。客机从不主动开口，所以**主机没装模组时整个大厅一条包都不会发**，客机行为与 1.13.1 完全一致。
- **本机那一行永远不被覆盖。** 收到关于自己的快照直接丢弃：这一行在本机是实测，在主机只是估算。
- **快照是覆盖写入，不是累加。** 主机那一份代表"这名玩家此刻的全部事实"，累加会把两台机器都看到的伤害算两遍。未观测的项照旧落到 `—`，不会因为同步反而变成 0。
- **全队伤害占比在客机上从 `—` 变成真实百分比**：主机把全队总伤害随快照一起发出，客机不必再用自己那一列去除以全队。
- 快照还带主/副武器的 archetype id 与名称（工具、近战两格只带名称），所以客机结算看板上的装备名与主机一致，而不是退化成 `GearStandard` 这类槽位名。
- **降级与自检。** 自定义数据类型注册失败、或对端协议版本对不上时，只打一条 warning 并退回 1.13.1 的行为；首次握手、首个对端、首次收发各有一条 `[DIAG] squad sync …` 日志，实机一眼可判。
- 回归断言 633 → **665**（新增 32 条：快照编解码、覆盖式写入不累加、未观测不留 0、本机行拒收、UTF-8 名称跨包与码点边界截断、capture→apply→capture 往返一致）。


## 1.13.1

- **名字里的富文本标记不再被当成名字打印。** 玩家名允许带 TextMeshPro 标记，实机日志里就有一个 `<#fa0>"<#0ff>A1pha<#f00>"`。表格绘制时 `richText` 是关的，于是「取前六个文本元素」的截断把标记切出一截前缀，结算看板第三名此前显示的就是裸的 `<#fa0>`。现在先剥掉可识别的标记（`<#rrggbb>`、`<color=…>`／`</color>`、`<b>`、`<i>`、`<size=…>`、`<sprite …>`、`<br>` 等），再按可见文本元素截到六个：同一个名字现在是 `"A1pha`。不成对的 `<` 仍按字面保留；名字全部由标记组成时输出空串，不会再让 `SubstringByTextElements` 抛异常。
- 回归断言 627 → **633**（新增六条名字用例，覆盖实机那名玩家、成对颜色标记、`<br>` 不能拆行、只由标记组成的名字、字面尖括号与未识别标记）。


## 1.13.0

- **结算看板的安全区域改成量出来的。** 成功页不再用「屏高 × 0.16」这个固定分数，而是读页面自己的两块地标：**完成分区**（分区底板、分区标题、四个分区图示）与**送回大厅按钮**，看板落在两者之间那条带里。两侧都读不到时才退回原来的分数，并且仍然只让开页面顶部，不会盖在量出来的元素上。字号与行距随这条带的高度缩放（渲染网格最多缩到 0.45 倍），因此 1920×1440、2560×1440、1280×720 下四个人的数据都在，不靠裁掉第三/第四名来腾地方。日志 `[DIAG] success region: completion=…, lobbyButton=…, boardTop=…, room=…`。
- **战斗数据按真正开火的那把枪归属。** 游戏给每一条子弹与近战伤害记录都带上开火武器的 `gearCategoryId`，插件现在用它把伤害、命中与友伤落到那把枪上，而不是落到「此刻手里拿着什么」。改枪之后才落地的穿透弹与延迟弹丸因此不会再算到新换的武器上。副作用是命中率与友伤也一起归位，因为三者走同一条伤害入口。
- **模组武器（含 EWC）不依赖任何第三方模组即可归属。** EWC（`Dinorush.ExtraWeaponCustomization`）的武器可能不暴露自己的 archetype id，插件改为用 `ItemEquippable.GearCategoryData.persistentID` 与伤害记录里的 gear category 配对；一个 category 在本机对应多把枪时**不做猜测**，退回「手里拿的那把」。这样运行时依赖仍然只有 BepInEx 本体。原先预留的 `PelletHitLedger` 已删除：它需要一个原版数据里不存在的「子发射稳定 ID」，在只依赖 BepInEx 的前提下没有任何数据能喂给它。
- **评分重新校准。** 救援队友权重 1.5 → 1.0，弱点率 0.7 → 0.55，命中率 0.6 → 0.35，其余不变，顺序与团队贡献优先的设计不变。仅救援 1 人的玩家因此不会再拿到最高名次，也拿不到「妙手回春」；救援次数达到门限（默认 2）后照常计分并在数据行里如实显示。
- **命中率带上置信度。** 命中率按「已发射弹丸数 ÷ 20」折算，最多 1 倍：3 发全中不再压过 100 发 80%。同一发射量下命中率越高得分仍不倒退（50%、60%、70%、80%、90% 五个档位已断言单调）。
- **旧默认权重自动迁移。** 配置文件里还留着 1.12.1 那组默认值（`1.1,1.2,0.9,1.5,0.7,0.6,1.4,1.3,0.9,0.7`）时，加载时自动换成新的默认值并打一条日志；任何手改过的权重原样保留。
- **扫描行改名「首次标记」并按次数计分。** 结算看板该行此前显示「首次揭示数 ÷ 扫描次数」的比值，现在直接显示**首次揭示的敌人数**（尝试次数只作展示），因为它比比值更直白也更难被重复扫描稀释。
- **未观察到的数据不再显示成 0。** 命中率、弱点率、伤害、友伤、弹丸数在没有任何观测时一律显示 `—`；只有「确实观测到、结果就是 0」才显示 `0`。新增 `DATA-AVAILABILITY.md`，逐项写明姓名、装备、命中率、敌人伤害、友伤、队友统计与全队伤害占比在「主机装了本模组 / 主机没装」两种情况下的来源、准确度与限制。
- Thunderstore 图标换成 1254×1254 粉色角色图。
- 回归断言 454 → **627**，Release 构建 0 警告 0 错误。仍未实战验证：成功页安全区域在真实转场中的落点、EWC 武器在实战中的 gear category 归属、客机面板内容、失败页淡入透明度同步。


## 1.12.1

- **机器人扫描的归属改成精确取值。** 1.12.0 靠“标记中心一米内唯一携带追踪仪的机器人”猜归属，而四人局里三个机器人都带追踪仪，实机必然失败（现场日志：`bot tag observed but no unique nearby bot bio-tracker owner resolved`，四人扫描效率全为 `—`）。反编译确认 `EnemyScanner.BotTag` 是静态方法，全游戏只有**一个**调用者：机器人自己的 `Player.PlayerBotActionUseEnemyScanner.UpdateStateScan()` 实例方法，它的 `m_agent` 就是该机器人。插件现在钩这个实例方法，在它执行期间发布 owner、返回即清除，静态 `BotTag` 因此拿到确定的那一个机器人；只有读不到时才退回原来的近距离规则。未归属时的诊断也会打印候选数与最近距离，成功时打印归属来源。
- **失败页的“单人存活”不再偷偷计分。** 这一行在失败页显示 `—`，但评分此前仍把它的权重算进总分；现在失败页显示与评分一起关闭，口径一致，不会再出现“看不见的加分”。
- **补给链路补上可观测性。** 弹药给予、弹药包回调、回血三条入口首次触发各打一条日志；此前一次补给都没发生时日志完全安静，无法区分“没人补给”和“读不到数值被跳过”。
- 454 条回归断言通过，Release 构建 0 警告 0 错误。真实对局仍需确认新的归属钩子在实战中命中（日志 `[DIAG] bot scan action observed` 与 `[DIAG] bot tag credited`）。

## 1.12.0

- **扫描效率只认首次揭示。** 全队共享一本揭示账本：某个敌人第一次被扫上生物追踪仪的那一次扫描记一分，之后无论谁再扫同一个敌人，都只增加扫描次数、不再增加揭示数。重复标记别人的目标不再刷分，扫描效率（首次揭示数 ÷ 扫描次数）只会被重复扫描拉低。
- **近战推击打断攻击计入硬直。** 推击在游戏里走自己的链路（`PushDamage` → `ReceivePushDamage`），从不经过近战伤害入口，也不造成伤害，因此钩子改在推击入口、在游戏处理推击之前读取敌人当时的攻击状态：打断正在进行或正在蓄力的攻击就记一次硬直。同一帧内同一敌人的重复上报只记一次；致死反应与无攻击状态一律不计。
- **硬直按“被打断的攻击次数”计**，不再累加伤害数值。
- **补给效率改成每次使用的有效量 ÷ 应有量的平均值**，记在使用者名下；自己给自己用同样计入，拾取不计入分母，溢出按 0% 计。旧的“相对量 ÷ 拾取容量”口径及其补丁已删除。
- **零分玩家固定“战地游客”。** 显示分正好是 0.0 的玩家拿到固定且可重复的负面称号；其他并列玩家仍然各自拿到不同称号。
- **击杀分类用完整文字标注**：小怪击杀 / 精英击杀 / BOSS击杀，结算表始终分行。
- **默认权重改为团队贡献优先**：总伤害 1.1、硬直 1.2、击杀 0.9、救援队友 1.5、弱点命中率 0.7、命中率 0.6、补给效率 1.4、扫描效率 1.3、单人存活 0.9、阵亡 0.7（越少越好）。配置文件里仍可改成任意十个非负数。
- **结算底板更透明**：底板 0.50、行带 0.020、分隔线 0.12。
- 450 条回归断言通过，Release 构建 0 警告 0 错误。真实对局的验证边界：推击在实战中的实际触发、跨玩家重复标记的去重、原生字体创建、失败页淡入同步仍需进游戏确认；仓库里的模型预览不是游戏截图。
## 1.11.1

- Fix the silent failure-board suppression path: unavailable title geometry now uses a logged screen-space fallback rather than skipping the entire table. Known hidden headings still wait, and known heading alpha is preserved. Add stage diagnostics for visibility, measurement, missing counters and fit failures.
- Bypass `Font.CreateDynamicFontFromOSFont`, whose current local Interop helper calls a missing Font constructor. Create the Font through its existing default constructor and invoke `Internal_CreateDynamicFont` directly.
- 414 regression assertions pass. Local installation hashes are checked separately from packaging. Actual rendered results and font creation remain gameplay checks.

## 1.11.0

- Reworked settlement readability: a dark plate, alternating rows, section rules, stronger names and scores, left-aligned descriptive cells and a shared right edge for numeric cells. Multi-value cells use measured ` / ` separators; the HUD retains field slots.
- Scores use podium colours only, with no gold/silver/bronze words. Kill tiers are labelled in both merged and split modes; fresh configurations default to split tiers. Existing configuration is preserved.
- Failure placement uses the menu camera and visible TextMeshPro heading. The board follows heading alpha and stays below its rendered bounds; negative offsets cannot move it over the heading. Insufficient room is not resolved by covering the heading.
- Human bio-tracker accounting uses one tag-attempt edge ledger and one enemy-instance identity set, sampled around `UpdateTagProgress` and by the frame observer. Passive radar growth is not a scan attempt.
- Bot scanning listens to `BotTag` only, not `BotGetScanResult`. It accepts only a unique nearby bot carrying a tracker and groups callback bursts using tag duration. Attribution/timing are still estimates, shown with `~`; unresolved events remain unavailable.
- 404 regression assertions pass. Actual rendered transitions and the one-human/three-bot scan case remain unverified in gameplay. Model preview is not a game screenshot.

## 1.10.0

- **The top three rows of the board are the player's place and name, the score and the sentence.** The old layout wrote the name twice: once on a rank strip and once on a row of its own. The duplicate is gone, the score moved up from the bottom of the block to the second row and the sentence to the third, so the four gear slots and the two weapons' figures follow straight after and the descriptive block is one row shorter (thirteen fixed rows, a separator and the ten metrics: 24 rows, 26 with the kill tiers split).
- **No two players wear the same sentence.** The evaluation now runs from the top of the board down: a player takes the sentence of the metric they lead the squad in outright that they contributed most to, and when somebody above them already wears it they step down their own list to the next one still free. A player who leads nothing ends up with a sentence of their own rather than sharing the neutral title with everyone else.
- **The result pages' own blocks are really gone this time.** Switching a block off is not enough: both pages animate their content in over several seconds and switch it back on as they go, which is why the artifact inventory kept coming back. The blocks are therefore also moved right out of the page, and the whole thing is re-applied on every frame the page is up rather than once per poll. `Results/ShowSuccessArtifacts`（默认 false = 隐藏）与 `Results/HideVanillaFailBlocks` still put them back, and this is the same problem and the same workaround that StatDisplay documents.
- **The failure board keeps clear of the failure message.** The message is measured through the camera that renders the page and the board is placed in whichever of the two gaps it leaves is the larger, so the board no longer lands on top of it. If it cannot be measured the board falls back to the place it has always used, and `Results/OffsetY` still nudges it either way.
- **A stagger is an interrupt and only an interrupt.** The row now needs the enemy's attack to be under way and not yet finished, and it will not count a kill: the hit reacts that report a death are rejected, and so is any blow that takes the last of the enemy's health, because a corpse that stopped moving was not interrupted. When the enemy's own attack state cannot be read the row stays empty rather than counting hits that interrupted nothing. `Scoring/StaggerNeedsAttack` turns the attack requirement back off.
## 1.9.0

- **The result screens are now an influence board.** Success and failure no longer print the plain per-weapon table; they print a board whose rows are fields and whose columns are players, ordered by a composite score out of 100. Each player gets a place, a sentence, the four gear slots they carried, both weapons' damage/share, hit/weakspot and pellet figures, and then the ten scored metrics below.
- **Ten metrics, each with a weight and a gold star.** Total damage (1.5), stagger interrupts (1.4), kills (1.3), revives (1.2), weakspot rate (1.0), hit rate (0.8), supply efficiency (0.9), scan efficiency (0.9), lone survivor (1.0) and deaths (0.8, reverse). The squad's outright leader in a metric has its cell tinted gold; a tie tints nobody. The score row is coloured gold, silver, copper, then plain white.
- **Kills are graded from the enemy's own maximum health**, so a modded enemy is graded by what it is rather than by a name list that would go stale: below `Scoring/KillMinorMaxHealth` (120) is a minor kill, below `Scoring/KillEliteMaxHealth` (1000) an elite, above it a boss. A boss is worth three minors and an elite two, and `Scoring/SplitKillTiers` prints the three classes on their own rows instead of one merged row.
- **A sentence per player**, taken from the metric they contributed most to and only when they lead the squad in it outright; a player who leads nothing is the squad's `团队中坚`.
- **Support kits are lifted.** A player whose class tool matches `Scoring/SupportToolNames` (bio tracker, glue gun, lock melter, C-foam) gets `Scoring/SupportRoleFirepowerBonus` (1.25) on the fighting metrics and `Scoring/SupportRoleUtilityBonus` (1.25) on the support ones. Set either to 1.0 to switch it off.
- **The vanilla blocks the board replaces are switched off in place**, one at a time and each in its own guard: the artifact inventory, the four prisoner report cards and the checkpoint usage on the success screen, the artifact inventory on the failure screen. `Results/ShowSuccessArtifacts`（默认 false = 隐藏）与 `Results/HideVanillaFailBlocks` put them back without recompiling. Nothing inside a report card is ever repositioned and no vanilla text object is ever written into.
- **Six new Harmony hooks** feed the metrics the damage and accuracy patches could not already reach: `ProcessReceivedDamage` (stagger), `InstantDead` and `EnemyAgent.OnDead` (kills, both feeding one de-duplicated ledger), `Interact_Revive.TriggerInteractionAction` (revives), `PlayerAgent.GiveAmmoRel` (supply handed out) and `ResourcePackPickup.OnInteractionPickUp` (the capacity it came from). Who is still standing, who is the last one standing and what the bio tracker has revealed are polled from the per-frame state observer instead, so no patch is spent on a signal that only ever needs comparing with its own previous value.
- Every hook logs the first time it fires, and every one that cannot read its input logs once and skips itself rather than throwing into the game. The two assumptions that only the real game can settle -- whether the bio tracker's blip array clears between sweeps, and whether a resource pack's capacity is the same unit as the relative amounts a player hands out -- are guarded so a build where they do not hold shows a dash instead of a wrong score.
- The in-level HUD table is untouched: same columns, same headers, same share arithmetic, same name colouring and right alignment, same `—` placeholder, same position under the flashlight icon.

## 1.8.2

- **Narrower columns.** The space surrounding every cell was cut by more than a third: the whitespace between two neighbouring columns went from 1.067em to 0.625em. That figure is the padding of both cells plus the gap between them (`PaddingFactor` 0.45 → 0.27, `GapFactor` 0.55 → 0.30), and the whole table follows it at every density.
- The four column floors whose header is the widest thing they hold came down with it: friendly fire 3.4 → 2.65em, accuracy and weakspot 4.6 → 3.05em, damage/share 7.0 → 6.0em (3.5 → 3.0em per field). A floor is only a minimum — a column still grows to whatever its widest cell measures — so a lower floor can never truncate a value; it only stops the column reserving room its header never used.
- The `总弹丸 命中 弱点` triplet keeps the spacing that was called "about right": its field gap factor is still exactly `0.60` and its column floor is still `6.8em`, and the suite asserts both of its distances are identical to 1.8.1 to within 1e-4.
- Nothing else moved: same columns, same headers, same share arithmetic, same name colouring and right alignment, same results layout, same `—` placeholder.
## 1.8.1

- **No slashes in the panel.** The merged columns are no longer held apart by a drawn `/`: the fields sit side by side, separated by a gap that is a fraction of the glyph size. `伤害/占比` and `总弹丸/命中/弱点` became `伤害 占比` and `总弹丸 命中 弱点`, so both merged columns are narrower and the table takes less of the screen.
- The result blocks follow, so no `/` appears anywhere in the mod's text any more.

## 1.8.0

- Merged columns are split into measured field slots: the damage/share figures and the pellet triple each keep one right edge per field from row to row, instead of the whole group being pushed around by the widest value.
- The pellet header qualifies "total" only, which is two characters narrower.

## 1.7.0

- Placed against the native flashlight icon: its screen rectangle is measured through the HUD's own camera until it holds still, then frozen, so the table sits next to the gear column and stops swaying with the view.

## 1.6.0

- Default position moved to a right-edge anchored fraction.

## 1.5.0

- The table moved to IMGUI in window pixels: rendered, immobile, and always fully on screen.

## 1.4.0

- Placed inside the game's HUD hierarchy. It rendered, but inherited the HUD's parallax and its right-hand columns were clipped by the screen edge.

## 1.3.0

- A canvas owned by the mod. It was sized correctly but produced no pixels at all.

## 1.2.0

- Parented into the HUD hierarchy and positioned through a screen-to-local conversion on the wrong camera. The table was displaced and skewed.

## 1.1.0

- Standalone build: player damage, friendly-fire loss, projectile accuracy and weakspot rate, per player and per weapon.

## 1.0.0

- Initial local build.
