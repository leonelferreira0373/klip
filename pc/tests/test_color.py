from PIL import Image
from PySide6.QtGui import QImage, QColor

from klip.color.extractor import extract_palette
from klip.color.picker import hex_at


def test_extract_palette_returns_k_colors():
    img = Image.new("RGB", (50, 50), (200, 100, 50))
    palette = extract_palette(img, k=5, sample_size=64)
    assert len(palette) == 5
    for c in palette:
        assert c.startswith("#") and len(c) == 7


def test_extract_palette_solid_color():
    """Solid red image should yield a palette dominated by red."""
    img = Image.new("RGB", (50, 50), (255, 0, 0))
    palette = extract_palette(img, k=1, sample_size=64)
    assert palette[0] == "#ff0000"


def test_color_picker_in_bounds(qtbot):
    img = QImage(10, 10, QImage.Format_ARGB32)
    img.fill(QColor("#abcdef"))
    assert hex_at(img, 5, 5) == "#abcdef"


def test_color_picker_out_of_bounds(qtbot):
    img = QImage(10, 10, QImage.Format_ARGB32)
    img.fill(QColor("#000000"))
    assert hex_at(img, -1, 0) is None
    assert hex_at(img, 0, 100) is None
