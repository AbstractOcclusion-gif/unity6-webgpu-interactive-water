# Real-terrain pipeline — analysis (2026-09-15)

Analysis only. No code was written or changed. Author: Claude, for Bert (AbstractOcclusion).
Scope: turn real Tarn terrain into web viewer / video / kiosk deliverables for tourist offices, communes, estates and architects.

**How I read the code.** The shell on your PC could not mount the folders today (a Windows update is blocking it), so I copied ~95 source files into the cloud workspace and read them there. Every claim below cites the file it comes from. Things I did **not** read are listed in §7.

---

## 0. Where the code and the brief disagree (read this first)

| # | Brief / doc says | Code shows | Evidence |
|---|---|---|---|
| C1 | VIZUDRONE holds "the TerraSketch port with electric lines" | `VIZUDRONE/06-dev/tools/terrasketch` is a **byte-identical copy** of `Investors3DJourney/terrasketch` (every file the same size; `cli.py` and `transform/building_volumes.py` compare equal with `cmp`). The electric-line work is in the **three.js mission viewer**, not in the Python code. | `VIZUDRONE/06-dev/tools/mission-viewer/mission-v2_16.html`: Overpass `power~line/minor_line/cable` + `tower/pole`, `fetchEnedisPower()` (overhead HTA/BT lines only), manual pole tracing, `checkClearance()` |
| C2 | Gaussian-splat experiments are "soon" in VIZUDRONE | VIZUDRONE has no splat code yet. The working splat lab is in **Investors3DJourney**. | `Investors3DJourney/Dev/gaussian_webgpu/` (PlayCanvas `supersplat-viewer`, `.sog`, WebGPU only), `Dev/16_hero_capture_workflow.md` (marked VALIDATED 2026-08-14) |
| C3 | Buildings use BD TOPO roof heights | **Bug:** `ingest/buildings_parser.py::_roof_rise` reads `z_min_toit` / `z_max_toit`. BD TOPO V3 returns `altitude_minimale_toit` / `altitude_maximale_toit`. So `roof_rise_m` is always `None` and every roof falls back to `ROOF_PITCH_DEFAULT_DEG = 30`. | Your croupou download `bdtopo_buildings_raw.geojson`: 19 features, 0 with `z_min_toit`, 14 with `altitude_*_toit` |
| C4 | Trail app "reuses the terrain algorithm" | It reuses the **idea** (smooth in metres, then take derivatives), not the code. `trek_geo` is a separate package with its own IGN fetch, block resampling and PMTiles output. | `TrailApp/geo/pipeline/src/trek_geo/visual_relief.py` docstring ("Like TerraSketch…"), `block_dem.py`, `ign_wms.py` |
| C5 | Splat maps can feed "the substrate channels IslandTerrain.shader expects" | The two systems use **different channel orders**. TerraSketch: R grass, G rock, B forest, A water. IslandTerrain: R seabed, G beach, B rock, A grass. No shader reads both. | `terrasketch/constants.py` L115-118; `island generator/Runtime/SubstrateControlMap.cs` ("CHANNEL ORDER IS A CONTRACT") |
| C6 | Dev/16: "sub-250 g = no pilot licence burden" | `VIZUDRONE/01-administratif` holds a training certificate and an operator record, `kmz_generator.py` targets the **Mini 5 Pro** (Dev/16 says Mini 4 Pro), and paid flights over populated areas need a declaration (§2.1 checklist). The note is out of date for paid work. | files listed; `kmz_generator.py` `DRONE_ENUM = 68  # Mini 5 Pro` |
| C7 | The island generator has a town / building algorithm | `IslandTownGreyboxFactory.cs` makes **no buildings**. It only stamps terrain: town platform, quays, dam crest, rivers. The **only** building algorithm in the workspace is TerraSketch's. | `Editor/IslandTownGreyboxFactory.cs` (ellipse/capsule stamps into `OperationWeights`) |
| C8 | Deleted modules | `terrasketch/**/__pycache__` still has compiled files for `canopy_mesh`, `canopy_zones`, `forest_paint`, `tree_scatter`, `blob_placement`, `hero_mesh`, `obj_buffer`, but their `.py` sources are gone. `Assets/TerraSketch/Imported/canopy.obj` (14.8 MB) and `blobs.json` remain. A canopy-shell approach was tried and removed. | directory listing |

Smaller notes: `requirements.txt` does not list `scipy`, but `heightmap.py`, `relief_normal.py` and `splatmap.py` import it. The WFS building request stops at `IGN_WFS_BUILDING_COUNT_CAP = 5000` with no paging (`source/ign_buildings.py` L82-116), which is fine for a hamlet but can truncate a whole commune.

---

## 1. Inventory (one page)

