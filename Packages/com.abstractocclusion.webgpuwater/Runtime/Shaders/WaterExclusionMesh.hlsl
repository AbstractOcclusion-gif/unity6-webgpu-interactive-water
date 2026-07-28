// WebGpuWater - MESH exclusion volumes: the screen-space half of the carve.
// A closed mesh has no closed-form ray test, so a Mesh-shape exclusion volume is carved from its
// real silhouette instead: WaterExclusionDepthPass rasterises every active mesh volume's FRONT
// faces into _ExclusionMeshFrontDepth and BACK faces into _ExclusionMeshBackDepth before
// transparents, and a consumer that knows its own pixel reads the two to get the dry column's
// ENTRY and EXIT along that pixel's camera ray. Same pattern - and the same convex assumption -
// as the mesh CHUNK prepass (WaterChunkDepth), which is where it is already proven.
//
// WHAT THIS CAN AND CANNOT ANSWER. The depths are taken from the CAMERA, so they answer questions
// about points on a camera ray and nothing else. That covers every geometry query the carve
// actually makes - the surface discard, the fog's dry span, the wall's veil - because all of them
// run along the view ray. It does NOT cover the sun shadow column, the particle cull, or the CPU
// point test, which trace their own directions; those keep using each mesh volume's analytic PROXY
// (its Box or Sphere, per the volume's Mesh Proxy field), which is why the analytic kernels in
// WaterExclusion.hlsl still see mesh volumes as their proxy shape.
//
// DIALECT-FREE ON PURPOSE. Consumers span two shader dialects - WaterSurface.shader is UnityCG,
// the fog and the wall are URP-core - and their depth helpers differ (single- vs two-argument
// LinearEyeDepth, Texture2D.Load vs LOAD_TEXTURE2D_X). So this header does the ONE thing both
// dialects spell identically: a texel fetch returning RAW depth. Linearising it, and reconstructing
// world positions from it, stays with the consumer that knows which dialect it is written in.
#ifndef WEBGPUWATER_EXCLUSION_MESH_INCLUDED
#define WEBGPUWATER_EXCLUSION_MESH_INCLUDED

// Written by WaterExclusionDepthPass (SetGlobalTextureAfterPass). Plain Texture2D + Load: a texel
// fetch needs no sampler, so the mesh tier costs nothing against the 16-sampler d3d11 ceiling.
Texture2D _ExclusionMeshFrontDepth;
Texture2D _ExclusionMeshBackDepth;

// Number of active MESH-shape exclusion volumes (published by WaterUniformPublisher alongside
// _ExclusionCount). 0 = no mesh volume in the scene, so every consumer skips the reads entirely -
// the zero-cost off state the whole exclusion header is built around. Note this counts AUTHORED
// mesh volumes, not whether the prepass feature is installed on the renderer: WaterExclusionVolume
// warns in the editor when a mesh volume is active, because a missing feature is a setup error and
// must not read as "the mesh simply carves nothing".
float _ExclusionMeshCount;

// 1 when WaterExclusionDepthPass actually RECORDED the prepass this frame, 0 otherwise. Lowered
// every frame by WaterUniformPublisher and raised by the pass itself, so what the shaders read is
// "the two RTs below were written for THIS frame" rather than "a volume exists" - a distinction
// _ExclusionMeshCount cannot make, because it counts AUTHORED volumes and says nothing about
// whether the render feature is installed on the renderer (a manual setup step). Polarity is
// deliberate: the unpublished default is 0, which is the pre-prepass behaviour, so a project that
// never installs the feature behaves exactly as it did before.
float _ExclusionPrepassValid;

// Fraction of the far plane past which a linearised depth means "nothing was drawn here". The
// prepass targets clear to far, so an untouched texel linearises to the far plane; testing at
// slightly under it absorbs the precision of the clear without swallowing real geometry, and works
// in BOTH dialects (unlike UNITY_RAW_FAR_CLIP_VALUE, which is SRP-only).
#define EXCLUSION_MESH_FAR_FRACTION 0.99

// RAW front/back depth of the mesh volumes at this pixel, as (front, back). Both read as far when
// no mesh covers the pixel. FRONT reads far while BACK is valid when the camera is INSIDE the mesh
// (its front faces are behind the eye, so nothing rasterised): the consumer then starts the dry
// column at the eye - the same "front empty + back valid = camera inside" rule the chunk wall and
// the chunk top-clip both use.
float2 ExclusionMeshRawSpan(int2 pixel)
{
    int3 texel = int3(pixel, 0);
    return float2(_ExclusionMeshFrontDepth.Load(texel).r,
                  _ExclusionMeshBackDepth.Load(texel).r);
}

// True when a LINEARISED eye depth means "no face here" (see EXCLUSION_MESH_FAR_FRACTION). The
// consumer linearises in its own dialect and passes the result in.
bool ExclusionMeshDepthEmpty(float eyeDepth, float farPlane)
{
    return eyeDepth >= farPlane * EXCLUSION_MESH_FAR_FRACTION;
}

// True when a fragment at `fragmentEyeDepth` lies inside the mesh volumes' dry column at this
// pixel - the carve test itself, for a consumer that only needs a yes/no (the water surface
// discarding its own sheet). `frontEye`/`backEye` are the prepass depths already LINEARISED by the
// caller, `farPlane` is _ProjectionParams.z. This is the Crest volume test the mesh chunk's
// top-clip uses, and it handles the camera-inside case the same way: no entry face means the dry
// column starts at the eye, so a submerged view from inside the carve still reads as inside.
bool ExclusionMeshCoversDepth(float frontEye, float backEye, float fragmentEyeDepth, float farPlane)
{
    if (ExclusionMeshDepthEmpty(backEye, farPlane)) return false;
    float nearBound = ExclusionMeshDepthEmpty(frontEye, farPlane) ? 0.0 : frontEye;
    return fragmentEyeDepth > nearBound && fragmentEyeDepth < backEye;
}

#endif // WEBGPUWATER_EXCLUSION_MESH_INCLUDED
