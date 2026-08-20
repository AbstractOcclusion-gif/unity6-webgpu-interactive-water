"""Prepare an original image-generated water detail texture for Unity.

The source image is intentionally kept outside the package. This script makes its
opposite edges agree and projects RGB values back onto the tangent-space normal
hemisphere so the installed texture behaves as a real normal map.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image


OUTPUT_SIZE_PIXELS = 1024
EDGE_BLEND_PIXELS = 96
CHANNEL_MIDPOINT = 127.5
CHANNEL_RADIUS = 127.5
MINIMUM_VECTOR_LENGTH = 1.0e-6
NORMAL_BLUE_MINIMUM = 0.05
RGB_CHANNEL_COUNT = 3
PNG_FORMAT = "PNG"
RESAMPLE_FILTER = Image.Resampling.LANCZOS


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Create the seamless Unity water detail normal map."
    )
    parser.add_argument("source", type=Path, help="Image-generated source PNG")
    parser.add_argument("destination", type=Path, help="Unity destination PNG")
    return parser.parse_args()


def smoothstep(value: float) -> float:
    return value * value * (3.0 - (2.0 * value))


def blend_opposite_edges(pixels: np.ndarray, axis: int) -> None:
    edge_length = pixels.shape[axis]
    for edge_offset in range(EDGE_BLEND_PIXELS):
        opposite_offset = edge_length - edge_offset - 1
        normalized_offset = edge_offset / float(EDGE_BLEND_PIXELS - 1)
        blend_weight = 0.5 * (1.0 - smoothstep(normalized_offset))

        leading_slice = [slice(None)] * pixels.ndim
        trailing_slice = [slice(None)] * pixels.ndim
        leading_slice[axis] = edge_offset
        trailing_slice[axis] = opposite_offset
        leading_values = pixels[tuple(leading_slice)].copy()
        trailing_values = pixels[tuple(trailing_slice)].copy()

        pixels[tuple(leading_slice)] = (
            leading_values * (1.0 - blend_weight)
            + trailing_values * blend_weight
        )
        pixels[tuple(trailing_slice)] = (
            trailing_values * (1.0 - blend_weight)
            + leading_values * blend_weight
        )


def normalize_tangent_space_normals(pixels: np.ndarray) -> np.ndarray:
    normals = (pixels - CHANNEL_MIDPOINT) / CHANNEL_RADIUS
    normals[:, :, 2] = np.maximum(normals[:, :, 2], NORMAL_BLUE_MINIMUM)
    lengths = np.linalg.norm(normals, axis=2, keepdims=True)
    normals /= np.maximum(lengths, MINIMUM_VECTOR_LENGTH)
    encoded = (normals * CHANNEL_RADIUS) + CHANNEL_MIDPOINT
    return np.clip(np.rint(encoded), 0.0, 255.0).astype(np.uint8)


def prepare_texture(source: Path, destination: Path) -> None:
    if not source.is_file():
        raise FileNotFoundError(f"Source image does not exist: {source}")

    with Image.open(source) as source_image:
        resized_image = source_image.convert("RGB").resize(
            (OUTPUT_SIZE_PIXELS, OUTPUT_SIZE_PIXELS), RESAMPLE_FILTER
        )
        pixels = np.asarray(resized_image, dtype=np.float32).copy()

    if pixels.shape[2] != RGB_CHANNEL_COUNT:
        raise ValueError(f"Expected RGB source pixels, received shape {pixels.shape}")

    blend_opposite_edges(pixels, axis=1)
    blend_opposite_edges(pixels, axis=0)
    normalized_pixels = normalize_tangent_space_normals(pixels)
    destination.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(normalized_pixels, mode="RGB").save(destination, PNG_FORMAT)


def main() -> None:
    arguments = parse_arguments()
    prepare_texture(arguments.source, arguments.destination)


if __name__ == "__main__":
    main()
