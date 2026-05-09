"""KLIP — minimal vector-style canvas tool.
Features: infinite canvas, images, text, shapes, layering, color picker,
palette extractor, PowerClip (image-in-shape), BG removal (offline).
"""
import os
import sys
import json
import base64
import traceback
from io import BytesIO
from pathlib import Path

# Silence stdout/stderr when running as --windowed PyInstaller exe
if sys.stdout is None:
    sys.stdout = open(os.devnull, "w", encoding="utf-8")
if sys.stderr is None:
    sys.stderr = open(os.devnull, "w", encoding="utf-8")

from PyQt6.QtWidgets import (
    QApplication, QMainWindow, QGraphicsScene, QGraphicsView,
    QGraphicsItem, QGraphicsPixmapItem, QGraphicsTextItem,
    QGraphicsRectItem, QGraphicsEllipseItem,
    QFileDialog, QColorDialog, QFontDialog, QMessageBox,
    QToolBar, QDockWidget, QWidget, QVBoxLayout, QHBoxLayout,
    QPushButton, QLabel, QListWidget, QListWidgetItem, QMenu,
    QInputDialog, QSizePolicy, QFrame, QGridLayout, QToolButton,
)
from PyQt6.QtGui import (
    QAction, QPixmap, QImage, QPainter, QPainterPath, QColor,
    QPen, QBrush, QFont, QTransform, QKeySequence, QShortcut,
    QPolygonF, QFontMetrics,
)
from PyQt6.QtCore import (
    Qt, QPointF, QRectF, QSizeF, QThread, pyqtSignal, QSize, QBuffer,
    QIODevice, QTimer,
)

from PIL import Image, ImageQt


# ─────────────────────────────────────────────────────────────
# Helpers
# ─────────────────────────────────────────────────────────────
def qpixmap_from_pil(img: Image.Image) -> QPixmap:
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    qim = ImageQt.ImageQt(img)
    return QPixmap.fromImage(QImage(qim))


def pil_from_qpixmap(pix: QPixmap) -> Image.Image:
    qim = pix.toImage().convertToFormat(QImage.Format.Format_RGBA8888)
    w, h = qim.width(), qim.height()
    ptr = qim.constBits()
    ptr.setsize(qim.sizeInBytes())
    buf = bytes(ptr)
    return Image.frombuffer("RGBA", (w, h), buf, "raw", "RGBA", 0, 1)


def pixmap_to_b64(pix: QPixmap) -> str:
    buf = QBuffer()
    buf.open(QIODevice.OpenModeFlag.WriteOnly)
    pix.save(buf, "PNG")
    return base64.b64encode(bytes(buf.data())).decode("ascii")


def b64_to_pixmap(b64: str) -> QPixmap:
    raw = base64.b64decode(b64)
    pix = QPixmap()
    pix.loadFromData(raw, "PNG")
    return pix


# ─────────────────────────────────────────────────────────────
# Custom items
# ─────────────────────────────────────────────────────────────
class KlipPixmapItem(QGraphicsPixmapItem):
    """Image item, supports optional clip-path mask (PowerClip)."""
    def __init__(self, pixmap: QPixmap):
        super().__init__(pixmap)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemIsMovable, True)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemIsSelectable, True)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemSendsGeometryChanges, True)
        self.setTransformationMode(Qt.TransformationMode.SmoothTransformation)
        self.clip_path: QPainterPath | None = None  # in item coordinates
        self._tag = "image"
        self._title = "Image"

    def paint(self, painter: QPainter, option, widget=None):
        if self.clip_path is not None:
            painter.save()
            painter.setClipPath(self.clip_path)
            super().paint(painter, option, widget)
            painter.restore()
        else:
            super().paint(painter, option, widget)


class KlipTextItem(QGraphicsTextItem):
    def __init__(self, text="Text"):
        super().__init__(text)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemIsMovable, True)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemIsSelectable, True)
        self.setTextInteractionFlags(Qt.TextInteractionFlag.NoTextInteraction)
        f = QFont("Segoe UI", 32)
        self.setFont(f)
        self.setDefaultTextColor(QColor("#111111"))
        self._tag = "text"
        self._title = "Text"

    def mouseDoubleClickEvent(self, event):
        self.setTextInteractionFlags(Qt.TextInteractionFlag.TextEditorInteraction)
        super().mouseDoubleClickEvent(event)
        self.setFocus()

    def focusOutEvent(self, event):
        self.setTextInteractionFlags(Qt.TextInteractionFlag.NoTextInteraction)
        super().focusOutEvent(event)


