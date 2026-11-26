using AutoDuty.IPC;
using Dalamud.Game.ClientState.Conditions;
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
    // - 首領戰歸 BMR 號令元件：BMR 有啟用中的模組或 AutoDuty 已鎖定首領就不先發、不拉回，等待中遇到就放手。
    // - 只下「號令：戰鬥開始」(BgcArmyAction 1)；原生呼叫擲例外＝這次不下令。
    // - Wrath 自動循環只在先發等待中暫停；每個出口都恢復，另有 Watchdog 每幀硬上限（放在 Framework_Update 最前面）。

    /// <summary>
    /// 冒險者小隊小怪路段的「號令先發」與「被圍拉回」。
    /// 先發：要對新的敵人設玩家目標前先對它下號令，等分隊（優先看坦）換過去才設目標；玩家目標已被別處設成敵人就立刻對那隻下令。
    /// 會設玩家目標的地方先問 <see cref="ShouldHold"/>，回 true 就這一次不要設。
    /// 拉回：敵人打玩家而分隊坦克沒在打它，就對它下號令（<see cref="SquadronRescuePlanner"/>）。
    /// </summary>
    internal static class SquadronOpener
    {
        private const uint CommandEngage = 1;
        private const long TickMs = 100;
        private const long VerifyMs = 3000;
        private const ulong InvalidTarget = 0xE0000000;
        private const long ClassifyMs = 1000;
        private const int ClassifyMisses = 3;
        private const int MaxAttackers = 16;

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

        private sealed class RescueVerify
        {
            internal ulong  Target, TankId;
            internal string Label = "", Path = "", TankBefore = "";
            internal long   IssuedMs;
            internal double DwellSeconds;
            internal int    Others;
            internal bool   UseOk, HpLow;
            internal long?  TankOnMs, MonsterOnTankMs;
        }

        private readonly struct Squad(int count, int tanksAlive, bool followed, string text)
        {
            internal readonly int    Count      = count;
            internal readonly int    TanksAlive = tanksAlive;
            internal readonly bool   Followed   = followed;
            internal readonly string Text       = text;
        }

        private static readonly SquadronCommandClock Clock = new();
        private static readonly SquadronOpenerPlanner Planner = new(Clock);
        private static readonly SquadronRescuePlanner Rescue = new(Clock);
        private static readonly List<Verify> Verifies = [];
        private static readonly List<RescueVerify> RescueVerifies = [];
        private static readonly HashSet<string> LoggedThisZone = [];
        private static readonly ulong[] AttackerIds = new ulong[MaxAttackers];
        private static readonly SquadronRescuePlanner.Attacker[] Attackers = new SquadronRescuePlanner.Attacker[MaxAttackers];

        private static HashSet<uint>? _tactics;
        private static bool _tablesFailed;
        private static ushort _zone;
        private static bool _zoneIsSquadron, _zoneNotSquadron;
        private static int _classifyMisses;
        private static long _nextClassifyMs;
        private static long _nextTickMs;
        private static string _pendingLabel = "";
        private static string _pendingPath = "";
        private static string _pendingVia = "";
        private static string _pendingPause = "";
        private static readonly StringBuilder PendingNotes = new();
        private static ushort _pendingSeqStart;
        private static long _pauseStartMs;
        private static int _pauses, _autoStarts;

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
            if (PlayerIsTank() || BossActive())
                return false;
            LogEnabled(squad);

            var now = Environment.TickCount64;
            if (!Planner.TryStart(candidate.GameObjectId, now, squad.Followed))
                return false;
            BeginPending(candidate, now, "AutoDuty 要選目標");
            return true;
        }

        /// <summary>每幀呼叫、放在 Framework_Update 最前面：Wrath 自動循環暫停超過硬上限就恢復，不管先發走到哪。</summary>
        internal static void Watchdog()
        {
            try
            {
                if (!Wrath_IPCSubscriber.AutoRotationPaused)
                    return;
                var now = Environment.TickCount64;
                if (_pauseStartMs == 0)
                    _pauseStartMs = now;
                var cap = PauseCapMs();
                if (now - _pauseStartMs < cap)
                    return;
                Svc.Log.Information($"[分隊先發] 暫停 Wrath Combo 自動循環已達硬上限 {cap / 1000.0:f1} 秒，強制恢復：{Wrath_IPCSubscriber.ResumeAutoRotation()}");
                _pauseStartMs = 0;
            }
            catch (Exception ex)
            {
                Once("watchdogex", $"暫停硬上限檢查失敗：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>插件卸載時呼叫：撤銷暫停，等待中的先發直接放棄。</summary>
        internal static void Shutdown(string cause)
        {
            var pending = Planner.Pending != 0;
            var note = ResumeRotation(Environment.TickCount64, cause);
            if (pending || note.Length > 0)
                Svc.Log.Information($"[分隊先發] {cause}：{(pending ? $"放棄等待中的「{_pendingLabel}」" : "沒有等待中的先發")}{(note.Length > 0 ? "；" + note : "")}");
        }

        /// <summary>每幀呼叫（內部節流）：推進等待中的先發、被圍拉回、驗收、換領地時印彙總。</summary>
        internal static void Tick()
        {
            try
            {
                TickCore();
            }
            catch (Exception ex)
            {
                Once("tickex", $"更新失敗，放棄這次先發：{ex.GetType().Name}: {ex.Message}");
                try
                {
                    var note = ResumeRotation(Environment.TickCount64, "更新失敗");
                    if (note.Length > 0)
                        Svc.Log.Information("[分隊先發] " + note);
                }
                catch (Exception ex2)
                {
                    Once("tickex2", $"更新失敗後恢復 Wrath 也失敗：{ex2.GetType().Name}: {ex2.Message}");
                }
                Planner.ResetZone();
                Rescue.ResetZone();
                Verifies.Clear();
                RescueVerifies.Clear();
            }
        }

        private static void TickCore()
        {
            var zone = Svc.ClientState.TerritoryType;
            var inCombat = Svc.Condition[ConditionFlag.InCombat];
            var watch = inCombat && Plugin.Configuration.SquadronOpener && !_tablesFailed && !_zoneNotSquadron;
            if (Planner.Pending == 0 && Verifies.Count == 0 && RescueVerifies.Count == 0 && !Wrath_IPCSubscriber.AutoRotationPaused && zone == _zone && !watch)
                return;
            var now = Environment.TickCount64;
            if (now < _nextTickMs)
                return;
            _nextTickMs = now + TickMs;
            SyncZone();

            for (var i = Verifies.Count - 1; i >= 0; --i)
                if (UpdateVerify(Verifies[i], now))
                    Verifies.RemoveAt(i);
            for (var i = RescueVerifies.Count - 1; i >= 0; --i)
                if (UpdateRescueVerify(RescueVerifies[i], now))
                    RescueVerifies.RemoveAt(i);

            if (Planner.Pending != 0)
                TickPending(now);
            else if (Wrath_IPCSubscriber.AutoRotationPaused)
                Svc.Log.Information("[分隊先發] 沒有等待中的先發卻還在暫停：" + ResumeRotation(now, "沒有等待中的先發"));

            if (!watch || !Classify(now) || !Running() || Planner.GaveUp || !Player.Available || PlayerIsTank() || BossActive())
                return;
            if (Planner.Pending == 0)
                TryAutoStart(now);
            if (Plugin.Configuration.SquadronRescue)
                TickRescue(now);
        }

        private static void BeginPending(IGameObject target, long now, string via)
        {
            _pendingLabel = Label(target);
            _pendingPath = "";
            _pendingVia = via;
            PendingNotes.Clear();
            _pendingSeqStart = ActionSequence();
            _nextTickMs = 0;
            var why = Wrath_IPCSubscriber.TryPauseAutoRotation();
            if (why == null)
            {
                _pauseStartMs = now;
                ++_pauses;
                _pendingPause = "有暫停 Wrath 自動循環";
            }
            else
            {
                _pendingPause = "沒暫停 Wrath 自動循環";
                Once("nopause:" + why, $"等待中不暫停 Wrath Combo 自動循環：{why}（照舊行為）");
            }
        }

        // 玩家目標已經是一隻沒處理過的敵人（手動或其他插件選的）：直接以它開始先發，下一拍就下路徑 B。
        private static void TryAutoStart(long now)
        {
            if (Svc.Targets.Target is not IBattleNpc { BattleNpcKind: BattleNpcSubKind.Enemy, IsDead: false, IsTargetable: true } npc
                || !npc.StatusFlags.HasFlag(StatusFlags.Hostile) || Planner.Handled(npc.GameObjectId))
                return;
            var squad = ScanSquad(npc.GameObjectId, false);
            if (squad.Count == 0)
                return;
            LogEnabled(squad);
            var onSquad = Svc.Objects.SearchById(npc.TargetObjectId) is IBattleNpc { BattleNpcKind: BattleNpcSubKind.NpcPartyMember };
            if (!Planner.TryStart(npc.GameObjectId, now, squad.Followed || onSquad))
                return;
            ++_autoStarts;
            BeginPending(npc, now, "玩家目標已被選成這隻（手動或其他插件）");
        }

        private static void TickPending(long now)
        {
            var id = Planner.Pending;
            var obj = Svc.Objects.SearchById(id);
            var valid = obj is { IsDead: false, IsTargetable: true };
            var squad = valid ? ScanSquad(id, true) : default;
            var hp = PlayerHp();
            var pt = Svc.Targets.Target;
            var ptId = pt?.GameObjectId ?? 0;
            var ptHostile = pt is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Enemy, IsDead: false } ptNpc && ptNpc.StatusFlags.HasFlag(StatusFlags.Hostile);
            uint status = uint.MaxValue;
            var busy = true;
            if (valid)
                status = CommandStatus(id, out busy);
            var inputs = new SquadronOpenerPlanner.Inputs(valid, ptId, ptHostile, BossActive(), !Running(), squad.Followed, hp, status, busy, MaxWaitMs());
            var aBefore = Planner.AIssuedMs;
            var bBefore = Planner.BIssuedMs;
            var step = Planner.Update(now, inputs, out var reason);

            switch (step)
            {
                case SquadronOpenerPlanner.Step.IssueA:
                    var okA = UseCommand(id);
                    Planner.NoteAIssued(now);
                    _pendingPath += "A";
                    if (!okA)
                        Once("afalse", "路徑 A 的 UseAction 回傳 false（遊戲沒收下這次號令）");
                    return;
                case SquadronOpenerPlanner.Step.IssueB:
                    if (ptId != id && obj != null)
                        Svc.Targets.Target = obj;
                    var okB = UseCommand(id);
                    Planner.NoteBIssued(now);
                    _pendingPath += "B";
                    PendingNotes.Append(ptId == id ? "；號令目標＝玩家目前目標" : "；本場改走 B，先選目標再下令");
                    if (!okB)
                        PendingNotes.Append("；路徑 B 的 UseAction 回傳 false");
                    return;
                case SquadronOpenerPlanner.Step.Retargeted:
                    var next = Svc.Objects.SearchById(Planner.Pending);
                    var nextLabel = next != null ? Label(next) : $"{Planner.Pending:X}";
                    PendingNotes.Append($"；玩家目標被別處換成「{nextLabel}」，改等那隻（原本等「{_pendingLabel}」）");
                    _pendingLabel = nextLabel;
                    _pendingPath = "";
                    return;
                case SquadronOpenerPlanner.Step.Wait:
                    return;
            }

            var waited = (now - Planner.StartMs) / 1000.0;
            var outcome = reason switch
            {
                SquadronOpenerPlanner.Reason.Followed               => $"路徑 A 下令 {(now - aBefore!.Value) / 1000.0:f1} 秒後分隊跟上",
                SquadronOpenerPlanner.Reason.FollowedAfterB         => $"路徑 B 下令 {(now - bBefore!.Value) / 1000.0:f1} 秒後分隊跟上",
                SquadronOpenerPlanner.Reason.FollowedWithoutCommand => "還沒下令分隊就自己跟上",
                SquadronOpenerPlanner.Reason.Timeout                => bBefore != null ? $"路徑 B 下令後等到 {waited:f1} 秒沒人跟上" : $"等 {waited:f1} 秒沒人跟上",
                SquadronOpenerPlanner.Reason.LowHp                  => $"玩家血量 {hp:P0} 低於 {SquadronOpenerPlanner.LowHpRatio:P0}，提前出手",
                SquadronOpenerPlanner.Reason.CommandUnavailable     => $"號令一直不可用（GetActionStatus={Planner.LastStatus}）",
                SquadronOpenerPlanner.Reason.TargetGone             => "目標已死亡或消失",
                SquadronOpenerPlanner.Reason.BossStarted            => "首領戰開始，交給 BMR",
                SquadronOpenerPlanner.Reason.Stopped                => "AutoDuty 停止、暫停或玩家倒地",
                _                                                  => reason.ToString(),
            };
            var resume = ResumeRotation(now, reason.ToString());
            var tail = $"開始原因＝{_pendingVia}{PendingNotes}；{_pendingPause}{(resume.Length > 0 ? "，" + resume : "")}";

            if (step == SquadronOpenerPlanner.Step.Abandon)
            {
                Svc.Log.Information($"[分隊先發] 「{_pendingLabel}」：等了 {waited:f1} 秒後放棄（{outcome}），沒有設玩家目標；等待中玩家動作序號 +{(ushort)(ActionSequence() - _pendingSeqStart)}；{tail}");
                return;
            }

            var v = new Verify
            {
                Target    = id,
                Label     = _pendingLabel,
                StartMs   = Planner.StartMs,
                ReleaseMs = now,
                SeqDelta  = (ushort)(ActionSequence() - _pendingSeqStart),
                Path      = _pendingPath,
            };

            // 放手：玩家目標空著才設（別處選的友方目標不蓋掉）；路徑 B 一定要選它，同一幀接著下令。
            if (step == SquadronOpenerPlanner.Step.IssueBAndRelease)
            {
                if (obj != null && ptId != id)
                    Svc.Targets.Target = obj;
                var ok = UseCommand(id);
                Planner.NoteBIssued(now);
                v.Path += "B";
                v.BIssuedMs = now;
                if (!ok)
                    outcome += "；路徑 B 的 UseAction 回傳 false";
            }
            else
            {
                if (obj != null && Svc.Targets.Target == null)
                    Svc.Targets.Target = obj;
                if (reason == SquadronOpenerPlanner.Reason.Timeout && bBefore == null)
                    outcome += $"；路徑 B 號令不可用（GetActionStatus={status}，忙碌={busy}）";
            }
            v.Outcome = $"{outcome}；放手時分隊目標＝{squad.Text}；{tail}";
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
                sb.Append(s is { } x ? $"；放手時的路徑 B 下令 {x:f1} 秒後分隊跟上" : "；放手時的路徑 B 下令後 3 秒內分隊沒跟上");
            }
            if (gone)
                sb.Append($"；放手 {(now - v.ReleaseMs) / 1000.0:f1} 秒後目標已死亡或消失");
            else
                sb.Append($"；放手 3 秒後分隊目標＝{squad.Text}，敵人目標＝{TargetLabel(obj!.TargetObjectId)}");
            sb.Append($"；等待 {(v.ReleaseMs - v.StartMs) / 1000.0:f1} 秒，等待中玩家動作序號 +{v.SeqDelta}");
            AppendGaveUp(sb);
            Svc.Log.Information(sb.ToString());
            return true;
        }

        private static void TickRescue(long now)
        {
            var me = Player.Object;
            var myId = me.GameObjectId;
            var ptId = Svc.Targets.Target?.GameObjectId ?? 0;
            ulong tankId = 0, tankTarget = 0;
            var n = 0;
            foreach (var o in Svc.Objects)
            {
                if (o is not IBattleNpc npc || npc.IsDead)
                    continue;
                if (npc.BattleNpcKind == BattleNpcSubKind.NpcPartyMember)
                {
                    if (tankId == 0 && (npc.ClassJob.ValueNullable?.Role ?? 0) == 1 && HasTactic(npc))
                    {
                        tankId = npc.GameObjectId;
                        tankTarget = npc.TargetObjectId;
                    }
                    continue;
                }
                if (npc.BattleNpcKind == BattleNpcSubKind.Enemy && npc.TargetObjectId == myId && n < MaxAttackers)
                    AttackerIds[n++] = npc.GameObjectId;
            }
            if (n == 0)
                return;
            if (tankId == 0)
            {
                Once("rescuenotank", "[分隊拉回] 沒有活著的分隊坦克：被怪打時不下號令");
                return;
            }
            for (var i = 0; i < n; ++i)
                Attackers[i] = new(AttackerIds[i], tankTarget == AttackerIds[i], ptId == AttackerIds[i]);

            var hpLow = PlayerHp() < SquadronOpenerPlanner.LowHpRatio;
            var pick = Rescue.Choose(now, Attackers.AsSpan(0, n), hpLow, Planner.Pending);
            if (pick == 0)
                return;
            var obj = Svc.Objects.SearchById(pick);
            if (obj is not { IsDead: false, IsTargetable: true })
                return;
            var status = CommandStatus(pick, out var busy);
            if (status != 0 || busy)
            {
                if (status != 0)
                    Once($"rescuestatus{status}", $"[分隊拉回] 號令不可用（GetActionStatus={status}）：這次不下令");
                return;
            }

            string path;
            if (ptId == pick)
                path = "B（號令目標＝玩家目前目標）";
            else if (Planner.BOnly)
            {
                Svc.Targets.Target = obj;
                path = "B（本場改走 B，先選目標再下令）";
            }
            else
                path = "A";
            var dwell = Rescue.DwellOf(pick, now) / 1000.0;
            var ok = UseCommand(pick);
            Rescue.NoteIssued(pick, now);
            RescueVerifies.Add(new RescueVerify
            {
                Target       = pick,
                TankId       = tankId,
                Label        = Label(obj),
                Path         = path,
                TankBefore   = TargetLabel(tankTarget),
                IssuedMs     = now,
                DwellSeconds = dwell,
                Others       = n - 1,
                UseOk        = ok,
                HpLow        = hpLow,
            });
        }

        // 回 true＝驗收完成（已印 log）。
        private static bool UpdateRescueVerify(RescueVerify v, long now)
        {
            var obj = Svc.Objects.SearchById(v.Target);
            var gone = obj is not { IsDead: false, IsTargetable: true };
            var tank = Svc.Objects.SearchById(v.TankId) as IBattleNpc;
            var tankAlive = tank is { IsDead: false };
            if (!gone)
            {
                if (v.TankOnMs == null && tankAlive && tank!.TargetObjectId == v.Target)
                    v.TankOnMs = now;
                if (v.MonsterOnTankMs == null && obj!.TargetObjectId == v.TankId)
                    v.MonsterOnTankMs = now;
            }
            if (!gone && now - v.IssuedMs < VerifyMs)
                return false;

            double? tankS = v.TankOnMs is { } t ? (t - v.IssuedMs) / 1000.0 : null;
            double? monS = v.MonsterOnTankMs is { } m ? (m - v.IssuedMs) / 1000.0 : null;
            Rescue.NoteResult(tankS, monS);
            var effective = tankS != null || monS != null;
            if (effective || !gone)
            {
                if (v.Path == "A")
                    Planner.NoteAResult(effective);
                else
                    Planner.NoteBResult(effective ? tankS ?? monS : null);
            }

            var sb = new StringBuilder($"[分隊拉回] 「{v.Label}」打玩家 {v.DwellSeconds:f1} 秒{(v.HpLow ? "（玩家血量過低，沒等）" : "")}，下令前坦克目標＝{v.TankBefore}；路徑 {v.Path}");
            if (!v.UseOk)
                sb.Append("（UseAction 回傳 false）");
            sb.Append(tankS is { } ts ? $"；坦克 {ts:f1} 秒後換過去" : "；坦克沒換過去");
            sb.Append(monS is { } ms ? $"；怪 {ms:f1} 秒後轉打坦克" : "；怪沒轉打坦克");
            if (gone)
                sb.Append($"；下令 {(now - v.IssuedMs) / 1000.0:f1} 秒後目標已死亡或消失");
            else
                sb.Append($"；3 秒後怪目標＝{TargetLabel(obj!.TargetObjectId)}，坦克目標＝{(tankAlive ? TargetLabel(tank!.TargetObjectId) : "陣亡或不在")}");
            sb.Append($"；同時打玩家的另有 {v.Others} 隻");
            AppendGaveUp(sb);
            Svc.Log.Information(sb.ToString());
            return true;
        }

        private static void AppendGaveUp(StringBuilder sb)
        {
            if (Planner.GaveUp && FirstTime("gaveup"))
                sb.Append($"；路徑 A、B 都連續 {SquadronOpenerPlanner.FailStreakLimit} 次沒效果，本場停用先發與拉回");
        }

        private static string ResumeRotation(long now, string cause)
        {
            if (!Wrath_IPCSubscriber.AutoRotationPaused)
            {
                _pauseStartMs = 0;
                return "";
            }
            var secs = _pauseStartMs != 0 ? (now - _pauseStartMs) / 1000.0 : 0;
            _pauseStartMs = 0;
            return $"Wrath 自動循環暫停 {secs:f1} 秒後恢復（{cause}：{Wrath_IPCSubscriber.ResumeAutoRotation()}）";
        }

        private static void SyncZone()
        {
            var zone = Svc.ClientState.TerritoryType;
            if (zone == _zone)
                return;
            var pending = Planner.Pending != 0;
            var note = ResumeRotation(Environment.TickCount64, "換領地");
            if (pending || note.Length > 0)
                Svc.Log.Information($"[分隊先發] 換領地：{(pending ? $"放棄等待中的「{_pendingLabel}」" : "沒有等待中的先發")}{(note.Length > 0 ? "；" + note : "")}");
            var p = Planner;
            var r = Rescue;
            if (_zoneIsSquadron && p.Started + p.AlreadyOn + r.Commands > 0)
            {
                var aAvg = p.AFollowed > 0 ? $"{p.AFollowSecondsSum / p.AFollowed:f1} 秒" : "-";
                var bAvg = p.BFollowed > 0 ? $"{p.BFollowSecondsSum / p.BFollowed:f1} 秒" : "-";
                var tAvg = r.TankSwitched > 0 ? $"{r.TankSwitchSecondsSum / r.TankSwitched:f1} 秒" : "-";
                var mAvg = r.MonsterSwitched > 0 ? $"{r.MonsterSwitchSecondsSum / r.MonsterSwitched:f1} 秒" : "-";
                Svc.Log.Information($"[分隊先發] 本場彙總（領地 {_zone}）：先發 {p.Started} 次（其中玩家目標已被選好 {_autoStarts} 次、等待中目標被換 {p.Retargets} 次）、分隊已在打而略過 {p.AlreadyOn} 次；"
                                  + $"路徑 A 下令 {p.ACommands} 次、跟上 {p.AFollowed} 次（平均 {aAvg}）；路徑 B 下令 {p.BCommands} 次、跟上 {p.BFollowed} 次（平均 {bAvg}）；"
                                  + $"逾時 {p.Timeouts}、血量提前出手 {p.LowHp}、號令不可用 {p.Unavailable}、放棄 {p.Abandoned}；暫停 Wrath 自動循環 {_pauses} 次；"
                                  + $"被圍拉回下令 {r.Commands} 次、坦克換過去 {r.TankSwitched} 次（平均 {tAvg}）、怪轉打坦克 {r.MonsterSwitched} 次（平均 {mAvg}）、同一隻下滿 {SquadronRescuePlanner.MaxPerTarget} 次 {r.GaveUpTargets} 隻；"
                                  + $"本場改走路徑 B={(p.BOnly ? "是" : "否")}，停用={(p.GaveUp ? "是" : "否")}");
            }
            _zone = zone;
            _zoneIsSquadron = _zoneNotSquadron = false;
            _classifyMisses = 0;
            _nextClassifyMs = 0;
            _pauses = _autoStarts = 0;
            Planner.ResetZone();
            Rescue.ResetZone();
            Verifies.Clear();
            RescueVerifies.Clear();
            LoggedThisZone.Clear();
        }

        // 戰鬥中才判這個領地是不是分隊攻略：有分隊員＝是；連續幾次都沒有＝不是，本領地不再掃。
        private static bool Classify(long now)
        {
            if (_zoneIsSquadron)
                return true;
            if (_zoneNotSquadron || now < _nextClassifyMs || !LoadTables())
                return false;
            _nextClassifyMs = now + ClassifyMs;
            if (ScanSquad(0, false).Count > 0)
                return _zoneIsSquadron = true;
            if (++_classifyMisses >= ClassifyMisses)
                _zoneNotSquadron = true;
            return false;
        }

        private static void LogEnabled(Squad squad)
        {
            if (FirstTime("enabled"))
                Svc.Log.Information($"[分隊先發] 已啟用：領地={_zone}，分隊員 {squad.Count} 人（坦 {squad.TanksAlive}），等待上限 {MaxWaitMs() / 1000.0:f1} 秒，被圍拉回={(Plugin.Configuration.SquadronRescue ? "開" : "關")}，"
                                  + $"AutoDuty 模式={Plugin.Configuration.DutyModeEnum}，BMR={(BossModReborn_IPCSubscriber.IsEnabled ? "有" : "沒有")}，"
                                  + $"AutoDuty 管輪替插件={(Plugin.Configuration.AutoManageRotationPluginState && !Plugin.Configuration.UsingAlternativeRotationPlugin ? "是" : "否")}，Wrath={(Wrath_IPCSubscriber.IsEnabled ? "有" : "沒有")}");
        }

        private static bool LoadTables()
        {
            if (_tactics != null)
                return true;
            if (_tablesFailed)
                return false;
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
                var on = target != 0 && npc.TargetObjectId == target;
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
            if (_tactics == null)
                return false;
            foreach (var s in npc.StatusList)
                if (s.StatusId != 0 && _tactics.Contains(s.StatusId))
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

        private static string Label(IGameObject o) => $"{o.Name.TextValue}({o.BaseId:X}#{o.GameObjectId & 0xFFFF:X})";

        private static string RoleLabel(byte role) => role switch
        {
            1 => "坦",
            4 => "補",
            2 => "近戰",
            3 => "遠程",
            _ => "?",
        };

        private static bool PlayerIsTank()
        {
            if (PlayerHelper.GetJob().GetCombatRole() != CombatRole.Tank)
                return false;
            Once("tank", "玩家是坦克：不先發、不拉回");
            return true;
        }

        private static float PlayerHp()
        {
            var player = Player.Available ? Player.Object : null;
            return player is { MaxHp: > 0 } ? (float)player.CurrentHp / player.MaxHp : 1f;
        }

        private static bool Running() => Plugin.Stage is not (Stage.Stopped or Stage.Paused or Stage.Dead);

        private static bool BossActive() =>
            Plugin.BossObject != null || BossModReborn_IPCSubscriber.IsEnabled && BossModReborn_IPCSubscriber.HasActiveModule();

        private static long MaxWaitMs() => (long)(Math.Clamp(Plugin.Configuration.SquadronOpenerMaxWaitSeconds, 0.5f, 6f) * 1000);

        // 規劃器在「上限＋寬限」放手；硬上限再多 0.5 秒，正常路徑永遠先到。
        private static long PauseCapMs() => MaxWaitMs() + SquadronOpenerPlanner.HardSlackMs + 500;

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

        private static bool FirstTime(string key) => LoggedThisZone.Add(key);

        private static void Once(string key, string msg)
        {
            if (FirstTime(key))
                Svc.Log.Information(msg.StartsWith('[') ? msg : "[分隊先發] " + msg);
        }
    }
}
