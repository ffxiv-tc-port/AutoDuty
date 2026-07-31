using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using AutoDuty.IPC;
using System.Numerics;
using System.Collections.Generic;
using ECommons.Throttlers;

namespace AutoDuty.Helpers
{
    internal static class FollowHelper
    {
        // ⚠️ 不要把 IGameObject 存進欄位跨 tick 用。
        // Dalamud 的 GameObject.Address 在建構時就凍結、永不重新解析
        // (GameObject.cs:137-139,所有屬性都走 Struct => (GameObject*)this.Address),
        // 而 IGameObject.IsValid() 只檢查「玩家有沒有登入」、完全不驗證位址
        // (GameObject.cs:170-177)。所以存 IGameObject == 存一根原生指標。
        // 原本的寫法在護送 NPC 消失、團滅、換區之後仍以 20Hz 持續解參考
        // (ClientState_TerritoryChanged 完全沒有清除它)→ 攔不到的 AccessViolation。
        // 正解:存 GameObjectId,每次用時重查物件表,查不到就自己停下來。
        private static ulong? _followTargetId = null;
        private static float _followDistance = 0.25f;
        private static bool _updateHooked = false;
        private static bool _enabled = false;

        internal static bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (value && !_updateHooked) {
                    _updateHooked = true;
                    Svc.Framework.Update += FollowUpdate;
                }
                else if (!value && _updateHooked)
                {
                    _updateHooked = false;
                    Svc.Framework.Update -= FollowUpdate;
                    VNavmesh_IPCSubscriber.Path_Stop();
                }
            }
        }

        internal static void SetFollow(IGameObject? gameObject, float followDistance = 0)
        {
            if (gameObject != null)
            {
                _followTargetId = gameObject.GameObjectId;
                Enabled = true;
            }
            else
            {
                _followTargetId = null;
                Enabled = false;
            }
            if (followDistance > 0)
                _followDistance = followDistance;
        }

        internal static void SetFollowTarget(IGameObject? gameObject) => _followTargetId = gameObject?.GameObjectId;

        internal static void SetFollowDistance(float f) => _followDistance = f + 0.1f;

        private static void FollowUpdate(IFramework framework)
        {
            if (_followTargetId == null || Svc.ClientState.LocalPlayer == null || !EzThrottler.Throttle("FollowUpdate", 50))
                return;

            // 每次重查物件表。目標消失(護送 NPC 段落結束、團滅、離開副本、換區)時
            // 這裡會拿到 null,於是停止跟隨並歸零——而不是拿舊指標繼續解參考。
            IGameObject? followTarget = Svc.Objects.SearchById(_followTargetId.Value);
            if (followTarget == null)
            {
                _followTargetId = null;
                VNavmesh_IPCSubscriber.Path_Stop();
                return;
            }

            if (ObjectHelper.GetDistanceToPlayer(followTarget) >= _followDistance)
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                List<Vector3> _followTargetList = [followTarget.Position];
                VNavmesh_IPCSubscriber.Path_MoveTo(_followTargetList, false);
            }
        }
    }
}
