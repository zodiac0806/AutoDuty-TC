# AUTODUTY - PROJECT KNOWLEDGE BASE

**Generated:** 2025-01-31
**Commit:** 4f326c0
**Branch:** dev-tc

## OVERVIEW

AutoDuty is a Dalamud plugin for FFXIV that automates dungeon running with Duty Support, Trusts, or Squadrons. C# .NET 9.0 + ImGui + FFXIVClientStructs.

## STRUCTURE

```
AutoDuty/
├── AutoDuty/           # Main plugin code
│   ├── Helpers/        # 42 helper classes (state machines for game actions)
│   ├── Windows/        # ImGui windows (MainWindow, Overlay, tabs)
│   ├── Managers/       # Content, Actions, Squadron, Variant managers
│   ├── IPC/            # Inter-plugin communication (vnavmesh, BossMod, etc.)
│   ├── Data/           # Enums, Classes, Extensions
│   ├── External/       # Camera override, AFK override
│   ├── Updater/        # Path patching, GitHub updates
│   └── Paths/          # 222 JSON dungeon navigation paths
├── ECommons/           # [SUBMODULE] Shared Dalamud utilities
└── ffxiv_pictomancy/   # [SUBMODULE] World-space drawing library
```

## WHERE TO LOOK

| Task | Location | Notes |
|------|----------|-------|
| Plugin entry point | `AutoDuty/AutoDuty.cs` | `IDalamudPlugin` impl, constructor inits all |
| Add new helper | `AutoDuty/Helpers/` | Extend `ActiveHelperBase<T>` |
| Modify UI | `AutoDuty/Windows/` | ImGui-based, tabs in separate files |
| Add dungeon path | `AutoDuty/Paths/` | JSON format: `(TerritoryType) Name.json` |
| IPC with other plugins | `AutoDuty/IPC/` | `IPCSubscriber.cs` for consuming, `IPCProvider.cs` for exposing |
| Game data/enums | `AutoDuty/Data/` | `Enums.cs`, `Classes.cs`, `Extensions.cs` |
| Configuration | `AutoDuty/Windows/Config.cs` | Per-character configs via `ConfigurationMain` |

## CONVENTIONS

- **Global usings** at top of `AutoDuty.cs`: `Data.Enums`, `Data.Extensions`, `Data.Classes`, `AutoDuty.AutoDuty`, `ECommons.GameHelpers`
- **Static Plugin access**: `Plugin` singleton accessible everywhere via `global using static AutoDuty.AutoDuty`
- **Stage enum**: State machine for plugin operation (`Stopped`, `Looping`, `Navigating`, `Action`, `Dead`, etc.)
- **PluginState flags**: `None`, `Looping`, `Navigating`, `Paused` - combined with bitwise ops
- **TaskManager**: ECommons `LegacyTaskManager` for sequential async operations
- **Helpers**: Static classes with `Invoke()` entry, `State` property (`ActionState` enum)

## ANTI-PATTERNS (THIS PROJECT)

- **DO NOT** modify `ECommons/` or `ffxiv_pictomancy/` - external submodules
- **DO NOT** use `Svc.ClientState.TerritoryType` without null checks on `CurrentTerritoryContent`
- **DO NOT** call navigation without checking `VNavmesh_IPCSubscriber.Nav_IsReady()`
- **NEVER** block the main thread - use `TaskManager.Enqueue()` for async sequences

## REQUIRED PLUGINS (RUNTIME)

| Plugin | Purpose | IPC Namespace |
|--------|---------|---------------|
| vnavmesh | Pathfinding/navigation | `vnavmesh` |
| BossMod/VBM | Boss fight automation | `BossMod` |
| Wrath Combo / RSR | Combat rotation | varies |

## OPTIONAL INTEGRATIONS

- **AutoRetainer** - retainer management between loops
- **Deliveroo** - GC turnin automation
- **Gearsetter** - auto equip best gear

## PATH FILE FORMAT

```json
{
  "Actions": [
    {
      "Tag": "None",
      "Name": "ActionName",
      "Position": { "X": 0.0, "Y": 0.0, "Z": 0.0 },
      "Arguments": ["arg1"],
      "Conditions": [],
      "Note": ""
    }
  ]
}
```

Key 比對不分大小寫(`BuildTab.jsonSerializerOptions` 設了 `PropertyNameCaseInsensitive = true`),
所以偶爾看到的全小寫檔案不會壞掉 —— 但那是例外不是慣例。326 個路徑檔裡:
**309 個是上面這種 PascalCase 物件**、8 個是全小寫 key、9 個是更舊的「整份就是一個陣列」格式。
新增或修改路徑檔一律照上面的範例。

**Common action names**: `MoveTo`, `Boss`, `TreasureCoffer`, `DutySpecificCode`, `Interactable`

## COMMANDS

```bash
# Build (requires Dalamud dev environment)
dotnet build AutoDuty/AutoDuty.csproj

# Chat commands in-game
/autoduty or /ad          # Open main window
/ad start                 # Start in current duty
/ad stop                  # Stop all operations
/ad config                # Open config
```

## NOTES

- **Lumina workaround**: `Svc.Data.GameData.Options.PanicOnSheetChecksumMismatch` toggled around `ContentHelper.PopulateDuties()` - temporary fix
- **Chinese UI**: Some popup messages are in Chinese (排程器 = Planner)
- **oldPaths/**: Legacy path format, 206 files - being migrated to new format in `Paths/`
- **No tests**: No unit test infrastructure exists