class KlipRectItem(QGraphicsRectItem):
    def __init__(self, w=200.0, h=120.0):
        super().__init__(0, 0, w, h)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemIsMovable, True)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemIsSelectable, True)
        self.setBrush(QBrush(QColor(120, 180, 255, 180)))
        self.setPen(QPen(QColor(40, 40, 40), 2))
        self._tag = "rect"
        self._title = "Rectangle"


class KlipEllipseItem(QGraphicsEllipseItem):
    def __init__(self, w=200.0, h=200.0):
        super().__init__(0, 0, w, h)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemIsMovable, True)
        self.setFlag(QGraphicsItem.GraphicsItemFlag.ItemIsSelectable, True)
        self.setBrush(QBrush(QColor(255, 180, 120, 180)))
        self.setPen(QPen(QColor(40, 40, 40), 2))
        self._tag = "ellipse"
        self._title = "Ellipse"


# ─────────────────────────────────────────────────────────────
# Canvas (View + Scene)
# ─────────────────────────────────────────────────────────────
class KlipScene(QGraphicsScene):
    def __init__(self):
        super().__init__()
        self.setSceneRect(-50_000, -50_000, 100_000, 100_000)
        self.setBackgroundBrush(QBrush(QColor("#fafafa")))


class KlipView(QGraphicsView):
    """Infinite canvas with pan/zoom, drag-drop, paste."""
    image_dropped = pyqtSignal(str)  # local path

    def __init__(self, scene: KlipScene):
        super().__init__(scene)
        self.setRenderHint(QPainter.RenderHint.Antialiasing)
        self.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        self.setRenderHint(QPainter.RenderHint.TextAntialiasing)
        self.setDragMode(QGraphicsView.DragMode.RubberBandDrag)
        self.setTransformationAnchor(QGraphicsView.ViewportAnchor.AnchorUnderMouse)
        self.setResizeAnchor(QGraphicsView.ViewportAnchor.AnchorUnderMouse)
        self.setAcceptDrops(True)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setBackgroundBrush(QBrush(QColor("#fafafa")))
        self._panning = False
        self._pan_start = QPointF()

    def wheelEvent(self, event):
        if event.modifiers() & Qt.KeyboardModifier.ControlModifier or True:
            factor = 1.15 if event.angleDelta().y() > 0 else 1 / 1.15
            self.scale(factor, factor)
        else:
            super().wheelEvent(event)

    def mousePressEvent(self, event):
        if event.button() == Qt.MouseButton.MiddleButton or (
            event.button() == Qt.MouseButton.LeftButton
            and event.modifiers() & Qt.KeyboardModifier.ShiftModifier
        ):
            self._panning = True
            self._pan_start = event.position()
            self.setCursor(Qt.CursorShape.ClosedHandCursor)
            event.accept()
            return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event):
        if self._panning:
            delta = event.position() - self._pan_start
            self._pan_start = event.position()
            self.horizontalScrollBar().setValue(self.horizontalScrollBar().value() - int(delta.x()))
            self.verticalScrollBar().setValue(self.verticalScrollBar().value() - int(delta.y()))
            event.accept()
            return
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event):
        if self._panning:
            self._panning = False
            self.setCursor(Qt.CursorShape.ArrowCursor)
            event.accept()
            return
        super().mouseReleaseEvent(event)

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls() or event.mimeData().hasImage():
            event.acceptProposedAction()

    def dragMoveEvent(self, event):
        if event.mimeData().hasUrls() or event.mimeData().hasImage():
            event.acceptProposedAction()

    def dropEvent(self, event):
        md = event.mimeData()
        if md.hasUrls():
            for url in md.urls():
                p = url.toLocalFile()
                if p:
                    self.image_dropped.emit(p)
            event.acceptProposedAction()


