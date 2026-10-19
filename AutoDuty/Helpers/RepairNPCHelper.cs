using ECommons;
using ECommons.DalamudServices;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoDuty.Helpers
{
    using Lumina.Excel.Sheets;

    public static class RepairNPCHelper
    {
        public class ENpcResidentData
        {
            public uint DataId { get; set; }
            public string Name { get; set; } = string.Empty;
            public uint TerritoryType { get; set; }
            public Vector3 Position { get; set; }
        }

        public class RepairNpcData : ENpcResidentData
        {
            public int RepairIndex { get; set; }
        }

        internal static List<RepairNpcData> RepairNPCs = [];

        private static List<ENpcResidentData> cityENpcResidents = [];

        internal static void PopulateRepairNPCs()
        {
            var enpcResidentsSheet = Svc.Data.GetExcelSheet<ENpcResident>();
            var enpcBaseSheet = Svc.Data.GetExcelSheet<ENpcBase>();
            var levelSheet = Svc.Data.GetExcelSheet<Level>();
            // todo: use IDs instead of english string
            // 這個 Where 只拿來判 null、從來沒被列舉（修理 NPC 是依 ENpcData 找的），所以不先把整張表 ToList。
            var menderENpcResidents = enpcResidentsSheet?.Where(x => x.RowId != 1025308 && (x.Singular.ToString().Contains("mender", StringComparison.InvariantCultureIgnoreCase) || x.Title.ToString().Contains("mender", StringComparison.InvariantCultureIgnoreCase) || x.Title.ToString().Contains("Repairman", StringComparison.InvariantCultureIgnoreCase)));

            var cityAreaTerritoryTypes = Svc.Data.GetExcelSheet<TerritoryType>().ToList().Where(x => x.TerritoryIntendedUse.Value.RowId == 0);

            if (menderENpcResidents == null)
                return;

            BuildEnpcFromLgbFile(cityAreaTerritoryTypes);

            List<(ENpcBase NpcBase, int RepairIndex)> repairNpcBases = [];
            foreach (ENpcBase npcBase in enpcBaseSheet)
            {
                int repairIndex = RepairHandlerIndex(npcBase);
                if (repairIndex >= 0)
                    repairNpcBases.Add((npcBase, repairIndex));
            }

            Dictionary<uint, Level> firstLevelByObject = FirstLevelByObject(levelSheet, repairNpcBases);

            foreach ((ENpcBase npcBase, int repairIndex) in repairNpcBases)
            {
                firstLevelByObject.TryGetValue(npcBase.RowId, out Level level);
                Vector3 position      = Vector3.Zero;
                uint    territoryType = 0;

                if (level.RowId != default)
                {
                    if (level.Territory.Value.RowId != 0 || level.Territory.Value.TerritoryIntendedUse.RowId != 0)
                    {
                        var npc = cityENpcResidents.FirstOrDefault(x => x.DataId == npcBase.RowId);
                        if (npc == null) 
                            continue;

                        RepairNPCs.Add(new RepairNpcData
                                       {
                                           DataId        = npc.DataId,
                                           Name          = npc.Name,
                                           Position      = npc.Position,
                                           TerritoryType = npc.TerritoryType,
                                           RepairIndex   = repairIndex
                                       });
                    }
                    else
                    {
                        var npc = enpcResidentsSheet.GetRow(npcBase.RowId);
                        position      = new Vector3(level.X, level.Y, level.Z);
                        territoryType = level.Territory.Value.RowId;
                        RepairNPCs.Add(new RepairNpcData
                                       {
                                           DataId        = npcBase.RowId,
                                           Name          = npc.Singular.ToString(),
                                           Position      = position,
                                           TerritoryType = territoryType,
                                           RepairIndex   = repairIndex
                                       });
                    }
                }
            }

            RepairNPCs.Sort((first, second) => first.TerritoryType.CompareTo(second.TerritoryType));
            cityENpcResidents = [];
        }

        /// <summary>ENpcData 裡第一個修理事件（720915）的位置，沒有回 -1。用索引走 Collection，不經 IEnumerable 裝箱。</summary>
        private static int RepairHandlerIndex(ENpcBase npcBase)
        {
            var data = npcBase.ENpcData;
            for (int i = 0; i < data.Count; i++)
                if (data[i].RowId == 720915)
                    return i;
            return -1;
        }

        /// <summary>
        /// 每個 NPC 在 Level 表裡第一筆 Object 指向它的列（等同逐一 FirstOrDefault）；沒有的不放進字典，查不到＝default(Level)。
        /// 整張 Level 表只走一遍，取代每個修理 NPC 各掃一遍。
        /// </summary>
        private static Dictionary<uint, Level> FirstLevelByObject(IEnumerable<Level> levelSheet, List<(ENpcBase NpcBase, int RepairIndex)> npcBases)
        {
            HashSet<uint> wanted = [];
            foreach (var entry in npcBases)
                wanted.Add(entry.NpcBase.RowId);

            Dictionary<uint, Level> result = new(wanted.Count);
            if (wanted.Count == 0)
                return result;

            foreach (Level level in levelSheet)
            {
                uint objectId = level.Object.RowId;
                if (wanted.Contains(objectId) && result.TryAdd(objectId, level) && result.Count == wanted.Count)
                    break;
            }

            return result;
        }

        private static void BuildEnpcFromLgbFile(IEnumerable<TerritoryType> territoryTypes)
        {
            foreach (var territoryType in territoryTypes)
            {
                var lgbFile = GetLgbFile(territoryType);

                if (lgbFile == null) continue;

                foreach (var sLgbGroup in lgbFile.Layers)
                {
                    foreach (var instanceObject in sLgbGroup.InstanceObjects)
                    {
                        if (instanceObject.AssetType != LayerEntryType.EventNPC)  continue;
                    
                        var eNPCInstanceObject = (LayerCommon.ENPCInstanceObject)instanceObject.Object;
                        var eNpcResidentDataId = eNPCInstanceObject.ParentData.ParentData.BaseId;
                        
                        if (eNpcResidentDataId == 0) continue;

                        var eNpcResident = Svc.Data.GetExcelSheet<ENpcResident>()?.GetRow(eNpcResidentDataId);
                        var eNpcBase = Svc.Data.GetExcelSheet<ENpcBase>()?.GetRow(eNpcResidentDataId);
                        
                        if (eNpcBase == null || eNpcResident == null) continue;

                        cityENpcResidents.Add(new ENpcResidentData
                        {
                            DataId = eNpcResidentDataId,
                            Name = eNpcResident.Value.Singular.ToString(),
                            Position = new Vector3(instanceObject.Transform.Translation.X, instanceObject.Transform.Translation.Y, instanceObject.Transform.Translation.Z),
                            TerritoryType = territoryType.RowId,
                        });
                    }
                }
            }
            
        }
        private static LgbFile? GetLgbFile(TerritoryType territoryType) => Svc.Data.GetFile<LgbFile>($"bg/{territoryType.Bg.ToString()[..(territoryType.Bg.ToString().IndexOf("/level/") + 1)]}level/planevent.lgb");
    }
}
