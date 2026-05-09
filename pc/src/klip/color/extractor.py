"""Extract a color palette from an image using simple k-means."""
from typing import List

import numpy as np
from PIL import Image


def extract_palette(img: Image.Image, k: int = 5, sample_size: int = 256) -> List[str]:
    """Return k hex-color strings sampled from img using k-means.

    The image is downsampled to sample_size x sample_size for speed.
    """
    if k < 1:
        raise ValueError("k must be >= 1")
    img = img.convert("RGB").resize((sample_size, sample_size), Image.BILINEAR)
    pixels = np.asarray(img, dtype=np.float32).reshape(-1, 3)

    rng = np.random.default_rng(42)
    idx = rng.choice(len(pixels), size=k, replace=False)
    centroids = pixels[idx].copy()

    for _ in range(20):
        dists = np.linalg.norm(pixels[:, None, :] - centroids[None, :, :], axis=2)
        labels = np.argmin(dists, axis=1)
        new_centroids = np.array([
            pixels[labels == j].mean(axis=0) if (labels == j).any() else centroids[j]
            for j in range(k)
        ])
        if np.allclose(new_centroids, centroids, atol=0.5):
            break
        centroids = new_centroids

    palette = []
    for c in centroids:
        r, g, b = (int(round(v)) for v in c)
        palette.append(f"#{r:02x}{g:02x}{b:02x}")
    return palette
