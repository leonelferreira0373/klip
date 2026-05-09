import pytest

from klip.canvas.handles import HandleOverlay
from klip.canvas.scene import KlipScene
from klip.canvas.view import KlipView
from klip.document.schema import (
    PageModel,
    ShapeItemModel,
    Transform,
)


def test_scene_loads_page_items(qtbot):
    page = PageModel(
        id="p1",
        size={"w": 1000, "h": 500, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#ffffff"},
        items=[
            ShapeItemModel(
                id="s1", transform=Transform(x=10, y=10, w=100, h=100),
                z=1, shape="rect", fill="#ff0000",
            ),
        ],
    )
    scene = KlipScene()
    scene.load_page(page, assets={})
    assert len(scene.items()) == 1


def test_scene_clear_on_reload(qtbot):
    page1 = PageModel(
        id="p1",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[
            ShapeItemModel(
                id="s1", transform=Transform(x=0, y=0, w=10, h=10),
                z=0, shape="rect", fill="#000",
            )
        ],
    )
    page2 = PageModel(
        id="p2",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[],
    )
    scene = KlipScene()
    scene.load_page(page1, assets={})
    assert len(scene.items()) == 1
    scene.load_page(page2, assets={})
    assert len(scene.items()) == 0


def test_view_starts_at_100_percent(qtbot):
    scene = KlipScene()
    view = KlipView(scene)
    qtbot.addWidget(view)
    assert abs(view.zoom_factor - 1.0) < 0.001


def test_view_set_zoom(qtbot):
    scene = KlipScene()
    view = KlipView(scene)
    qtbot.addWidget(view)
    view.set_zoom(2.0)
    assert view.zoom_factor == pytest.approx(2.0)


def test_handles_appear_on_selection(qtbot):
    page = PageModel(
        id="p1",
        size={"w": 500, "h": 500, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[
            ShapeItemModel(
                id="s1", transform=Transform(x=10, y=10, w=100, h=100),
                z=0, shape="rect", fill="#000",
            ),
        ],
    )
    scene = KlipScene()
    scene.load_page(page, assets={})
    overlay = HandleOverlay(scene)
    overlay.attach()
    item = scene.items()[0]
    item.setSelected(True)
    overlay.refresh()
    assert len(overlay.handles()) == 8
