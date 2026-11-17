using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.IPC.Subscribers;
using ECommons.IPC.Subscribers.AutoRetainer;
using ECommons.IPC.Subscribers.BossMod;
using ECommons.IPC.Subscribers.Gearsetter;
using ECommons.IPC.Subscribers.PandorasBox;
using ECommons.IPC.Subscribers.Vnavmesh;
using ECommons.IPC.Subscribers.YesAlready;
using ECommons.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WrathCombo.API;
using ApiConfigOption = WrathCombo.API.Enum.AutoRotationConfigOption;
using ApiDpsMode = WrathCombo.API.Enum.DPSRotationMode;
using ApiHealerMode = WrathCombo.API.Enum.HealerRotationMode;
using ApiSetResult = WrathCombo.API.Enum.SetResult;
#nullable disable

// 門面層的共同規則（wrapper 明確傳入建構式等）見 IPCSubscriber.cs 檔頭。

namespace AutoDuty.IPC
{
    using System.ComponentModel;
    using ECommons.GameFunctions;
    using Helpers;

    /// <summary>
    /// YesAlready 門面。壓制改走<b>具名租約</b>，提供端給不了時退回舊的開關寫入。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>改用租約的理由＝舊的開關沒有主人。</b><c>SetPluginEnabled</c> 寫的是單一格全域
    /// 布林 <c>C.Enabled</c>，而 Questionable／SomethingNeedDoing 也寫同一格：AutoDuty 跑本
    /// 副本時巨集碰一下那個開關，離開副本時我們又無條件寫回 <see langword="true"/> ——
    /// 結果不是「整趟 YesAlready 一直開著搶按窗」就是「別人的壓制被我們掀掉」。<b>全程零訊息。</b>
    /// <para>
    /// 🔑 租約是<b>記名</b>的 refcount：我們只放開自己那一把，也完全不碰使用者的開關。
    /// 長時間的多輪本要靠 <see cref="Tick"/> 續約（提供端上限 5 分鐘）。
    /// </para>
    /// </remarks>
    internal static class YesAlready_IPCSubscriber
    {
        /// <summary>租約登記的名字，會出現在 YesAlready 的 log 與設定視窗。</summary>
        private const string LeaseOwner = "AutoDuty";

        /// <summary>每次取得／續約要求的租期（5 分鐘）＝提供端的硬性上限。</summary>
        /// <remarks>
        /// 🔑 全艦隊的壓制租約時間政策統一成「租 5 分鐘、每 30 秒續約」（AutoRetainer 那套
        /// 本來就是這個值）。取捨是：租期短 ⇒ 我們當掉或被卸載時，使用者最多等 5 分鐘
        /// YesAlready 就自己恢復；續約間隔留 10 倍餘裕 ⇒ 要連續漏掉 9 次心跳才會真的過期。
        /// <para>
        /// 🔴 這個值<b>不可以</b>大於提供端的上限：提供端是<b>夾值不是拒絕</b>，要多了只會
        /// 被靜默砍短，續約反而會來不及。
        /// </para>
        /// </remarks>
        private const int LeaseMilliseconds = 300_000;

        /// <summary>續約間隔（30 秒），是 <see cref="LeaseMilliseconds"/> 的十分之一。</summary>
        private const int RenewIntervalMilliseconds = 30_000;

        private static readonly YesAlreadyIPC Pkg = new(SafeWrapper.IPCException);

        /// <summary>我們<b>認為</b>現在應該壓著（由 <see cref="SetState"/> 驅動）。</summary>
        private static bool _suppressing;

        /// <summary>目前持有的租約；<see cref="Guid.Empty"/>＝沒有。</summary>
        private static Guid _lease;

        /// <summary>
        /// 有沒有走 fail-safe 舊路徑（真的寫過 <c>SetPluginEnabled(false)</c>）。
        /// </summary>
        /// <remarks>
        /// 🔴 這個旗標決定解除時<b>要不要把使用者的開關寫回去</b>。混用兩條路（先舊路徑壓下去、
        /// 後來又升級成租約）會讓解除時只放開租約而忘了寫回開關 ⇒ 使用者的 YesAlready
        /// <b>永遠關著</b>。所以一旦走了舊路徑就<b>不再升級</b>成租約。
        /// </remarks>
        private static bool _legacyEngaged;

        /// <summary><see cref="Environment.TickCount64"/> 座標系的下次續約時刻。</summary>
        private static long _nextRenewAt;

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("YesAlready");

