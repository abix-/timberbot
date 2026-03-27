# Thread-Safe Surfaces

Concrete Timberborn thread-safety guidance for Timberbot development.

This document is based on live inspection through Timberbot's `/api/debug` endpoint on a running game, plus the current Timberbot architecture. It distinguishes between:

- explicitly thread-safe game services,
- services that appear snapshot-backed and may be usable off-thread but should be verified per method,
- normal gameplay/entity services that should be treated as main-thread only.

## Safe off-thread

These are the surfaces Timberborn exposes as explicitly thread-safe, or Timberbot publishes as read-only snapshots.

### Timberborn thread-safe services

#### `IThreadSafeWaterMap`

Runtime type observed via `/api/debug`:

`Timberborn.WaterSystem.ThreadSafeWaterMap`

Confirmed read methods:

- `ColumnCount(int)`
- `ColumnFloor(int)`
- `ColumnCeiling(int)`
- `WaterDepth(int)`
- `WaterDepth(Vector3Int)`
- `ColumnContamination(Vector3Int)`
- `WaterFlowDirection(Vector3Int)`
- `CeiledWaterHeight(Vector3Int)`
- `WaterHeightOrFloor(Vector3Int)`
- `CellIsUnderwater(Vector3Int)`
- `TryGetColumnFloor(Vector3Int, out int)`

Confirmed read properties:

- `ColumnCounts`
- `WaterColumns`
- `FlowDirections`

#### `IThreadSafeColumnTerrainMap`

Runtime type observed via `/api/debug`:

`Timberborn.TerrainSystem.ThreadSafeColumnTerrainMap`

Confirmed read methods:

- `GetColumnCount(int)`
- `GetColumnCeiling(int)`
- `GetColumnFloor(int)`
- `TryGetIndexAtCeiling(int, int, out int)`
- `TryGetIndexAtOrAboveCeiling(int, int, out int)`

Confirmed read properties:

- `ColumnCounts`
- `TerrainColumns`

### Timberbot snapshot data

These are not live Timberborn objects. They are Timberbot-owned snapshot surfaces and are safe to read off-thread within Timberbot's current design.

- `Cache.Buildings.Read`
- `Cache.NaturalResources.Read`
- `Cache.Beavers.Read`
- `Cache.Districts`
- cached primitive/string fields in the cached entity classes
- cached immutable/shareable data such as building tile footprints

## Verify first

These services are not explicitly named thread-safe, but live inspection shows they are backed by thread-safe terrain references and internal arrays. They may be safe for specific read methods, but do not treat them as universally thread-safe without proving the specific call path.

### `SoilMoistureService`

Observed internal fields:

- `_threadSafeColumnTerrainMap:IThreadSafeColumnTerrainMap`
- `_threadSafeMoistureLevels:Single[]`

Observed read methods:

- `SoilMoisture(int)`
- `SoilIsMoist(Vector3Int)`

### `SoilContaminationService`

Observed internal fields:

- `_threadSafeColumnTerrainMap:IThreadSafeColumnTerrainMap`
- `_threadSafeContaminationLevels:Single[]`

Observed read methods:

- `Contamination(int)`
- `SoilIsContaminated(Vector3Int)`

### Guidance for this bucket

- Use `/api/debug` to inspect backing fields before relying on a method off-thread.
- Prefer methods that read thread-safe arrays or maps directly.
- Avoid methods that traverse entity/component graphs, registries, or mutable gameplay collections.
- If a method is important to a public endpoint, add a debug probe or validation path before treating it as safe.

## Main-thread only

Treat these as main-thread only unless there is specific contrary evidence for a particular method.

### Normal gameplay and registry services

- `BuildingService`
- `DistrictCenterRegistry`
- `EntityRegistry`
- `FactionNeedService`
- `ScienceService`
- `BuildingUnlockingService`
- `NotificationSaver`
- `INavMeshService`
- `PlantingService`
- `TreeCuttingArea`
- `PreviewFactory`
- `BlockObjectPlacerService`
- `EntityService`

### Live entity/component data

- `EntityComponent`
- `GetComponent<T>()` results
- building, beaver, and natural resource component properties
- live district/building/beaver registries and lists
- any normal `List<T>` or `ReadOnlyList<T>` exposed by gameplay services

## Working rule

- If the type is explicitly `ThreadSafe*`, it is the primary candidate for off-thread reads.
- If the data is published by Timberbot as a snapshot, it is safe to read off-thread.
- If the type is a normal Timberborn gameplay service or live component graph, keep it on the main thread.

## Implication for Timberbot

Safe to keep lean and off-thread:

- water
- terrain
- tile geometry queries built on `IThreadSafeWaterMap` and `IThreadSafeColumnTerrainMap`
- Timberbot snapshot reads

Still needs snapshotting or main-thread execution:

- buildings
- beavers
- districts
- science
- distribution
- notifications
- need/wellbeing data from live services
- anything that touches live entities or registries

## Revalidation workflow

When adding a new endpoint or trying to remove snapshotting:

1. Inspect the service with `/api/debug target:fields`.
2. Look for explicit `ThreadSafe*` types or internal thread-safe backing arrays.
3. Probe the exact method you want to use.
4. Only then decide whether the call can run off-thread or belongs in the snapshot path.
