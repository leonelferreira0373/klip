import pytest

from klip.document.schema import (
    AssetModel,
    DocumentModel,
    ImageItemModel,
    PageModel,
    ShapeItemModel,
    TextItemModel,
    Transform,
)


def _t():
    return Transform(x=0, y=0, w=100, h=100)


def test_transform_defaults():
    t = Transform(x=10, y=20, w=100, h=50)
    assert t.x == 10
    assert t.y == 20
    assert t.w == 100
    assert t.h == 50
    assert t.rotation == 0.0
    assert t.opacity == 1.0


def test_transform_clamps_opacity():
    with pytest.raises(ValueError):
        Transform(x=0, y=0, w=10, h=10, opacity=1.5)


def test_text_item():
    item = TextItemModel(
        id="t1", transform=_t(), z=1,
        text="hello", font_family="Inter", font_size=24, color="#000000"
    )
    assert item.type == "text"
    assert item.text == "hello"


def test_shape_item_rect():
    item = ShapeItemModel(
        id="s1", transform=_t(), z=1,
        shape="rect", fill="#ff0000", stroke=None, stroke_width=0
    )
    assert item.type == "shape"
    assert item.shape == "rect"


def test_image_item():
    item = ImageItemModel(
        id="i1", transform=_t(), z=1, asset_ref="asset_a"
    )
    assert item.type == "image"
    assert item.asset_ref == "asset_a"


def test_image_item_with_clip_mask():
    item = ImageItemModel(
        id="i1", transform=_t(), z=1, asset_ref="asset_a",
        clip_mask={"shape": "ellipse"}
    )
    assert item.clip_mask["shape"] == "ellipse"


def test_page_with_items():
    page = PageModel(
        id="p1",
        size={"w": 1080, "h": 1080, "unit": "px", "dpi": 144},
        background={"type": "solid", "color": "#ffffff"},
        items=[],
    )
    assert page.size["w"] == 1080


def test_document_with_pages():
    doc = DocumentModel(
        version=1,
        name="test",
        pages=[
            PageModel(
                id="p1",
                size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
                background={"type": "solid", "color": "#fff"},
                items=[],
            )
        ],
    )
    assert doc.version == 1
    assert len(doc.pages) == 1


def test_document_rejects_unknown_version():
    with pytest.raises(ValueError):
        DocumentModel(version=999, name="x", pages=[])