# ─────────────────────────────────────────────────────────────
# Background-removal worker
# ─────────────────────────────────────────────────────────────
class BgRemoveWorker(QThread):
    finished_pix = pyqtSignal(object, QPixmap)  # (item, new_pixmap)
    failed = pyqtSignal(object, str)

    _session_cache = {}

    def __init__(self, item: KlipPixmapItem, model_key: str):
        super().__init__()
        self.item = item
        self.model_key = model_key

    def run(self):
        try:
            from rembg import remove, new_session
            sess = BgRemoveWorker._session_cache.get(self.model_key)
            if sess is None:
                sess = new_session(self.model_key)
                BgRemoveWorker._session_cache[self.model_key] = sess
            pil = pil_from_qpixmap(self.item.pixmap())
            buf = BytesIO()
            pil.save(buf, "PNG")
            cut = remove(buf.getvalue(), session=sess)
            new = Image.open(BytesIO(cut)).convert("RGBA")
            self.finished_pix.emit(self.item, qpixmap_from_pil(new))
        except Exception as e:
            self.failed.emit(self.item, f"{e}\n\n{traceback.format_exc()}")


# ─────────────────────────────────────────────────────────────
# Layers / right panel
# ─────────────────────────────────────────────────────────────
class LayersPanel(QWidget):
    def __init__(self, main):
        super().__init__()
        self.main = main
        v = QVBoxLayout(self)
        v.setContentsMargins(8, 8, 8, 8)
        v.addWidget(QLabel("<b>Layers</b> (top = front)"))
        self.list = QListWidget()
        self.list.itemSelectionChanged.connect(self.sync_selection)
        v.addWidget(self.list, 1)

        row = QHBoxLayout()
        for txt, fn in (("⏶⏶", main.layer_to_front),
                        ("⏶", main.layer_forward),
                        ("⏷", main.layer_backward),
                        ("⏷⏷", main.layer_to_back)):
            b = QPushButton(txt); b.setFixedWidth(40); b.clicked.connect(fn); row.addWidget(b)
        v.addLayout(row)

    def refresh(self, items):
        self.list.blockSignals(True)
        self.list.clear()
        for it in items:
            li = QListWidgetItem(getattr(it, "_title", "Item"))
            li.setData(Qt.ItemDataRole.UserRole, it)
            self.list.addItem(li)
        self.list.blockSignals(False)

    def sync_selection(self):
        scene = self.main.scene
        scene.blockSignals(True)
        scene.clearSelection()
        for li in self.list.selectedItems():
            it = li.data(Qt.ItemDataRole.UserRole)
            if it is not None:
                it.setSelected(True)
        scene.blockSignals(False)


