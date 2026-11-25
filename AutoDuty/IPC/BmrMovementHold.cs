using ECommons.DalamudServices;
using System;
using SendResult = AutoDuty.Helpers.MovementHoldSession.SendResult;

namespace AutoDuty.IPC
{
    /// <summary>
    /// BossModReborn 的「暫停自動移動與閃避」租約端點（<c>BossMod.AI.SuppressMovement</c>／<c>ReleaseMovementSuppression</c>）。
    /// 租約在 BMR 端自己到期，血量比低於 minHpRatio 時 BMR 端也不壓制 ⇒ 這裡掛掉或停擺時 BMR 最晚到期後恢復。
    /// </summary>
    /// <remarks>
    /// 不走 EzIPC／SafeWrapper：要分得出「端點不存在（舊版 BMR）」與「被拒收」，所以直接看 <c>HasFunction</c>。
    /// 任何例外（含型別對不上）一律當成端點不存在，不讓它打斷 Framework.Update。
    /// </remarks>
    internal static class BmrMovementHold
    {
        private const string SuppressEndpoint = "BossMod.AI.SuppressMovement";
        private const string ReleaseEndpoint  = "BossMod.AI.ReleaseMovementSuppression";

        internal static SendResult Suppress(float seconds, float minHpRatio)
        {
            try
            {
                if (!BossModReborn_IPCSubscriber.IsEnabled)
                    return SendResult.Unavailable;
                var gate = Svc.PluginInterface.GetIpcSubscriber<string, float, float, bool>(SuppressEndpoint);
                if (!gate.HasFunction)
                    return SendResult.Unavailable;
                return gate.InvokeFunc(Svc.PluginInterface.InternalName, seconds, minHpRatio) ? SendResult.Ok : SendResult.Rejected;
            }
            catch (Exception)
            {
                return SendResult.Unavailable;
            }
        }

        /// <summary>交回租約；端點不在或失敗就算了（BMR 端租約自己會到期）。</summary>
        internal static void Release()
        {
            try
            {
                var gate = Svc.PluginInterface.GetIpcSubscriber<string, bool>(ReleaseEndpoint);
                if (gate.HasFunction)
                    gate.InvokeFunc(Svc.PluginInterface.InternalName);
            }
            catch (Exception)
            {
            }
        }
    }
}
