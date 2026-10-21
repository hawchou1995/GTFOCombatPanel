using System;
using System.IO;
using System.Net;
using System.Text;
using HawChow.GTFOCombatPanel;

internal static class BoardPreview
{
    internal static ImpactBoardLayout Sample()
    {
        var inputs = new ImpactInput[4];
        // Two shooters, one support player who carries the bio tracker and hands out supplies, and
        // two players who loaded in and contributed nothing: the last two are the zero-score case,
        // which is what the repeatable sentence is checked against.
        string[] names = { "前锋", "后勤", "侦察", "新兵" };
        for (int i = 0; i < inputs.Length; i++)
        {
            var primary = new PlayerCombatStats();
            primary.AddEnemyDamage(2400 - i * 300); primary.AddAccuracy(180 + i * 25, 130 - i * 8, 45 - i * 3);
            var secondary = new PlayerCombatStats();
            secondary.AddEnemyDamage(1600 - i * 180); secondary.AddAccuracy(96 + i * 8, 75 - i * 5, 24 - i * 2);
            var input = new ImpactInput { Name = names[i], EnemyDamage = primary.EnemyDamage + secondary.EnemyDamage,
                Primary = primary, Secondary = secondary, PrimaryName = "突击步枪", SecondaryName = "霰弹枪", MeleeName = "大锤",
                ToolName = i == 1 ? "生物追踪仪" : "自动炮塔", SupportRole = i == 1,
                ShotsFired = primary.ShotsFired + secondary.ShotsFired, Hits = primary.Hits + secondary.Hits,
                WeakspotHits = primary.WeakspotHits + secondary.WeakspotHits };
            input.Counters.MinorKills = 45 - i * 5;
            input.Counters.EliteKills = 6 - i;
            input.Counters.BossKills = i == 0 ? 1 : 0;
            // Interrupts, including the melee pushes that break an attack without dealing damage.
            for (int interrupted = 0; interrupted < 8 - i; interrupted++) input.Counters.CreditInterrupt();
            for (int revive = 0; revive < (i == 1 ? 3 : 1); revive++) input.Counters.CreditRevive();
            // Two uses of a pack that both landed in full, so the average is exactly 100%.
            if (i == 0) { input.Counters.RecordSupply(0.2, 0.2); input.Counters.RecordSupply(0.35, 0.35); }
            else if (i == 1) input.Counters.RecordSupply(75, 100);
            // Two sweeps that revealed six enemies: the efficiency is enemies per sweep, and a
            // repeat sweep of an enemy somebody already revealed adds nothing at all.
            if (i == 1) { input.Counters.CreditScan(6); input.Counters.CreditScan(0); }
            input.Counters.Downs = i;
            if (i >= 2)
            {
                input.EnemyDamage = 0; input.ShotsFired = input.Hits = input.WeakspotHits = 0;
                input.Primary = new PlayerCombatStats(); input.Secondary = new PlayerCombatStats();
                input.Counters.Reset(); input.Counters.Downs = 1;
            }
            inputs[i] = input;
        }
        return ImpactBoard.Build(ImpactScoring.Evaluate(inputs, ImpactScoring.DefaultWeights, 1.25, 1.25), true, false);
    }

    internal static void Write(string path, ImpactBoardLayout board)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>CombatPanel 结算设计预览</title><style>");
        html.Append("*{box-sizing:border-box}body{margin:0;background-color:#17191b;background-image:repeating-linear-gradient(35deg,transparent 0,transparent 100px,rgba(130,24,18,.48) 102px,rgba(130,24,18,.48) 114px,transparent 116px,transparent 220px);color:#eceff1;font-family:'Microsoft YaHei UI','Microsoft YaHei',sans-serif}main{width:min(90vw,1380px);margin:40px auto}h1{color:#da635b;font-size:38px;font-weight:500;letter-spacing:.12em;text-align:center;margin:0 0 16px}p{font-size:12px;color:#a0a9af;margin:0 0 20px;text-align:center}table{width:100%;border-collapse:collapse;background:rgba(9,12,14,.5);border:1px solid rgba(255,255,255,.12);table-layout:fixed}td{padding:5px 12px;font-size:16px;text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums}td:first-child{width:24%;text-align:left;color:#b4bcc3}tr:nth-child(even){background:rgba(255,255,255,.02)}tr.section{border-top:1px solid rgba(255,255,255,.12)}tr.desc td{text-align:left}tr.emphasis td{font-weight:700}.gold{color:#ffd600}.silver{color:#bfbfbf}.copper{color:#cc8033}.player{color:#7fcee1}footer{margin-top:14px;font-size:12px;color:#aab3ba;line-height:1.8} @media(max-width:900px){td{padding:4px 6px;font-size:11px}h1{font-size:26px}main{width:96vw;margin:24px auto}} </style><main><h1>任务失败</h1><p>CombatPanel · 模型生成的设计预览 · 示例数据，非游戏截图</p><table>");
        for (int row = 0; row < board.Rows; row++)
        {
            var kind = board.Kinds[row];
            bool desc = kind == ImpactRowKind.PlayerName || kind == ImpactRowKind.Rating || kind >= ImpactRowKind.PrimaryWeapon && kind <= ImpactRowKind.ToolWeapon;
            bool section = kind == ImpactRowKind.Separator || kind == ImpactRowKind.PrimaryWeapon || kind == ImpactRowKind.PrimaryDamage || kind == ImpactRowKind.SecondaryDamage;
            bool emphasis = kind == ImpactRowKind.PlayerName || kind == ImpactRowKind.Score || kind == ImpactRowKind.Separator;
            html.Append("<tr class=\"").Append(desc ? "desc " : "").Append(section ? "section " : "").Append(emphasis ? "emphasis" : "").Append("\">");
            for (int column = 0; column < board.ColumnCount; column++)
            {
                string tone = board.Tone(row, column) switch { ImpactTone.Gold => "gold", ImpactTone.Silver => "silver", ImpactTone.Copper => "copper", ImpactTone.PlayerColor => "player", _ => "" };
                html.Append("<td class=\"").Append(tone).Append("\">").Append(WebUtility.HtmlEncode(board.Joined(row, column))).Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</table><footer>评分只用颜色区分名次。伤害 / 全队占比；命中率 / 弱点率；弹丸总数 / 命中 / 弱点。击杀分类独立标注（小怪击杀 / 精英击杀 / BOSS击杀）。<br>硬直按「被打断的攻击次数」计，近战推击打断正在进行的攻击同样计入；扫描效率按首次揭示的敌人计，同一敌人被任何人重复标记都不再计分。<br>补给效率按每次使用的有效量 / 应有量取平均，拾取不计入分母，溢出按 0% 计。<br>游戏内使用 Unity IMGUI 字体测量、标题相机投影与可见度；此 HTML 仅检查信息层级，不验证淡入同步或真实扫描钩子。</footer></main></html>");
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllText(fullPath, html.ToString(), new UTF8Encoding(false));
        Console.WriteLine("PREVIEW=" + fullPath);
    }
}
