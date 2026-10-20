using System;

namespace AutoDuty.Helpers;

/// <summary>MovementHelper.Move 交給 vnavmesh 之前的「抵達／上坐騎／起飛」判斷;只吃數值、不碰遊戲狀態,可以離線驗。</summary>
internal static class MountMoveLogic
{
    /// <summary>到目標的直線距離小於這個值就不上坐騎、不起飛,直接走(與 Questionable 採集、visland 的預設同值)。</summary>
    internal const float MountMinDistance = 20f;

    internal enum Step { Arrived, Mount, TakeOff, Proceed }

    // 回傳 Proceed 時 fly 就是要交給 vnavmesh 的值;不變式:fly 為真必定已騎乘(vnavmesh 拿到飛行路徑卻沒騎乘會停在原地、不回報)。
    // 飛行或潛水中不降成地面:從空中／水下排地面路徑找不到起點。
    // pathActive 只在「沒騎乘、又不是短程步行」時才問:已經在走路徑(多半是自己排的短程步行)就不半路召喚坐騎。
    internal static Step Decide(bool positionIsZero, float arrivalDistance, float lastPointTolerance, float distance,
                                bool flyingSupported, bool mounted, bool inFlight, bool diving, Func<bool> pathActive, ref bool fly)
    {
        if (fly && !flyingSupported)
            fly = false;

        if (positionIsZero || arrivalDistance <= lastPointTolerance)
            return Step.Arrived;

        if (!flyingSupported)
            return Step.Proceed;

        bool walk = distance < MountMinDistance && !inFlight && !diving;
        if (walk)
            fly = false;

        if (!mounted)
        {
            if (!walk && !pathActive())
                return Step.Mount;
            fly = false;
            return Step.Proceed;
        }

        if (fly && !inFlight)
            return Step.TakeOff;

        return Step.Proceed;
    }
}
