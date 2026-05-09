from pathlib import Path

from PIL import Image
from PySide6.QtGui import QImage

from klip.document.schema import PageModel, ShapeItemModel, Transform
from klip.export.exporter import export_page, render_page_to_image


def _page_with_red_rect(w=200, h=200):
    return PageModel(
        id="p1",
        size={"w": w, "h": h, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#ffffff"},
        items=[
            ShapeItemModel(
                id="r1", transform=Transform(x=0, y=0, w=w, h=h),
                z=0, shape="rect", fill="#ff0000",
            ),
        ],
    )


def test_render_page_native_resolution(qtbot):
    page = _page_with_red_rect(400, 300)
    img = render_page_to_image(page, assets={})
    assert isinstance(img, QImage)
    assert img.width() == 400
    assert img.height() == 300


def test_render_page_4k(qtbot):
    page = _page_with_red_rect(3840, 2160)
    img = render_page_to_image(page, assets={})
    assert img.width() == 3840
    assert img.height() == 2160


def test_export_png(qtbot, tmp_path: Path):
    page = _page_with_red_rect(100, 100)
    out = tmp_path / "x.png"
    export_page(page, assets={}, out_path=out, fmt="PNG")
    assert out.exists()
    pil = Image.open(out)
    assert pil.size == (100, 100)
    # center pixel should be red-ish (rect covers whole page)
    px = pil.getpixel((50, 50))
    assert px[0] > 200 and px[1] < 100 and px[2] < 100


def test_export_jpg(qtbot, tmp_path: Path):
    page = _page_with_red_rect(100, 100)
    out = tmp_path / "x.jpg"
    export_page(page, assets={}, out_path=out, fmt="JPG")
    assert out.exists()
    pil = Image.open(out)
    assert pil.size == (100, 100)
