using AutoDuty.IPC;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Text;

namespace AutoDuty.Helpers
{
    // 本檔假設與契約：
    // - 分隊員＝NpcPartyMember 且身上有 GcArmyCaptureTactics 的作戰狀態（支援者／親信戰友沒有）；表讀不到＝功能停用。
    // - 只在框架執行緒用（Framework_Update 與 TaskManager 的任務都是）。不跨幀保存 IGameObject：只存 id，每次重查。
    // - 首領戰歸 BMR 號令元件：BMR 有啟用中的模組或 AutoDuty 已鎖定首領就不先發，等待中遇到就放手。
    // - 只下「號令：戰鬥開始」(BgcArmyAction 1)；原生呼叫擲例外＝這次不下令。

    /// <summary>
    /// 冒險者小隊小怪路段的「號令先發」：要對新的敵人設玩家目標前先對它下號令，等分隊（優先看坦）換過去才設目標，
    /// 讓輪替插件（手動目標模式）晚一步出手。會設玩家目標的地方先問 <see cref="ShouldHold"/>，回 true 就這一次不要設。
    /// </summary>
    internal static class SquadronOpener
    {
        private const uint CommandEngage = 1;
        private const long TickMs = 100;
        private const long VerifyMs = 3000;
        private const ulong InvalidTarget = 0xE0000000;

        private sealed class Verify
        {
            internal ulong  Target;
            internal string Label = "";
            internal string Path = "";
            internal string Outcome = "";
            internal long   StartMs, ReleaseMs;
            internal long?  BIssuedMs, BFollowedMs;
            internal int    SeqDelta;
        }

        private readonly struct Squad(int count, int tanksAlive, bool followed, string text)
        {
            internal readonly int    Count      = count;
            internal readonly int    TanksAlive = tanksAlive;
            internal readonly bool   Followed   = followed;
            internal readonly string Text       = text;
        }

        private static readonly SquadronOpenerPlanner Planner = new();
        private static readonly List<Verify> Verifies = [];
        private static readonly HashSet<string> LoggedThisZone = [];

        private static HashSet<uint>? _tactics;
        private static bool _tablesFailed;
        private static ushort _zone;
        private static bool _zoneIsSquadron;
        private static long _nextTickMs;
        private static string _pendingLabel = "";
        private static ushort _pendingSeqStart;

