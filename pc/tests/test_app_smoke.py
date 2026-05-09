from PySide6.QtCore import QPointF

from klip.app import MainWindow
from klip.document.io import load_document, save_document
from klip.document.schema import (
    DocumentModel,
    PageModel,
    ShapeItemModel,
    Transform,
)
from klip.toolbar.toolbar import Tool


def test_main_window_opens(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    window.show()
    assert window.windowTitle().startswith("Klip")
    assert window.isVisible()


def test_main_window_has_canvas_and_toolbar(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    assert window.view is not None
    assert window.scene is not None
    assert window.toolbar is not None


def test_main_window_loads_document(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    doc = DocumentModel(
        version=1, name="t",
        pages=[
            PageModel(
                id="p1",
                size={"w": 200, "h": 200, "unit": "px", "dpi": 72},
                background={"type": "solid", "color": "#fff"},
                items=[
                    ShapeItemModel(
                        id="s1", transform=Transform(x=0, y=0, w=50, h=50),
                        z=0, shape="rect", fill="#f00",
                    )
                ],
            )
        ],
    )
    window.load_document_model(doc)
    assert len(window.scene.items()) == 1


def test_clicking_canvas_with_rect_tool_creates_rect(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    window.show()
    window.toolbar.set_active_tool(Tool.RECT)
    before = len(window.scene.items())
    window.scene.handle_canvas_click(QPointF(50, 50))
    after = len(window.scene.items())
    assert after == before + 1


def test_save_load_round_trip(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)
    window.toolbar.set_active_tool(Tool.RECT)
    window.scene.handle_canvas_click(QPointF(100, 100))
    path = tmp_path / "doc.mcv"
    save_document(window._document.to_schema(), path)

    window2 = MainWindow()
    qtbot.addWidget(window2)
    model = load_document(path)
    window2.load_document_model(model)
    # window2.scene now has 1 item (the rect from saved doc)
    assert len(window2.scene.items()) >= 1
