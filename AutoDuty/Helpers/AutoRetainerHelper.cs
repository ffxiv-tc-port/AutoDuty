using AutoDuty.IPC;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Linq;

namespace AutoDuty.Helpers
{
    internal class AutoRetainerHelper : ActiveHelperBase<AutoRetainerHelper>
    {
        protected override string Name        { get; } = nameof(AutoRetainerHelper);
        protected override string DisplayName { get; } = "AutoRetainer";

        protected override int TimeOut { get; set; } = 600_000;

        protected override string[] AddonsToClose { get; } = ["RetainerList", "SelectYesno", "SelectString", "RetainerTaskAsk"];

        internal override void Start()
        {
            if (!AutoRetainer_IPCSubscriber.IsEnabled || !AutoRetainer_IPCSubscriber.AreAnyRetainersAvailableForCurrentChara())
                return;
            DebugLog("AutoRetainerHelper.Invoke");
            if (!AutoRetainer_IPCSubscriber.IsEnabled)
                Svc.Log.Info("AutoRetainer requires a plugin, visit https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json for more info");
            else if (State != ActionState.Running)
            {
                this.CaptureSchedulerPrior();
                base.Start();
            }
        }

        internal override void Stop()
        {
            this._autoRetainerStarted = false;
            this._autoRetainerStopped = false;
            GotoInnHelper.ForceStop();

            base.Stop();

            bool busy = AutoRetainer_IPCSubscriber.IsBusy();
            if (busy)
                AutoRetainer_IPCSubscriber.AbortAllTasks();

            SchedulerPrior prior = this._schedulerPrior;
            this._schedulerPrior = SchedulerPrior.NotCaptured;
            if (prior == SchedulerPrior.NotCaptured)
                return; // 本次已由先前的 Stop 處理過（含待重開的旗標），重複 Stop 不再送。
            StopPlan plan = PlanStop(prior, busy, Plugin.Disposing);
            if (prior == SchedulerPrior.WasOn)
                Svc.Log.Information(plan.ReenableAfterClose
                    ? "[AutoRetainer 排程器] 開跑前是開著的：先關掉以便收起僱員視窗，離開傳喚鈴後再打開"
                    : plan.SendDisable
                        ? "[AutoRetainer 排程器] 開跑前是開著的，但停止時它正在處理僱員（已中止）⇒ 維持關閉，不在停止後自動重開"
                        : "[AutoRetainer 排程器] 開跑前是開著的，卸載時不關它");
            if (plan.SendDisable)
                SendSchedulerCommand("d");
            this._reenableAfterClose = plan.ReenableAfterClose;
        }

        /// <summary>排程器在本 helper 開始前的狀態；<see cref="Stop"/> 讀一次就清回 <see cref="SchedulerPrior.NotCaptured"/>。</summary>
        internal enum SchedulerPrior : byte { NotCaptured = 0, Unknown, WasOff, WasOn }

        internal readonly record struct StopPlan(bool SendDisable, bool ReenableAfterClose);

        private SchedulerPrior _schedulerPrior = SchedulerPrior.NotCaptured;
        private bool           _reenableAfterClose;

        /// <summary>
        /// AR 沒有「排程器開著沒」的端點，由三個讀值推：<paramref name="anyActive"/>＝<c>AutoRetainer.IsBusy</c>
        /// （排程器 ∨ 多角模式作用中 ∨ 任務佇列忙），<paramref name="taskBusy"/>＝<c>PluginState.IsBusy</c>（任務佇列忙的超集），
        /// <paramref name="multiModeEnabled"/>＝<c>PluginState.GetMultiModeStatus</c>。任一讀不到（null）或推不出來＝Unknown。
        /// </summary>
        internal static SchedulerPrior ClassifySchedulerPrior(bool? anyActive, bool? taskBusy, bool? multiModeEnabled)
        {
            if (anyActive is not bool active)
                return SchedulerPrior.Unknown;
            if (!active)
                return SchedulerPrior.WasOff;
            if (taskBusy == false && multiModeEnabled == false)
                return SchedulerPrior.WasOn;
            return SchedulerPrior.Unknown;
        }

        /// <summary>
        /// 停止時對排程器做什麼。Unknown／WasOff 維持原本的「一律關」；WasOn 先關（AR 開著時會在僱員清單上自己排關窗，與我們同幀搶按），
        /// 離開傳喚鈴後再打開；停止當下 AR 正在處理（被我們中止）則不重開，避免急停後被打開；卸載時不關。
        /// </summary>
        internal static StopPlan PlanStop(SchedulerPrior prior, bool arBusyAtStop, bool disposing) => prior switch
        {
            SchedulerPrior.NotCaptured => new(false, false),
            SchedulerPrior.WasOn when arBusyAtStop => new(true, false),
            SchedulerPrior.WasOn when disposing => new(false, false),
            SchedulerPrior.WasOn => new(true, true),
            _ => new(true, false),
        };

