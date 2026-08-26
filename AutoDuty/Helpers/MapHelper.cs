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
        // 🔴 AgentMap.Instance() 是 [Agent] 產生器產出的取得子,本體即
        //    「agentModule == null ? null : (AgentMap*)agentModule->GetAgentByInternalId(...)」,
        //    兩層都能合法回 null(換區/登入途中 AgentModule 還沒建好)。
        //    裸解參考 = AccessViolationException,在 .NET Core 屬 corrupted-state exception,
        //    try/catch 與 HookSafety.ExecuteSafe 全部攔不到。
        // fail-closed:讀不到就當「沒有標旗」。呼叫端(88 行、149 行)對 false 的既有反應是
        //    「不走標旗路線」,那是本來就會走到的分支。
        internal static unsafe bool IsFlagMarkerSet
        {
            get
            {
                AgentMap* agentMap = AgentMap.Instance();
                return agentMap != null && agentMap->FlagMarkerCount > 0;
            }
        }

        // 只在 IsFlagMarkerSet 為 true 時才會被讀(151 行),但各自判空 ——
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
            Svc.Log.Info("Moving to Flag Marker");
            State = ActionState.Running;
            Plugin.States |= PluginState.Other;
            if (!Plugin.States.HasFlag(PluginState.Looping))
                Plugin.SetGeneralSettings(false);
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
                flagMapMarkerVector3 = VNavmesh_IPCSubscriber.Query_Mesh_PointOnFloor(new(flagMapMarker.Value.XFloat, 1024, flagMapMarker.Value.YFloat), false, 5);
                GotoHelper.ForceStop();
                GotoHelper.Invoke(flagMapMarker.Value.TerritoryId, [flagMapMarkerVector3.Value], 0.25f, 0.25f, false, MovementHelper.IsFlyingSupported);
                return;
            }

            if (GotoHelper.State == ActionState.Running)
                return;

            if (VNavmesh_IPCSubscriber.Path_IsRunning())
                return;

            if (GenericHelpers.TryGetAddonByName("AreaMap", out AtkUnitBase* addonAreaMap) && GenericHelpers.IsAddonReady(addonAreaMap))
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