class PalettePanel(QWidget):
    def __init__(self, main):
        super().__init__()
        self.main = main
        v = QVBoxLayout(self); v.setContentsMargins(8, 8, 8, 8)
        v.addWidget(QLabel("<b>Color</b>"))

        row = QHBoxLayout()
        self.swatch = QLabel(); self.swatch.setFixedSize(40, 40)
        self.swatch.setStyleSheet("background:#111111; border:1px solid #999;")
        row.addWidget(self.swatch)
        b = QPushButton("Pick...")
        b.clicked.connect(self.pick_color)
        row.addWidget(b)
        row.addStretch(1)
        v.addLayout(row)

        v.addSpacing(12)
        v.addWidget(QLabel("<b>Palette from image</b>"))
        self.extract_btn = QPushButton("Extract from selected image")
        self.extract_btn.clicked.connect(self.extract_palette)
        v.addWidget(self.extract_btn)

        self.palette_grid = QGridLayout()
        v.addLayout(self.palette_grid)
        v.addStretch(1)

        self._color = QColor("#111111")

    @property
    def color(self) -> QColor:
        return QColor(self._color)

    def set_color(self, c: QColor):
        self._color = QColor(c)
        self.swatch.setStyleSheet(f"background:{c.name()}; border:1px solid #999;")

    def pick_color(self):
        c = QColorDialog.getColor(self._color, self.main, "Pick a color")
        if c.isValid():
            self.set_color(c)
            self.main.apply_color_to_selection(c)

    def extract_palette(self):
        sel = [it for it in self.main.scene.selectedItems() if isinstance(it, KlipPixmapItem)]
        if not sel:
            QMessageBox.information(self.main, "Extract palette", "Select an image first.")
            return
        pil = pil_from_qpixmap(sel[0].pixmap()).convert("RGB")
        # Quick palette via Pillow's quantize (median-cut)
        small = pil.copy()
        small.thumbnail((256, 256))
        q = small.quantize(colors=8, method=Image.Quantize.MEDIANCUT)
        pal = q.getpalette()[: 8 * 3]
        colors = [QColor(pal[i], pal[i + 1], pal[i + 2]) for i in range(0, 24, 3)]
        # Clear grid
        while self.palette_grid.count():
            item = self.palette_grid.takeAt(0)
            w = item.widget()
            if w: w.deleteLater()
        for i, c in enumerate(colors):
            sw = QPushButton()
            sw.setFixedSize(48, 32)
            sw.setStyleSheet(f"background:{c.name()}; border:1px solid #888;")
            sw.setToolTip(c.name().upper())
            sw.clicked.connect(lambda _=False, col=c: (self.set_color(col), self.main.apply_color_to_selection(col)))
            self.palette_grid.addWidget(sw, i // 4, i % 4)


# ─────────────────────────────────────────────────────────────
# Main window
# ─────────────────────────────────────────────────────────────
class KlipMainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("KLIP")
        self.resize(1280, 820)
        self.scene = KlipScene()
        self.view = KlipView(self.scene)
        self.setCentralWidget(self.view)
        self.view.image_dropped.connect(self.add_image_from_path)
        self.scene.selectionChanged.connect(self.on_selection_changed)

        self._project_path: Path | None = None
        self._workers: list[BgRemoveWorker] = []

        self._build_menu()
        self._build_toolbar()
        self._build_docks()
        self._build_shortcuts()
        self.statusBar().showMessage("Ready — drag-drop images, double-click text to edit, scroll to zoom, Shift+drag or middle-mouse to pan")

    # Menus / toolbars / docks ────────────────────────────────
    def _build_menu(self):
        mb = self.menuBar()
        # File
        m = mb.addMenu("&File")
        m.addAction(QAction("New", self, shortcut="Ctrl+N", triggered=self.new_project))
        m.addAction(QAction("Open...", self, shortcut="Ctrl+O", triggered=self.open_project))
        m.addAction(QAction("Save", self, shortcut="Ctrl+S", triggered=self.save_project))
        m.addAction(QAction("Save As...", self, shortcut="Ctrl+Shift+S", triggered=self.save_project_as))
        m.addSeparator()
        m.addAction(QAction("Import Image...", self, shortcut="Ctrl+I", triggered=self.import_image_dialog))
        m.addAction(QAction("Export PNG...", self, shortcut="Ctrl+E", triggered=self.export_png))
        m.addSeparator()
        m.addAction(QAction("Exit", self, shortcut="Ctrl+Q", triggered=self.close))

        m = mb.addMenu("&Edit")
        m.addAction(QAction("Delete", self, shortcut="Delete", triggered=self.delete_selection))
        m.addAction(QAction("Paste image", self, shortcut="Ctrl+V", triggered=self.paste_clipboard))

        m = mb.addMenu("&View")
        m.addAction(QAction("Zoom in", self, shortcut="Ctrl++", triggered=lambda: self.view.scale(1.2, 1.2)))
        m.addAction(QAction("Zoom out", self, shortcut="Ctrl+-", triggered=lambda: self.view.scale(1/1.2, 1/1.2)))
        m.addAction(QAction("Reset zoom", self, shortcut="Ctrl+0", triggered=lambda: self.view.resetTransform()))
        m.addAction(QAction("Fit to selection", self, shortcut="Ctrl+F", triggered=self.fit_to_selection))

        m = mb.addMenu("&Layer")
        m.addAction(QAction("Bring to front", self, shortcut="Ctrl+Shift+]", triggered=self.layer_to_front))
        m.addAction(QAction("Bring forward", self, shortcut="Ctrl+]", triggered=self.layer_forward))
        m.addAction(QAction("Send backward", self, shortcut="Ctrl+[", triggered=self.layer_backward))
        m.addAction(QAction("Send to back", self, shortcut="Ctrl+Shift+[", triggered=self.layer_to_back))

        m = mb.addMenu("&Object")
        m.addAction(QAction("Add Text", self, shortcut="T", triggered=self.add_text))
        m.addAction(QAction("Add Rectangle", self, shortcut="R", triggered=self.add_rect))
        m.addAction(QAction("Add Ellipse", self, shortcut="O", triggered=self.add_ellipse))
        m.addSeparator()
        m.addAction(QAction("Remove Background (u2net)", self, triggered=lambda: self.remove_bg("u2net")))
        m.addAction(QAction("Remove Background (BiRefNet)", self, triggered=lambda: self.remove_bg("birefnet-general")))
        m.addSeparator()
        m.addAction(QAction("PowerClip — image inside selected shape", self, shortcut="P", triggered=self.power_clip))
        m.addAction(QAction("Release PowerClip", self, triggered=self.release_clip))
        m.addSeparator()
        m.addAction(QAction("Edit text font...", self, triggered=self.edit_font))

    def _build_toolbar(self):
        tb = QToolBar("Tools")
        tb.setIconSize(QSize(24, 24))
        self.addToolBar(Qt.ToolBarArea.LeftToolBarArea, tb)
        for label, fn, tip in (
            ("➕ Image", self.import_image_dialog, "Import image (Ctrl+I)"),
            ("Aa Text", self.add_text, "Add text (T)"),
            ("▭ Rect", self.add_rect, "Add rectangle (R)"),
            ("◯ Ellipse", self.add_ellipse, "Add ellipse (O)"),
            ("✂ Remove BG", lambda: self.remove_bg("u2net"), "Remove background (u2net, fast)"),
            ("◈ BiRefNet", lambda: self.remove_bg("birefnet-general"), "Remove background (BiRefNet, best)"),
            ("⊠ Clip", self.power_clip, "PowerClip image into shape (P)"),
            ("🎨 Color", self.pick_color, "Pick color"),
        ):
            btn = QToolButton()
            btn.setText(label)
            btn.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextOnly)
            btn.setToolTip(tip)
            btn.clicked.connect(fn)
            tb.addWidget(btn)

    def _build_docks(self):
        self.layers = LayersPanel(self)
        d = QDockWidget("Layers", self); d.setWidget(self.layers)
        self.addDockWidget(Qt.DockWidgetArea.RightDockWidgetArea, d)
        self.palette = PalettePanel(self)
        d2 = QDockWidget("Color & Palette", self); d2.setWidget(self.palette)
        self.addDockWidget(Qt.DockWidgetArea.RightDockWidgetArea, d2)

    def _build_shortcuts(self):
        QShortcut(QKeySequence("Ctrl+="), self, lambda: self.view.scale(1.2, 1.2))

    # Events ───────────────────────────────────────────────────
    def on_selection_changed(self):
        items = sorted(self.scene.items(), key=lambda x: -x.zValue())
        self.layers.refresh(items)
        # Highlight selected in panel
        sel = set(self.scene.selectedItems())
        self.layers.list.blockSignals(True)
        for i in range(self.layers.list.count()):
            li = self.layers.list.item(i)
            if li.data(Qt.ItemDataRole.UserRole) in sel:
                li.setSelected(True)
        self.layers.list.blockSignals(False)

    def keyPressEvent(self, event):
        if event.matches(QKeySequence.StandardKey.Delete) or event.key() == Qt.Key.Key_Delete:
            self.delete_selection(); return
        super().keyPressEvent(event)

    # Object creation ──────────────────────────────────────────
    def _add_at_view_center(self, item):
        self.scene.addItem(item)
        center = self.view.mapToScene(self.view.viewport().rect().center())
        item.setPos(center - QPointF(item.boundingRect().width() / 2, item.boundingRect().height() / 2))
        item.setSelected(True)
        self.on_selection_changed()
        return item

    def add_text(self):
        t = KlipTextItem("Text")
        t.setDefaultTextColor(self.palette.color)
        return self._add_at_view_center(t)

    def add_rect(self):
        return self._add_at_view_center(KlipRectItem())

    def add_ellipse(self):
        return self._add_at_view_center(KlipEllipseItem())

    def add_image_from_path(self, path: str):
        try:
            pil = Image.open(path)
            pix = qpixmap_from_pil(pil)
        except Exception as e:
            QMessageBox.critical(self, "Cannot open image", f"{path}\n\n{e}")
            return
        item = KlipPixmapItem(pix)
        item._title = os.path.basename(path)
        self._add_at_view_center(item)

    def import_image_dialog(self):
        files, _ = QFileDialog.getOpenFileNames(
            self, "Import images", "", "Images (*.png *.jpg *.jpeg *.webp *.bmp *.gif *.tiff *.tif)"
        )
        for f in files:
            self.add_image_from_path(f)

    def paste_clipboard(self):
        clip = QApplication.clipboard()
        md = clip.mimeData()
        if md.hasImage():
            pix = clip.pixmap()
            if not pix.isNull():
                item = KlipPixmapItem(pix)
                item._title = "Pasted Image"
                self._add_at_view_center(item)
                return
        if md.hasUrls():
            for url in md.urls():
                if url.isLocalFile():
                    self.add_image_from_path(url.toLocalFile())

    # Color ─────────────────────────────────────────────────────
    def pick_color(self):
        self.palette.pick_color()

    def apply_color_to_selection(self, c: QColor):
        for it in self.scene.selectedItems():
            if isinstance(it, KlipTextItem):
                it.setDefaultTextColor(c)
            elif isinstance(it, (KlipRectItem, KlipEllipseItem)):
                it.setBrush(QBrush(c))

    # Layering ──────────────────────────────────────────────────
    def _selected_or_msg(self):
        sel = self.scene.selectedItems()
        if not sel:
            self.statusBar().showMessage("Nothing selected", 2000)
        return sel

    def layer_forward(self):
        for it in self._selected_or_msg():
            it.setZValue(it.zValue() + 1)
        self.on_selection_changed()

    def layer_backward(self):
        for it in self._selected_or_msg():
            it.setZValue(it.zValue() - 1)
        self.on_selection_changed()

    def layer_to_front(self):
        items = self.scene.items()
        if not items: return
        top = max(i.zValue() for i in items)
        for it in self._selected_or_msg():
            it.setZValue(top + 1)
        self.on_selection_changed()

    def layer_to_back(self):
        items = self.scene.items()
        if not items: return
        bot = min(i.zValue() for i in items)
        for it in self._selected_or_msg():
            it.setZValue(bot - 1)
        self.on_selection_changed()

    # Selection ops ─────────────────────────────────────────────
    def delete_selection(self):
        for it in list(self.scene.selectedItems()):
            self.scene.removeItem(it)
        self.on_selection_changed()

    def fit_to_selection(self):
        sel = self.scene.selectedItems()
        if not sel:
            return
        rect = sel[0].sceneBoundingRect()
        for it in sel[1:]:
            rect = rect.united(it.sceneBoundingRect())
        self.view.fitInView(rect.adjusted(-40, -40, 40, 40), Qt.AspectRatioMode.KeepAspectRatio)

    def edit_font(self):
        sel = [it for it in self.scene.selectedItems() if isinstance(it, KlipTextItem)]
        if not sel: return
        f, ok = QFontDialog.getFont(sel[0].font(), self, "Choose font")
        if ok:
            for it in sel:
                it.setFont(f)

    # Background removal ────────────────────────────────────────
    def remove_bg(self, model_key: str):
        sel = [it for it in self.scene.selectedItems() if isinstance(it, KlipPixmapItem)]
        if not sel:
            QMessageBox.information(self, "Remove background", "Select an image first.")
            return
        for it in sel:
            self.statusBar().showMessage(f"Removing background ({model_key})...")
            w = BgRemoveWorker(it, model_key)
            w.finished_pix.connect(self._bg_done)
            w.failed.connect(self._bg_failed)
            self._workers.append(w)
            w.start()

    def _bg_done(self, item, new_pix: QPixmap):
        item.setPixmap(new_pix)
        self.statusBar().showMessage("Background removed", 3000)

    def _bg_failed(self, item, msg: str):
        QMessageBox.critical(self, "BG removal failed", msg)
        self.statusBar().showMessage("BG removal failed", 5000)

    # PowerClip ─────────────────────────────────────────────────
    def power_clip(self):
        sel = self.scene.selectedItems()
        imgs = [it for it in sel if isinstance(it, KlipPixmapItem)]
        shapes = [it for it in sel if isinstance(it, (KlipRectItem, KlipEllipseItem))]
        if not imgs or not shapes:
            QMessageBox.information(self, "PowerClip", "Select one image and one shape (rect or ellipse).")
            return
        img = imgs[0]; shape = shapes[0]
        path = QPainterPath()
        # shape bounds in scene coords, then mapped to image local coords
        if isinstance(shape, KlipRectItem):
            rect = shape.mapRectToScene(shape.rect())
            local = img.mapRectFromScene(rect)
            path.addRect(local)
        else:
            rect = shape.mapRectToScene(shape.rect())
            local = img.mapRectFromScene(rect)
            path.addEllipse(local)
        img.clip_path = path
        img.update()
        # Hide the shape (it's been "consumed" by the clip)
        shape.setVisible(False)
        # Remember pairing for release
        if not hasattr(self, "_clips"):
            self._clips = {}
        self._clips[id(img)] = shape
        self.statusBar().showMessage("PowerClip applied", 3000)

    def release_clip(self):
        for it in self.scene.selectedItems():
            if isinstance(it, KlipPixmapItem) and it.clip_path is not None:
                it.clip_path = None
                it.update()
                if hasattr(self, "_clips") and id(it) in self._clips:
                    self._clips[id(it)].setVisible(True)
                    del self._clips[id(it)]
        self.statusBar().showMessage("Clip released", 2000)

    # Save / Load ───────────────────────────────────────────────
    def serialize(self) -> dict:
        items = []
        for it in self.scene.items():
            t = self.get_transform_data(it)
            data = {"z": it.zValue(), "x": it.x(), "y": it.y(), **t}
            if isinstance(it, KlipPixmapItem):
                data["type"] = "image"
                data["pixmap_b64"] = pixmap_to_b64(it.pixmap())
                data["title"] = it._title
                if it.clip_path is not None:
                    # Encode clip-path as polygon points
                    poly = it.clip_path.toFillPolygon()
                    data["clip_points"] = [(p.x(), p.y()) for p in [poly.at(i) for i in range(poly.count())]]
            elif isinstance(it, KlipTextItem):
                data["type"] = "text"
                data["text"] = it.toPlainText()
                data["font"] = it.font().toString()
                data["color"] = it.defaultTextColor().name(QColor.NameFormat.HexArgb)
            elif isinstance(it, KlipRectItem):
                data["type"] = "rect"
                r = it.rect()
                data["w"], data["h"] = r.width(), r.height()
                data["fill"] = it.brush().color().name(QColor.NameFormat.HexArgb)
                data["stroke"] = it.pen().color().name(QColor.NameFormat.HexArgb)
            elif isinstance(it, KlipEllipseItem):
                data["type"] = "ellipse"
                r = it.rect()
                data["w"], data["h"] = r.width(), r.height()
                data["fill"] = it.brush().color().name(QColor.NameFormat.HexArgb)
                data["stroke"] = it.pen().color().name(QColor.NameFormat.HexArgb)
            else:
                continue
            items.append(data)
        return {"version": 1, "items": items}

    def get_transform_data(self, it):
        return {
            "rotation": it.rotation(),
            "scale": it.scale(),
        }

    def deserialize(self, data: dict):
        # Clear scene
        for it in list(self.scene.items()):
            self.scene.removeItem(it)
        for d in data.get("items", []):
            t = d.get("type")
            it = None
            if t == "image":
                it = KlipPixmapItem(b64_to_pixmap(d["pixmap_b64"]))
                it._title = d.get("title", "Image")
                if d.get("clip_points"):
                    p = QPainterPath()
                    pts = [QPointF(x, y) for x, y in d["clip_points"]]
                    if pts:
                        p.addPolygon(QPolygonF(pts))
                    it.clip_path = p
            elif t == "text":
                it = KlipTextItem(d.get("text", ""))
                f = QFont(); f.fromString(d.get("font", "Segoe UI,32,-1,5,50,0,0,0,0,0"))
                it.setFont(f)
                it.setDefaultTextColor(QColor(d.get("color", "#111111")))
            elif t == "rect":
                it = KlipRectItem(d.get("w", 200), d.get("h", 120))
                it.setBrush(QBrush(QColor(d.get("fill", "#7AB4FF"))))
                it.setPen(QPen(QColor(d.get("stroke", "#222222")), 2))
            elif t == "ellipse":
                it = KlipEllipseItem(d.get("w", 200), d.get("h", 200))
                it.setBrush(QBrush(QColor(d.get("fill", "#FFB47A"))))
                it.setPen(QPen(QColor(d.get("stroke", "#222222")), 2))
            if it is None:
                continue
            it.setPos(d.get("x", 0), d.get("y", 0))
            it.setZValue(d.get("z", 0))
            it.setRotation(d.get("rotation", 0))
            it.setScale(d.get("scale", 1.0))
            self.scene.addItem(it)
        self.on_selection_changed()

    def new_project(self):
        for it in list(self.scene.items()):
            self.scene.removeItem(it)
        self._project_path = None
        self.setWindowTitle("KLIP")
        self.on_selection_changed()

    def open_project(self):
        f, _ = QFileDialog.getOpenFileName(self, "Open project", "", "KLIP project (*.klip *.json)")
        if not f: return
        try:
            with open(f, "r", encoding="utf-8") as fh:
                data = json.load(fh)
            self.deserialize(data)
            self._project_path = Path(f)
            self.setWindowTitle(f"KLIP — {self._project_path.name}")
        except Exception as e:
            QMessageBox.critical(self, "Open failed", str(e))

    def save_project(self):
        if self._project_path is None:
            return self.save_project_as()
        self._do_save(self._project_path)

    def save_project_as(self):
        f, _ = QFileDialog.getSaveFileName(self, "Save project", "untitled.klip", "KLIP project (*.klip)")
        if not f: return
        if not f.lower().endswith((".klip", ".json")):
            f += ".klip"
        self._project_path = Path(f)
        self._do_save(self._project_path)
        self.setWindowTitle(f"KLIP — {self._project_path.name}")

    def _do_save(self, path: Path):
        try:
            data = self.serialize()
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(data, fh)
            self.statusBar().showMessage(f"Saved to {path}", 3000)
        except Exception as e:
            QMessageBox.critical(self, "Save failed", str(e))

    def export_png(self):
        sel = self.scene.selectedItems()
        if sel:
            rect = sel[0].sceneBoundingRect()
            for it in sel[1:]:
                rect = rect.united(it.sceneBoundingRect())
        else:
            items = [it for it in self.scene.items() if it.isVisible()]
            if not items:
                QMessageBox.information(self, "Export PNG", "Nothing to export.")
                return
            rect = items[0].sceneBoundingRect()
            for it in items[1:]:
                rect = rect.united(it.sceneBoundingRect())
        rect = rect.adjusted(-20, -20, 20, 20)

        f, _ = QFileDialog.getSaveFileName(self, "Export PNG", "klip_export.png", "PNG image (*.png)")
        if not f: return
        if not f.lower().endswith(".png"):
            f += ".png"

        # Render at 2x for sharpness
        scale = 2.0
        img = QImage(int(rect.width() * scale), int(rect.height() * scale), QImage.Format.Format_ARGB32)
        img.fill(Qt.GlobalColor.transparent)
        p = QPainter(img)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        p.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        self.scene.render(p, target=QRectF(0, 0, img.width(), img.height()), source=rect)
        p.end()
        img.save(f, "PNG")
        self.statusBar().showMessage(f"Exported {f}", 3000)


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("KLIP")
    app.setStyle("Fusion")
    w = KlipMainWindow()
    w.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    try:
        main()
    except Exception:
        # Make crashes visible if launched as windowed exe
        from PyQt6.QtWidgets import QMessageBox as _MB, QApplication as _QA
        if _QA.instance() is None:
            _ = _QA(sys.argv)
        _MB.critical(None, "KLIP crashed", traceback.format_exc())
        raise
