# AUTODUTY PLUGIN - MODULE KNOWLEDGE

## OVERVIEW

Main plugin module. Entry point, state machine, loop orchestration, UI windows.

## KEY FILES

| File | Purpose | Lines |
|------|---------|-------|
| `AutoDuty.cs` | Plugin entry, state machine, loop logic | ~1200 |
| `Windows/MainWindow.cs` | Primary UI window | ~800 |
| `Windows/Config.cs` | Configuration tab | ~600 |
| `Windows/BuildTab.cs` | Path builder UI | ~500 |

## STATE MACHINE

```
Stage enum flow:
Stopped → Looping → Navigating → Reading_Path → Action → Waiting_For_Combat
                                                    ↓
                                              Dead → Revived
                                                    ↓
                                              Condition (area transition)
```

## PLUGIN LIFECYCLE

1. **Constructor**: `ECommonsMain.Init()` → `PictoService.Initialize()` → `EzConfig.Init<ConfigurationMain>()` → populate helpers → register commands/events
2. **Framework_Update**: Main tick loop, drives state machine
3. **Dispose**: Unregister all events, dispose services

## LOOP FLOW

```
Run() → PreLoopActions → Queue(duty) → WaitDutyStarted → StartNavigation()
     ↓
ClientState_TerritoryChanged → LoopTasks() → BetweenLoopActions → Queue(next)
     ↓
LoopsCompleteActions() → TerminationActions → Stage.Stopped
```

## WHERE TO LOOK

| Task | File | Method/Section |
|------|------|----------------|
| Add chat command | `AutoDuty.cs` | `OnCommand()` |
| Modify loop behavior | `AutoDuty.cs` | `LoopTasks()`, `LoopsCompleteActions()` |
| Add UI tab | `Windows/` | Create new file, add to `MainWindow.cs` |
| Change stage behavior | `AutoDuty.cs` | `Stage` property setter |

## CONVENTIONS

- **Configuration access**: `Plugin.Configuration` (shorthand for `ConfigurationMain.Instance.GetCurrentConfig`)
- **Logging**: `Svc.Log.Debug/Info/Error()` from ECommons
- **Scheduling**: `SchedulerHelper.ScheduleAction()` for delayed execution
- **Throttling**: `EzThrottler` from ECommons for rate limiting

## ANTI-PATTERNS

- **DO NOT** set `Stage` directly in most cases - use the property setter which handles transitions
- **DO NOT** access `Player` without `PlayerHelper.IsValid` check
- **DO NOT** add blocking waits - use `TaskManager.Enqueue(() => condition, timeout, "name")`
