# Chunk water — v1.1 development note

The current chunk-water implementation is a reduced-control feature. It provides finite box,
sphere, and mesh water bodies with their own surface and fog shell, but it is not yet intended to
offer the full authoring range or rendering parity of the standard ocean.

Full chunk-water implementation is planned for v1.1. The target is to let a chunk use the same
cohesive water controls as a standard ocean, including wave shaping, surface shading, foam, optical
volume, underwater effects, and quality controls, while preserving shape-specific boundary and
stitch behavior.

Until that work lands:

- treat the chunk controls as a focused preview subset;
- avoid duplicating the complete ocean settings into chunk-only fields;
- prefer shared evaluators and settings so standard water and chunk water cannot drift apart;
- keep box/sphere/mesh boundary behavior explicit and independently testable.

The current box boundary modes (`Vertical`, `Stabilized`, and `Calm Rim`) are expected to remain as
shape-specific controls when the shared v1.1 water-authoring model is introduced.