| System | Where | Input | Output | Reusable for real data? |
|---|---|---|---|---|
| **TerraSketch** (Python, ~10.5 k lines, the original) | `Investors3DJourney/terrasketch` (copy in VIZUDRONE) | WGS84 bbox → L93 (`ingest/reproject.py`); IGN WMS-r RGE ALTI `ELEVATION.ELEVATIONGRIDCOVERAGE.HIGHRES`; WMTS `ORTHOIMAGERY.ORTHOPHOTOS` z18 ≈ 0.45 m/px; WFS `BDTOPO_V3:batiment`; WMS LiDAR HD MNH; OSM Overpass | `heightmap.png` 16-bit, 4×4 OBJ chunks × 2 LODs + skirt, `color_stylized.png` (warm-pastoral/misty grades, `stylize.py`), `splatmap_biomes.png` (k-means on ortho HSV + slope, `splatmap.py`), `normal_relief.png`, `road_mask`, river carve/mesh, `pois.json`, `buildings.obj/json`, `tree_scatter.json`, `scatter.json` (tree/rock/grass/plant, `scatter_rules.py`), `manifest.json` | **Yes. This is the backbone.** CLI subcommands: generate, enrich, roadmask, carve, river, river-bank, buildings, vegetation, scatter (`cli.py` L177-230). Weak spots: OBJ text (a 2048 lod0 chunk is ~55 MB), 4-channel colour-only classification, gable-only roofs |
| **TerraSketch Unity side** | `Investors3DJourney/Assets/TerraSketch`, `Assets/Vegetation`, `Assets/HeroZone` | bundle files | `TerraSketchTerrain.shader` (splatmap + detail Texture2DArray + aerial chroma tint + road/bank masks, `Dev/12`); `ScatterRenderer` / `ForestRenderer` (cell grid, frustum + distance cull, near mesh / far billboard, atlas foliage cards with wind, `DrawMeshInstanced` ≤1023 per batch, WebGL2-safe); `ScatterPalette`; `HeroZone*` subscene loader | Yes for kiosk/video. The web path is WebGL2 CPU instancing, so it has hard limits |
| **Gaussian lab** | `Investors3DJourney/Dev/gaussian_webgpu` + `Dev/16` | `.ply/.sog/.spz` | WebGPU viewer site (PlayCanvas `supersplat-viewer`), itch package | **Yes.** Capture → Postshot → SuperSplat → `.sog` flow is written down and gated (≤30 MB, ≤1.5 M splats per zone) |
| **Mission viewer + KMZ** (VIZUDRONE) | `06-dev/tools/mission-viewer/mission-v2_16.html` (three.js r128, data inlined), `kmz-generator/kmz_generator.py` | a TerraSketch bundle (heightmap, ortho, `buildings.json`, `tree_scatter.json`), OSM/Enedis power lines | Flight plans: orbits ×3, facades, POI video, nadir grid, transits, AGL/clearance vs wires + MNH canopy, 200-waypoint cap → DJI WPML `.kmz` | **Yes, and it is the capture front end for Q1a.** The orbit, facade and nadir tracks are exactly the patterns splats and photogrammetry need |
| **Island generator** (C#) | `ThreeJSWaterPort/Assets/island generator` | settings JSON, optional `IslandAuthoringMap` (land, height guide, operation weights RGBA) | heightmap, slope/flow/cliff/river masks, substrate control RGBA (seabed/beach/rock/grass), Unity Terrain + `IslandTerrain.shader` (triplanar rock, wetness, WebGpuWater caustics) | **Partly.** Reusable: `SlopeMap`, `FlowMap`, `CliffMask`, `DepressionFill`, the **hard-stamp channel G** (pads/quays) as the model for building pads and road grading, and the wetness/caustic shading for river banks. Not reusable on real DEMs: noise, erosion, coastline, beach (they invent relief) |
| **WebGpuWater** | `ThreeJSWaterPort/Assets/WebGpuWater`, `ThreeJsDemo/` | water bodies/rivers | river/pond/ocean, fog, god rays; three.js WebGPU ocean demo | Yes for river and lake hero shots. Note from memory: Luminex has no WebGPU renderer target |
| **Trek / TrailApp** (`trek_geo`) | `Claude/Projects/TrailApp/geo/pipeline` | IGN RGE ALTI via WMS, OSM relations, Geotrek API v2 | Block-aligned DEM (`block_dem.py`), seam-safe hillshade PMTiles (`visual_relief.py`), contours, OSM hike import that never bridges gaps (`hikes.py`), walking graph (`path_graph.py`), SHA-256 provenance. **A Lacabarède pack already exists** (`docs/product/relief-1261.md`) | **Yes for trails and provenance.** Marked trails with source + date are ready to overlay on the 3D scene |

**What real data needs that nothing provides yet:** land-cover-driven materials, species-aware vegetation, roof shapes, a web-grade mesh/texture container (glTF + meshopt/KTX2, or heightmap tiles), and georeferenced splat placement.

---

## 2. Question 1 — Drone capture: "splat mapping"

### Which reading the goal needs
**Both, in order: (b) then (a).** (b) textures the *whole commune*, and every deliverable has that. (a) is the *wow* on 2–5 hero spots. A splat set in a blurry, photo-draped terrain looks pasted on. A good (b) makes a mid-quality splat look intentional.

### 2.a 3D Gaussian splatting / photogrammetry of hero spots

**Capture protocol.** Reuse the mission viewer. It already generates these tracks.

1. Three orbits (high ~30 m / mid ~15 m / low 3–5 m, camera pointed at the centre) + facade passes + a nadir grid. This matches `Dev/16 §2` and the viewer's "+ Orbites ×3 / + Façades / + Grille nadir".
2. Use **photos at waypoints, not video**, for anything that must be georeferenced. EXIF GPS gives a first alignment for free, and the KMZ is already one photo per waypoint. Keep video for phone ground passes.
3. Overlap: ≥80 % forward / ≥70 % side on the nadir grid; orbits ≤10° between shots (≥36 photos per orbit).
4. GSD = altitude × pixel pitch / focal length. Target ≤1.5 cm/px for splats and ≤3 cm for an ortho/DSM. Compute it from your Mini 5 Pro specs; I have not verified the sensor numbers.
5. Same light for the whole zone, low wind, exposure/WB locked, shutter ≥1/500 (`Dev/16 §2`).

**Georeferencing without RTK** (the Mini has no RTK):

| Option | Accuracy | Effort | Notes |
|---|---|---|---|
| 1. EXIF GPS only (COLMAP/ODM georegistration) | metres | 0 | Not enough to sit on the IGN ground |
| 2. **EXIF + ICP of splat ground points onto LiDAR HD classified cloud (class 2 ground)** | ~10–30 cm | 1 d once | Recommended. LiDAR HD is ≥10 pts/m² ground truth you already trust. Solve scale + rotation + translation (7 parameters) |
| 3. 4–8 printed targets, coordinates read off BD ORTHO 20 cm + LiDAR MNT height | ~20–50 cm | +1 h per site | Fallback where the ground is under canopy |
| 4. RTK drone or GNSS rover | cm | €€€ | Only if architects pay for survey-grade work. They are the only client who will ask |

**Tools.** SfM: COLMAP (or GLOMAP for speed). Georeferenced ortho + DSM + cloud: OpenDroneMap. Training: Postshot (already chosen, local on the 5090) or Nerfstudio `splatfacto` / `gsplat` (free, scriptable). Clean + export: SuperSplat (`.sog`).

**Viewers.**

| Target | Option | Status |
|---|---|---|
| Web, standalone | PlayCanvas `supersplat-viewer` (WebGPU) | **Already proven** in your lab (`Dev/16`) |
| Web, inside a three.js terrain scene | **Spark 2.0** (three.js, WebGL2, LoD tree, depth compositing with meshes, 1.5 M desktop / 500 k mobile default budget, needs three r179+) | Recommended for the terrain viewer. **The mission viewer is on r128 and would need an upgrade** |
| Unity URP (kiosk/video) | aras-p UnityGaussianSplatting (DX12/Vulkan/Metal), WebGPU fork named in `Dev/16 §8` | Fine for offline video and kiosk. Check maintenance status before committing |

**Web file sizes.** ≤30 MB `.sog` and ≤1.5 M splats per zone (`Dev/16 §4`), load ≤15 s on 4G. The streamed-LoD encoding is the lever (`Dev/16 §7`).

**Quality vs effort.** Splat: best for trees, stone and texture, ½–1 day per spot, no clean mesh, no relighting. Photogrammetry mesh: worse on vegetation, better for measurements and architects, 1–2 days per spot with cleanup. **Tourist office → splat. Architect → mesh + ortho.**

**French drone rules: checklist, not legal advice.** Check each item on official sources before every paid job.

- [ ] Operator registered (AlphaTango) and ID shown on the drone. Your `01-administratif` suggests this is done.
- [ ] Remote pilot competence matches the drone class/weight. Check the Mini 5 Pro's class marking **with the battery you actually fly** (a heavier battery changes the weight class).
- [ ] Open category limits: ≤120 m above ground, visual line of sight, no flight over gatherings.
- [ ] Zone check on the official map (cartes.gouv.fr "restrictions UAS"): military low-flying areas are common in the Tarn/Montagne Noire, plus aerodromes, Natura 2000 / reserve rules, and restrictions near power lines and rail.
- [ ] Paid flight over a **populated area**: prefectural declaration (Cerfa 15476, delay reported as 10 working days since 2026). Verify on Légifrance/DGAC, because secondary sources disagree.
- [ ] Private land: owner's written consent for take-off and filming. Privacy (GDPR): blur faces and number plates, and don't film into private gardens you weren't hired for.
- [ ] Churches/chapels: ask the commune (owner) and the parish. Classified monuments (Monuments historiques): check whether the DRAC must be told.
- [ ] Insurance (RC drone pro) covering paid work.

**Pilot (1 weekend).** Capture one hero spot you can legally fly: your Vol 1 "Domicile et jardin" (croupou) zone already has its IGN bundle.
Pass if: (1) splat ground matches LiDAR MNT within **≤0.3 m** RMS after ICP; (2) no visible seam where the splat meets the TerraSketch terrain from the 3 default cameras; (3) ≤30 MB, ≥30 fps desktop / ≥25 fps on your target phone in Spark or PlayCanvas.
Fail and stop if the ICP does not converge under foliage (the crop has too little bare ground). Fall back to targets (option 3).

**Effort.** Pilot 2 d. Productised per spot: ~1 d (½ capture, ½ train/clean/align). Integration of Spark into the terrain viewer: 3–4 d (includes the three.js upgrade).

**What could kill it.** Wind on foliage (floaters). No-fly zones at the prettiest spots. Mobile budget with splat + terrain + trees in one frame. A client asking to "update after renovation" (a re-capture costs a day each time).

### 2.b Terrain splat maps from ortho / drone / LiDAR

**What exists.** `transform/splatmap.py` clusters BD ORTHO pixels with k-means (`SPLATMAP_KMEANS_CLUSTERS`), labels each cluster by HSV rules (`_hsv_to_biome`), overrides steep slopes to rock, and blurs the result. It has no land-cover data, so shadows turn into "forest", roofs and roads into "rock", and ploughed fields into "grass". The ortho only reaches ~0.45 m/px (`WMTS_ZOOM_LEVEL = 18`) and is blended with detail textures in `TerraSketchTerrain.shader` (`Dev/12`).

| Option | How | Accuracy | Effort | Maintenance |
|---|---|---|---|---|
| **B1. Rule fusion of vector data** (recommended) | Rasterise OCS GE (cover + use), BD TOPO (roads with width, hydro surfaces, buildings), BD Forêt V2 polygons, then refine with LiDAR HD: slope/roughness from MNT, canopy from MNH, bare rock = slope > X & MNH < 0.5 m. Ortho is used **only as colour tint** (the existing chroma-tint path) | Very good for the classes in the data; deterministic; explainable to a client | 3–4 d | Low: re-run on new data releases |
| B2. Pixel classifier (random forest) | Features: BD ORTHO RGB + IRC, MNH, slope, roughness. Labels sampled from OCS GE. Produces sub-polygon detail (clearings, hedges, bare patches) | Better edges, some noise | +4–5 d on top of B1 | Medium (retraining) |
| B3. Deep segmentation (e.g. IGN's FLAIR work) | Pretrained aerial segmentation | Best detail | 6–10 d, GPU, licence to check | High |
| B4. Drone orthomosaic classification | ODM ortho at 2–3 cm → B2 on the hero spot only | Excellent locally | +1 d per spot | Per site |

**Channel contract (decide once).** Tarn inland needs about 6 materials: meadow/pasture, crop/bare soil, forest floor, rock/scree, road/gravel/built, water/bank. That does not fit 4 channels.

- Keep **`TerraSketchTerrain.shader` as the real-terrain shader.** It already has a detail Texture2DArray (`DetailArrayBaker`, `TerraSketchSlices.hlsl`), aerial tint, and road/bank masks. Extend it to two RGBA control maps (8 weights) or index + weight.
- **Do not force real terrain into IslandTerrain's seabed/beach contract.** Mapping seabed→riverbed and beach→gravel works, but forest floor and road get lost (C5). Reuse IslandTerrain's **wetness + caustics includes** only on river-bank pixels, where they add real value.

**Pilot (1 weekend).** A 2×2 km crop around the village. Pass if on 100 random points checked by eye against BD ORTHO, ≥85 % carry the right material (the current k-means map is the baseline to beat), and there are no road or building-roof pixels labelled forest/grass. Fail if OCS GE is missing or too old for dept 81. Then B2 becomes mandatory, not optional.

**Effort.** B1 3–4 d · B1+B2 8–9 d. **Kill risks:** OCS GE availability for the Tarn (the national product covers metropolitan France but lands per département; verify 81), BD Forêt age (2007–2018), shader sampler budget on WebGL2/mobile.

**Recommendation for a tourist-office deliverable: pilot 2.b first (B1), then one 2.a hero spot.**

---

## 3. Question 2 — Real vegetation, realistic OR beautiful

### Data: what goes where

| Attribute | Source | Existing code | Change |
|---|---|---|---|
| **Position + height** | LiDAR HD MNH 50 cm | `transform/vegetation.py`: tallest pixel per 6 m cell (`TREE_MIN_SPACING_M`), cap 20 k **ranked by height** (`TREE_SCATTER_COUNT_CAP`) | Replace cell decimation with a local-maxima filter on smoothed MNH (window scaled to height), so you get real crowns and no grid pattern. Remove the height-ranked cap: it deletes young woods and hedges first. Cap per LOD ring instead |
| **Crown radius** | MNH watershed from each maximum | none | Gives a per-tree scale that matches the ortho |
| **Species / type** | BD Forêt V2 (32 classes, ≥0.5 ha units, photo-interpreted 2007–2018) → 6–10 archetypes (oak, chestnut, beech, other broadleaf, Douglas fir, pine, mixed, poplar). OCS GE for hedgerows, heath, orchards, isolated trees | `type` field already reserved for "Tier 3" (`TREE_DEFAULT_TYPE`) | Polygon lookup only; no schema change (memory: Tier 3 was designed for this) |
| **Where forest has since been cut** | MNH (recent) beats BD Forêt (old) | — | Rule: no MNH canopy → no tree, whatever BD Forêt says |
| **Under-storey / grass / rocks** | slope, material map from Q1b | `scatter_rules.py` layers tree/rock/grass/plant with Poisson spacing | Source the masks from the Q1b B1 material map, not from k-means |
| **Colour** | BD ORTHO sampled per crown (`_sample_tint` exists); IRC channel for vigour | tint exists, **not consumed yet** in Unity (memory) | Clamp the tint to each archetype's palette, so shadowed crowns don't turn black |
| **Season** | Ortho date = summer | — | Shader: per-archetype hue/brightness curve from a `season` parameter (deciduous only; conifers fixed). One slider = four seasonal videos for the tourist office |

### Rendering at territory scale

Rule (from `Dev/09`): **the forest kills you, not the terrain.** A commune is millions of trees. Use rings:

| Ring | Distance | Representation | Budget (desktop kiosk / mid phone web) |
|---|---|---|---|
| Near | ≤300 m | real instanced meshes, 2 LODs, wind | ~20 k / ~5 k instances |
| Mid | 300 m–1.5 km | **octahedral impostors** (8×8 views, albedo + normal) | ~150 k / ~40 k quads |
| Far | >1.5 km | **no instances**: canopy baked into terrain (MNH-displaced normal + darkened ortho) | 0 draws |

Ceilings: ≤250 draws, ≤500 k visible verts, 30 fps phone (`Dev/10`).

**Unity URP (kiosk, video):**

1. Keep `ScatterRenderer` (data, palette, cells).
2. For the desktop targets add a `Graphics.RenderMeshIndirect` + compute-cull path. Keep the WebGL2 `DrawMeshInstanced` path for web builds.
3. Impostors: build your own octahedral baker. `Dev/10` already lists it; Amplify Impostors is not installed.
4. Wind: The Visual Engine global wind is already in the project (memory `world_dressing_strategy`).
5. Video only: go heavy (HDRP-grade density, 60 k near trees) because frame time does not matter offline.

**three.js / WebGPU (web viewer):**

1. `InstancedMesh` per archetype × LOD, cells culled on CPU.
2. Impostor ring as one `InstancedMesh` of quads with an atlas.
3. Far canopy baked into the terrain texture.
4. Wind as vertex noise in a `ShaderMaterial` / TSL node.

The mission viewer already loads `tree_scatter.json` as obstacles, so the same file drives display.

**Assets.**

| Option | Cost | Look | Maintenance |
|---|---|---|---|
| Synty (present: `Assets/Synty`) | paid, owned | stylised, low poly | none. Web-friendly |
| NatureManufacture (present) | paid, owned | realistic | heavy for web; OK for video |
| Procedural (Blender geometry nodes / Sapling) | time | your own style | you own the look |
| **Your own Blender archetypes** (8–10 trees, 2 LODs + impostor bake) | ~5–6 d once | coherent brand look | lowest long-term |

**Beautiful vs photoreal.** A tourist office sells *emotion and orientation*: "where is the chapel, how green is the valley, where does the trail go". An illustrated-but-exact look (real positions, heights and species, stylised materials, seasonal colour) sells better. It is cheaper to render on phones, and nobody complains "that's not my oak". Photoreal invites comparison with Google Earth and loses; save it for the splat hero spots, where it is real by construction. **Recommendation: "beautiful-exact" for territory, photoreal only inside splats.** The one exception is estates and architects, who may pay for photoreal video. Do that in Unity offline, not on the web.

**Pilot (1 weekend).** 2×2 km with a village edge + a wood + hedgerows.
Pass if: (1) tree count within ±15 % of a manual crown count on 3 × 100 m sample squares against the ortho; (2) archetype matches BD Forêt on ≥80 % of 50 sampled trees inside forest polygons; (3) web: ≥30 fps on your target phone, ≤200 draws; kiosk: ≥60 fps.
Fail if MNH maxima split crowns (fix the window) or impostors pop (fix the ring blend).

**Effort.** Data (maxima, crowns, Tier 3 types, seasons) 4 d · Unity indirect + impostor baker 5 d · three.js viewer vegetation 4 d · 8 Blender archetypes 5–6 d. **Total ≈ 18–19 d**, split over separate batches.

**Kill risks.** Mobile memory with impostor atlases (use KTX2). BD Forêt too old in plantation areas (clear-cuts, Douglas fir). Cost creep if you chase photoreal.

**Ask before re-proposing:** a canopy-shell approach existed and was deleted (C8). Why? If it failed for a known reason, the far-ring "baked canopy" above must avoid that reason.

---

## 4. Question 3 — A better building algorithm

### The existing algorithm (the only one, C7)

Pipeline: `source/ign_buildings.py::fetch_buildings` (WFS `BDTOPO_V3:batiment`, L93, cap 5000, cached) → `ingest/buildings_parser.py::parse_buildings` (outer ring only, holes dropped, MultiPolygon split) → `transform/building_volumes.py::build_building_solids` → `export/building_mesh.py::write_buildings` → `buildings.obj` + `buildings.json`. Unity imports the OBJ (`TerraSketchImporter.cs`). The mission viewer re-reads `buildings.json` triangles and recomputes the long axis with PCA.

- **Walls:** true footprint, extruded from `floor_y` (lowest ground corner − `BUILDING_GROUND_SINK_M` 0.30) to eave. Eave height = `hauteur` → storeys × 3 m → 6 m default (`_eave_height`).
- **Roof:** always a gable over the **minimum-area bounding rectangle** (rotating calipers on the convex hull), pitch clamped to 15–45°, default 30°.
- **Output:** flat-shaded per-triangle OBJ, no UVs, no materials; metadata carries `nature` and `usage`.

**Limits:**

1. The roof-height bug means every roof is 30° (C3).
2. On L, T and U footprints the rectangle roof covers space that is not building: it floats over the courtyard, with walls stopping below it.
3. No hip, flat, shed or multi-ridge roofs. Barns, bourg rows and churches all look like the same house.
4. Walls sink on the uphill side (fine) but have no plinth on the downhill side (visible gap on slopes).
5. No UVs, so no textures.
6. Unused BD TOPO fields: `materiaux_des_murs`, `materiaux_de_la_toiture`, `nombre_de_logements`, `date_d_apparition`, `construction_legere`, `identifiants_rnb` (all present in your croupou download).
7. The 5000 cap has no paging.
8. Adjacent terraced houses in a bourg give duplicate internal walls and z-fighting.

### Options

| Option | What | Quality | Effort | Maintenance |
|---|---|---|---|---|
| **Q3-0. Fix** | Read `altitude_minimale_toit/altitude_maximale_toit`, page the WFS, add a downhill plinth | small gain | ½ d | none |
| **Q3-1. Straight-skeleton roofs from the real footprint** | Hip roof = skeleton of the footprint; gable = skeleton with end edges made vertical; flat for `nature` industrial/commercial with `roof rise < 0.5 m`. Pitch from the (fixed) roof rise. Python: `polyskel`-style or CGAL skeleton; fallback to today's rectangle roof when the skeleton fails | Big: correct L/T/U roofs, real ridge heights | 4–5 d | low |
| **Q3-2. LiDAR roof classification** | Per footprint: MNS − MNT inside the polygon → RANSAC planes → roof type (flat / shed / gable / hip / complex) + ridge azimuth + real ridge height. Feeds Q3-1's choice instead of rules | Correct roof type for ~90 % of rural buildings (estimate, to be measured) | +3 d | low |
| **Q3-3. roofer (3DBAG, GPLv3)** as an offline tool | Footprint + LiDAR HD building points (class 6) → LoD2.2 CityJSON → convert to glTF | Best for complex bourg roofs, churches | 3–4 d integration + C++ build on Windows/Docker | medium (external tool). GPL is fine for an internal tool whose output you sell, but still get your own licence check |
| **Q3-4. Typology facades, no per-building work** | Classify each building: `usage` + `nature` + wall/roof material codes + age + neighbour density (bourg row / farm / annex / barn / church). Assign a **trim sheet** (wall, stone course, window strip, door, roof tile) and generate UVs in world metres along each wall and roof plane. Windows placed per storey by rule, only in the near ring | "Believable village" at zero manual cost | 5–6 d incl. 5 trim sheets | low |
| **Q3-5. LOD by distance** | LOD2 (Q3-1 roofs + trim sheet) ≤400 m; LOD1 (today's walls + flat/ridge cap, roof colour from ortho) ≤2 km; beyond = baked into the terrain texture | holds budget | 2 d | low |

**Village cases.**

- **Bourg:** merge shared walls (union of adjacent footprints per block, then keep roof breaks at the original party walls). Q3-3 is worth it only here.
- **Isolated farms:** Q3-1 + Q3-2 are enough.
- **Annexes / `construction_legere`:** shed roof, no windows.
- **Churches:** always a manual hero asset or a splat (Q1a). An algorithm will never do a bell tower well.

**Recommendation.** **Keep** the TerraSketch buildings stage: fetch, parse, drape, local-metre contract, OBJ/JSON outputs, importer, mission-viewer consumption. **Replace** only the roof generator (Q3-1 driven by Q3-2) and **add** Q3-4 + Q3-5. Treat roofer (Q3-3) as an optional "bourg hero" tool, not the core. Order: Q3-0 → Q3-1 → Q3-2 → Q3-4 → Q3-5.

**Pilot (1 weekend).** Your hamlet (19 buildings in the croupou bundle) + one bourg block.
Pass if: (1) ridge height within ±0.5 m of `altitude_maximale_toit` on ≥90 % of buildings that have it; (2) roof type matches the ortho on ≥80 % (checked by eye); (3) no roof overhanging a courtyard on L/U footprints.

**Effort.** Q3-0..Q3-2 ≈ 8 d · Q3-4 + Q3-5 ≈ 8 d. **Kill risks:** skeleton robustness on dirty cadastre-derived rings (they need cleaning first), missing LiDAR HD building points under trees, trim sheets that look "game-like" unless the style is decided first.

---

## 5. Pilot plan: one real site (your commune + its marked trails)

Site: **Lacabarède** (your profile; the Trek relief pack already exists) and the **Vol 1 / le Croupou** zone already bundled. Working CRS everywhere: **Lambert-93 EPSG:2154, heights NGF-IGN69**. Local metres are derived from the bbox SW corner, as TerraSketch already does.

### 5.1 Datasets to download

For a whole commune, use department packages (D081) instead of the WMS/WFS calls the pipeline makes today. The live services are fine for ≤1 km², but slow and capped beyond.

| Data | Link | Format / CRS | Use |
|---|---|---|---|
| RGE ALTI 1 m | https://geoservices.ign.fr/rgealti | ASC tiles, L93 / IGN69, by department | Terrain far ring (and fallback) |
| LiDAR HD MNT / MNS / MNH 50 cm | https://cartes.gouv.fr/telechargement/IGNF_MNT-LIDAR-HD (and the MNS / MNH pages on cartes.gouv.fr) | GeoTIFF 1 km tiles, L93 | Terrain near ring, trees, roofs |
| LiDAR HD classified point cloud | cartes.gouv.fr → "Nuages de points LiDAR HD" | COPC LAZ 1 km tiles, L93 | Splat ICP ground (class 2), roofer (class 6) |
| BD ORTHO 20 cm RGB + IRC | https://geoservices.ign.fr/bdortho | JPEG2000 by department, L93 | Tint, crown colour, classifier features. **Note the acquisition date in the metadata** |
| BD TOPO V3 | https://geoservices.ign.fr/bdtopo | GeoPackage by department, L93 | Buildings, roads (width), hydro surfaces, power lines |
| BD Forêt V2 | https://geoservices.ign.fr/bdforet (also data.gouv.fr "BD Forêt®") | Vector by department, L93 | Tree archetypes |
| OCS GE | https://geoservices.ign.fr/ocsge (data.gouv.fr "OCS GE") | GeoPackage/Shapefile by department, L93 | Material map. **Check that D081 is published** |
| Cadastre (Etalab) | https://cadastre.data.gouv.fr | GeoJSON per commune, EPSG:4326 | Estate plot boundaries (client-facing) |
| OSM | Overpass (already used) or Geofabrik regional extract | PBF / JSON, 4326 | Paths, POIs, power lines (already in the viewer) |
| Marked trails | OSM hiking relations via `trek_geo/hikes.py`; Tarn departmental trail network (PDIPR) / Geotrek if available (`trek_geo/geotrek.py`) | GeoJSON, 4326 | Trail overlay with source + date |
| Enedis overhead HTA/BT | Enedis open data (already called in the viewer) | GeoJSON | Obstacles + optional visible lines |

### 5.2 Processing steps (batches, not micro-steps)

**Batch A — Ground (3 d)**
1. Commune polygon + 500 m buffer → L93 bbox, split into 1 km tiles aligned to LiDAR HD tiles.
2. Mosaic MNT 50 cm (near) + RGE ALTI 1 m (far) → 16-bit heightmap tiles + `normal_relief` (existing `heightmap.py`, `relief_normal.py`, fed from files instead of WMS).
3. BD ORTHO 20 cm mosaic → existing `stylize.py` grade → colour tiles.
Output: terrain tiles + ortho, same local-metre contract as today.

**Batch B — Materials (3–4 d)**
4. B1 fusion (§2.b) → 8-weight control maps + road/bank masks.
Output: `splatmap_*.png` replacing the k-means one.

**Batch C — Vegetation (4 d data)**
5. MNH maxima + crowns + BD Forêt/OCS GE archetypes + ortho tint → `tree_scatter.json` (same schema, `type` filled).
6. `scatter_rules.py` driven by the B1 material map.

**Batch D — Buildings (8 d)**
7. Q3-0 fix + Q3-1 skeleton roofs + Q3-2 LiDAR roof typing → `buildings.obj/json`.

**Batch E — Trails + POIs (1 d)**
8. `trek_geo` hikes for the commune → GeoJSON → draped ribbons in local metres; POIs from existing `poi_classify.py`.

**Batch F — Hero spot (2 d)**
9. One mission-viewer flight plan → KMZ → capture → Postshot → SuperSplat → ICP to LiDAR → `.sog`/`.spz` placed in local metres.

**Batch G — Deliverable (4–5 d)**
10. Web: a three.js viewer (terrain tiles, material shader, instanced trees + impostors, buildings, trails, one splat via Spark). Export glTF + meshopt + KTX2 **instead of OBJ**.
11. Video: the same bundle in Unity URP (existing importer + `ScatterRenderer`), Cinemachine flythrough, seasonal slider.

**Expected output:** one web link (commune in 3D, ~30–60 MB total streamed), one 60–90 s video, same data. Pilot total ≈ **25–28 working days**, done batch by batch with a test stop after each.

### 5.3 Decisions only you can make

1. **Style bar:** "beautiful-exact" illustrated vs photoreal (drives Blender archetypes and trim sheets).
2. **First deliverable:** web viewer or video? (Video skips all mobile budgets and is faster to sell.)
3. **Web viewer stack:** three.js (Spark for splats, light, no engine upgrades) vs Unity WebGPU build (reuses WebGpuWater and the importer but brings back engine maintenance). My lean: three.js for web, Unity for video/kiosk.
4. **Which hero spot** is legal to fly and will impress the tourist office.
5. **Accept GPL tools** (roofer) in the internal pipeline?
6. **Tree archetype list** (6, 8 or 10) and whether you model them yourself.
7. **Material list** for the control maps (my proposed 6–8).
8. **Where the unified pipeline lives:** `Investors3DJourney/terrasketch` (original) or the VIZUDRONE copy. Today they are identical, and two copies will drift.

---

## 6. What I still need from Bert

1. **Why were `canopy_mesh` / `canopy_zones` / `forest_paint` / `blob_placement` / `hero_mesh` deleted** (C8)? That answer shapes the far vegetation ring.
2. **GO on fix C3** (BD TOPO roof attribute names) in its own tiny batch, before any roof work.
3. **Commune bbox or INSEE code**, plus which marked trails (names or OSM relation IDs) you want in the pilot.
4. **Drone specifics:** exact model and battery, and what your training certificate covers. I will not guess the class marking.
5. **Target phone** for web budgets now that the X6 is retired (memory: Poco planned).
6. **Is Postshot bought**, or should the pilot use the free Nerfstudio/gsplat path?
7. **Does OCS GE exist for dept 81 on your side?** One click on geoservices will tell; if it doesn't, Q1b goes to B2.
8. **Decisions §5.3** (at least 1, 2, 3 before Batch G).
9. **Confirmation of the single source of truth** for TerraSketch (C1 / §5.3-8).

---

## 7. Not read / not verified

- Not read: `Assets/TerraSketch/Editor/TerraSketchImporter.cs` in full (grepped only), `IslandTerrain.shader` body beyond properties/includes, `ThreeJsDemo/`, WebGpuWater internals (relied on memory), `trek_geo/world.py` and `visual_pack.py` beyond headers, Trek mobile app code.
- Not verified on official sources: exact 2026 drone declaration delays and forms (secondary sources disagree), Mini 5 Pro sensor/weight class, OCS GE publication for dept 81, maintenance status of the Unity splat packages, exact cartes.gouv.fr dataset IDs for MNS/MNH/point clouds.
- Effort figures are estimates for one developer who knows the code, excluding waiting on data downloads.

### Sources (web)
- [DGAC open-category 2026 summary — Dronexperts (secondary, contains noted errors)](https://dronexperts.io/blog/guide-categorie-ouverte-2026-mise-a-jour-dgac)
- [Drone rules 2026 — DroneSpot (zones method)](https://dronespot.fr/fr/blog/reglementation-drone-france-geoportail-zones-vol)
- [MNT LiDAR HD download — cartes.gouv.fr](https://cartes.gouv.fr/telechargement/IGNF_MNT-LIDAR-HD)
- [MNS LiDAR HD — data.gouv.fr](https://www.data.gouv.fr/datasets/mns-lidar-hd)
- [BD Forêt V2 documentation — IGNF GitHub](https://github.com/IGNF/cartes.gouv.fr-documentation/blob/main/content/fr/partenaires/ign/referentiels-description-territoire/foret/bd-foret-v2.md)
- [OCS GE — data.gouv.fr](https://www.data.gouv.fr/datasets/ocs-ge)
- [Spark renderer LoD — sparkjs.dev](https://sparkjs.dev/docs/new-spark-renderer/)
- [roofer — 3DBAG GitHub](https://github.com/3DBAG/roofer)
