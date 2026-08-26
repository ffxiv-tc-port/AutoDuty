using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;
using AutoDuty.Helpers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoDuty.Data
{
    using ECommons.GameHelpers;
    using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

    /// <summary>
    ///     路徑步驟的執行條件。一個步驟可以掛 0..n 個條件,全部成立才會執行,
    ///     否則整個步驟被跳過（indexer 直接往下走）。
    ///     <para>
    ///     序列化格式必須與上游逐字相容：上游用 Newtonsoft 的 <c>TypeNameHandling</c>，
    ///     判別碼長 <c>"AutoDuty.Data.PathActionConditionXxx, AutoDuty"</c>。
    ///     我方用 System.Text.Json，所以把同一串字面值當成
    ///     <see cref="JsonDerivedTypeAttribute"/> 的判別值 —— 這樣兩邊的 path json
    ///     可以互相讀寫，上游同步不需要改檔。
    ///     </para>
    ///     <para>
    ///     🔴 每個 <see cref="IsFulfilled"/> 都在單一幀內解析並用完物件，
    ///     不得把 <see cref="IGameObject"/> 或原生指標存進欄位跨幀留存。
    ///     </para>
    /// </summary>
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type",
                     UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
    [JsonDerivedType(typeof(PathActionConditionDistance),      "AutoDuty.Data.PathActionConditionDistance, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionItemCount),     "AutoDuty.Data.PathActionConditionItemCount, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionObjectData),    "AutoDuty.Data.PathActionConditionObjectData, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionJob),           "AutoDuty.Data.PathActionConditionJob, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionActionStatus),  "AutoDuty.Data.PathActionConditionActionStatus, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionVariantPath),   "AutoDuty.Data.PathActionConditionVariantPath, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionConditionFlag), "AutoDuty.Data.PathActionConditionConditionFlag, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionNot),           "AutoDuty.Data.PathActionConditionNot, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionOr),            "AutoDuty.Data.PathActionConditionOr, AutoDuty")]
    [JsonDerivedType(typeof(PathActionConditionAnd),           "AutoDuty.Data.PathActionConditionAnd, AutoDuty")]
    public abstract class PathActionCondition
    {
        [JsonIgnore]
        public abstract ConditionType ParseKey { get; }

        [JsonIgnore]
        public static readonly Dictionary<string, Func<object, object, bool>> Operations = new()
        {
            { ">",  (x, y) => Convert.ToSingle(x) >  Convert.ToSingle(y) },
            { ">=", (x, y) => Convert.ToSingle(x) >= Convert.ToSingle(y) },
            { "<",  (x, y) => Convert.ToSingle(x) <  Convert.ToSingle(y) },
            { "<=", (x, y) => Convert.ToSingle(x) <= Convert.ToSingle(y) },
            { "==", (x, y) => Convert.ToSingle(x) == Convert.ToSingle(y) },
            { "!=", (x, y) => Convert.ToSingle(x) != Convert.ToSingle(y) }
        };

        /// <summary>條件是否成立。實作必須自己吞掉例外語意上的「不知道」,回 false 代表不執行該步驟。</summary>
        public abstract bool IsFulfilled();

        /// <summary>給路徑清單一行顯示用的短描述（純文字,不含 ImGui 依賴）。</summary>
        public abstract string Describe();
    }

    public class PathActionConditionNot : PathActionCondition
    {
        public override ConditionType ParseKey => ConditionType.Not;

        public PathActionCondition? condition;

        // 沒掛子條件時視為成立（等同上游：null 就不擋）。
        public override bool IsFulfilled() => !this.condition?.IsFulfilled() ?? true;

        public override string Describe() => $"非({this.condition?.Describe() ?? "-"})";
    }

    public class PathActionConditionJob : PathActionCondition
    {
        public override ConditionType ParseKey => ConditionType.Job;

        [JsonConverter(typeof(JsonStringEnumConverter<JobWithRole>))]
        public JobWithRole job = JobWithRole.All;

        public override bool IsFulfilled() => this.job.HasJob(PlayerHelper.GetJob());

        public override string Describe() => $"職業={this.job}";
    }

    public class PathActionConditionActionStatus : PathActionCondition
    {
        public override ConditionType ParseKey => ConditionType.ActionStatus;

        [JsonConverter(typeof(JsonStringEnumConverter<ActionType>))]
        public ActionType type = ActionType.Action;

        public uint id;
        public uint statusCode;

        public override unsafe bool IsFulfilled() =>
            ActionManager.Instance()->GetActionStatus(this.type, this.id) == this.statusCode;

        public override string Describe() => $"技能狀態={this.type}:{this.id}=={this.statusCode}";
    }

    public class PathActionConditionItemCount : PathActionCondition
    {
        public override ConditionType ParseKey => ConditionType.ItemCount;

        public uint   itemId;
        public uint   quantity;
        public string operatorValue = ">";

        public override bool IsFulfilled()
        {
            if (!Operations.TryGetValue(this.operatorValue, out Func<object, object, bool>? operationFunc))
                return false;

            return operationFunc(InventoryHelper.ItemCount(this.itemId), this.quantity);
        }

        public override string Describe() => $"物品{this.itemId}{this.operatorValue}{this.quantity}";
    }

    public class PathActionConditionObjectData : PathActionCondition
    {
        public override ConditionType ParseKey => ConditionType.ObjectData;

        // 名稱沿用上游的 json 鍵 baseId,但查表走本 repo 既有的 ObjectHelper.GetObjectByDataId
        // （比對 IGameObject.DataId）。上游同步時若整批改成 BaseId 要連這裡一起改。
        public uint baseId;

        [JsonConverter(typeof(JsonStringEnumConverter<ObjectDataProperty>))]
        public ObjectDataProperty property;

        public int value;

        public override unsafe bool IsFulfilled()
        {
            // 當幀查表、當幀用完:不把 IGameObject 或 GameObject* 留到下一幀。
            IGameObject? gameObject = ObjectHelper.GetObjectByDataId(this.baseId);
            if (gameObject == null)
                return false;

            GameObjectStruct* csObj = gameObject.Struct();
            if (csObj == null)
                return false;

            return this.property switch
            {
                ObjectDataProperty.EventState   => csObj->EventState        == (byte)this.value,
                ObjectDataProperty.IsTargetable => csObj->GetIsTargetable() == (this.value != 0),
                _                               => false
            };
        }

        public override string Describe() => $"物件{this.baseId}.{this.property}=={this.value}";
    }

    public class PathActionConditionDistance : PathActionCondition
    {
        public override ConditionType ParseKey => ConditionType.Distance;

        [JsonConverter(typeof(JsonStringEnumConverter<DistanceLocationTypes>))]
        public DistanceLocationTypes origin = DistanceLocationTypes.Location;

        public uint    originId;
        public Vector3 originLoc;

        [JsonConverter(typeof(JsonStringEnumConverter<DistanceLocationTypes>))]
        public DistanceLocationTypes target = DistanceLocationTypes.Player;

        public uint    targetId;
        public Vector3 targetLoc;

        public string operatorValue = "<";
        public float  distance      = 1f;

        private static unsafe bool TryResolve(DistanceLocationTypes kind, uint dataId, Vector3 loc, out Vector3 result)
        {
            switch (kind)
            {
                case DistanceLocationTypes.Player:
                    if (!Player.Available)
                    {
                        result = Vector3.Zero;
                        return false;
                    }

                    result = Player.GameObject->Position;
                    return true;
                case DistanceLocationTypes.Object:
                    // 只讀當幀查到的座標,不保留物件。
                    IGameObject? gameObject = ObjectHelper.GetObjectByDataId(dataId);
                    if (gameObject == null)
                    {
                        result = Vector3.Zero;
                        return false;
                    }

                    result = gameObject.Position;
                    return true;
                case DistanceLocationTypes.Location:
                    result = loc;
                    return true;
                default:
                    result = Vector3.Zero;
                    return false;
            }
        }

        public override bool IsFulfilled()
        {
            // 端點解不出來（物件不在場、還沒登入）＝條件不成立,而不是丟例外。
            if (!TryResolve(this.origin, this.originId, this.originLoc, out Vector3 originVec) ||
                !TryResolve(this.target, this.targetId, this.targetLoc, out Vector3 targetVec))
                return false;

            return Operations.TryGetValue(this.operatorValue, out Func<object, object, bool>? operationFunc) &&
                   operationFunc(Vector3.Distance(originVec, targetVec), this.distance);
        }

        public override string Describe() => $"距離{this.operatorValue}{this.distance:0.##}";
    }

    public class PathActionConditionConditionFlag : PathActionCondition
    {
        public override ConditionType ParseKey => ConditionType.ConditionFlag;

        [JsonConverter(typeof(JsonStringEnumConverter<ConditionFlag>))]
        public ConditionFlag flag;

        public override bool IsFulfilled() => Svc.Condition[this.flag];

        public override string Describe() => $"狀態旗標={this.flag}";
    }

    public class PathActionConditionVariantPath : PathActionCondition
    {
        public override ConditionType ParseKey => ConditionType.VariantPath;

        public List<byte> pathIndices = [];

        public override bool IsFulfilled() => this.pathIndices.Contains(Plugin.VariantPath);

        public override string Describe() => $"多變迷宮分歧={string.Join(",", this.pathIndices)}";
    }

    public abstract class PathActionConditionLogicCollection : PathActionCondition
    {
        public List<PathActionCondition> conditions = [];
    }

    public class PathActionConditionOr : PathActionConditionLogicCollection
    {
        public override ConditionType ParseKey => ConditionType.Or;

        public override bool IsFulfilled() => this.conditions.Count > 0 && this.conditions.Any(x => x.IsFulfilled());

        public override string Describe() => $"任一({string.Join(" | ", this.conditions.Select(x => x.Describe()))})";
    }

    public class PathActionConditionAnd : PathActionConditionLogicCollection
    {
        public override ConditionType ParseKey => ConditionType.And;

        public override bool IsFulfilled() => this.conditions.Count > 0 && this.conditions.All(x => x.IsFulfilled());

        public override string Describe() => $"全部({string.Join(" & ", this.conditions.Select(x => x.Describe()))})";
    }
}
