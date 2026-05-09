"""Image import: file path, QImage (clipboard), DnD URL drop."""
from pathlib import Path

from PIL import Image
from PySide6.QtCore import QMimeData, QUrl
from PySide6.QtGui import QImage

from klip.app import MainWindow
from klip.document.schema import ImageItemModel


def _make_test_png(path: Path, color=(200, 80, 40), size=(120, 80)) -> Path:
    img = Image.new("RGB", size, color)
    img.save(path, format="PNG")
    return path


def test_insert_image_from_path_creates_asset_and_item(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)

    src = _make_test_png(tmp_path / "src.png")
    item = window.insert_image_from_path(src)

    assert isinstance(item, ImageItemModel)
    assert len(window._document.assets) == 1
    asset = window._document.assets[0]
    assert asset.mime == "image/png"
    assert item.asset_ref == asset.id
    page = window._document.current_page()
    assert item in page.items


def test_insert_image_centers_and_fits_inside_page(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)

    src = _make_test_png(tmp_path / "huge.png", size=(4000, 3000))
    item = window.insert_image_from_path(src)
    assert item is not None

    page = window._document.current_page()
    pw, ph = page.size["w"], page.size["h"]
    assert 0 <= item.transform.x <= pw
    assert 0 <= item.transform.y <= ph
    assert item.transform.w <= pw * 0.8 + 0.001
    assert item.transform.h <= ph * 0.8 + 0.001


def test_insert_image_from_qimage_uses_png(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    img = QImage(64, 48, QImage.Format_RGB32)
    img.fill(0xFF112233)

    item = window.insert_image_from_qimage(img)
    assert item is not None
    assert window._document.assets[0].mime == "image/png"


def test_insert_image_is_undoable(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)
    src = _make_test_png(tmp_path / "u.png")
    window.insert_image_from_path(src)

    page = window._document.current_page()
    assert len(page.items) == 1
    window.undo_stack.undo()
    assert len(page.items) == 0


def test_drop_event_with_image_url_inserts(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)
    src = _make_test_png(tmp_path / "drop.png")

    mime = QMimeData()
    mime.setUrls([QUrl.fromLocalFile(str(src))])
    assert window._drop_has_image(mime) is True


def test_drop_event_rejects_non_image_url(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)
    txt = tmp_path / "notes.txt"
    txt.write_text("hello")

    mime = QMimeData()
    mime.setUrls([QUrl.fromLocalFile(str(txt))])
    assert window._drop_has_image(mime) is False


def test_paste_image_from_clipboard_inserts(qtbot):
    from PySide6.QtGui import QGuiApplication

    window = MainWindow()
    qtbot.addWidget(window)
    img = QImage(40, 30, QImage.Format_RGB32)
    img.fill(0xFFAABBCC)
    QGuiApplication.clipboard().setImage(img)

    window._paste_clipboard()
    assert len(window._document.current_page().items) == 1
