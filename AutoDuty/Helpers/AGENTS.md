# HELPERS - MODULE KNOWLEDGE

## OVERVIEW

42 helper classes implementing game automation actions. Each helper is a static state machine with `Invoke()` entry and `State` tracking.

## ARCHITECTURE

```
ActiveHelperBase<T>          # Generic base for helpers with State tracking
    ├── RepairHelper         # Self/NPC repair
    ├── ExtractHelper        # Materia extraction  
    ├── DesynthHelper        # Desynthesis
    ├── GCTurninHelper       # Grand Company turnin
    ├── CofferHelper         # Treasure coffer looting
    └── ... (37 more)
```

## HELPER PATTERN

```csharp
internal class ExampleHelper : ActiveHelperBase<ExampleHelper>
{
    internal static ActionState State => _state;
    internal unsafe static void Invoke()
    {
        if (State == ActionState.Running) return;
        _state = ActionState.Running;
        // Queue tasks via Plugin.TaskManager
        Plugin.TaskManager.Enqueue(() => DoThing(), "ExampleHelper-DoThing");
        Plugin.TaskManager.Enqueue(() => { _state = ActionState.None; }, "ExampleHelper-Done");
    }
    internal static void Stop() => _state = ActionState.None;
}
```

## KEY HELPERS

| Helper | Purpose | Trigger |
|--------|---------|---------|
| `RepairHelper` | Repair gear (self or NPC) | Between loops if enabled |
| `GCTurninHelper` | Turn in items to GC | Between loops |
| `TeleportHelper` | Teleport to aetheryte | Navigation |
| `QueueHelper` | Queue for duty | Loop start |
| `ContentHelper` | Duty/content data | Static data |
| `PlayerHelper` | Player state checks | Everywhere |
| `ObjectHelper` | Game object utilities | Navigation |
| `MovementHelper` | Movement/pathfinding | Navigation |
| `DeathHelper` | Death/revive handling | Combat |

## STATE ENUM

```csharp
enum ActionState { None, Running, Complete, Error }
```

## WHERE TO LOOK

| Task | File |
|------|------|
| Add new automation | Create `NewHelper.cs`, extend `ActiveHelperBase<T>` |
| Player state checks | `PlayerHelper.cs` |
| Object/NPC interaction | `ObjectHelper.cs`, `AddonHelper.cs` |
| Inventory operations | `InventoryHelper.cs` |
| Navigation | `MovementHelper.cs`, `GotoHelper.cs` |
| Teleportation | `TeleportHelper.cs` |

## CONVENTIONS

- **Naming**: `{Action}Helper.cs`
- **Entry**: Static `Invoke()` method
- **State**: Static `State` property returning `ActionState`
- **Stop**: Static `Stop()` method to abort
- **Tasks**: Use `Plugin.TaskManager.Enqueue()` for sequencing
- **Waits**: `TaskManager.Enqueue(() => condition, timeout, "name")`

## ANTI-PATTERNS

- **DO NOT** run helper if `State == ActionState.Running`
- **DO NOT** forget to reset state to `None` on completion
- **DO NOT** use synchronous waits - always `TaskManager.Enqueue`
- **DO NOT** access game state without `PlayerHelper.IsValid` / `PlayerHelper.IsReady`
