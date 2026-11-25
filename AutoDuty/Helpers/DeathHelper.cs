using AutoDuty.IPC;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace AutoDuty.Helpers
{
    using System.Numerics;
    using global::AutoDuty.Multibox;

    internal static class DeathHelper
    {
        private static PlayerLifeState _deathState = PlayerLifeState.Alive;
        internal static PlayerLifeState DeathState
        {
            get => _deathState;
            set
            {
                // 多開死亡同步內部自帶 Config.MultiBox 閘門，沒開就是 no-op。
                if (_deathState != value)
                    MultiboxUtility.IsDead(value == PlayerLifeState.Dead);

                // 刻意不再依任務模式早退：使用者常以「一般」模式跑分隊攻略，早退會讓復活後永遠停在 Revived。
                if (value == PlayerLifeState.Dead)
                {
                    if (value != _deathState)
                    {
                        Svc.Log.Debug("DeathHelper - Player is Dead changing state to Dead");
                        int generation = Plugin.StopGeneration;
                        SchedulerHelper.ScheduleAction("OnDeath", () => OnDeath(generation), 500, false);
                    }
                }
                else if (value == PlayerLifeState.Revived)
                {
                    SchedulerHelper.DescheduleAction("OnDeath");
                    Svc.Log.Debug("DeathHelper - Player is Revived changing state to Revived");
                    _oldIndex = Plugin.Indexer;
                    _findShortcutStartTime = Environment.TickCount;
                    _shortcutPressed = false;
                    FindShortcut(Plugin.StopGeneration);
                }
                _deathState = value;
            }
        }

        /// <summary>
        /// 停止路徑（StopAndResetALL，在 <c>StopGeneration++</c> 之後）呼叫：移除 OnDeath，停在 Dead 的死亡狀態歸回 Alive。
        /// </summary>
        /// <remarks>
        /// 只動自己的排程與欄位，不按任何視窗、不送多開訊息；執行中復活那一支（Revived）的取消照舊。
        /// 歸回 Alive：否則下一輪第一次死亡時 setter 看到 Dead→Dead，不會排 OnDeath 也不會回報多開。
        /// 本類別所有延遲回呼（OnDeath、FindShortcut、OnRevive）都帶著排程當下的 StopGeneration，對不上就不動作。
        /// </remarks>
        internal static void OnAutomationStopped()
        {
            SchedulerHelper.DescheduleAction("OnDeath");
            if (_deathState == PlayerLifeState.Dead)
                _deathState = PlayerLifeState.Alive;
        }

        private static unsafe void OnDeath(int generation)
        {
            // 停下之前排的：只移除自己、什麼都不做。上面的 DescheduleAction 若和排程落在同一幀會被後處理的新增蓋掉，
            // 靠這裡補；回呼正在跑＝它就在排程表裡，這個移除在同一輪 ScheduleInvoker 結尾生效。
            if (generation != Plugin.StopGeneration)
            {
                SchedulerHelper.DescheduleAction("OnDeath");
                return;
            }

            Plugin.StopForCombat = true;
            Plugin.SkipTreasureCoffer = true;

            if (VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();

            if (Plugin.TaskManager.IsBusy)
                Plugin.TaskManager.Abort();

            // 同步的一般副本／討伐戰／大型任務刻意維持不按（與移除模式早退之前相同）：
            // 這扇是「傳送到返回點」，和真人組隊時按下去等於放棄等隊友復活。
            DutyMode mode = Plugin.Configuration.DutyModeEnum;
            if (mode == DutyMode.Variant || (mode.EqualsAny(DutyMode.Regular, DutyMode.Trial, DutyMode.Raid) && Plugin.Configuration.Unsynced))
            {
                Svc.Log.Debug("DeathHelper - On Death, looking for YesNo");
                if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) && GenericHelpers.IsAddonReady(addonSelectYesno))
                    AddonHelper.ClickSelectYesno();
            }
        }

        private static int _oldIndex = 0;
        private static IGameObject? _gameObject => ObjectHelper.GetObjectByDataId(2000700);
        private static int _findShortcutStartTime = 0;

        /// <summary>這次復活有沒有按下捷徑確認框（只用來寫接續方式的 log）。</summary>
        private static bool _shortcutPressed;

        /// <summary>按下捷徑確認框的時刻；之後這段時間內不再互動也不重讀那扇窗（關閉中的窗文字會讀壞）。</summary>
        private static long _shortcutPressedAt;

        private const int ShortcutPressSettleMs = 5000;

        private static int FindWaypoint()
        {
            /*
            if (Plugin.Indexer == 0)
            {
                float closestWaypointDistance = float.MaxValue;
                int closestWaypointIndex = -1;

                for (int i = 0; i < Plugin.Actions.Count; i++)
                {
                    string node = Plugin.Actions[i].Name;
                    Vector3 position = Plugin.Actions[i].Position;

                    float currentDistance = ObjectHelper.GetDistanceToPlayer(position);
                    if (currentDistance < closestWaypointDistance)

                    {
                        closestWaypointDistance = node.Equals("Boss", StringComparison.InvariantCultureIgnoreCase) ?
                                                      currentDistance :
                                                      ObjectHelper.GetDistanceToPlayer(Plugin.Actions[Plugin.Indexer].Position);
                        closestWaypointIndex = i;
                    }
                }
                Svc.Log.Info($"Closest Waypoint was {closestWaypointIndex}");
                return closestWaypointIndex;
            }*/

            if (Plugin.Indexer != -1)
            {
                // 🔴 防禦性查表：死亡重生的當下，CurrentTerritoryType 可能不在 DictionaryPaths 裡（這個副本本來就沒有
                //    路徑資料），或 CurrentPath 指到一個已經不存在／被移除的路徑（越界或 -1）。
                //    OnRevive 是 Framework.Update 的 handler，例外逃出去會讓後面的 Stop() 永遠執行不到、
                //    訂閱解不掉，之後每一幀重丟一次例外洗版。查不到就當作「沒有復生點資料」處理。
                bool revivalFound = false;
                if (ContentPathsManager.DictionaryPaths.TryGetValue(Plugin.CurrentTerritoryType, out ContentPathsManager.ContentPathContainer? container)
                    && Plugin.CurrentPath >= 0 && Plugin.CurrentPath < container.Paths.Count)
                {
                    revivalFound = container.Paths[Plugin.CurrentPath].RevivalFound;
                }
                else
                {
                    Svc.Log.Information($"[DeathHelper] 查不到 TerritoryType {Plugin.CurrentTerritoryType}、路徑索引 {Plugin.CurrentPath} 的 RevivalFound，" +
                                        "當作沒有復生點處理。");
                }

                bool isBoss = Plugin.Actions[Plugin.Indexer].Name.Equals("Boss");
                if (!revivalFound)
                {
                    if (Plugin.Indexer > 0 && isBoss)
                        return Plugin.Indexer;
                }




                Svc.Log.Info($"Finding Revival Point starting at {Plugin.Indexer}. Using Revival Action: {revivalFound}");
                for (int i = Plugin.Indexer; i >= 0; i--)
                {
                    if (Plugin.Actions[i].Name.Equals(isBoss ? "Revival" : "Boss", StringComparison.InvariantCultureIgnoreCase) && i != Plugin.Indexer)
                        return isBoss ? i : i + 1;
                    /* Pre 7.2
                    else
                    {
                        if (Plugin.Actions[i].Name.Equals("Boss", StringComparison.InvariantCultureIgnoreCase) && i != Plugin.Indexer)
                            return i + 1;
                    }*/
                }
            }

            return 0;
        }

        /// <summary>OnRevive 目前這次訂閱屬於哪一次停止之前（見 <see cref="OnAutomationStopped"/>）。只在框架執行緒讀寫。</summary>
        private static int _onReviveGeneration;

        private static void FindShortcut(int generation)
        {
            // 復活後這段最多會自己重排 5 秒；中途 AutoDuty 停過（即使又重新開始）就不要再掛 OnRevive，
            // 否則它收尾時把 Stage 設回 Reading_Path，等於停了又自己跑起來，或改掉新一輪的 Indexer。
            if (generation != Plugin.StopGeneration)
            {
                Svc.Log.Information("[DeathHelper] AutoDuty 在復活後停過，放棄這次復活的捷徑搜尋。");
                return;
            }

            if (_gameObject == null && Environment.TickCount <= (_findShortcutStartTime + 5000))
            {
                Svc.Log.Debug($"OnRevive: Searching for shortcut");
                SchedulerHelper.ScheduleAction("FindShortcut", () => FindShortcut(generation), 500);
                return;
            }
            
            if (_gameObject == null || !_gameObject.IsTargetable)
            {
                Svc.Log.Debug($"OnRevive: Couldn't find shortcut");
                Plugin.Indexer = 0;
                //Stop();
                //return;
            } else
                Svc.Log.Debug("OnRevive: Found shortcut");
            _onReviveGeneration = generation;
            Svc.Framework.Update += OnRevive;
        }

        internal static void Stop()
        {
            Svc.Framework.Update -= OnRevive;
            if (VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();
            Plugin.Stage = Stage.Reading_Path;
            Svc.Log.Debug("DeathHelper - Player is Alive, and we are done with Revived Actions, changing state to Alive");
            _deathState               = PlayerLifeState.Alive;
            Plugin.SkipTreasureCoffer = false;
        }

        private static unsafe void OnRevive(IFramework _)
        {
            // 同上：停過就只解除訂閱，不走 Stop()（它會把 Stage 設回 Reading_Path）。
            if (_onReviveGeneration != Plugin.StopGeneration)
            {
                Svc.Framework.Update -= OnRevive;
                return;
            }

            if (!EzThrottler.Throttle("OnRevive", 500) || (!PlayerHelper.IsReady && !Conditions.Instance()->OccupiedInQuestEvent) || PlayerHelper.IsCasting) return;

            float distanceToPlayer;

            if (_gameObject == null || !_gameObject.IsTargetable || (distanceToPlayer = ObjectHelper.GetDistanceToPlayer(_gameObject)) > 50)
            {
                Svc.Log.Debug("OnRevive: Done");
                if(Plugin.Indexer == 0)
                    Plugin.Indexer = FindWaypoint();
                FinishRevive(_shortcutPressed ? "走捷徑" : "走回去（附近沒有可用的捷徑）");
                return;
            }
            if (_oldIndex == Plugin.Indexer)
                Plugin.Indexer = FindWaypoint();

            if (distanceToPlayer > 2)
            {
                MovementHelper.Move(_gameObject, 0.25f, 2);
                Svc.Log.Debug($"OnRevive: Moving to {_gameObject.Name} at: {_gameObject.Position} which is {distanceToPlayer} away");
            }
            else if (!TryUseShortcut(_gameObject, out string giveUpReason))
                FinishRevive($"走回去（{giveUpReason}）");
        }

        private static void FinishRevive(string how)
        {
            int index = Plugin.Indexer;
            string step = index >= 0 && index < Plugin.Actions.Count ? Plugin.Actions[index].Name : "?";
            Svc.Log.Information($"[DeathHelper] 復活後接續：{how}，從第 {index} 步「{step}」繼續。");
            Stop();
        }

        /// <summary>捷徑（EObj 2000700）的確認框文字＝GimmickAccessor 第 74 列指向的 GimmickYesNo 列。</summary>
        private const uint ShortcutPromptRowId = 36;

        private static string? _shortcutPromptText;

        private static string ShortcutPromptText =>
            _shortcutPromptText ??= Normalize(Svc.Data.GetExcelSheet<GimmickYesNo>().GetRowOrDefault(ShortcutPromptRowId)?.Unknown0.ExtractText());

        internal static string Normalize(string? text) => (text ?? string.Empty).ReplaceLineEndings("").Trim();

        /// <summary>確認框文字是不是捷徑的那一句。期望文字讀不到（空字串）時一律不是。</summary>
        internal static bool IsShortcutPrompt(string? shownText, string expected) =>
            expected.Length > 0 && string.Equals(Normalize(shownText), expected, StringComparison.Ordinal);

        /// <summary>
        /// 在捷徑旁邊時每次 OnRevive 呼叫一次：還沒開窗就互動，窗開了且條件都成立才按「是」。回 false＝放棄捷徑、改走回去。
        /// </summary>
        /// <remarks>
        /// 🔴 這扇 SelectYesno 是 YesAlready 也會按的窗：只在當場確認過壓制租約還活著時才按，
        /// 租約不在就連互動都不做（開了窗卻不按會把人卡在事件裡）。窗與文字都在同一幀重找重讀。
        /// </remarks>
        private static unsafe bool TryUseShortcut(IGameObject shortcut, out string giveUpReason)
        {
            giveUpReason = string.Empty;

            if (_shortcutPressed && Environment.TickCount64 - _shortcutPressedAt < ShortcutPressSettleMs)
                return true;

            if (!YesAlready_IPCSubscriber.HoldsSuppressionLease)
            {
                giveUpReason = "沒有 YesAlready 壓制租約，不按捷徑確認框";
                return false;
            }

            if (!GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addon) || !GenericHelpers.IsAddonReady(addon))
            {
                Svc.Log.Debug($"OnRevive: Interacting with {shortcut.Name} until SelectYesno Addon appears");
                ObjectHelper.InteractWithObjectUntilAddon(shortcut, "SelectYesno");
                return true;
            }

            string shown = new AddonMaster.SelectYesno(addon).Text;
            if (!IsShortcutPrompt(shown, ShortcutPromptText))
            {
                giveUpReason = $"確認框文字不是捷徑那一句（畫面上：{Normalize(shown)}），不按";
                return false;
            }

            if (!YesAlready_IPCSubscriber.ConfirmSuppressionLease())
            {
                giveUpReason = "YesAlready 壓制租約已失效，不按捷徑確認框";
                return false;
            }

            Svc.Log.Debug("OnRevive: Shortcut prompt confirmed, ClickingYes");
            AddonHelper.ClickSelectYesno();
            _shortcutPressed   = true;
            _shortcutPressedAt = Environment.TickCount64;
            return true;
        }
    }
}
