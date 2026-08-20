# Water detail normal provenance

`water detail.png` was replaced on 2026-08-19 with an original image-generated
source made specifically for this package. The built-in OpenAI image generation
workflow was prompted for a seamless tangent-space water micro-ripple normal map
with no text, logos, recognizable imagery, or borrowed style.

`prepare_water_detail.py` performs the technical production pass: it resizes the
source to 1024 x 1024, blends opposite edges, and normalizes each RGB tangent-space
vector. The original generated source is intentionally not distributed because
only the prepared game texture is needed by the package.
