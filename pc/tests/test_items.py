import base64
from io import BytesIO

from PIL import Image
from PySide6.QtWidgets import QGraphicsScene

from klip.document.items.base import ItemAdapter
from klip.document.schema import (
    AssetModel,
    ImageItemModel,
    ShapeItemModel,
    TextItemModel,
    Transform,
)


def _transform():
    return Transform(x=10, y=20, w=100, h=50, rotation=15)


def _png_b64() -> str:
    img = Image.new("RGBA", (10, 10), (255, 0, 0, 255))
    buf = BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def test_apply_transform_to_item(qtbot):
    scene = QGraphicsScene()
    model = ShapeItemModel(
        id="x", transform=_transform(), z=1,
        shape="rect", fill="#ff0000",
    )
    qitem = ItemAdapter.create_qitem(model)
    scene.addItem(qitem)
    assert qitem.pos().x() == 10
    assert qitem.pos().y() == 20
    assert qitem.rotation() == 15
    assert qitem.zValue() == 1


def test_rect_shape_renders_correctly(qtbot):
    model = ShapeItemModel(
        id="r", transform=_transform(), z=0,
        shape="rect", fill="#00ff00", corner_radius=8,
    )
    qitem = ItemAdapter.create_qitem(model)
    bounds = qitem.boundingRect()
    assert bounds.width() == 100
    assert bounds.height() == 50


def test_ellipse_shape(qtbot):
    model = ShapeItemModel(
        id="e", transform=_transform(), z=0,
        shape="ellipse", fill="#0000ff",
    )
    qitem = ItemAdapter.create_qitem(model)
    bounds = qitem.boundingRect()
    assert bounds.width() == 100


def test_text_item_renders(qtbot):
    model = TextItemModel(
        id="t", transform=_transform(), z=0,
        text="HELLO", font_family="Segoe UI",
        font_size=24, color="#000000",
    )
    qitem = ItemAdapter.create_qitem(model)
    bounds = qitem.boundingRect()
    assert bounds.width() > 0


def test_image_item_renders(qtbot):
    asset = AssetModel(id="a1", mime="image/png", data=_png_b64())
    model = ImageItemModel(id="i", transform=_transform(), z=0, asset_ref="a1")
    qitem = ItemAdapter.create_qitem_with_assets(model, {asset.id: asset})
    bounds = qitem.boundingRect()
    assert bounds.width() == 100