        public static bool IsPluginEnabled() => Pkg.IsPluginEnabled();

        internal static void Dispose() => YesAlreadyExtraIPC.Dispose();

        /// <summary>
        /// <paramref name="on"/>＝<see langword="false"/> 請 YesAlready 讓開；
        /// <see langword="true"/> 解除。冪等。
        /// </summary>
        public static void SetState(bool on)
        {
            if (!on)
            {
                if (_suppressing)
                    return;

                _suppressing = true;

                if (TryAcquireLease())
                    return;

                // ── fail-safe：提供端沒裝、或版本太舊沒有租約端點 ⇒ 退回舊的開關寫入 ──
                Svc.Log.Information("[AutoDuty] YesAlready 沒有壓制租約端點（沒安裝或版本太舊），退回舊的開關寫入");
                _legacyEngaged = true;
                Pkg.SetPluginEnabled(false);
                return;
            }

            _suppressing = false;
            ReleaseLease();

            // 當初是走舊路徑壓下去的，還原也要走舊路徑，否則使用者的開關會永遠關著。
            if (_legacyEngaged)
            {
                _legacyEngaged = false;
                Pkg.SetPluginEnabled(true);
            }
        }

        /// <summary>
        /// 續約心跳。由 <c>AutoDuty.Framework_Update</c> 每幀呼叫，內部自行節流。
        /// </summary>
        /// <remarks>
        /// 🔴 一輪多本可以跑好幾個小時，而租約上限只有 5 分鐘 ⇒ 不續約的話 YesAlready
        /// 會在副本跑到一半自己醒過來搶按窗。續約回 <see langword="false"/> 代表那把已經
        /// 不在了，必須<b>重新取得</b>，不能繼續假設自己還壓著。
        /// </remarks>
        internal static void Tick()
        {
            // 沒在壓、或走的是舊路徑（舊路徑沒有到期時間，不需要心跳）。
            if (!_suppressing || _legacyEngaged || _lease == Guid.Empty)
                return;

            if (Environment.TickCount64 < _nextRenewAt)
                return;

            _nextRenewAt = Environment.TickCount64 + RenewIntervalMilliseconds;

            bool renewed;
            try
            {
                renewed = YesAlreadyExtraIPC.RenewSuppressionFor(_lease, LeaseMilliseconds);
            }
            catch
            {
                // SafeWrapper 只吃 IpcNotReadyError；別的例外不能讓它打斷 Framework.Update。
                renewed = false;
            }

            if (renewed)
                return;

            Svc.Log.Information($"[AutoDuty] YesAlready 壓制租約 {_lease} 已經不在了，重新取得一把");
            _lease = Guid.Empty;

            if (TryAcquireLease())
                return;

            // 完全拿不到（YesAlready 被卸載或重載過？）：退回舊路徑以維持壓制，
            // 解除時 _legacyEngaged 會負責把開關寫回去。
            Svc.Log.Information("[AutoDuty] 重新取得 YesAlready 壓制租約失敗，退回舊的開關寫入");
            _legacyEngaged = true;
            Pkg.SetPluginEnabled(false);
        }

        /// <summary>取一把租約。回 <see langword="false"/>＝提供端給不了。</summary>
        private static bool TryAcquireLease()
        {
            Guid lease;
            try
            {
                lease = YesAlreadyExtraIPC.AcquireSuppressionFor(LeaseOwner, LeaseMilliseconds);
            }
            catch
            {
                lease = Guid.Empty;
            }

            if (lease == Guid.Empty)
                return false;

            _lease = lease;
            _nextRenewAt = Environment.TickCount64 + RenewIntervalMilliseconds;
            Svc.Log.Information($"[AutoDuty] 已向 YesAlready 取得壓制租約 {lease}（{LeaseMilliseconds} 毫秒）");
            return true;
        }

        /// <summary>交回租約（沒有就什麼都不做）。冪等。</summary>
        private static void ReleaseLease()
        {
            if (_lease == Guid.Empty)
                return;

            var lease = _lease;

            // 🔴 先清欄位再送出：送出途中擲例外的話手上這把也已經是廢的了。
            _lease = Guid.Empty;

            try
            {
                YesAlreadyExtraIPC.ReleaseSuppression(lease);
            }
            catch
            {
                // 交不回去也不要緊：提供端會讓它自行逾時。
            }

            Svc.Log.Information($"[AutoDuty] 已交回 YesAlready 壓制租約 {lease}");
        }
    }
}
