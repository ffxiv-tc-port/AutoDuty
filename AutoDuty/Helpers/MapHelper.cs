using ECommons.DalamudServices;
using ECommons.MathHelpers;
using System.Numerics;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ECommons.Throttlers;
using AutoDuty.IPC;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoDuty.Helpers
{
    using Lumina.Excel.Sheets;

    internal static class MapHelper
    {
        // 🔴 AgentMap.Instance() 是 [Agent] 產生器產出的取得子,AgentModule 與代理人兩層都能合法回 null
        //    (換區/登入途中 AgentModule 還沒建好);裸解參考 = AccessViolationException,try/catch 攔不到。
        // fail-closed:讀不到就當「沒有標旗」。呼叫端對 false 的既有反應是「不走標旗路線」,那是本來就會走到的分支。
        internal static unsafe bool IsFlagMarkerSet
        {
            get
            {
                AgentMap* agentMap = AgentMap.Instance();
                return agentMap != null && agentMap->FlagMarkerCount > 0;
            }
        }

        // 只在 IsFlagMarkerSet 為 true 時才會被讀,但各自判空 ——
        // 屬性是分開的兩次呼叫,不共用上面那次的結果。
        internal static unsafe FlagMapMarker GetFlagMarker
        {
            get
            {
                AgentMap* agentMap = AgentMap.Instance();
                return agentMap == null ? default : agentMap->FlagMapMarkers[0];
            }
        }

        internal static Vector2 ConvertWorldXZToMap(Vector2 coords, Map map) => Dalamud.Utility.MapUtil.WorldToMap(coords, map.OffsetX, map.OffsetY, map.SizeFactor);

        internal static Vector2 ConvertMarkerToMap(MapMarker mapMarker, Map map) => new((float)(mapMarker.X * 42.0 / 2048 / map.SizeFactor * 100 + 1), (float)(mapMarker.Y * 42.0 / 2048 / map.SizeFactor * 100 + 1));

        internal static Aetheryte? GetAetheryteForAethernet(Aetheryte aetheryte) => Svc.Data.GetExcelSheet<Aetheryte>()?.FirstOrDefault(x => x.IsAetheryte == true && x.AethernetGroup == aetheryte.AethernetGroup);

        internal static Aetheryte? GetClosestAethernet(uint territoryType, Vector3 location)
        {
            var closestDistance = float.MaxValue;
            Aetheryte? closestAetheryte = null;
            var map = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryType)?.Map.Value;
            var aetherytes = Svc.Data.GetExcelSheet<Aetheryte>();

            if (aetherytes == null || map == null)
                return null;

            foreach (var aetheryte in aetherytes)
            {
                if (( aetheryte.IsAetheryte && aetheryte.Territory.RowId != territoryType ) || aetheryte.Territory.ValueNullable == null || aetheryte.Territory.Value.RowId != territoryType) continue;
                MapMarker mapMarker = Svc.Data.GetSubrowExcelSheet<MapMarker>().AllRows().FirstOrDefault(m => m.DataType == 4 && m.DataKey.RowId == aetheryte.AethernetName.RowId);

                if (mapMarker.RowId > 0)
                {
                    var distance = Vector2.Distance(ConvertWorldXZToMap(location.ToVector2(), map.Value), ConvertMarkerToMap(mapMarker, map.Value));

                    if (distance < closestDistance)
                    {
                        closestDistance  = distance;
                        closestAetheryte = aetheryte;
                    }
                }
            }

            return closestAetheryte;
        }

        internal static Aetheryte? GetClosestAetheryte(uint territoryType, Vector3 location)
        {
            var closestDistance = float.MaxValue;
            Aetheryte? closestAetheryte = null;
            var map = Svc.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryType)?.Map.Value;
            var aetherytes = Svc.Data.GetExcelSheet<Aetheryte>();

            if (aetherytes == null || map == null)
                return null;

            foreach (var aetheryte in aetherytes)
            {
                if (!aetheryte.IsAetheryte || aetheryte.Territory.ValueNullable == null || aetheryte.Territory.Value.RowId != territoryType || aetheryte.PlaceName.ValueNullable == null) continue;

                var mapMarker = Svc.Data.GetSubrowExcelSheet<MapMarker>().Flatten().FirstOrDefault(m => m.DataType == 3 && m.DataKey.RowId == aetheryte.RowId);

                var distance = Vector2.Distance(ConvertWorldXZToMap(location.ToVector2(), map.Value), ConvertMarkerToMap(mapMarker, map.Value));

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestAetheryte = aetheryte;
                }
            }

            return closestAetheryte;
        }

        internal static void MoveToMapMarker()
        {
            if (!IsFlagMarkerSet)
            {
                Svc.Log.Info("There is no flag marker set");
                return;
            }
            // 已在跑就不再掛第二份：停止只拆一份，殘留那份會在停止後自己重新出發。
            if (State == ActionState.Running)
            {
                Svc.Log.Info("Already moving to Flag Marker");
                return;
            }
            Svc.Log.Info("Moving to Flag Marker");
            State = ActionState.Running;
            Plugin.States |= PluginState.Other;
            if (!Plugin.States.HasFlag(PluginState.Looping))
                Plugin.SetGeneralSettings(false);
            Svc.Framework.Update -= MoveToMapMarkerUpdate;
            Svc.Framework.Update += MoveToMapMarkerUpdate;
        }

        internal static ActionState State = ActionState.None;

        private static Vector3? flagMapMarkerVector3 = Vector3.Zero;
        private static FlagMapMarker? flagMapMarker = null;

        internal unsafe static void StopMoveToMapMarker()
        {
            Svc.Framework.Update -= MoveToMapMarkerUpdate;
            VNavmesh_IPCSubscriber.Path_Stop();
            State = ActionState.None;
            Plugin.States &= ~PluginState.Other;
            if (!Plugin.States.HasFlag(PluginState.Looping))
                Plugin.SetGeneralSettings(true);
            flagMapMarker = null;
        }

        internal unsafe static void MoveToMapMarkerUpdate(IFramework _)
        {
            // 不變式：只在 Running 時做事；任何停止路徑漏拆的殘留訂閱在這裡自行退訂，不會再叫 GotoHelper。
            if (State != ActionState.Running)
            {
                Svc.Framework.Update -= MoveToMapMarkerUpdate;
                return;
            }

            if (!EzThrottler.Throttle("MoveToMapMarker"))
                return;

            if (!PlayerHelper.IsReady)
                return;

            if (flagMapMarker != null && Svc.ClientState.TerritoryType == flagMapMarker.Value.TerritoryId && ObjectHelper.GetDistanceToPlayer(flagMapMarkerVector3!.Value) < 2)
            {
                StopMoveToMapMarker();
                GotoHelper.ForceStop();
                return;
            }

            if (flagMapMarker != null && Svc.ClientState.TerritoryType == flagMapMarker.Value.TerritoryId && flagMapMarkerVector3 != null && flagMapMarkerVector3.Value.Y == 0)
            {
                // 🔴 提供端(vnavmesh Query.Mesh.PointOnFloor)回的是 Vector3?：查不到落點時是 null。
                //    宣告成 Vector3 的話,null 會在 Dalamud 的 CallGateChannel.InvokeFunc 拆箱值型別時擲 NullReferenceException,
                //    而 SafeWrapper.IPCException 只攔 IpcNotReadyError,攔不到,整個 Framework.Update 處理器會在這一行中止。
                // 🔴 不要寫 ?? Vector3.Zero —— 那會把「查不到落點」變成「落點在地圖原點」然後真的走過去。
                //    null ＝ 這一輪不動,下個 tick 再試(flagMapMarkerVector3 的 Y 仍是 0,上面的守衛條件會再進來)。
                Vector3? floorPoint = VNavmesh_IPCSubscriber.Query_Mesh_PointOnFloor(new(flagMapMarker.Value.XFloat, 1024, flagMapMarker.Value.YFloat), false, 5);
                if (floorPoint == null)
                {
                    Svc.Log.Debug("[MapHelper] vnavmesh 找不到旗標座標的落點,這一輪不動,下個 tick 再試。");
                    return;
                }
                flagMapMarkerVector3 = floorPoint;
                GotoHelper.ForceStop();
                GotoHelper.Invoke(flagMapMarker.Value.TerritoryId, [floorPoint.Value], 0.25f, 0.25f, false, MovementHelper.IsFlyingSupported);
                return;
            }

            if (GotoHelper.State == ActionState.Running)
                return;

            if (VNavmesh_IPCSubscriber.Path_IsRunning())
                return;

            // 每個 tick 重跑到地圖窗消失為止;Close(true) 會送 callback,關閉中那幾幀 IsAddonReady 三關全過 —— 過守衛。
            if (GenericHelpers.TryGetAddonByName("AreaMap", out AtkUnitBase* addonAreaMap) && GenericHelpers.IsAddonReady(addonAreaMap)
                && AddonPressGuard.TryBeginClose("AreaMap", addonAreaMap))
                addonAreaMap->Close(true);

            if (IsFlagMarkerSet)
            {
                flagMapMarker = GetFlagMarker;
                flagMapMarkerVector3 = new Vector3(flagMapMarker.Value.XFloat, 0, flagMapMarker.Value.YFloat);
                GotoHelper.Invoke(flagMapMarker.Value.TerritoryId, [flagMapMarkerVector3.Value], 0.25f, 0.25f, false, MovementHelper.IsFlyingSupported);
            }
        }
    }
}
