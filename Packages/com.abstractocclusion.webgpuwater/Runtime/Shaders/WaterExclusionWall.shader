// WebGpuWater - exclusion-volume water walls (the carve boundary, drawn).
// The volume's own unit MESH (cube or sphere, matching its authored Shape) rendered per
// exclusion volume with the volume's shape-to-world matrix (Graphics.DrawMesh from
// WaterExclusionVolume), shaded as STANDING WATER: the same lit
// in-scatter colour the underwater fog uses, depth-darkened, with a per-volume scatter
// boost so the wall reads slightly denser than open fog. This is what fills the carve's
// boundary for volumes WITHOUT covering geometry - a bare dry volume otherwise exposes the
// unlit void (through the surface hole from above, and at the carve edges underwater).
// The mesh IS the boundary, so every term below can assume the fragment sits exactly ON it.
//
// Cull Off ON PURPOSE: exterior faces paint the near boundary at fog colour (an air
// pocket seen from open water blends back into the fog instead of punching a dark hole,
// and a submerged volume seen from ABOVE shows a fog-coloured lid where the surface sheet
// is discarded); interior faces are the aquarium walls seen from inside the dry space.
// Volumes covered by real geometry (boat hulls, rooms with windows) should draw with
// drawWaterWalls OFF - the wall would paint over their openings.
//
// TRANSPARENT by the water's own optical depth: opacity = 1 - exp(-extinction * density *
// path), premultiplied per channel - clear (low-density) water is a see-through veil, murky
// water saturates to the full scatter colour. The wall deliberately does NOT write depth
// and has NO depth-prepass passes: it must stay OUT of _CameraDepthTexture so the
// fullscreen underwater fog integrates to the REAL scene behind it (carved through the
// box) - the transparent wall then tints on top. God rays likewise march through it
// (their in-box samples are skipped + sun-shadowed by the volume itself).
// No fresnel/specular yet; the waterline clip is the primary body's REST plane - the
// meniscus/wavy seal is the next step on top of this pass.
Shader "AbstractOcclusion/WebGpuWater/WaterExclusionWall"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha // premultiplied: rgb carries per-channel opacity, a = coverage

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterFog.hlsl"       // WaterInscatterColor + DownwellingAttenuation + fog globals
            #include "WaterVolume.hlsl"    // _VolumeCenter: the primary body's rest plane (waterline clip)
            #include "WaterExclusion.hlsl" // carve helpers + shared shadow-column terms (fog reconstruction)
            #include "WaterExclusionMeshSpan.hlsl" // MESH volumes: the prepass dry span (URP-core only)
            #include "WaterShore.hlsl"     // ShoreShoalDepth: the fog pass's depth-clarity input
            #include "WaterWaterline.hlsl" // SurfaceHeightAtXZ: the displaced waterline the wall clips at

            // Sun globals (published by WaterUniformPublisher), same declarations as the fog pass.
            float3 _LightDir;
            // _SunColor is declared by WaterFog.hlsl (included above) - the header that owns the in-scatter needing it.
            // "The eye is IN WATER" (published by PublishUnderwater): with the armed flag below it
            // gates the above-water fog reconstruction. Camera state -> uniform -> screen-coherent.
            // NOT "below the surface plane": an eye inside a dry carve is below sea level and still
            // in air, and reads 0 - so with the fullscreen fog disarmed the wall correctly
            // self-completes the water seen through the pane from inside a sunken room.
            float _CameraUnderwater;
            // 1 when the fullscreen underwater fog pass runs this frame (published by
            // PublishUnderwater from WaterVolume.UnderwaterFogActive). When armed, the fog paints
            // the water behind the veil AFTER transparents - the wall must NOT self-complete or its
            // opaque backdrop would hide the correctly fogged scene (bounded lakes seen from above).
            float _UnderwaterFogArmed;
            // 1 = the quality tier's Simple fog mode (flat waterline): the wall then keeps the
            // flat rest-plane clip, the same branch the fog itself takes on that tier.
            float _UnderwaterFogSimple;
            // The rest of the fog's own gate state, read here so this wall can work out how much
            // of each pixel the fullscreen pass will actually paint (see FogCoverageAtPixel).
            float _UnderwaterUnbounded; // 1 = ocean half-space, 0 = bounded body (fog never masked)
            float _CameraDryVolume;     // 1 = the EYE sits inside a dry carve
            float _UnderwaterSurfaceY;  // CPU surface height at the eye, the Simple tier's waterline
            // Opaque scene colour behind this fragment (the wall stays out of the depth texture, so
            // depth + opaque colour both hold the REAL scene through the carve). Same codebase-wide
            // sampler2D style as WaterSurfaceScreen.hlsl.
            sampler2D _CameraOpaqueTexture;
            // This volume's PRIMITIVE_SHAPE_* selector (MaterialPropertyBlock). A plain float,
            // not a keyword: the wall material is shared by every volume, so a keyword would
            // make the last volume drawn decide the shape for all of them.
            float _WallShape;
            // Per-volume wall density (MaterialPropertyBlock): >1 reads denser than open fog,
            // the "different scatter values" of the carve boundary.
            float _WallScatterBoost;
            // Per-volume edge look (MaterialPropertyBlock, same values the publisher sends the
            // fog as _ExclusionEdgeColor/_ExclusionEdgeParams for this volume's slot - the wall
            // is drawn per volume, so plain uniforms replace the array lookup here).
            float4 _WallEdgeColor;  // rgb = tint target, a = intensity
            float  _WallEdgeSpread;

            // Floor for the eye -> near-plane direction, mirroring the fog's own guard.
            #define WALL_CLASSIFY_DIR_EPSILON 1e-5

            // How much of THIS PIXEL the fullscreen fog will paint. The wall's mirror of the fog's
            // ArmWeight, so the two can hand off PER PIXEL instead of through a screen-wide flag.
            //
            // WHY THIS EXISTS. The wall used to self-complete on _UnderwaterFogArmed alone: fog
            // armed -> wall stands down. But the fog's mask is per pixel, and with the eye in air
            // just above the surface it admits NOTHING while the pass is still armed. So arming
            // switched the wall off without switching the fog on in its place, and a carve seen
            // from just above the water was painted by nobody - the empty zone, which read as the
            // wall "disappearing a bit too quick" from this side and as a masked-away span
            // (debug mode 8 magenta) from the fog's. The same binary handoff, on the other side,
            // is what produced the crossing hole fixed in the fog's arming gate.
            //
            // DUPLICATED ONLY THIS FAR, and deliberately: the CURVE is the shared
            // WaterlineCoverage, so the two edges cannot come out different shapes. Only the
            // classification POINT is re-derived here - the project rule is that duplicating a
            // classification is cheap while duplicating a silhouette is not, so share the test and
            // let each consumer resolve its own edge per pixel.
            //
            // Derivative safety: every branch below is on a UNIFORM global, so the fwidth sits in
            // uniform control flow, and the caller evaluates this before any per-pixel clip.
            float FogCoverageAtPixel(float2 screenUV)
            {
                if (_UnderwaterFogArmed < 0.5) return 0.0;  // the pass will not run: it paints nothing
                if (_UnderwaterUnbounded < 0.5) return 1.0; // bounded body: the fog is never masked
                float3 classifyPoint = ComputeWorldSpacePosition(screenUV, UNITY_NEAR_CLIP_VALUE,
                                                                 UNITY_MATRIX_I_VP);
                if (_CameraDryVolume > 0.5)
                {
                    // Eye inside a carve: the fog pushes its point out to where the ray LEAVES the
                    // carve (the Crest portal move) because a lens in a sunken room says nothing
                    // about the water outside. Same push, same function.
                    float3 toNear = classifyPoint - _WorldSpaceCameraPos;
                    float3 rayDir = toNear / max(length(toNear), WALL_CLASSIFY_DIR_EPSILON);
                    classifyPoint += rayDir * ExclusionPushToExit(classifyPoint, rayDir, 0.0,
                                                                  _ProjectionParams.z);
                }
                float gap = (_UnderwaterFogSimple > 0.5) ? classifyPoint.y - _UnderwaterSurfaceY
                                                         : SurfaceSignedGap(classifyPoint);
                float overCover = (_CameraDryVolume > 0.5) ? WATERLINE_CARVE_OVER_COVER_PIXELS : 0.0;
                return WaterlineCoverage(gap, fwidth(gap), overCover);
            }

            // Waterline classification (see frag): the curve and its gradient floor are SHARED
            // with the fullscreen fog's mask (WaterWaterline.hlsl, WaterlineCoverage) so the two
            // edges are the same shape and cannot leave a band between them. Only the coverage
            // below which a fragment is dropped instead of paying the blend lives here.
            #define WALL_MIN_COVERAGE 0.002

            // The sun-wrap, edge-occlusion and facet constants live in WaterExclusion.hlsl
            // (EXCLUSION_PANE_* / EXCLUSION_EDGE_*): the fog's carve-boundary pane shading and
            // this wall must shade the same edges identically, whoever ends up drawing them.

            // Fog reconstruction for above-water views (camera in air, fullscreen fog disarmed):
            // nothing else paints the water behind the veil, so the wall runs the SAME
            // absorb + inscatter + shadow-column + downwelling integral the fog pass runs, over
            // the wet span from this fragment to the real scene point behind it (the wall stays
            // OUT of the depth texture, so scene depth + opaque colour hold the true backdrop
            // through the carve). The hole seen from outside then matches the water seen when
            // diving in. Mirrors WaterUnderwaterFog's UnderwaterFog/FragInscatter step for step.
            float3 ReconstructedFogBackground(float3 wallWS, float3 viewDirWS, float3 sceneWorld,
                                              float2 screenUV)
            {
                float3 sceneColor = tex2Dlod(_CameraOpaqueTexture, float4(screenUV, 0.0, 0.0)).rgb;

                float level = _VolumeCenter.y; // the rest plane, the same waterline the wall clips at
                float3 seg = sceneWorld - wallWS;
                float segLen = max(length(seg), 1e-5);
                float3 segDir = seg / segLen;
                // Wet span behind the wall: the below-waterline part of [wall -> scene] minus the
                // dry boxes it crosses. The fragment sits ON its own box, so an outward ray loses
                // ~nothing and a ray through the volume loses exactly the dry interior.
                float wetSpanLen = WaterPathLength(sceneWorld, wallWS, level);
                float dryLen = ExclusionRayLength(wallWS, segDir, wetSpanLen);
                if (_ExclusionMeshCount > 0.5)
                    dryLen += ExclusionMeshRayLength(screenUV, wallWS, segDir, wetSpanLen);
                float pathLen = max(wetSpanLen - dryLen, 0.0);

                // Deepest WET point of the span (the downwelling reference), pulled out of any dry
                // volume containing it - the same correction, for the same reason, as the fog pass.
                float deepestY = level;
                float sunVisibility = 1.0;
                if (pathLen > 0.0)
                {
                    float tDeep = (segDir.y <= 0.0)
                                ? ExclusionPullToEntry(wallWS, segDir, wetSpanLen)
                                : ExclusionPushToExit(wallWS, segDir, 0.0, wetSpanLen);
                    deepestY = max(min(level, min(wallWS.y, sceneWorld.y)), wallWS.y + segDir.y * tDeep);
                    // Carved presence: the shared analytic shadow column (a wall always has at
                    // least its own volume active, so no _ExclusionCount gate is needed here).
                    sunVisibility = ExclusionSpanSunVisibility(wallWS, segDir, wetSpanLen, pathLen,
                                                               _LightDir);
                }

                // The same depth-clarity density the fog pass uses, so the reach through the hole
                // matches the reach seen when submerged. Fog off -> transmittance 1: the hole
                // shows the clear scene, matching the fogless water around it.
                float clarity = WaterDepthClarity(ShoreShoalDepth(sceneWorld.xz));
                float density = _WaterFogDensity * lerp(CLARITY_FOG_DENSITY_MAX, 1.0, clarity);
                float3 transmittance = (_WaterFogEnabled > 0.5)
                                     ? exp(-_WaterExtinction.rgb * (density * pathLen))
                                     : float3(1.0, 1.0, 1.0);
                float3 fogColor = WaterInscatterColor(viewDirWS, _LightDir, _SunColor * sunVisibility, 0.0)
                                * lerp(EXCLUSION_SHADOW_FLOOR, 1.0, sunVisibility);
                float3 depthAttenuation = DownwellingAttenuation(deepestY, level);
                return (sceneColor * transmittance + fogColor * (1.0 - transmittance)) * depthAttenuation;
            }

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Walls exist only below the WAVY waterline - the same displaced surface the fog
                // integrates against (SurfaceHeightAtXZ). The old flat rest-plane clip left an
                // EMPTY band between the wall top and a wave crest on partially submerged
                // volumes (the surface sheet is discarded inside the carve, so nothing else
                // filled it). Simple fog tiers keep the flat clip, matching the fog's own branch
                // there. The screen-space meniscus seal remains the follow-up pass.
                float waterlineY = (_UnderwaterFogSimple > 0.5)
                                 ? _VolumeCenter.y
                                 : SurfaceHeightAtXZ(IN.positionWS.xz);
                // CLASSIFY the fragment against that waterline - do NOT cut the mesh at it.
                // A clip turns this test into a SILHOUETTE, and a silhouette then has to line up to
                // the pixel with the surface sheet's own carve silhouette AND with wherever the fog
                // stopped integrating. Where it did not, the frame showed an empty band between the
                // waterline and the fog. A classification cannot gap: the wall mesh is continuous
                // across the line and its contribution simply falls to zero over ONE pixel, resolved
                // from the screen derivative of the gap - the same metres-per-pixel trick the fog's
                // meniscus pass uses for a constant-width band. Crest's portal fragment does exactly
                // this (returns above/below per pixel; it never clips the portal mesh).
                // The derivative is taken HERE, in uniform control flow, ahead of every clip below.
                float surfaceGap = IN.positionWS.y - waterlineY;   // > 0 = this fragment is in air
                // No over-cover here: the wall must not paint water ABOVE its own waterline. The
                // fog is the side that over-covers, and it over-covers ONTO this fragment.
                float submerged = WaterlineCoverage(surfaceGap, fwidth(surfaceGap), 0.0);

                float3 viewDirWS = normalize(_WorldSpaceCameraPos - IN.positionWS);

                // Real scene behind this fragment (the wall stays OUT of the depth texture, so
                // depth + opaque colour hold the true backdrop through the carve). Shared by the
                // veil's carved-span cap below and the above-water fog reconstruction.
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                float3 sceneWorld = ComputeWorldSpacePosition(screenUV, SampleSceneDepth(screenUV),
                                                              UNITY_MATRIX_I_VP);

                // How much of this pixel the fullscreen fog will paint (see FogCoverageAtPixel).
                // Taken HERE: still ahead of every per-pixel clip, so its fwidth and the one above
                // sit in the same uniform control flow, and screenUV is derived only once.
                float fogWeight = FogCoverageAtPixel(screenUV);

                // Unit-local coords of this fragment: the draw matrix IS the volume's
                // shape-to-world, so its inverse lands us in exactly the space the carve math
                // uses. Shared by the surface normal and the boundary occlusion below.
                float3 shapeLocal = mul(GetWorldToObjectMatrix(), float4(IN.positionWS, 1.0)).xyz;

                // Surface normal from the PRIMITIVE, not from screen derivatives: the fragment
                // lies on the analytic surface, so this is exact for both shapes - and on a
                // sphere the derivative normal would face the tessellated FACET instead of the
                // true surface, faceting the rim the edge occlusion is drawn against. A MESH has
                // no analytic surface, so there the facet IS the answer and the derivatives are
                // right. Flipped toward the camera for the Cull Off double-sided draw.
                float3 normalWS = (_WallShape >= EXCLUSION_SHAPE_MESH)
                                ? SafeFacetNormal(IN.positionWS, true, viewDirWS)
                                : normalize(mul(PrimitiveSurfaceNormal(_WallShape, shapeLocal),
                                                (float3x3)GetWorldToObjectMatrix()));
                if (dot(normalWS, viewDirWS) < 0.0) normalWS = -normalWS;
                // Sun side vs shade side: wrapped lambert on the DIRECT term only, so the box
                // reads 3D while the ambient scatter keeps the shade side alive.
                float sunWrap = saturate((dot(normalWS, _LightDir) + EXCLUSION_PANE_SUN_WRAP)
                                       / (1.0 + EXCLUSION_PANE_SUN_WRAP));

                // Standing water: lit in-scatter (falls back to the flat fog colour when volume
                // scattering is off).
                float3 color = WaterInscatterColor(viewDirWS, _LightDir, _SunColor * sunWrap, 0.0);

                // Water opacity over the CARVED span this ray actually crosses behind the
                // fragment (capped at the real scene), per channel: this is BOTH the colour
                // saturation and (via its max channel) the blend coverage. The veil is the exact
                // stand-in for the water the carve removed from the fog integral - an entering
                // face carries the box's dry chord, an exiting face carries ~0. So from open
                // water the pocket blends seamlessly back into the fog, and from INSIDE the
                // carve the veil vanishes instead of double-counting scatter on top of the
                // fully-fogged water behind (the "brighter inside than outside" bug). A fixed
                // 8m stand-in depth previously did that double-counting. The per-volume boost
                // multiplies the OPTICAL DEPTH, so a boosted wall reads as denser water.
                float3 rayDirWS = -viewDirWS; // camera -> fragment, continuing behind it
                float sceneDist = max(dot(sceneWorld - IN.positionWS, rayDirWS), 0.0);
                // Analytic volumes contribute their closed-form chord; MESH volumes contribute the
                // prepass span at this pixel (the analytic loop skips them by design). An entering
                // face therefore still carries the full dry chord and an exiting face ~0, whichever
                // tier the volume belongs to - so the veil stays the exact stand-in for the water
                // the carve removed.
                float carvedSpan = ExclusionRayLength(IN.positionWS, rayDirWS, sceneDist);
                if (_ExclusionMeshCount > 0.5)
                    carvedSpan += ExclusionMeshRayLength(screenUV, IN.positionWS, rayDirWS, sceneDist);
                float3 opacity = 1.0 - exp(-_WaterExtinction.rgb *
                                           (_WaterFogDensity * carvedSpan * _WallScatterBoost));
                color *= opacity;

                // Boundary occlusion: the shared per-shape outline term (a box's edges and
                // corners, a sphere's silhouette rim), tinted by this volume's edge look - the
                // SAME function the fog's pane shading calls, so the outline reads identically
                // whether the wall draws it or the fog reconstructs it.
                float3 edgeTint = ExclusionEdgeTint(
                    ExclusionBoundaryOcclusion(_WallShape, shapeLocal, normalWS, viewDirWS,
                                               _WallEdgeSpread), _WallEdgeColor);
                color *= edgeTint;

                color *= DownwellingAttenuation(IN.positionWS.y, _VolumeCenter.y);
                // Premultiplied output: colour already carries the per-channel opacity; the
                // alpha is the widest channel's coverage for the blend against the scene.
                float coverage = max(opacity.r, max(opacity.g, opacity.b));

                // Above-water views with the fullscreen fog DISARMED (ocean fog arms only for a
                // submerged camera): nothing else paints the water behind the veil, so a clear
                // wall exposed a flat "plain fog" slab in the surface hole. Reconstruct the fog's
                // result for the real scene behind the wall and composite the veil over it - the
                // hole from outside now matches the water seen when diving in. When the fog IS
                // armed (submerged camera, or a bounded lake viewed from any angle) it paints
                // behind the veil after transparents, so the wall must NOT cover it with an
                // opaque backdrop. A submerged camera without fog (tier Off) keeps the bare veil,
                // matching the fogless open water around it.
                // PER-PIXEL HANDOFF. The fullscreen fog paints fogWeight of this pixel; the wall
                // completes the remaining (1 - fogWeight). The two sum to ONE by construction, so
                // there is no gap for an empty zone to live in and no overlap to double-paint -
                // where the fog's mask admits nothing, the wall covers all of it, and where the fog
                // paints fully the wall adds no backdrop at all. This replaces a screen-wide
                // _UnderwaterFogArmed switch, which could only ever be right for the whole frame at
                // once and was wrong for every frame where the pass was armed but masked out.
                // _CameraUnderwater still gates it: with the eye IN water the fullscreen pass owns
                // the frame and an opaque backdrop here would hide the correctly fogged scene.
                float reconstructFill = 1.0 - fogWeight;
                if (_CameraUnderwater < 0.5 && reconstructFill > 0.0)
                {
                    float3 background = ReconstructedFogBackground(IN.positionWS, viewDirWS,
                                                                   sceneWorld, screenUV);
                    // Carve-boundary pane on the reconstructed water: this fragment IS the face
                    // being looked through, so its own edge tint + facet shade the background
                    // exactly as the armed fog pass shades its pierced face.
                    background *= edgeTint * ExclusionFacetFactor(normalWS, _LightDir);
                    // Premultiplied: fill the veil's remaining transparency by reconstructFill and
                    // raise the alpha by the same fraction, so a partial handoff composites exactly
                    // like the old full one did at reconstructFill = 1.
                    color += (1.0 - coverage) * background * reconstructFill;
                    coverage = lerp(coverage, 1.0, reconstructFill);
                }
                // Waterline classification applied LAST so it fades the reconstructed background out
                // together with the veil: above the line this wall contributes nothing at all.
                color *= submerged;
                coverage *= submerged;
                clip(coverage - WALL_MIN_COVERAGE); // fully dry fragments skip the blend entirely
                // NO Unity scene fog on this wall (removed 2026-08-14, bisected to the Aug 8
                // "unity fog" commit). The water SURFACE carries no scene fog, so fogging the
                // wall alone washes its scattering/color with camera distance whenever the eye
                // is in air, and re-opens the water/wall junction seam. If scene fog is ever
                // wanted here again, it must be added to surface AND wall with the same factor.
                return half4(color, coverage);
            }
            ENDHLSL
        }

        // NO DepthOnly / DepthNormals passes ON PURPOSE: the wall must stay out of
        // _CameraDepthTexture so the fullscreen underwater fog and the god rays integrate to
        // the REAL scene behind it (through the carved box) - the transparent veil then
        // composites on top. Putting the wall in the depth texture would clamp the fog at the
        // boundary and show UNfogged scene through a clear (low-density) wall.
    }
}
