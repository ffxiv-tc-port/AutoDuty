//Entire file from vnavmesh
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using System;
using System.Numerics;

namespace AutoDuty.External;

// NOTE: do not reintroduce a hand-rolled camera struct or the old prologue signature
// (40 53 48 83 EC 70 44 0F 29 44 24 ?? 48 8B D9): the TC layout differs and that signature still scans
// but resolves to the WRONG function. Use FFXIVClientStructs.FFXIV.Client.Game.Camera and keep the
// signature fallible, so a mismatch only disables camera auto-facing instead of failing the plugin load.

public unsafe class OverrideCamera : IDisposable
{
    internal void Face(Vector3 pos)
    {
        Enabled = true;
        SpeedH = SpeedV = 360.Degrees();
        DesiredAzimuth = Angle.FromDirectionXZ(pos - Player.Object.Position) + 180.Degrees();
        DesiredAltitude = -30.Degrees();
    }

    public bool Enabled
    {
        get => _rmiCameraHook?.IsEnabled ?? false;
        set
        {
            if (_rmiCameraHook == null)
                return;
            if (value)
                _rmiCameraHook.Enable();
            else
                _rmiCameraHook.Disable();
        }
    }

    public bool IgnoreUserInput; // if true - override even if user tries to change camera orientation, otherwise override only if user does nothing
    public Angle DesiredAzimuth;
    public Angle DesiredAltitude;
    public Angle SpeedH = 360.Degrees(); // per second
    public Angle SpeedV = 360.Degrees(); // per second

    private delegate void RMICameraDelegate(Camera* self, int inputMode, float speedH, float speedV);
    [Signature("48 8B C4 53 48 81 EC ?? ?? ?? ?? 44 0F 29 50 ??", Fallibility = Fallibility.Fallible)]
    private Hook<RMICameraDelegate>? _rmiCameraHook;

    public OverrideCamera()
    {
        Svc.Hook.InitializeFromAttributes(this);
        if (_rmiCameraHook != null)
            Svc.Log.Information($"RMICamera address: 0x{_rmiCameraHook.Address:X}");
        else
            Svc.Log.Error("RMICamera signature not found - camera auto-facing disabled");
    }

    public void Dispose()
    {
        _rmiCameraHook?.Dispose();
    }

    // fail-closed: a detour is a managed function the *native* code calls directly, so a managed
    // exception escaping it unwinds through native frames that have no handler for it. Everything we
    // add on top of Original() therefore runs inside a try; the degraded behaviour is "don't override"
    // (Original has already run). Framework.Instance() *throws* InvalidOperationException when its
    // signature stops resolving. NOTE: this does NOT protect against AccessViolationException.
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex)
    {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information because this is a diagnostic
        // we want the user to report back.
        var now = DateTime.UtcNow;
        if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30))
            return;
        _lastDetourErrorLog = now;
        Svc.Log.Information($"OverrideCamera: camera override threw, leaving the game's own camera input alone (total {_detourErrors}): {ex}");
    }

    private void RMICameraDetour(Camera* self, int inputMode, float speedH, float speedV)
    {
        _rmiCameraHook!.OriginalDisposeSafe(self, inputMode, speedH, speedV);
        try
        {
            if (self == null)
                return;
            if (IgnoreUserInput || inputMode == 0) // let user override...
            {
                // 🔴 上面那圈 try/catch 擋的是特徵碼失配(ThrowNullAddress),但 Framework.Instance() 是 isPointer: true:
                //    判空判的是**外層指標槽的位址**,回傳的卻是**槽裡的內容**,而那個內容在遊戲還沒建好/正在拆掉
                //    Framework 時合法為 null。裸解參考它是 AccessViolationException,catch 攔不到。
                // fail-closed:拿不到就當這一幀 dt = 0。Original() 已先跑過,
                //    遊戲自己的鏡頭處理原封不動。每幀都會跑,刻意不寫 log。
                var framework = Framework.Instance();
                var dt = framework == null ? 0f : framework->FrameDeltaTime;
                var deltaH = (DesiredAzimuth - self->DirH.Radians()).Normalized();
                var deltaV = (DesiredAltitude - self->DirV.Radians()).Normalized();
                var maxH = SpeedH.Rad * dt;
                var maxV = SpeedV.Rad * dt;
                //self->InputDeltaH = Math.Clamp(deltaH.Rad, -maxH, maxH);
                //self->InputDeltaV = Math.Clamp(deltaV.Rad, -maxV, maxV);
                self->InputDeltaH = deltaH.Rad;
                self->InputDeltaV = deltaV.Rad;
                Enabled = false;
            }
        }
        catch (Exception ex)
        {
            OnDetourError(ex);
        }
    }
}
