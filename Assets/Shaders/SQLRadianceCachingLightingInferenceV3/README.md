# SQL Radiance Live V3

Open `SQLTestV3.unity` in this folder for a separate copy of the existing SQL test scene, or `Demo/SQLV3Features.unity` for moving rigid geometry, a bone-animated character, an emissive checker texture, and all five Unity light types. Enter Play mode to animate the feature demo.

The original SQL folders, scenes, and materials are preserved. The new namespace/assembly is `SQLRadianceLiveV3`, avoiding the archived `V3Wrong` types. The shader is `InferenceLighting/SQLRadianceLiveV3Lit`. Add one `SQLRadianceV3System` to a scene and assign this shader to surfaces that should receive the cached lighting. Other opaque renderers can still participate in reflection visibility and transport.

## Rendering and update costs

- Mirrors retain the working local-space analytic intersections, ray origins, reflection directions, and 32-step ceiling. Infinite mode uses the existing approximate closed-form tail. Raising the displayed bounce count does not add ray iterations. This tail is an approximation, not an exact arbitrary-scene infinite transport solution.
- Rigid transforms update in `LateUpdate`. Cubes, planes, spheres, and capsules use analytic intersections. Custom meshes and skinned characters use separate bounds hierarchies; a missed object bound skips its triangles. Bone poses and blend shapes are captured with `BakeMesh`, once per character per update, and bounds are refitted without rebuilding the hierarchy. This has CPU cost proportional to character complexity.
- Diffuse GI is spatial transport: surface samples gather previous-frame outgoing radiance at actual ray hits, including albedo, direct lighting, and textured emission. Repeated budgeted updates propagate further bounces. The old scalar multiplier of direct light is not used as GI.
- The default budget is 2,048 surface samples and four GI rays per sample per frame, independent of mirror depth. Primitive caches contain 8x8 samples per face and use hardware bilinear sampling; mesh lighting is cached at vertices. Geometry movement is immediate, while reflected lighting and indirect shadows can take a full cache refresh to respond. The inspector reports this latency.
- Directly visible directional, point, and spot lighting uses URP Forward/Forward+ lighting and shadow maps. The cache independently evaluates those light types plus rectangle and disc area lights, including occlusion. Area-light direct diffuse illumination comes from this cache, so it works even though URP 14 does not render these as realtime punctual lights. [Unity light type reference](https://docs.unity.com/en-us/engine/6000.3/manual/lighting-overview/lighting/light-component).
- Reflected specular lighting uses the strongest cached, shadowed light at each surface sample. This keeps metallic objects lit without a light/shadow loop per reflection bounce. Highlights from weaker lights are approximated; this is not a full directional radiance cache.

## Current limits

There are 32 surface slots (each mesh submaterial uses a slot), 32 cached lights, and a configurable mesh-vertex budget. Overflow is reported explicitly. New/removed objects and material/mesh assignments are discovered once per second; use **Rebuild scene registry** for immediate discovery. Transform, bone, light intensity, and emission changes do not require a bake.

The path supports opaque materials, albedo/emission textures and their tiling/offset, procedural patterns, uniform metallic/smoothness, and primary-surface normal maps. Texture caches are 128x128 per material. Transparency, alpha-cutout visibility, reflected normal maps, light cookies inside the transport cache, and rendering-layer filtering inside that cache are not implemented. Primary URP lighting supports its cookies/rendering layers; cached lights honor GameObject culling masks. Imported static/custom meshes must be readable. Primitive intersection accuracy and scale assumptions follow the working baseline; use mesh geometry for irregular shapes.

GI and reflected/area shadows are intentionally lower resolution than primary shadow maps. Small emitters and fine geometry may require more GI rays and a higher update budget. Very dense characters increase skin-capture and reflection traversal cost. A renderer can only belong to one active V3 cache; multiple independent V3 systems in the same render context are unsupported.

## Validation

Use **Tools > Inference Lighting > SQL V3 Live** to create a demo, copy the active scene with separate materials, or run the GPU checks. Creating a copy preserves the source scene's unsaved state.

`python Tools/check_sql_live_v3.py` compiles runtime/editor C#, compute kernels, and Forward/Forward+/shadow/depth shader variants against installed Unity 2022.3 and URP 14. It also checks the 156 captured original files for changes.

`python Tools/run_sql_live_v3.py Run` checks lights-off black, all five light types, moving shadow occlusion, shadow flags, rigid movement, offscreen bone deformation, emission-texture changes lighting another object, spatial diffuse bounce, GI disabling, and fixed dispatch budgets. `CaptureAndBenchmark` and `CaptureSceneCopy` render comparison images and synchronized editor timings under `Tools/sql-live-v3-validation`.

Timing reports include rendering, update submission, and a GPU-synchronizing readback. They are not isolated GPU timestamps or standalone-player guarantees. See the generated `performance.txt` and `scene-copy.txt` for the latest measurements and `gpu-validation.txt` for correctness results.
