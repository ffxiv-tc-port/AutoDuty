using System;
using AutoDuty.Helpers;
using AutoDuty.IPC;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace AutoDuty;

// 站定等待（路徑步驟 holdPosition）：Wait 計時中請 BMR 暫停自動移動與閃避，讓角色站滿機關要求的秒數。
// 不看「自動管理 BossMod AI」設定：這是路徑檔對單一步驟的要求，不是在接管使用者的 BMR 設定。
// 契約：只在框架執行緒呼叫 Tick；押著時每秒續一次 5 秒租約，離開步驟／計時結束／站滿上限／死亡／血量不足／有怪以玩家為目標／條件成立／停止都主動釋放。
public sealed partial class AutoDuty
{
    private const float MovementHoldMinHpRatio   = 0.70f;
    private const float MovementHoldLeaseSeconds = 5f;
    private const int   MovementHoldRenewMs      = 1000;
    private const int   MovementHoldMaxMs        = 15000;

    private readonly MovementHoldSession _movementHold = new(MovementHoldRenewMs);
    private long _movementHoldEpisode;
    private bool _movementHoldUntilFulfilled;
    private long _movementHoldNextUntilCheckAt;
    private StepPolicies.MovementHold _movementHoldLastDecision = StepPolicies.MovementHold.NotHoldStep;

    private static readonly Func<MovementHoldSession.SendResult> SendMovementHold = () => BmrMovementHold.Suppress(MovementHoldLeaseSeconds, MovementHoldMinHpRatio);
    private static readonly Action ReleaseMovementHold = BmrMovementHold.Release;

    private void TickBmrMovementHold()
    {
        int  index    = Indexer;
        bool holdStep = Stage == Stage.Action && index >= 0 && index < Actions.Count && Actions[index].HoldPosition;
        if (!holdStep && !_movementHold.Held)
        {
            _movementHoldEpisode      = 0;
            _movementHoldLastDecision = StepPolicies.MovementHold.NotHoldStep;
            return;
        }

        long now     = Environment.TickCount64;
        long waitEnd = WaitStepEndTick;
        if (holdStep && waitEnd != 0 && waitEnd != _movementHoldEpisode)
        {
            // 新的一次等待（含離開條件不成立後的重做）：重新探測端點、重新檢查條件。
            _movementHoldEpisode          = waitEnd;
            _movementHoldUntilFulfilled   = false;
            _movementHoldNextUntilCheckAt = 0;
            _movementHoldLastDecision     = StepPolicies.MovementHold.NotHoldStep;
            _movementHold.NewEpisode();
        }

        bool alive = false, attacked = false;
        uint hp    = 0, maxHp = 0;
        string? attacker = null;
        if (holdStep)
        {
            // 當幀讀完就丟，不留 IPlayerCharacter／IBattleNpc。
            var player = Svc.Objects.LocalPlayer;
            if (player != null)
            {
                alive = !player.IsDead;
                hp    = player.CurrentHp;
                maxHp = player.MaxHp;
                if (waitEnd != 0)
                {
                    ulong myId = player.GameObjectId;
                    foreach (var o in Svc.Objects)
                    {
                        if (o is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Enemy, IsDead: false } npc && npc.TargetObjectId == myId)
                        {
                            attacked = true;
                            attacker = $"{npc.Name.TextValue}({npc.BaseId:X})";
                            break;
                        }
                    }
                }
            }

            if (waitEnd != 0 && now >= _movementHoldNextUntilCheckAt)
            {
                _movementHoldNextUntilCheckAt = now + MovementHoldRenewMs;
                var until = Actions[index].ConditionsUntil;
                _movementHoldUntilFulfilled = until.Count != 0 && StepConditionsHold(until, valueOnError: true);
            }
        }

        var decision = StepPolicies.DecideMovementHold(holdStep, now, waitEnd, WaitStepDurationMs, MovementHoldMaxMs,
                                                       alive, hp, maxHp, MovementHoldMinHpRatio, _movementHoldUntilFulfilled, attacked);
        var previous = _movementHoldLastDecision;
        _movementHoldLastDecision = decision;

        switch (_movementHold.Step(decision == StepPolicies.MovementHold.Hold, now, SendMovementHold, ReleaseMovementHold))
        {
            case MovementHoldSession.Event.Started:
                Svc.Log.Information($"[站定等待] 第 {index} 步「{Actions[index].Name}」：已請 BMR 暫停自動移動與閃避（每秒續 {MovementHoldLeaseSeconds:0} 秒租約，最多 {MovementHoldMaxMs / 1000} 秒；血量低於 {MovementHoldMinHpRatio:p0} 就不壓制）。");
                break;
            case MovementHoldSession.Event.Released:
                Svc.Log.Information($"[站定等待] 已請 BMR 恢復自動移動與閃避（{MovementHoldReason(decision, hp, maxHp, attacker)}）。");
                break;
            case MovementHoldSession.Event.Unavailable:
                Svc.Log.Information($"[站定等待] 第 {index} 步：BossModReborn 沒有暫停閃避的介面（沒有載入或版本太舊），這一步照舊流程、不壓制。");
                break;
            case MovementHoldSession.Event.Rejected:
                Svc.Log.Information($"[站定等待] 第 {index} 步：BossModReborn 拒絕暫停閃避的要求，這一步照舊流程、不壓制。");
                break;
            case MovementHoldSession.Event.None when decision is StepPolicies.MovementHold.LowHp or StepPolicies.MovementHold.Attacked && previous != decision:
                Svc.Log.Information($"[站定等待] 第 {index} 步：{MovementHoldReason(decision, hp, maxHp, attacker)}，這次不請 BMR 暫停閃避。");
                break;
        }
    }

    /// <summary>停止時呼叫：釋放租約並清掉這一次等待的狀態。冪等。</summary>
    private void ResetBmrMovementHold()
    {
        if (_movementHold.ForceRelease(ReleaseMovementHold))
            Svc.Log.Information("[站定等待] 已請 BMR 恢復自動移動與閃避（AutoDuty 停止）。");
        _movementHoldEpisode      = 0;
        _movementHoldLastDecision = StepPolicies.MovementHold.NotHoldStep;
    }

    private static string MovementHoldReason(StepPolicies.MovementHold decision, uint hp, uint maxHp, string? attacker = null) => decision switch
    {
        StepPolicies.MovementHold.NotHoldStep => "離開站定步驟",
        StepPolicies.MovementHold.NotWaiting  => "等待計時結束",
        StepPolicies.MovementHold.CapReached  => $"已站滿 {MovementHoldMaxMs / 1000} 秒上限",
        StepPolicies.MovementHold.Dead        => "角色無法行動",
        StepPolicies.MovementHold.LowHp       => $"血量 {(maxHp == 0 ? "?" : ((float)hp / maxHp).ToString("p0"))} 低於 {MovementHoldMinHpRatio:p0}",
        StepPolicies.MovementHold.Fulfilled   => "離開條件已成立",
        StepPolicies.MovementHold.Attacked    => $"有怪在打玩家：{attacker ?? "?"}，交給分隊坦克接走前先照常閃",
        _                                     => decision.ToString(),
    };
}
