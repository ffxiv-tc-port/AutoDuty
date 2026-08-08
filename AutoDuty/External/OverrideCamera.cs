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

// NOTE: the old hand-rolled `CameraEx` struct is gone on purpose (same fix as vnavmesh/Lifestream on
// TC 7.20). Its 0x130-based FieldOffsets are the pre-7.20 layout — TC 7.20 shifted the native struct
// +0x10, so DirH at 0x130 now reads FoV. Worse, the old prologue signature
// (40 53 48 83 EC 70 44 0F 29 44 24 ?? 48 8B D9) still scans on TC 7.20 but resolves to the WRONG
// function, so the hook was silently detouring an unrelated function and writing floats through its
// first argument. Use FFXIVClientStructs.FFXIV.Client.Game.Camera (verified against the API13 pin)
// and the prologue signature vnavmesh verified on TC 7.20, kept fallible so a future mismatch only
// disables camera auto-facing instead of failing the whole plugin load.

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
    // add on top of Original() therefore runs inside a try, and the degraded behaviour is "don't
    // override" - Original has already run, so the game's own camera handling passes through intact.
    // The most realistic exception source here is Framework.Instance(): it is a ClientStructs
    // [StaticAddress], and when its signature stops resolving it *throws* InvalidOperationException
    // (InteropGenerator's ThrowHelper.ThrowNullAddress) instead of returning null.
    // NOTE: this does NOT protect against AccessViolationException (corrupted-state, uncatchable in
    // .NET Core). What it catches is managed exceptions.
    private long _detourErrors;
    private DateTime _lastDetourErrorLog = DateTime.MinValue;

    private void OnDetourError(Exception ex)
    {
        ++_detourErrors;
        // this runs per frame - never log unthrottled. Information (not Debug) because reporting
        // users run at LogLevel 2.
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
                var dt = Framework.Instance()->FrameDeltaTime;
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
