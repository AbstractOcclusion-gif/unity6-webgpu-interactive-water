// WebGpuWater - the debug-view selector, shared by every consumer.
// ONE declaration of _WaterDebugMode and ONE list of the mode ordinals. They are mirrored by
// WaterDebugView.Mode on the C# side and read by shaders in two different dialects
// (WaterSurface.shader is UnityCG, WaterUnderwaterFog.shader is URP-core), so letting each keep
// its own copy of the list would be a third and a fourth place for the ordinals to drift out of
// step with the enum - and a drifted ordinal shows up as the WRONG debug view, which is a trap
// during exactly the kind of hunt these views exist for.
//
// Dialect-free on purpose (a float uniform and integer defines, nothing else), so both dialects
// can include it - the same rule WaterExclusionMesh.hlsl follows for the same reason.
#ifndef WEBGPUWATER_DEBUG_MODE_INCLUDED
#define WEBGPUWATER_DEBUG_MODE_INCLUDED

// Published every frame by WaterDebugView.cs. 0 = off, and every consumer sits behind a UNIFORM
// branch, so a scene without the component pays one comparison per pixel.
float _WaterDebugMode;

#define WATER_DEBUG_OFF               0

// ---- Surface views: WaterSurfaceDebug.hlsl, replacing WaterSurface.shader Pass 0's colour ----
#define WATER_DEBUG_REFLECTION_GATE   1
#define WATER_DEBUG_RENDERER_ID       2
#define WATER_DEBUG_PLANAR_UV         3
#define WATER_DEBUG_VIEW_NORMAL       4
#define WATER_DEBUG_RAW_MIRROR        5
#define WATER_DEBUG_MIRROR_EMPTY      6

// ---- Fullscreen fog views: WaterFogDebug.hlsl, replacing the whole frame ----
#define WATER_DEBUG_FOG_ARM_WEIGHT    7
#define WATER_DEBUG_FOG_UNPAINTED     8
#define WATER_DEBUG_FOG_CLASSIFY_SRC  9
#define WATER_DEBUG_FOG_PATH_BRANCH  10
#define WATER_DEBUG_FOG_GATES        11
#define WATER_DEBUG_FOG_MASK_VS_SPAN 12
#define WATER_DEBUG_FOG_SHEET_SIDE   13

// The first fog ordinal, so each side can ignore the other's views by range instead of by
// listing them: the surface pass must not claim a fog view, and the fog pass must not claim a
// surface one, or a single mode would be painted twice.
#define WATER_DEBUG_FOG_FIRST         WATER_DEBUG_FOG_ARM_WEIGHT
#define WATER_DEBUG_FOG_LAST          WATER_DEBUG_FOG_SHEET_SIDE

// ---- Surface views added after the fog block ----
// APPENDED, never renumbered: WaterDebugView.Mode is serialized as an int on the component, so
// reusing an ordinal would silently repoint every saved scene's selection at a different view.
// Both sides therefore test the fog block as a RANGE rather than as everything past FOG_FIRST.
#define WATER_DEBUG_SIM_HEADROOM     14
#define WATER_DEBUG_FOAM_MASK        15
#define WATER_DEBUG_SIM_WINDOW       16
#define WATER_DEBUG_RIPPLE_FIELD     17

#endif // WEBGPUWATER_DEBUG_MODE_INCLUDED
