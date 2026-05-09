"""Direct ONNX background remover — uses BiRefNet model from ~/.u2net cache.

We talk to onnxruntime directly instead of going through rembg, so we don't pull
in opencv/scikit-image as transitive dependencies (~100 MB).
"""
from __future__ import annotations

import os
from pathlib import Path
from typing import Optional

import numpy as np
import onnxruntime as ort
from PIL import Image
from PySide6.QtCore import QObject, QThread, Signal


_DEFAULT_MODEL_NAMES = {
    "birefnet-general": ("birefnet-general.onnx", (1024, 1024)),
    "u2net": ("u2net.onnx", (320, 320)),
}


def _model_path(model_name: str) -> Path:
    filename, _ = _DEFAULT_MODEL_NAMES[model_name]
    cache = Path(os.environ.get("U2NET_HOME", str(Path.home() / ".u2net")))
    return cache / filename


def _input_size(model_name: str):
    return _DEFAULT_MODEL_NAMES[model_name][1]


class _Session:
    _cache: dict[str, ort.InferenceSession] = {}

    @classmethod
    def get(cls, model_name: str) -> ort.InferenceSession:
        if model_name in cls._cache:
            return cls._cache[model_name]
        path = _model_path(model_name)
        if not path.exists():
            raise FileNotFoundError(f"BG remover model not found: {path}")
        providers = ["CPUExecutionProvider"]
        sess = ort.InferenceSession(str(path), providers=providers)
        cls._cache[model_name] = sess
        return sess


def _normalize_birefnet(img: Image.Image, size):
    """BiRefNet preprocess: resize → ImageNet normalize."""
    img = img.convert("RGB").resize(size, Image.BILINEAR)
    arr = np.asarray(img, dtype=np.float32) / 255.0
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    arr = (arr - mean) / std
    arr = arr.transpose(2, 0, 1)  # HWC -> CHW
    return arr[np.newaxis, ...]   # add batch dim


def _normalize_u2net(img: Image.Image, size):
    """U2Net preprocess: resize → /255 → custom mean/std."""
    img = img.convert("RGB").resize(size, Image.BILINEAR)
    arr = np.asarray(img, dtype=np.float32) / 255.0
    # u2net's stats from rembg
    mean = np.array([0.485, 0.456, 0.406], dtype=np.float32)
    std = np.array([0.229, 0.224, 0.225], dtype=np.float32)
    arr = (arr - mean) / std
    arr = arr.transpose(2, 0, 1)
    return arr[np.newaxis, ...]


def _postprocess_mask(mask: np.ndarray, target_size) -> Image.Image:
    """ONNX output → 8-bit alpha mask sized to target_size (W, H)."""
    if mask.ndim == 4:
        mask = mask[0]
    if mask.ndim == 3:
        mask = mask[0]
    mask = mask - mask.min()
    if mask.max() > 0:
        mask = mask / mask.max()
    mask = (mask * 255).astype(np.uint8)
    pil = Image.fromarray(mask, mode="L")
    return pil.resize(target_size, Image.BILINEAR)


def remove_background(
    img: Image.Image,
    model_name: str = "birefnet-general",
) -> Image.Image:
    """Run BG removal. Returns an RGBA PIL image with the background transparent."""
    if model_name not in _DEFAULT_MODEL_NAMES:
        raise ValueError(f"Unknown model: {model_name}")

    sess = _Session.get(model_name)
    in_size = _input_size(model_name)

    if model_name == "birefnet-general":
        x = _normalize_birefnet(img, in_size)
    else:
        x = _normalize_u2net(img, in_size)

    input_name = sess.get_inputs()[0].name
    outputs = sess.run(None, {input_name: x})
    mask_arr = outputs[0]

    target = (img.width, img.height)
    alpha = _postprocess_mask(mask_arr, target)

    rgba = img.convert("RGBA")
    r, g, b, _ = rgba.split()
    rgba = Image.merge("RGBA", (r, g, b, alpha))
    return rgba


class BgRemovalWorker(QObject):
    """QObject for running BG removal off the UI thread."""

    finished = Signal(object, object)  # (PIL.Image | None, error message | None)

    def __init__(self, img: Image.Image, model_name: str = "birefnet-general"):
        super().__init__()
        self._img = img
        self._model = model_name

    def run(self):
        try:
            out = remove_background(self._img, self._model)
            self.finished.emit(out, None)
        except Exception as e:
            self.finished.emit(None, str(e))


def run_async(
    img: Image.Image,
    model_name: str,
    on_done,
    parent: Optional[QObject] = None,
) -> tuple[QThread, BgRemovalWorker]:
    """Run remove_background in a QThread. on_done(img_or_none, err_or_none)."""
    thread = QThread(parent)
    worker = BgRemovalWorker(img, model_name)
    worker.moveToThread(thread)
    thread.started.connect(worker.run)
    worker.finished.connect(lambda result, err: on_done(result, err))
    worker.finished.connect(thread.quit)
    worker.finished.connect(worker.deleteLater)
    thread.finished.connect(thread.deleteLater)
    thread.start()
    return thread, worker
