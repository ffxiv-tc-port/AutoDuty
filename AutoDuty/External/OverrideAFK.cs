//All from ﻿https://github.com/awgil/ffxiv_visland/blob/master/OverrideAFK.cs.

using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AutoDuty.External;

internal unsafe class OverrideAFK
{
    private bool _loggedFirstReset;

    public void ResetTimers()
    {
        // 原生指標一律每次呼叫重查,不跨幀保存:模組會隨登出/換角色重建,
        // 存下來的位址失效後解參考就是 AccessViolationException,try/catch 攔不到。
        // UIModule.Instance() 是 FFXIVClientStructs 裡手寫的取得子,
        // Framework 還沒建立(登入流程早期、切換角色期間)時回 null。
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        // AFK 計時器住在 InputTimerModule:走 FFXIVClientStructs 的具名取得子,槽位由 CS 維護,
        // 不要自己寫死虛擬表槽位(差一槽就是把 0 寫進別的模組)。
        var module = uiModule->GetInputTimerModule();
        if (module == null)
            return;

        module->AfkTimer = 0;
        module->ContentInputTimer = 0;
        module->InputTimer = 0;
        module->Unk1C = 0;

        if (!_loggedFirstReset)
        {
            _loggedFirstReset = true;
            Svc.Log.Information($"[OverrideAFK] 首次成功重置 AFK 計時器:InputTimerModule 位址 0x{(nint)module:X}(UIModule 虛擬表槽位 56)");
        }
    }
}
