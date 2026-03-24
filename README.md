# Timberbot API

C# mod + Python client that lets AI agents read and control a running Timberborn game over HTTP.

```
Timberborn (Unity)
  |-- Timberbot API mod (port 8085)   read + write game state

Python client
  |-- timberbot.py                   single-file API client + CLI + dashboard
```

## Quick start

```bash
# with Timberborn running + mod loaded
python timberbot.py summary                              # full colony snapshot
python timberbot.py buildings                            # list all buildings
python timberbot.py set_speed speed:3                    # fast forward
python timberbot.py place_building prefab:Path x:100 y:130 z:2 orientation:south
python timberbot.py beavers                              # beaver wellbeing + critical needs
python timberbot.py distribution                         # import/export settings per district
python timberbot.py science                               # science points + unlockable buildings
python timberbot.py tree_clusters                         # find densest tree clusters
python timberbot.py map x:120 y:140 radius:10             # ASCII map with terrain height
python timberbot.py top                                  # live colony dashboard
python timberbot.py                                      # list all methods
```

Or use raw HTTP -- no Python needed:

```bash
curl http://localhost:8085/api/summary
curl -X POST http://localhost:8085/api/speed -d '{"speed": 3}'
```

## Docs

- [Getting Started](docs/getting-started.md). Install, first steps, examples
- [API Reference](docs/api-reference.md). All HTTP endpoints
- [Timberbot AI](docs/timberbot.md). Strategy guide for AI agents playing Timberborn. Works as a Claude Code skill
- [Developing](docs/developing.md). Build from source, add endpoints, Workshop publishing

## Requirements

- Timberborn (Steam)
- .NET SDK 6+ (to build the mod)
- Python 3.8+ with `requests` (for the client, optional)
- `pip install toons` for compact TOON output (optional, falls back to JSON)

## Credits

Learned from these Timberborn modding projects:

- [mechanistry/timberborn-modding](https://github.com/mechanistry/timberborn-modding) -- official modding tools, wiki, and examples
- [thomaswp/BeaverBuddies](https://github.com/thomaswp/BeaverBuddies) -- `BlockObjectPlacerService.Place()` for building placement, `TemplateInstantiator` + `MarkAsPreviewAndInitialize` + `IsValid()` for game-native placement validation, `BuildingUnlockingService.Unlock()` for science, `WorkingHoursManager` for work schedules
- [datvm/TimberbornMods](https://github.com/datvm/TimberbornMods) -- `TreeCuttingArea.AddCoordinates()` for tree marking, `IAlertFragment` patterns for building alerts
- [ihsoft/TimberbornMods](https://github.com/ihsoft/TimberbornMods) -- `Inventories.AllInventories` for building inventory, `BuildingUnlockingService.Unlocked()` for science checks
- [CordialGnom/timberborn-unity-modding](https://github.com/CordialGnom/timberborn-unity-modding) -- `PlantingService.SetPlantingCoordinates()` for crop planting, `PlantingAreaValidator.CanPlant()` for planting validation
- [Timberborn-KyP-Mods/TimberPrint](https://github.com/Timberborn-KyP-Mods/TimberPrint) -- `PreviewFactory` + `BlockValidator` patterns for placement validation
- [toon-format/toon](https://github.com/toon-format/toon) -- Token-Oriented Object Notation for compact AI output