        private void CaptureSchedulerPrior()
        {
            if (this._reenableAfterClose)
            {
                // 上一輪關掉後還沒重開就又開始：現在讀到的「關」是我們造成的，原狀態仍是開。
                this._reenableAfterClose = false;
                this._schedulerPrior     = SchedulerPrior.WasOn;
                return;
            }
            if (!Svc.Framework.IsInFrameworkUpdateThread)
            {
                this._schedulerPrior = SchedulerPrior.Unknown;
                Svc.Log.Information("[AutoRetainer 排程器] 不在框架執行緒上，無法讀開跑前狀態 ⇒ 停止時照舊關閉");
                return;
            }

            AutoRetainer_IPCSubscriber.ReadSchedulerInputs(out bool? anyActive, out bool? taskBusy, out bool? multiModeEnabled);
            this._schedulerPrior = ClassifySchedulerPrior(anyActive, taskBusy, multiModeEnabled);
            if (this._schedulerPrior == SchedulerPrior.Unknown)
                Svc.Log.Information($"[AutoRetainer 排程器] 推不出開跑前是否開著（IsBusy={anyActive?.ToString() ?? "?"}、任務忙={taskBusy?.ToString() ?? "?"}、多角模式={multiModeEnabled?.ToString() ?? "?"}）⇒ 停止時照舊關閉");
            else
                Svc.Log.Debug($"[AutoRetainer 排程器] 開跑前狀態：{this._schedulerPrior}");
        }

        /// <summary>只在框架執行緒送；遊戲關閉中（框架卸載）不送聊天指令。</summary>
        private static void SendSchedulerCommand(string arg)
        {
            if (Svc.Framework.IsFrameworkUnloading)
            {
                Svc.Log.Warning($"[AutoRetainer 排程器] 遊戲正在關閉，略過 /autoretainer {arg}");
                return;
            }
            if (Svc.Framework.IsInFrameworkUpdateThread)
                Chat.ExecuteCommand($"/autoretainer {arg}");
            else
                _ = Svc.Framework.Run(() => Chat.ExecuteCommand($"/autoretainer {arg}"));
        }

        private bool         _autoRetainerStarted = false;
        private bool         _autoRetainerStopped = false;
        private IGameObject? SummoningBellGameObject => Svc.Objects.FirstOrDefault(x => x.BaseId == SummoningBellHelper.SummoningBellDataIds((uint)Plugin.Configuration.PreferredSummoningBellEnum));

        protected override unsafe void HelperStopUpdate(IFramework framework)
        {
            if (!Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                base.HelperStopUpdate(framework);
                if (this._reenableAfterClose && State == ActionState.None)
                {
                    this._reenableAfterClose = false;
                    Svc.Log.Information("[AutoRetainer 排程器] 已離開傳喚鈴，打開回開跑前的狀態");
                    SendSchedulerCommand("e");
                }
            }
            else
            {
                if (Svc.Targets.Target != null)
                    Svc.Targets.Target = null;
                this.CloseAddons();
            }
        }
    

        protected override unsafe void HelperUpdate(IFramework framework)
        {
            if (!this.UpdateBase())
                return;

            if (!PlayerHelper.IsValid) return;

            if (this._autoRetainerStopped)
            {
                if (AutoRetainer_IPCSubscriber.IsBusy())
                {
                    this._autoRetainerStopped = false;
                }
                else
                {
                    this.Stop();
                    return;
                }
            }

            if (!this._autoRetainerStarted && AutoRetainer_IPCSubscriber.IsBusy())
            {
                DebugLog("AutoRetainer has Started");
                this._autoRetainerStarted = true;
                UpdateBaseThrottle        = 1000;
                return;
            }
            else if (this._autoRetainerStarted && !AutoRetainer_IPCSubscriber.IsBusy())
            {
                DebugLog("AutoRetainer is Complete");
                this._autoRetainerStopped = true;
                EzThrottler.Throttle(this.Name, 2000, true);
            }

            if (this.SummoningBellGameObject != null && !SummoningBellHelper.HousingZones.Contains(Player.Territory) && ObjectHelper.GetDistanceToPlayer(this.SummoningBellGameObject) > 4)
            {
                DebugLog("Moving Closer to Summoning Bell");
                MovementHelper.Move(this.SummoningBellGameObject, 0.25f, 4);
            }
            else if ((this.SummoningBellGameObject == null || SummoningBellHelper.HousingZones.Contains(Player.Territory)) && GotoHelper.State != ActionState.Running)
            {
                DebugLog("Moving to Summoning Bell Location");
                SummoningBellHelper.Invoke(Plugin.Configuration.PreferredSummoningBellEnum);
            }
            else if (this.SummoningBellGameObject != null && ObjectHelper.GetDistanceToPlayer(this.SummoningBellGameObject) <= 4 && !this._autoRetainerStarted && !GenericHelpers.TryGetAddonByName("RetainerList", out AtkUnitBase* _) && (ObjectHelper.InteractWithObjectUntilAddon(this.SummoningBellGameObject, "RetainerList") == null))
            {
                if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
                {
                    if (VNavmesh_IPCSubscriber.Path_IsRunning())
                        VNavmesh_IPCSubscriber.Path_Stop();
                    DebugLog("Waiting for AutoRetainer to Start");
                    Chat.ExecuteCommand("/autoretainer e");
                }
                else
                    DebugLog("Interacting with SummoningBell");
                
            }
        }
    }
}
