"""Render Klip pages to PNG / JPG at 4K-original quality."""
from pathlib import Path
from typing import Mapping

from PySide6.QtCore import QRectF, Qt
from PySide6.QtGui import QImage, QPainter

from ..canvas.scene import KlipScene
from ..document.schema import AssetModel, PageModel


def render_page_to_image(page: PageModel, assets: Mapping[str, AssetModel]) -> QImage:
    """Rasterize a Page at its native pixel dimensions (no downscale)."""
    w = int(page.size["w"])
    h = int(page.size["h"])
    img = QImage(w, h, QImage.Format_ARGB32)
    img.fill(Qt.transparent)

    scene = KlipScene()
    scene.load_page(page, assets)

    painter = QPainter(img)
    try:
        painter.setRenderHint(QPainter.Antialiasing, True)
        painter.setRenderHint(QPainter.SmoothPixmapTransform, True)
        painter.setRenderHint(QPainter.TextAntialiasing, True)
        scene.render(painter, QRectF(0, 0, w, h), scene.sceneRect())
    finally:
        painter.end()
    return img


def export_page(
    page: PageModel,
    assets: Mapping[str, AssetModel],
    out_path: Path,
    fmt: str = "PNG",
    quality: int = 95,
) -> None:
    """Export a page to PNG or JPG at native canvas resolution.

    fmt: 'PNG' or 'JPG' (case-insensitive).
    quality: only used for JPG (1-100); ignored for PNG.
    """
    img = render_page_to_image(page, assets)
    fmt_upper = fmt.upper().strip(".")
    if fmt_upper in ("JPG", "JPEG"):
        # JPG has no alpha — blend onto white
        flat = QImage(img.size(), QImage.Format_RGB32)
        flat.fill(Qt.white)
        painter = QPainter(flat)
        try:
            painter.drawImage(0, 0, img)
        finally:
            painter.end()
        flat.save(str(out_path), "JPG", quality)
    else:
        img.save(str(out_path), "PNG")
