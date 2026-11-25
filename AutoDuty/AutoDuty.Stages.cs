using System;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.IO;
using ECommons;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using AutoDuty.Windows;
using AutoDuty.IPC;
using AutoDuty.External;
using AutoDuty.Helpers;
using ECommons.Throttlers;
using Dalamud.Game.ClientState.Objects.Types;
using System.Linq;
using ECommons.GameFunctions;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.IoC;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using AutoDuty.Properties;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AutoDuty.Updater;

namespace AutoDuty;

using System.Text.RegularExpressions;
using Dalamud.Utility.Numerics;
using Data;
using ECommons.Configuration;
using ECommons.SimpleGui;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using Pictomancy;
using static Data.Classes;
using TaskManager = ECommons.Automation.LegacyTaskManager.TaskManager;

public sealed partial class AutoDuty
{
    private void StageReadingPath()
    {
        // 側移期間不在 Stage.Moving,卡住偵測不會跑,側移的逾時只能在這裡收。
        StuckEscalationHelper.TickSidestep();

        if (!PlayerHelper.IsValid || !EzThrottler.Check("PathFindFailure") || Indexer == -1 || Indexer >= Actions.Count)
            return;

        // 使用者在操作移動:不送路徑、不推進步驟,等他停手。
        if (ManualMoveYield.Yielding)
            return;

        Action = $"{(Actions.Count > Indexer ? Plugin.Actions[Indexer].ToCustomString() : "")}";

        PathAction = Actions[Indexer];

        bool sync = !this.Configuration.Unsynced || this.Configuration.DutyModeEnum is not (DutyMode.Raid or DutyMode.Regular or DutyMode.Trial);
        if (PathAction.Tag.HasFlag(ActionTag.Unsynced) && sync)
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer]} because we are synced");
            Indexer++;
            return;
        }

        if (PathAction.Tag.HasFlag(ActionTag.W2W) && !Configuration.IsW2W(unsync: !sync))
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer]} because we are not W2W-ing");
            this.Indexer++;
            return;
        }

        if (PathAction.Tag.HasFlag(ActionTag.Synced) && Configuration.Unsynced)
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer]} because we are unsynced");
            Indexer++;
            return;
        }

        if (PathAction.Tag.HasFlag(ActionTag.Comment))
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer].Name} because it is a comment");
            Indexer++;
            return;
        }

        if (PathAction.Tag.HasFlag(ActionTag.Revival))
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer].Name} because it is a Revival Tag");
            Indexer++;
            return;
        }

        if ((SkipTreasureCoffer || !Configuration.LootTreasure || Configuration.LootBossTreasureOnly) && PathAction.Tag.HasFlag(ActionTag.Treasure))
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer].Name} because we are either in revival mode, LootTreasure is off or BossOnly");
            Indexer++;
            return;
        }

        // 步驟條件:全部成立才執行。空集合(絕大多數步驟)不進這個分支,行為與加入條件之前相同。
        if (PathAction.Conditions.Count > 0)
        {
            PathActionCondition? unfulfilled = null;
            foreach (PathActionCondition condition in PathAction.Conditions)
            {
                bool ok;
                try
                {
                    ok = condition.IsFulfilled();
                }
                catch (Exception ex)
                {
                    // 條件算不出來就當成不成立(跳過該步驟),而不是讓例外把整個 Framework.Update 打斷。
                    Svc.Log.Warning($"Path condition {condition.ParseKey} threw, treating as not fulfilled: {ex}");
                    ok = false;
                }

                if (!ok)
                {
                    unfulfilled = condition;
                    break;
                }
            }

            if (unfulfilled != null)
            {
                Svc.Log.Debug($"Skipping path entry {PathAction.Name} because condition [{unfulfilled.Describe()}] is not fulfilled");
                Indexer++;
                return;
            }
        }

        // 隊形:非王步驟時跟著坦克走,除非這一步標了 NoPartyCoherency。
        // ⚠️ StayCloseToTank 內部還有 Configuration.PartyCoherency 這道閘門(預設關),關著時一律送 None。
        BossMod_IPCSubscriber.StayCloseToTank(!(PathAction.Name.Equals("Boss", StringComparison.InvariantCultureIgnoreCase) ||
                                                PathAction.Flags.HasFlag(PathActionFlags.NoPartyCoherency)));

        if (PathAction.Position == Vector3.Zero)
        {
            Stage = Stage.Action;
            return;
        }

        if (!VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() && !VNavmesh_IPCSubscriber.Path_IsRunning())
        {
            Chat.Instance.ExecuteCommand("/automove off");
            VNavmesh_IPCSubscriber.Path_RequestTolerance(0.25f);
            if (PathAction.Name == "MoveTo" && PathAction.Arguments.Count > 0 && bool.TryParse(PathAction.Arguments[0], out bool useMesh) && !useMesh)
            {
                VNavmesh_IPCSubscriber.Path_MoveTo([PathAction.Position], false);
            }
            else
                VNavmesh_IPCSubscriber.SimpleMove_PathfindAndMoveTo(PathAction.Position, false);
            Stage = Stage.Moving;
        }
    }

    private void StageMoving()
    {
        if (!PlayerHelper.IsReady || Indexer == -1 || Indexer >= Actions.Count)
            return;

        if (Indexer == -1 || Indexer >= Actions.Count)
            return;

        Action = $"{Plugin.Actions[Indexer].ToCustomString()}";

        // 持續條件刻意只在 Moving 判:上游在 Action 階段讀的是 ActionInvoke 已清空的 PathAction(永不觸發),
        // 路徑檔是照這個實際行為調的;在 Action 判,王戰中途走出半徑就可能把 Boss 步驟中止。
        if (PathAction.ConditionsWhile.Count != 0 && EzThrottler.Throttle("ConditionsWhile" + Indexer, 100) &&
            !StepConditionsHold(PathAction.ConditionsWhile, valueOnError: false))
        {
            AbortStepForConditionsWhile();
            return;
        }

        unsafe
        {
            if (PlayerHelper.IsMoving && EzThrottler.Throttle("AutoSprint", 300) && ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 4) == 0 && ActionManager.Instance()->QueuedActionId != 4 && !PlayerHelper.IsCasting)
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 4);
        }

        if (PlayerHelper.InCombat && Plugin.StopForCombat)
        {
            if (Configuration.AutoManageRotationPluginState && !Configuration.UsingAlternativeRotationPlugin)
                SetRotationPluginSettings(true);

            if (Configuration.UnsyncedKeepMovingInCombat && Configuration.IsUnsyncActive())
            {
                // 解限模式「交戰中繼續走到定點」:不切進 Waiting_For_Combat,讓 vnavmesh 繼續跑路徑,技能交給輪替外掛。
                // 🔴 走位權必須只有一個主人:BossMod 的 NormalMovement 是另一套獨立的 pathfinder,兩邊同時要走會
                //    互相抵銷、角色原地不動。所以這裡明確把 BossMod 的移動關掉,由 vnavmesh 獨佔走位。
                //    ⚠️ SetMovement 只有在 AutoManageBossModAISettings 開著時才會真的送出去;
                //    沒開的話 AutoDuty 本來就沒有幫忙套 BossMod 的 preset,也就沒有走位權之爭。
                if (!_unsyncedKeepMovingArmed)
                {
                    _unsyncedKeepMovingArmed = true;
                    BossMod_IPCSubscriber.SetMovement(false);
                    Svc.Log.Information("[解限] 交戰中繼續走到定點:已關閉 BossMod 的自動移動,改由 vnavmesh 獨佔走位。");
                }

                KeepMovingTargetAssist();
            }
            else
            {
                // 戰鬥中留著殘路徑,BMR 就整段不注入閃避移動 ⇒ 連還在算的尋路一起取消。
                VNavmesh_IPCSubscriber.Nav_PathfindCancelAll();
                VNavmesh_IPCSubscriber.Path_Stop();
                _combatCalmSince = null;
                Stage = Stage.Waiting_For_Combat;
                return;
            }
        }
        else if (_unsyncedKeepMovingArmed && !PlayerHelper.InCombat)
        {
            // 離開戰鬥就把移動權還給 BossMod,免得這個設定的影響漏到後面的一般導航。
            // 🔴 這裡一定要再判一次 !InCombat:上面那個 if 為假也可能是「還在戰鬥中,但
            //    StopForCombat 被路徑步驟關掉了」,那種情況的 SetMovement(false) 是那個步驟
            //    自己要的,不可以被我們還原掉。
            _unsyncedKeepMovingArmed = false;
            BossMod_IPCSubscriber.SetMovement(true);
            Svc.Log.Information("[解限] 交戰結束:已還原 BossMod 的自動移動。");
        }

        // 路徑是被使用者的移動輸入取消的,不是走到了:不推進、不重送,停手後由 Framework_Update 改回 Reading_Path 重走這一步。
        if (ManualMoveYield.Yielding)
            return;

        if (StuckHelper.IsStuck(out byte stuckCount))
        {
            Vector3 stuckAt = Player.Position;

            // 第 2 階刻意排在 Path_Stop 之前:邊走邊跳才有機會越過卡住的邊緣。
            if (Configuration.StuckEscalationEnabled && StuckEscalationHelper.TryJump(stuckCount, stuckAt))
                return;

            VNavmesh_IPCSubscriber.Path_Stop();

            if (Configuration.StuckEscalationEnabled && StuckEscalationHelper.TrySidestep(stuckCount, PathAction.Position))
            {
                // 側移路徑跑完之前 StageReadingPath 不會重下原目標(它只在沒有路徑在跑時才下)。
                Stage = Stage.Reading_Path;
                return;
            }

            if (Configuration.StuckEscalationEnabled && StuckEscalationHelper.ShouldSkipStep(stuckCount) && CanSkipCurrentStep)
            {
                string stepName = Actions[Indexer].Name;
                if (!StepPolicies.StuckSkipAllowed(stepName))
                {
                    Svc.Log.Information($"[卡住處理] 第 4 階:在 {stuckAt} 連續卡住 {stuckCount + 1} 次,但第 {Indexer} 步是「{stepName}」,不跳過、停下等玩家接手。");
                    MarkSelfStop("Stuck on the way to a boss. Skipping the boss would leave the duty unfinished, so AutoDuty stopped. Please take over.", needsHelp: true);
                    Stage = Stage.Stopped;
                    return;
                }

                Svc.Log.Information($"[卡住處理] 第 4 階:在 {stuckAt} 連續卡住 {stuckCount + 1} 次,跳過目前這一步。");
                _autoSkippedStepsThisDuty++;
                SkipCurrentStep();
                return;
            }

            if (stuckCount >= StuckEscalationHelper.RebuildAtCount)
            {
                // 🔴 最後一階一定要把卡住計數歸零,否則計數只會一直往上加(玩家還卡著就不會滿足
                //    StuckHelper 那個「一段時間沒再卡住」的歸零條件),結果是門檻一旦越過,
                //    之後每一次卡住偵測都再觸發一次 —— 而全量重建期間玩家更動不了,
                //    形成自我維持迴圈。
                if (Configuration.RebuildNavmeshOnStuck)
                {
                    Svc.Log.Information($"[卡住處理] 第 5 階:在 {stuckAt} 連續卡住 {stuckCount + 1} 次,要求 vnavmesh 重建網格(計數已歸零)。");
                    VNavmesh_IPCSubscriber.Nav_Rebuild();
                    StuckHelper.ResetStuckCount();
                }
                else if (Configuration.StuckEscalationEnabled)
                {
                    Svc.Log.Information($"[卡住處理] 在 {stuckAt} 連續卡住 {stuckCount + 1} 次,重建網格已關閉,階梯從頭再跑一輪。");
                    StuckHelper.ResetStuckCount();
                }
            }

            Stage = Stage.Reading_Path;
            return;
        }

        if (((!VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() && VNavmesh_IPCSubscriber.Path_NumWaypoints() == 0) || (!PathAction.Name.IsNullOrEmpty() && PathAction.Position != Vector3.Zero && ObjectHelper.GetDistanceToPlayer(PathAction.Position) <= (PathAction.Name.EqualsIgnoreCase("Interactable") ? 2f : 0.25f))) &&
            UntilThrottlePasses())
        {
            if (PathAction.Name.IsNullOrEmpty() || PathAction.Name.Equals("MoveTo") || PathAction.Name.Equals("TreasureCoffer") || PathAction.Name.Equals("Revival"))
            {
                Stage = Stage.Reading_Path;
                if (CurrentStepUntilFulfilled())
                    Indexer++;
            }
            else
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                Stage = Stage.Action;
            }

            return;
        }

        if (EzThrottler.Throttle("BossChecker", 25) && PathAction.Name.Equals("Boss") && PathAction.Position != Vector3.Zero && ObjectHelper.BelowDistanceToPlayer(PathAction.Position, 50, 10))
        {
            BossObject = ObjectHelper.GetBossObject(25);
            if (BossObject != null)
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                Stage = Stage.Action;
                return;
            }
        }
    }

    private void StageAction()
    {
        if (Indexer == -1 || Indexer >= Actions.Count)
            return;
        
        if (this.Configuration is { AutoManageRotationPluginState: true, UsingAlternativeRotationPlugin: false } && !Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            SetRotationPluginSettings(true);
        
        if (!TaskManager.IsBusy && UntilThrottlePasses())
        {
            BossMod_IPCSubscriber.DisableRealAIPreset();
            Stage = Stage.Reading_Path;
            if (CurrentStepUntilFulfilled())
                Indexer++;
            return;
        }
    }

    // 沒有離開條件的步驟不節流,推進時機與加入這兩種條件之前完全相同。
    private bool UntilThrottlePasses() =>
        Indexer < 0 || Indexer >= Actions.Count || Actions[Indexer].ConditionsUntil.Count == 0 ||
        EzThrottler.Throttle("AdvanceStep" + Indexer, 100);

    /// <summary>目前這一步的離開條件是否全部成立(沒有離開條件＝成立)。不成立時呼叫端不推進 Indexer,這一步會從頭重做。</summary>
    /// <remarks>讀 <c>Actions[Indexer]</c> 而不是 <see cref="PathAction"/>:Action 階段的 PathAction 已被 ActionInvoke 清空。</remarks>
    private bool CurrentStepUntilFulfilled()
    {
        if (Indexer < 0 || Indexer >= Actions.Count)
            return true;

        PathAction step = Actions[Indexer];
        if (step.ConditionsUntil.Count == 0 || StepConditionsHold(step.ConditionsUntil, valueOnError: true))
        {
            if (_untilHoldIndex == Indexer)
                Svc.Log.Information($"[步驟條件] 第 {Indexer} 步「{step.Name}」的離開條件已成立,往下一步。");
            _untilHoldIndex = -1;
            _untilFailures  = 0;
            return true;
        }

        if (_untilHoldIndex != Indexer)
        {
            _untilHoldIndex = Indexer;
            _untilFailures  = 0;
            Svc.Log.Information($"[步驟條件] 第 {Indexer} 步「{step.Name}」的離開條件[{string.Join(" & ", step.ConditionsUntil.Select(c => c.Describe()))}]還沒成立,重做這一步。");
        }

        _untilFailures++;
        if (step.UntilMaxRedo > 0)
            Svc.Log.Information($"[步驟條件] 第 {Indexer} 步「{step.Name}」離開條件第 {_untilFailures} 次不成立(最多重做 {step.UntilMaxRedo} 次)。");

        if (StepPolicies.ShouldGiveUpRedo(_untilFailures, step.UntilMaxRedo))
        {
            // 刻意停下而不是往下跳:沒做成的機關會讓後面的步驟整段卡住,往下跳只是把問題藏到更後面。
            Svc.Log.Information($"[步驟條件] 第 {Indexer} 步「{step.Name}」重做 {step.UntilMaxRedo} 次後離開條件仍未成立,停下等玩家接手。");
            MarkSelfStop("A path step still did not succeed after its retries. Please take over.", needsHelp: true);
            Stage = Stage.Stopped;
        }
        return false;
    }

    /// <summary>全部條件都成立才回 true。條件擲例外時回 <paramref name="valueOnError"/>,讓呼叫端選「往下走」的那一邊,不卡在原地。</summary>
    private static bool StepConditionsHold(List<PathActionCondition> conditions, bool valueOnError)
    {
        foreach (PathActionCondition condition in conditions)
        {
            try
            {
                if (!condition.IsFulfilled())
                    return false;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"Path condition {condition.ParseKey} threw, treating as {(valueOnError ? "fulfilled" : "not fulfilled")}: {ex}");
                return valueOnError;
            }
        }
        return true;
    }

    /// <summary>持續條件不成立:停掉這一步的導航與排隊中的任務,直接進下一步。</summary>
    private void AbortStepForConditionsWhile()
    {
        int        abortedIndex = Indexer;
        PathAction step         = Actions[abortedIndex];

        StopNavigationIfRunning();
        TaskManager.Abort();
        StuckEscalationHelper.Reset();

        Indexer = abortedIndex + 1;
        Stage   = Stage.Reading_Path;

        Svc.Log.Information($"[步驟條件] 第 {abortedIndex} 步「{step.Name}」的持續條件[{string.Join(" & ", step.ConditionsWhile.Select(c => c.Describe()))}]不再成立,中止這一步、往下一步。");
    }

    /// <summary>
    /// 解限模式「交戰中繼續走」用的目標補位:沒有目標時鎖定最近的、已經在戰鬥中的敵對 NPC,
    /// 讓輪替外掛有東西可以打。(這條路徑不會進 Waiting_For_Combat,所以那邊的鎖定邏輯不會跑。)
    /// </summary>
    /// <remarks>
    /// 🔴 只在同一幀內掃描並設定目標,不把任何 IGameObject 留到下一幀。
    /// 判敵意刻意只用 Dalamud 的 <c>StatusFlags</c>/<c>BattleNpcKind</c>(直接讀 CS 結構欄位),
    /// 不走 ECommons 那個吃寫死特徵碼的 IsHostile()/GetNameplateKind(),
    /// 免得特徵碼在台服失效時靜默失準。
    /// </remarks>
    private static void KeepMovingTargetAssist()
    {
        if (Svc.Targets.Target != null)
            return;

        IGameObject? nearest         = null;
        float        nearestDistance = float.MaxValue;

        foreach (IGameObject obj in Svc.Objects)
        {
            if (obj is not IBattleNpc battleNpc
                || battleNpc.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Enemy
                || !obj.IsTargetable
                || obj.IsDead
                || !battleNpc.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Hostile)
                || !battleNpc.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat))
                continue;

            float distance = ObjectHelper.GetDistanceToPlayer(obj);

            if (distance > 75f || distance >= nearestDistance)
                continue;

            nearest         = obj;
            nearestDistance = distance;
        }

        if (nearest != null)
            Svc.Targets.Target = nearest;
    }

    private void StageWaitingForCombat()
    {
        if (!EzThrottler.Throttle("CombatCheck", 250) || !PlayerHelper.IsReady || Indexer == -1 || Indexer >= Actions.Count || PathAction == null)
            return;

        Action = $"Waiting For Combat";

        // AI.SetPreset 只在 InCombat 時才真的武裝(見 IPCSubscriber.SetPreset)。進副本時打的那一次武裝不到
        // 卻已經吃掉 5 秒節流;若進場沒幾秒就撞到雜魚,StageMoving 的補打會被節流擋掉並立刻切進這裡。
        // 所以這裡持續嘗試(受同一個節流保護,不會洗頻),節流一過期就補上。
        if (PlayerHelper.InCombat && this.Configuration is { AutoManageRotationPluginState: true, UsingAlternativeRotationPlugin: false })
            SetRotationPluginSettings(true);

        if (ReflectionHelper.Avarice_Reflection.PositionalChanged(out Positional positional) && !Plugin.Configuration.UsingAlternativeBossPlugin && IPCSubscriber_Common.IsReady("BossModReborn"))
            BossMod_IPCSubscriber.SetPositional(positional);

        if (PathAction.Name.Equals("Boss") && PathAction.Position != Vector3.Zero && ObjectHelper.GetDistanceToPlayer(PathAction.Position) < 50)
        {
            BossObject = ObjectHelper.GetBossObject(25);
            if (BossObject != null)
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                Stage = Stage.Action;
                return;
            }
        }

        if (PlayerHelper.InCombat)
        {
            _combatCalmSince = null;
            if (Svc.Targets.Target == null)
            {
                //find and target closest attackable npc, if we are not targeting
                var gos = ObjectHelper.GetObjectsByObjectKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)?.FirstOrDefault(o => o.GetNameplateKind() is NameplateKind.HostileEngagedSelfUndamaged or NameplateKind.HostileEngagedSelfDamaged && ObjectHelper.GetBattleDistanceToPlayer(o) <= 75);

                if (gos != null && !SquadronOpener.ShouldHold(gos))
                    Svc.Targets.Target = gos;
            }
            if (Configuration.AutoManageBossModAISettings)
            {
                if (Svc.Targets.Target != null)
                {
                    var enemyCount = ObjectFunctions.GetAttackableEnemyCountAroundPoint(Svc.Targets.Target.Position, 15);

                    if (!VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() && VNavmesh_IPCSubscriber.Path_IsRunning())
                        VNavmesh_IPCSubscriber.Path_Stop();

                    if (enemyCount > 2)
                    {
                        Svc.Log.Debug($"Changing MaxDistanceToTarget to {Configuration.MaxDistanceToTargetAoEFloat}, because enemy count = {enemyCount}");
                        BossMod_IPCSubscriber.SetRange(Configuration.MaxDistanceToTargetAoEFloat);
                    }
                    else
                    {
                        Svc.Log.Debug($"Changing MaxDistanceToTarget to {this.Configuration.MaxDistanceToTargetFloat}, because enemy count = {enemyCount}");
                        BossMod_IPCSubscriber.SetRange(this.Configuration.MaxDistanceToTargetFloat);
                    }
                }
            }
            else if (!VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() && VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();
        }
        else if (!PlayerHelper.InCombat && !VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress())
        {
            // 脫戰要連續 CombatSettleMs 才收回 AI 預設:單次看到就收,下一隻接著打時會被 5 秒的重新武裝節流擋住。
            long now = Environment.TickCount64;
            _combatCalmSince ??= now;
            if (now - _combatCalmSince.Value < CombatSettleMs)
                return;

            _combatCalmSince = null;
            Svc.Log.Debug($"[戰鬥等待] 已連續脫戰 {CombatSettleMs} 毫秒,收回 AI 預設、繼續路徑。");
            BossMod_IPCSubscriber.SetRange(Configuration.MaxDistanceToTargetFloat);
            BossMod_IPCSubscriber.DisableRealAIPreset();

            VNavmesh_IPCSubscriber.Path_Stop();
            Stage = Stage.Reading_Path;
        }
    }

    public void StartNavigation(bool startFromZero = true)
    {
        Svc.Log.Debug($"StartNavigation: startFromZero={startFromZero}");
        if (ContentHelper.DictionaryContent.TryGetValue(Svc.ClientState.TerritoryType, out var content))
        {
            CurrentTerritoryContent = content;
            PathFile = ContentPathsManager.BuildDefaultPathFilePath(Svc.ClientState.TerritoryType, content.EnglishName);
            LoadPath();
        }
        else
        {
            CurrentTerritoryContent = null;
            PathFile = "";
            MainWindow.ShowPopup("Error".Loc(), "Unable to load content for Territory".Loc());
            return;
        }
        //MainWindow.OpenTab("Mini");
        if (Configuration.ShowOverlay)
        {
            //MainWindow.IsOpen = false;
            Overlay.IsOpen = true;
        }
        MainListClicked = false;
        // 每一場（含每一輪循環）開始導航都會走到這裡，復活後的流程不會 ⇒ 死亡計數的歸零點。
        _deathsThisDuty = 0;
        _autoSkippedStepsThisDuty = 0;
        Stage = Stage.Reading_Path;
        States |= PluginState.Navigating;
        StopForCombat = true;
        // 對齊鏡頭:設定說明承諾的是「開始時打開,結束時若本來沒開就關回去」。
        // 這裡只負責前半段,並記下「進來時是關的」;還原在 SetGeneralSettings(true)。
        // 🔴 一定要先過 IsEnabled:vnavmesh 沒裝時 SafeWrapper.IPCException 會把
        //    Path_GetAlignCamera() 靜默吃成 default(false),少了這道閘門就會把
        //    「根本沒有 vnavmesh」誤記成「本來是關的」,結束時對著空氣還原。
        if (Configuration.AutoManageVnavAlignCamera && VNavmesh_IPCSubscriber.IsEnabled && !VNavmesh_IPCSubscriber.Path_GetAlignCamera())
        {
            _settingsActive |= SettingsActive.Vnav_Align_Camera_On;
            Svc.Log.Information("vnavmesh 對齊鏡頭:進導航前是關的,導航期間打開,整趟結束時會關回去");
            VNavmesh_IPCSubscriber.Path_SetAlignCamera(true);
        }

        if (this.Configuration is { AutoManageBossModAISettings: true, BM_UpdatePresetsAutomatically: true })
        {
            BossMod_IPCSubscriber.RefreshPreset("AutoDuty",         Resources.AutoDutyPreset);
            BossMod_IPCSubscriber.RefreshPreset("AutoDuty Passive", Resources.AutoDutyPassivePreset);
        }

        if (Configuration.AutoManageBossModAISettings)
            SetBMSettings();
        if (this.Configuration is { AutoManageRotationPluginState: true, UsingAlternativeRotationPlugin: false })
            SetRotationPluginSettings(true);
        if (Configuration.LootTreasure)
        {
            if (PandorasBox_IPCSubscriber.IsEnabled)
                SetPandoraChests(this.Configuration.LootMethodEnum is LootMethod.Pandora or LootMethod.All);
            this._lootTreasure = this.Configuration.LootMethodEnum is LootMethod.AutoDuty or LootMethod.All;
        }
        else
        {
            if (PandorasBox_IPCSubscriber.IsEnabled)
                SetPandoraChests(false);
            this._lootTreasure = false;
        }
        Svc.Log.Info("Starting Navigation");
        if (startFromZero)
            Indexer = 0;
    }

    private void DoneNavigating()
    {
        States &= ~PluginState.Navigating;
        // 路徑真的跑完了。Update() 尾端那個通用 fallback 蓋不到 Reading_Path;最後一步若是單純 MoveTo,
        // Action 會停在舊字串,讓 CheckFinishing 誤判「還沒做完」白等一輪逃生口。
        Action = string.Empty;
        this.CheckFinishing();
    }

    /// <summary>
    /// Nav.IsReady only means vnavmesh has *a* navmesh object loaded, not that it actually
    /// covers the player's current position (some instanced solo/Trust duties stream in
    /// geometry vnavmesh's auto-build never fully captures). Passively waiting on IsReady
    /// forever, as the old code did, hangs indefinitely in that case. Force a full rebuild
    /// (no cache) if it hasn't become ready within a few seconds.
    /// </summary>
    private static Func<bool?> WaitForNavReady()
    {
        DateTime waitStart = DateTime.MinValue;
        return () =>
        {
            if (VNavmesh_IPCSubscriber.Nav_IsReady())
                return true;
            if (waitStart == DateTime.MinValue)
                waitStart = DateTime.Now;
            else if ((DateTime.Now - waitStart).TotalSeconds >= 5)
            {
                Svc.Log.Info("Navmesh not ready after 5s, forcing rebuild");
                VNavmesh_IPCSubscriber.Nav_Rebuild();
                waitStart = DateTime.Now;
            }
            return false;
        };
    }

    private unsafe void CheckFinishing()
    {
        //we finished lets exit the duty or stop
        var plannerRun = ActiveRunContext?.Source == RunSource.Planner;

        if (Configuration.AutoExitDuty || plannerRun || CurrentLoop < GetEffectiveLoopTimes())
        {
            if (!Stage.EqualsAny(Stage.Stopped, Stage.Paused)                                     &&
                (!Configuration.OnlyExitWhenDutyDone || this.DutyState == DutyState.DutyComplete) &&
                !this.States.HasFlag(PluginState.Navigating))
            {
                // 骰寶箱（Need/Greed/Pass）視窗還開著就先別退本 —— 就算是靠別的外掛（例如
                // LazyLoot）自動幫忙骰，也要親自確認那個視窗真的關閉了才算數，不能只憑
                // Action／路徑這種間接狀態去猜「應該骰完了」。這個等待刻意不受下面的逾時
                // 保底影響：骰寶箱是玩家在意、有真實意義的等待，不是卡死的 bug，強制跳過
                // 反而會在還沒骰完的時候就把大家拉出副本，弄丟稀有素材／坐騎的擲骰機會。
                if (GenericHelpers.TryGetAddonByName("NeedGreed", out AtkUnitBase* needGreedAddon) && GenericHelpers.IsAddonReady(needGreedAddon))
                {
                    SchedulerHelper.ScheduleAction("CheckFinishing-WaitNeedGreed", this.CheckFinishing, 500);
                    return;
                }

                // 遊戲可能在戰後的路徑步驟還沒跑完時就把 DutyCompleted 立起來(例如王之後的剝取 Interactable 步驟),
                // 那時候並不處於 Navigating,上面那個判斷蓋不到。只看 Action 也不夠:Update() 每一格都會把它重設成
                // Stage 自己的名字,中間有可能有一格讀到空字串。所以另外要求路徑本身也真的跑到底
                // （Indexer >= Actions.Count），還沒做完的 Interactable 步驟一樣擋得住退本。
                if (!string.IsNullOrEmpty(Action) || (Actions.Count > 0 && Indexer < Actions.Count))
                {
                    _checkFinishingWaitSince ??= DateTime.Now;
                    if (DateTime.Now - _checkFinishingWaitSince.Value < TimeSpan.FromSeconds(CheckFinishingWaitTimeoutSeconds))
                    {
                        SchedulerHelper.ScheduleAction("CheckFinishing-WaitAction", this.CheckFinishing, 500);
                        return;
                    }

                    Svc.Log.Information($"[CheckFinishing] 等待 Action／路徑跑完已超過 {CheckFinishingWaitTimeoutSeconds} 秒" +
                                        $"（Action='{Action}', Indexer={Indexer}/{Actions.Count}），放棄等待直接退本，避免永遠卡在副本裡。");
                }

                _checkFinishingWaitSince = null;
                if (ExitDutyHelper.State != ActionState.Running)
                    ExitDuty();
                if (Configuration.AutoManageRotationPluginState && !Configuration.UsingAlternativeRotationPlugin)
                    SetRotationPluginSettings(false);
                if (Configuration.AutoManageBossModAISettings) 
                    BossMod_IPCSubscriber.DisablePresets();
            }
        }
        else if (Stage != Stage.Stopped)
        {
            // 已停止時不再設一次 Stopped：setter 每次都整套重跑 StopAndResetALL，會關掉使用者之後自己開的循環插件。
            // 這一條不經過 LoopsCompleteActions(),所以既有的完成音效在這裡本來就不會響。
            if (StepPolicies.PathEndNeedsHelp(_autoSkippedStepsThisDuty, this.DutyState == DutyState.DutyComplete))
            {
                Svc.Log.Information($"[CheckFinishing] 路線跑完了,但這一場卡住時自動跳過了 {_autoSkippedStepsThisDuty} 步、副本還沒完成(DutyState={this.DutyState}),不算跑完,停下等玩家接手。");
                MarkSelfStop("The route ended after skipping steps it was stuck on, but the duty is not complete. Please take over.", needsHelp: true);
            }
            else
                MarkSelfStop("All loops finished.");
            Stage = Stage.Stopped;
        }
    }

    private unsafe void ActionInvoke()
    {
        if (PathAction == null) return;

        if (!TaskManager.IsBusy && !PathAction.Name.IsNullOrEmpty())
        {
            _actions.InvokeAction(PathAction);
            PathAction = new();
        }
    }
}