        /// <summary>要把 <paramref name="candidate"/> 設成玩家目標之前呼叫；回 true＝先不要設（號令先發中）。</summary>
        internal static bool ShouldHold(IGameObject? candidate)
        {
            try
            {
                return ShouldHoldCore(candidate);
            }
            catch (Exception ex)
            {
                Once("holdex", $"判斷失敗，本次不先發：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool ShouldHoldCore(IGameObject? candidate)
        {
            if (!Plugin.Configuration.SquadronOpener || candidate == null || _tablesFailed)
                return false;
            if (Planner.Pending != 0)
                return true;
            if (Planner.GaveUp || Svc.Targets.Target != null || !Player.Available || Planner.Handled(candidate.GameObjectId))
                return false;
            SyncZone();
            if (!LoadTables())
                return false;

            var squad = ScanSquad(candidate.GameObjectId, false);
            if (squad.Count == 0)
                return false;
            _zoneIsSquadron = true;
            if (PlayerHelper.GetJob().GetCombatRole() == CombatRole.Tank)
            {
                Once("tank", "玩家是坦克：不先發");
                return false;
            }
            if (BossActive())
                return false;
            if (FirstTime("enabled"))
                Svc.Log.Information($"[分隊先發] 已啟用：領地={_zone}，分隊員 {squad.Count} 人（坦 {squad.TanksAlive}），等待上限 {MaxWaitMs() / 1000.0:f1} 秒，"
                                  + $"AutoDuty 模式={Plugin.Configuration.DutyModeEnum}，BMR={(BossModReborn_IPCSubscriber.IsEnabled ? "有" : "沒有")}，號令對應技能 id={SpellIdForCommand()}");

            if (!Planner.TryStart(candidate.GameObjectId, Environment.TickCount64, squad.Followed))
                return false;
            _pendingLabel = $"{candidate.Name.TextValue}({candidate.BaseId:X}#{candidate.GameObjectId & 0xFFFF:X})";
            _pendingSeqStart = ActionSequence();
            _nextTickMs = 0;
            return true;
        }

        /// <summary>每幀呼叫（內部節流）：推進等待中的先發、驗收、換領地時印彙總。</summary>
        internal static void Tick()
        {
            try
            {
                TickCore();
            }
            catch (Exception ex)
            {
                Once("tickex", $"更新失敗，放棄這次先發：{ex.GetType().Name}: {ex.Message}");
                Planner.ResetZone();
                Verifies.Clear();
            }
        }

        private static void TickCore()
        {
            if (Planner.Pending == 0 && Verifies.Count == 0 && Svc.ClientState.TerritoryType == _zone)
                return;
            var now = Environment.TickCount64;
            if (now < _nextTickMs)
                return;
            _nextTickMs = now + TickMs;
            SyncZone();

            for (var i = Verifies.Count - 1; i >= 0; --i)
                if (UpdateVerify(Verifies[i], now))
                    Verifies.RemoveAt(i);

            var id = Planner.Pending;
            if (id == 0)
                return;

            var obj = Svc.Objects.SearchById(id);
            var valid = obj is { IsDead: false, IsTargetable: true };
            var squad = valid ? ScanSquad(id, true) : default;
            var player = Player.Available ? Player.Object : null;
            var hp = player is { MaxHp: > 0 } ? (float)player.CurrentHp / player.MaxHp : 1f;
            uint status = uint.MaxValue;
            var busy = true;
            if (valid)
                status = CommandStatus(id, out busy);
            var inputs = new SquadronOpenerPlanner.Inputs(valid, Svc.Targets.Target != null, BossActive(), Plugin.Stage is Stage.Stopped or Stage.Paused or Stage.Dead,
                                                          squad.Followed, hp, status, busy, MaxWaitMs());
            var aBefore = Planner.AIssuedMs;
            var step = Planner.Update(now, inputs, out var reason);

            switch (step)
            {
                case SquadronOpenerPlanner.Step.IssueA:
                    var ok = UseCommand(id);
                    Planner.NoteAIssued(now);
                    if (!ok)
                        Once("afalse", "路徑 A 的 UseAction 回傳 false（遊戲沒收下這次號令）");
                    return;
                case SquadronOpenerPlanner.Step.Wait:
                    return;
            }

            var v = new Verify
            {
                Target    = id,
                Label     = _pendingLabel,
                StartMs   = Planner.StartMs,
                ReleaseMs = now,
                SeqDelta  = (ushort)(ActionSequence() - _pendingSeqStart),
                Path      = aBefore != null ? "A" : "",
            };
            var waited = (now - Planner.StartMs) / 1000.0;
            var outcome = reason switch
            {
                SquadronOpenerPlanner.Reason.Followed               => $"路徑 A 下令 {(now - aBefore!.Value) / 1000.0:f1} 秒後分隊跟上",
                SquadronOpenerPlanner.Reason.FollowedWithoutCommand => "還沒下令分隊就自己跟上",
                SquadronOpenerPlanner.Reason.Timeout                => $"等 {waited:f1} 秒沒人跟上",
                SquadronOpenerPlanner.Reason.LowHp                  => $"玩家血量 {hp:P0} 低於 {SquadronOpenerPlanner.LowHpRatio:P0}，提前出手",
                SquadronOpenerPlanner.Reason.CommandUnavailable     => $"號令一直不可用（GetActionStatus={Planner.LastStatus}）",
                SquadronOpenerPlanner.Reason.BOnly                  => "本場路徑 A 已連續無效，直接走路徑 B",
                SquadronOpenerPlanner.Reason.TargetGone             => "目標已死亡或消失",
                SquadronOpenerPlanner.Reason.TargetSetElsewhere      => $"玩家目標被別處設成「{TargetLabel(Svc.Targets.Target?.GameObjectId ?? 0)}」（手動或其他插件）",
                SquadronOpenerPlanner.Reason.BossStarted            => "首領戰開始，交給 BMR",
                SquadronOpenerPlanner.Reason.Stopped                => "AutoDuty 停止或暫停",
                _                                                  => reason.ToString(),
            };

            if (step == SquadronOpenerPlanner.Step.Abandon)
            {
                Svc.Log.Information($"[分隊先發] 「{v.Label}」：等了 {waited:f1} 秒後放棄（{outcome}），沒有設玩家目標；等待中玩家動作序號 +{v.SeqDelta}");
                return;
            }

            // 放手：設玩家目標；路徑 B 在同一幀接著下令（遊戲若只認玩家目前目標，這次會生效）。
            if (obj != null)
                Svc.Targets.Target = obj;
            if (step == SquadronOpenerPlanner.Step.IssueBAndRelease)
            {
                var ok = UseCommand(id);
                Planner.NoteBIssued();
                v.Path += "B";
                v.BIssuedMs = now;
                if (!ok)
                    outcome += "；路徑 B 的 UseAction 回傳 false";
            }
            else if (reason == SquadronOpenerPlanner.Reason.Timeout)
                outcome += $"；路徑 B 號令不可用（GetActionStatus={status}，忙碌={busy}）";
            v.Outcome = $"{outcome}；放手時分隊目標＝{squad.Text}";
            Verifies.Add(v);
        }

        // 回 true＝驗收完成（已印 log）。
        private static bool UpdateVerify(Verify v, long now)
        {
            var obj = Svc.Objects.SearchById(v.Target);
            var gone = obj is not { IsDead: false, IsTargetable: true };
            var squad = gone ? default : ScanSquad(v.Target, true);
            if (v.BIssuedMs != null && v.BFollowedMs == null && squad.Followed)
                v.BFollowedMs = now;
            if (!gone && now - v.ReleaseMs < VerifyMs)
                return false;

            var path = v.Path switch { "A" => "路徑 A", "AB" => "路徑 A→B", "B" => "路徑 B", _ => "沒有下令" };
            var sb = new StringBuilder($"[分隊先發] 「{v.Label}」：{path}，{v.Outcome}");
            if (v.BIssuedMs is { } b)
            {
                double? s = v.BFollowedMs is { } f ? (f - b) / 1000.0 : null;
                Planner.NoteBResult(s);
                sb.Append(s is { } x ? $"；路徑 B 下令 {x:f1} 秒後分隊跟上" : "；路徑 B 下令後 3 秒內分隊沒跟上");
            }
            if (gone)
                sb.Append($"；放手 {(now - v.ReleaseMs) / 1000.0:f1} 秒後目標已死亡或消失");
            else
                sb.Append($"；放手 3 秒後分隊目標＝{squad.Text}，敵人目標＝{TargetLabel(obj!.TargetObjectId)}");
            sb.Append($"；等待 {(v.ReleaseMs - v.StartMs) / 1000.0:f1} 秒，等待中玩家動作序號 +{v.SeqDelta}");
            if (Planner.GaveUp && FirstTime("gaveup"))
                sb.Append($"；路徑 A、B 都連續 {SquadronOpenerPlanner.FailStreakLimit} 次沒效果，本場停用先發");
            Svc.Log.Information(sb.ToString());
            return true;
        }

        private static void SyncZone()
        {
            var zone = Svc.ClientState.TerritoryType;
            if (zone == _zone)
                return;
            if (_zoneIsSquadron && Planner.Started + Planner.AlreadyOn > 0)
            {
                var p = Planner;
                var aAvg = p.AFollowed > 0 ? $"{p.AFollowSecondsSum / p.AFollowed:f1} 秒" : "-";
                var bAvg = p.BFollowed > 0 ? $"{p.BFollowSecondsSum / p.BFollowed:f1} 秒" : "-";
                Svc.Log.Information($"[分隊先發] 本場彙總（領地 {_zone}）：先發 {p.Started} 次、分隊已在打而略過 {p.AlreadyOn} 次；路徑 A 下令 {p.ACommands} 次、跟上 {p.AFollowed} 次（平均 {aAvg}）；"
                                  + $"路徑 B 下令 {p.BCommands} 次、跟上 {p.BFollowed} 次（平均 {bAvg}）；逾時 {p.Timeouts}、血量提前出手 {p.LowHp}、號令不可用 {p.Unavailable}、放棄 {p.Abandoned}；"
                                  + $"本場改走路徑 B={(p.BOnly ? "是" : "否")}，停用={(p.GaveUp ? "是" : "否")}");
            }
            _zone = zone;
            _zoneIsSquadron = false;
            Planner.ResetZone();
            Verifies.Clear();
            LoggedThisZone.Clear();
        }

        private static bool LoadTables()
        {
            if (_tactics != null)
                return true;
            var tactics = new HashSet<uint>();
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.GcArmyCaptureTactics>();
            if (sheet != null)
                foreach (var row in sheet)
                    if (row.Name.RowId != 0)
                        tactics.Add(row.Name.RowId);
            if (tactics.Count == 0)
            {
                _tablesFailed = true;
                Svc.Log.Information("[分隊先發] 停用：讀不到 GcArmyCaptureTactics 表（分不出分隊員）");
                return false;
            }
            _tactics = tactics;
            return true;
        }

        // 坦還活著＝看坦有沒有打這隻；沒有活著的坦＝任一活著的分隊員。
        private static Squad ScanSquad(ulong target, bool withText)
        {
            int count = 0, alive = 0, tanks = 0;
            bool tankOn = false, anyOn = false;
            var sb = withText ? new StringBuilder() : null;
            foreach (var o in Svc.Objects)
            {
                if (o is not IBattleNpc { BattleNpcKind: BattleNpcSubKind.NpcPartyMember } npc || !HasTactic(npc))
                    continue;
                ++count;
                var role = npc.ClassJob.ValueNullable?.Role ?? 0;
                if (sb != null)
                {
                    if (sb.Length > 0)
                        sb.Append('/');
                    sb.Append(RoleLabel(role)).Append(':');
                }
                if (npc.IsDead)
                {
                    sb?.Append("陣亡");
                    continue;
                }
                ++alive;
                var on = npc.TargetObjectId == target;
                anyOn |= on;
                if (role == 1)
                {
                    ++tanks;
                    tankOn |= on;
                }
                sb?.Append(on ? "這隻" : TargetLabel(npc.TargetObjectId));
            }
            var followed = tanks > 0 ? tankOn : anyOn;
            return new(count, tanks, alive > 0 && followed, sb?.ToString() ?? "");
        }

        private static bool HasTactic(IBattleNpc npc)
        {
            foreach (var s in npc.StatusList)
                if (s.StatusId != 0 && _tactics!.Contains(s.StatusId))
                    return true;
            return false;
        }

        // 不印任何角色名：玩家寫「玩家」、分隊員寫職能。
        private static string TargetLabel(ulong id)
        {
            if (id is 0 or InvalidTarget)
                return "-";
            if (Player.Available && Player.Object.GameObjectId == id)
                return "玩家";
            var o = Svc.Objects.SearchById(id);
            if (o is IBattleNpc { BattleNpcKind: BattleNpcSubKind.NpcPartyMember } npc)
                return RoleLabel(npc.ClassJob.ValueNullable?.Role ?? 0);
            if (o == null)
                return $"{id:X}";
            return o.ObjectKind == ObjectKind.BattleNpc ? $"{o.Name.TextValue}({o.BaseId:X})" : o.ObjectKind.ToString();
        }

        private static string RoleLabel(byte role) => role switch
        {
            1 => "坦",
            4 => "補",
            2 => "近戰",
            3 => "遠程",
            _ => "?",
        };

        private static bool BossActive() =>
            Plugin.BossObject != null || BossModReborn_IPCSubscriber.IsEnabled && BossModReborn_IPCSubscriber.HasActiveModule();

        private static long MaxWaitMs() => (long)(Math.Clamp(Plugin.Configuration.SquadronOpenerMaxWaitSeconds, 0.5f, 6f) * 1000);

        private static unsafe uint CommandStatus(ulong target, out bool busy)
        {
            busy = true;
            try
            {
                var am = ActionManager.Instance();
                if (am == null)
                    return uint.MaxValue;
                busy = am->AnimationLock > 0 || am->ActionQueued || am->CastSpellId != 0;
                return am->GetActionStatus(ActionType.BgcArmyAction, CommandEngage, target);
            }
            catch (Exception ex)
            {
                Once("statusex", $"讀不到號令狀態：{ex.GetType().Name}: {ex.Message}");
                return uint.MaxValue;
            }
        }

        private static unsafe bool UseCommand(ulong target)
        {
            try
            {
                var am = ActionManager.Instance();
                return am != null && am->UseAction(ActionType.BgcArmyAction, CommandEngage, target);
            }
            catch (Exception ex)
            {
                Once("useex", $"下令失敗：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static unsafe ushort ActionSequence()
        {
            try
            {
                var am = ActionManager.Instance();
                return am != null ? am->LastUsedActionSequence : (ushort)0;
            }
            catch
            {
                return 0;
            }
        }

        private static string SpellIdForCommand()
        {
            try
            {
                return ActionManager.GetSpellIdForAction(ActionType.BgcArmyAction, CommandEngage).ToString();
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        private static bool FirstTime(string key) => LoggedThisZone.Add(key);

        private static void Once(string key, string msg)
        {
            if (FirstTime(key))
                Svc.Log.Information("[分隊先發] " + msg);
        }
    }
}
