Token optimization (~50% reduction on tiles, ~26% across all endpoints):
- [breaking] map: x/y/radius -> x1/y1/x2/y2
- [breaking] booleans: true/false -> 0/1 everywhere
- [breaking] uniform schema: all list endpoints always emit all fields (enables toon CSV)
- [breaking] tiles occupants: z-range format (DistrictCenter:z2-6), moved to last column

- [feature] brain: full colony awareness in one command -- faction, DC, summary, building roles, tree/food clusters, maps, tasks. Persists to brain.toon. Auto-creates with DC map on first run
- [feature] food_clusters endpoint: grid-clustered gatherable food near DC
- [feature] map name param: saves ANSI map to memory and indexes in brain
- [feature] map delta ANSI: 35KB -> 6KB
- [feature] find_placement distance: path cost from DC via flow field
- [feature] summary: speed field

- [internal] uniform schema + 0/1 booleans documented in architecture.md
