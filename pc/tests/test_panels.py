from klip.document.document import Document
from klip.document.schema import PageModel, ShapeItemModel, Transform
from klip.panels.layers_panel import LayersPanel
from klip.panels.pages_panel import PagesPanel


def _make_page_with_items():
    return PageModel(
        id="p1",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[
            ShapeItemModel(
                id="a", transform=Transform(x=0, y=0, w=10, h=10),
                z=0, shape="rect", fill="#000",
            ),
            ShapeItemModel(
                id="b", transform=Transform(x=20, y=20, w=10, h=10),
                z=1, shape="ellipse", fill="#f00",
            ),
        ],
    )


def test_layers_panel_lists_items(qtbot):
    panel = LayersPanel()
    qtbot.addWidget(panel)
    page = _make_page_with_items()
    panel.set_page(page)
    assert panel._list.count() == 2


def test_layers_panel_emits_selection(qtbot):
    panel = LayersPanel()
    qtbot.addWidget(panel)
    panel.set_page(_make_page_with_items())
    received = []
    panel.layer_selected.connect(received.append)
    panel._list.setCurrentRow(0)
    assert len(received) == 1


def test_pages_panel_thumbnails(qtbot):
    d = Document(name="x")
    d.add_page(_make_page_with_items())
    d.add_page(_make_page_with_items())
    panel = PagesPanel()
    qtbot.addWidget(panel)
    panel.set_document(d)
    assert panel._list.count() == 2


def test_pages_panel_emits_added(qtbot):
    panel = PagesPanel()
    qtbot.addWidget(panel)
    received = []
    panel.page_added.connect(lambda: received.append(True))
    panel._add.click()
    assert received == [True]
