using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoDuty.Helpers
{
    using System;
    using System.Collections.Generic;
    using static FFXIVClientStructs.FFXIV.Client.Game.GcArmyManager.Delegates;

    public static class ActiveHelper
    {
        internal static HashSet<IActiveHelper> activeHelpers = [];
    }

    internal interface IActiveHelper
    {
        internal        void        StopIfRunning();
    }

    internal abstract class ActiveHelperBase<T> : IActiveHelper where T : ActiveHelperBase<T>, new()
    {
        protected abstract string   Name          { get; }
        protected abstract string   DisplayName   { get; }

        protected virtual string[] AddonsToClose { get; } = [];

        protected virtual int TimeOut { get; set; } = 300_000;

        private static T? instance;
        public static T Instance
        {
            get
            {
                T helper = new();
                ActiveHelper.activeHelpers.Add(helper);
                return instance ??= helper;
            }
        }


        internal static void Invoke()
        {
            Instance.Start();
        }

        internal virtual void Start()
        {
            if(State == ActionState.Running)
            {
                this.DebugLog(this.Name + " already running");
                return;
            }
            this.InfoLog(this.Name + " started");
            State         =  ActionState.Running;
            Plugin.States |= PluginState.Other;

            if (!Plugin.States.HasFlag(PluginState.Looping))
                Plugin.SetGeneralSettings(false);

            if(this.TimeOut > 0)
                SchedulerHelper.ScheduleAction($"Helper_{this.Name}_TimeOut", this.Stop, this.TimeOut);

            if (this.DisplayName != string.Empty)
                Plugin.Action = this.DisplayName;
            Svc.Framework.Update += this.HelperUpdate;
        }

        internal static ActionState State  = ActionState.None;

        internal static void ForceStop()
        {
            instance?.Stop();
        }

        public void StopIfRunning()
        {
            if(State == ActionState.Running)
                this.Stop();
        }

        internal virtual void Stop()
        {
            if (State == ActionState.Running)
                this.InfoLog(this.Name + " finished");

            if (this.DisplayName != string.Empty)
                Plugin.Action = string.Empty;

            if (!Plugin.States.HasFlag(PluginState.Looping))
                Plugin.SetGeneralSettings(false);

            SchedulerHelper.DescheduleAction($"Helper_{this.Name}_TimeOut");

            Svc.Framework.Update += this.HelperStopUpdate;
            Svc.Framework.Update -= this.HelperUpdate;
        }

        protected abstract unsafe void HelperUpdate(IFramework framework);

        protected virtual int UpdateBaseThrottle { get; set; } = 500;

        protected bool UpdateBase()
        {
            if (Plugin.States.HasFlag(PluginState.Navigating) || Plugin.InDungeon)
            {
                this.Stop();
                return false;
            }

            if (!EzThrottler.Throttle(this.Name, this.UpdateBaseThrottle))
                return false;

            if (GotoHelper.State == ActionState.Running)
            {
                //Svc.Log.Debug("Goto Running");
                return false;
            }

            return true;
        }

        protected virtual unsafe void HelperStopUpdate(IFramework framework)
        {
            if (!this.CloseAddons())
                return;

            State         =  ActionState.None;
            Plugin.States &= ~PluginState.Other;

            if (!Plugin.States.HasFlag(PluginState.Looping))
                Plugin.SetGeneralSettings(true);
            Svc.Framework.Update -= this.HelperStopUpdate;
        }

        /// <remarks>
        /// 🔴 這支掛在 <see cref="HelperStopUpdate"/> 上<b>每幀</b>跑到清單全空為止,而 <c>Close(true)</c> 的 <c>true</c>
        /// 就是 fireCallback —— 對「前一幀才被 <c>ClickSelectYesno</c>/<c>ClickTalk</c> 按過、正在關閉中」的窗
        /// 再送一次 callback 就是攔不到的存取違規(<c>IsVisible</c> 在那幾幀仍然是 true,擋不住)。
        /// 所以每一發 <c>Close(true)</c> 都先過 <see cref="AddonPressGuard.TryBeginClose"/>:
        /// 同一位址上任何按法還在逃生口內就這一幀不關,照原本的「還沒關完」路徑回 false,下一幀再來。
        /// </remarks>
        public unsafe bool CloseAddons()
        {
            for (int i = 0; i < this.AddonsToClose.Length; i++)
            {
                if (GenericHelpers.TryGetAddonByName(this.AddonsToClose[i], out AtkUnitBase* atkUnitBase) && atkUnitBase->IsVisible)
                {
                    if (AddonPressGuard.TryBeginClose(this.AddonsToClose[i], atkUnitBase))
                    {
                        this.DebugLog("Closing Addon " + this.AddonsToClose[i]);
                        atkUnitBase->Close(true);
                    }

                    return false;
                }
            }

            return true;
        }

        protected void DebugLog(string s)
        {
            Svc.Log.Debug($"{this.Name}: {s}");
        }

        protected void InfoLog(string s)
        {
            Svc.Log.Info($"{this.Name}: {s}");
        }
    }
}
