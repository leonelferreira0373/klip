"""Toolbar wiring: each button does the right thing."""
from pathlib import Path
from unittest.mock import patch

from PIL import Image
from PySide6.QtCore import QPointF
from PySide6.QtGui import QImage

from klip.app import MainWindow
from klip.toolbar.toolbar import Tool


def _make_test_png(path: Path, color=(150, 60, 30), size=(120, 90)) -> Path:
    Image.new("RGB", size, color).save(path, format="PNG")
    return path


def test_select_tool_initializes_at_startup(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    assert window.scene._active_tool == "select"


def test_shape_tool_shows_persistent_hint(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    window.toolbar.set_active_tool(Tool.RECT)
    msg = window.statusBar().currentMessage()
    assert "rectangle" in msg.lower()
    assert "click" in msg.lower()


def test_image_button_opens_file_dialog_and_reverts(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)
    src = _make_test_png(tmp_path / "i.png")

    with patch(
        "klip.app.QFileDialog.getOpenFileName",
        return_value=(str(src), "Images (*.png)"),
    ):
        window.toolbar.set_active_tool(Tool.IMAGE)

    assert window.toolbar.active_tool == Tool.SELECT
    assert len(window._document.current_page().items) == 1


def test_font_button_opens_file_dialog_and_reverts(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)

    with patch(
        "klip.app.QFileDialog.getOpenFileName", return_value=("", "")
    ):
        window.toolbar.set_active_tool(Tool.FONT)

    assert window.toolbar.active_tool == Tool.SELECT


def test_bg_remove_with_no_selection_shows_info_and_reverts(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)

    with patch("klip.app.QMessageBox.information") as mock_info:
        window.toolbar.set_active_tool(Tool.BG_REMOVE)

    assert mock_info.called
    assert window.toolbar.active_tool == Tool.SELECT


def test_palette_with_no_selection_shows_info_and_reverts(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)

    with patch("klip.app.QMessageBox.information") as mock_info:
        window.toolbar.set_active_tool(Tool.EXTRACT)

    assert mock_info.called
    assert window.toolbar.active_tool == Tool.SELECT


def test_clip_shows_coming_soon_and_reverts(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    window.toolbar.set_active_tool(Tool.CLIP)
    assert window.toolbar.active_tool == Tool.SELECT
    assert "coming" in window.statusBar().currentMessage().lower()


def test_eyedrop_armed_then_canvas_click_samples_and_reverts(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    window.show()
    window.toolbar.set_active_tool(Tool.PICK)
    assert window.scene._active_tool == "pick"

    window.scene.handle_canvas_click(QPointF(10, 10))
    assert window.toolbar.active_tool == Tool.SELECT


def test_escape_reverts_to_select(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    window.show()
    qtbot.waitExposed(window)
    window.toolbar.set_active_tool(Tool.RECT)
    assert window.toolbar.active_tool == Tool.RECT
    # Trigger Esc shortcut directly — QTest can't reliably dispatch into
    # an application-context shortcut from inside pytest-qt.
    for child in window.findChildren(type(window).__mro__[0]):
        pass
    from PySide6.QtGui import QShortcut
    shortcuts = [s for s in window.findChildren(QShortcut)
                 if s.key().toString() == "Esc"]
    assert shortcuts, "Esc shortcut not registered"
    shortcuts[0].activated.emit()
    assert window.toolbar.active_tool == Tool.SELECT


def test_palette_with_image_selected_opens_dialog(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)
    src = _make_test_png(tmp_path / "p.png")

    with patch(
        "klip.app.QFileDialog.getOpenFileName",
        return_value=(str(src), "Images (*.png)"),
    ):
        window.insert_image_from_path(src)

    for qitem in window.scene.items():
        if qitem.data(0):
            window.scene.clearSelection()
            qitem.setSelected(True)
            break

    with patch.object(window, "_show_palette_dialog") as mock_show:
        window.toolbar.set_active_tool(Tool.EXTRACT)

    assert mock_show.called
    palette = mock_show.call_args.args[0]
    assert len(palette) == 5
    assert all(c.startswith("#") and len(c) == 7 for c in palette)
    assert window.toolbar.active_tool == Tool.SELECT


def test_scene_sample_color_at_returns_hex(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    color = window.scene.sample_color_at(QPointF(20, 20))
    assert color is not None
    assert color.startswith("#") and len(color) == 7
