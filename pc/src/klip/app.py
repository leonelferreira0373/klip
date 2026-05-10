import base64
import uuid
from pathlib import Path
from typing import Optional

from PySide6.QtCore import QBuffer, QByteArray, QIODevice, Qt
from PySide6.QtGui import QGuiApplication, QImage, QKeySequence, QShortcut, QUndoStack
from PySide6.QtWidgets import (
    QDockWidget,
    QFileDialog,
    QMainWindow,
    QMessageBox,
    QStatusBar,
)

from .ai.bg_remover import remove_background
from .canvas.handles import HandleOverlay
from .canvas.scene import KlipScene
from .canvas.view import KlipView
from .color.extractor import extract_palette
from .color.picker import hex_at
from .document.document import Document
from .document.io import load_document, save_document
from .document.schema import (
    AssetModel,
    DocumentModel,
    ImageItemModel,
    PageModel,
    Transform,
)
from .export.exporter import export_page
from .fonts.installer import install_user_font
from .panels.layers_panel import LayersPanel
from .panels.pages_panel import PagesPanel
from .toolbar.toolbar import KlipToolbar, Tool
from .undo.commands import (
    AddItemCommand,
    AddPageCommand,
    MoveItemZCommand,
    RemoveItemCommand,
    RemovePageCommand,
)


_IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"}
_MIME_BY_EXT = {
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".webp": "image/webp",
    ".bmp": "image/bmp",
    ".gif": "image/gif",
    ".tif": "image/tiff",
    ".tiff": "image/tiff",
}


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Klip")
        self.resize(1280, 820)
        self.setAcceptDrops(True)

        self._document = Document(name="Untitled")
        self._current_path: Optional[Path] = None

        self.scene = KlipScene(self)
        self.scene.item_added.connect(self._on_item_added)
        self.scene.color_picked.connect(self._on_color_picked)
        self.view = KlipView(self.scene, self)
        self.view.setAcceptDrops(False)
        self.setCentralWidget(self.view)

        self._handles = HandleOverlay(self.scene)
        self._handles.attach()
        self.scene.selectionChanged.connect(self._on_scene_selection_changed)

        self.toolbar = KlipToolbar(self)
        self.addToolBar(Qt.TopToolBarArea, self.toolbar)
        self.toolbar.tool_changed.connect(self._on_tool_changed)
        self.scene.set_active_tool("select")

        self.undo_stack = QUndoStack(self)

        # Pages panel — left dock
        self.pages_panel = PagesPanel(self)
        self.pages_dock = QDockWidget("Pages", self)
        self.pages_dock.setObjectName("pages_dock")
        self.pages_dock.setWidget(self.pages_panel)
        self.pages_dock.setAllowedAreas(Qt.LeftDockWidgetArea | Qt.RightDockWidgetArea)
        self.addDockWidget(Qt.LeftDockWidgetArea, self.pages_dock)
        self.pages_panel.page_selected.connect(self._on_page_selected)
        self.pages_panel.page_added.connect(self._add_page)
        self.pages_panel.page_removed.connect(self._remove_page)

        # Layers panel — right dock
        self.layers_panel = LayersPanel(self)
        self.layers_dock = QDockWidget("Layers", self)
        self.layers_dock.setObjectName("layers_dock")
        self.layers_dock.setWidget(self.layers_panel)
        self.layers_dock.setAllowedAreas(Qt.LeftDockWidgetArea | Qt.RightDockWidgetArea)
        self.addDockWidget(Qt.RightDockWidgetArea, self.layers_dock)
        self.layers_panel.layer_selected.connect(self._on_layer_selected)
        self.layers_panel.layer_deleted.connect(self._on_layer_deleted)
        self.layers_panel.layer_moved.connect(self._on_layer_moved)

        self.setStatusBar(QStatusBar())
        self.statusBar().showMessage("Ready")

        self._build_menus()

        paste_sc = QShortcut(QKeySequence.Paste, self)
        paste_sc.activated.connect(self._paste_clipboard)

        esc_sc = QShortcut(QKeySequence(Qt.Key_Escape), self)
        esc_sc.setContext(Qt.ApplicationShortcut)
        esc_sc.activated.connect(lambda: self.toolbar.set_active_tool(Tool.SELECT))

        self._new_document()

    # ---------------- public API ----------------
    def load_document_model(self, model: DocumentModel) -> None:
        self._document = Document.from_schema(model)
        self.undo_stack.clear()
        self._refresh_all()
        self._refresh_title()

    # ---------------- menus ----------------
    def _build_menus(self):
        m_file = self.menuBar().addMenu("&File")
        m_file.addAction("&New", self._new_document, QKeySequence.New)
        m_file.addAction("&Open...", self._open, QKeySequence.Open)
        m_file.addAction("&Save", self._save, QKeySequence.Save)
        m_file.addAction("Save &As...", self._save_as, QKeySequence.SaveAs)
        m_file.addSeparator()
        m_export = m_file.addMenu("&Export")
        m_export.addAction("Current page as PNG...", lambda: self._export_current("PNG"))
        m_export.addAction("Current page as JPG...", lambda: self._export_current("JPG"))
        m_export.addAction("All pages as PNG...", lambda: self._export_all("PNG"))
        m_file.addSeparator()
        m_file.addAction("E&xit", self.close, QKeySequence.Quit)

        m_edit = self.menuBar().addMenu("&Edit")
        undo_act = self.undo_stack.createUndoAction(self, "&Undo")
        undo_act.setShortcut(QKeySequence.Undo)
        m_edit.addAction(undo_act)
        redo_act = self.undo_stack.createRedoAction(self, "&Redo")
        redo_act.setShortcut(QKeySequence.Redo)
        m_edit.addAction(redo_act)
        m_edit.addSeparator()
        m_edit.addAction("&Paste image", self._paste_clipboard, QKeySequence.Paste)

        m_insert = self.menuBar().addMenu("&Insert")
        m_insert.addAction(
            "&Image...", self._insert_image_dialog, QKeySequence("Ctrl+I")
        )

        m_view = self.menuBar().addMenu("&View")
        m_view.addAction(self.pages_dock.toggleViewAction())
        m_view.addAction(self.layers_dock.toggleViewAction())

    # ---------------- file ops ----------------
    def _new_document(self):
        self._document = Document(name="Untitled")
        self._document.add_page(PageModel(
            id="p1",
            size={"w": 1080, "h": 1080, "unit": "px", "dpi": 144},
            background={"type": "solid", "color": "#ffffff"},
            items=[],
        ))
        self._current_path = None
        self.undo_stack.clear()
        self._refresh_all()
        self._refresh_title()

    def _open(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "Open Klip document", "", "Klip files (*.mcv)"
        )
        if not path:
            return
        try:
            model = load_document(Path(path))
        except Exception as e:
            QMessageBox.critical(self, "Open failed", str(e))
            return
        self._document = Document.from_schema(model)
        self._current_path = Path(path)
        self.undo_stack.clear()
        self._refresh_all()
        self._refresh_title()

    def _save(self):
        if self._current_path is None:
            self._save_as()
            return
        save_document(self._document.to_schema(), self._current_path)
        self.statusBar().showMessage(f"Saved {self._current_path.name}", 3000)

    def _save_as(self):
        path, _ = QFileDialog.getSaveFileName(
            self, "Save Klip document", "Untitled.mcv", "Klip files (*.mcv)"
        )
        if not path:
            return
        self._current_path = Path(path)
        self._save()
        self._refresh_title()

    def _export_current(self, fmt: str):
        if self._document.current_page_index < 0:
            return
        ext = "png" if fmt.upper() == "PNG" else "jpg"
        default = self._current_path.stem if self._current_path else "page"
        path, _ = QFileDialog.getSaveFileName(
            self, f"Export page as {fmt}", f"{default}.{ext}",
            f"{fmt} (*.{ext})"
        )
        if not path:
            return
        page = self._document.current_page()
        assets = {a.id: a for a in self._document.assets}
        export_page(page, assets, Path(path), fmt=fmt)
        self.statusBar().showMessage(f"Exported {Path(path).name}", 4000)

    def _export_all(self, fmt: str):
        if not self._document.pages:
            return
        folder = QFileDialog.getExistingDirectory(self, "Export all pages to folder")
        if not folder:
            return
        ext = "png" if fmt.upper() == "PNG" else "jpg"
        base = (self._current_path.stem if self._current_path else "klip")
        assets = {a.id: a for a in self._document.assets}
        for i, page in enumerate(self._document.pages):
            out = Path(folder) / f"{base}-page-{i + 1}.{ext}"
            export_page(page, assets, out, fmt=fmt)
        self.statusBar().showMessage(
            f"Exported {len(self._document.pages)} pages to {folder}", 5000
        )

    # ---------------- image import ----------------
    def _insert_image_dialog(self):
        path, _ = QFileDialog.getOpenFileName(
            self,
            "Insert image",
            "",
            "Images (*.png *.jpg *.jpeg *.webp *.bmp *.gif *.tif *.tiff)",
        )
        if not path:
            return
        self.insert_image_from_path(Path(path))

    def insert_image_from_path(self, path: Path) -> Optional[ImageItemModel]:
        try:
            data = Path(path).read_bytes()
        except OSError as e:
            QMessageBox.warning(self, "Insert image failed", str(e))
            return None
        ext = Path(path).suffix.lower()
        mime = _MIME_BY_EXT.get(ext, "image/png")
        return self.insert_image_bytes(data, mime)

    def insert_image_from_qimage(self, image: QImage) -> Optional[ImageItemModel]:
        if image.isNull():
            return None
        buf = QBuffer()
        buf.open(QIODevice.WriteOnly)
        image.save(buf, "PNG")
        return self.insert_image_bytes(bytes(buf.data()), "image/png")

    def insert_image_bytes(
        self, data: bytes, mime: str
    ) -> Optional[ImageItemModel]:
        if self._document.current_page_index < 0:
            return None

        img = QImage()
        if not img.loadFromData(QByteArray(data)):
            QMessageBox.warning(
                self, "Insert image failed", "Could not decode image data."
            )
            return None
        iw, ih = img.width(), img.height()
        if iw <= 0 or ih <= 0:
            return None

        page = self._document.current_page()
        pw = float(page.size["w"])
        ph = float(page.size["h"])
        max_w, max_h = pw * 0.8, ph * 0.8
        scale = min(max_w / iw, max_h / ih, 1.0)
        w, h = iw * scale, ih * scale
        x, y = (pw - w) / 2, (ph - h) / 2

        asset = AssetModel(
            id=f"asset_{uuid.uuid4().hex[:12]}",
            mime=mime,
            data=base64.b64encode(data).decode("ascii"),
        )
        self._document.assets.append(asset)

        next_z = 1 + max((it.z for it in page.items), default=0)
        item = ImageItemModel(
            id=f"image_{uuid.uuid4().hex[:12]}",
            transform=Transform(x=x, y=y, w=w, h=h),
            z=next_z,
            asset_ref=asset.id,
        )

        cmd = AddItemCommand(
            self._document, self._document.current_page_index, item
        )
        self.undo_stack.push(cmd)
        self._refresh_scene()
        self._refresh_layers()
        self.statusBar().showMessage(f"Inserted image ({iw}x{ih})", 3000)
        return item

    def _paste_clipboard(self):
        cb = QGuiApplication.clipboard()
        mime = cb.mimeData()
        if mime is None:
            return
        if mime.hasImage():
            self.insert_image_from_qimage(cb.image())
            return
        if mime.hasUrls():
            for url in mime.urls():
                if url.isLocalFile():
                    p = Path(url.toLocalFile())
                    if p.suffix.lower() in _IMAGE_EXTS:
                        self.insert_image_from_path(p)
                        return

    # ---------------- font / bg-remove / palette ----------------
    def _install_font_dialog(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "Install font", "", "Fonts (*.ttf *.otf)"
        )
        if not path:
            return
        try:
            dst = install_user_font(Path(path))
        except Exception as e:
            QMessageBox.warning(self, "Install font failed", str(e))
            return
        self.statusBar().showMessage(f"Installed font: {dst.name}", 4000)

    def _selected_image_item(self) -> Optional[ImageItemModel]:
        sel = self.scene.selectedItems()
        if not sel:
            return None
        item_id = sel[0].data(0)
        if not item_id or self._document.current_page_index < 0:
            return None
        page = self._document.current_page()
        for it in page.items:
            if it.id == item_id and isinstance(it, ImageItemModel):
                return it
        return None

    def _run_bg_remove_on_selection(self):
        item = self._selected_image_item()
        if item is None:
            QMessageBox.information(
                self,
                "No image selected",
                "Select an image on the canvas first, then click BG Remove.",
            )
            return
        asset = next(
            (a for a in self._document.assets if a.id == item.asset_ref), None
        )
        if asset is None:
            return
        self.statusBar().showMessage("Running background remover…")
        QGuiApplication.processEvents()
        try:
            from PIL import Image
            import io as _io
            raw = base64.b64decode(asset.data)
            pil = Image.open(_io.BytesIO(raw))
            cutout = remove_background(pil, "birefnet-general")
            buf = _io.BytesIO()
            cutout.save(buf, format="PNG")
            asset.data = base64.b64encode(buf.getvalue()).decode("ascii")
            asset.mime = "image/png"
        except Exception as e:
            QMessageBox.warning(self, "BG Remove failed", str(e))
            self.statusBar().clearMessage()
            return
        self._refresh_scene()
        self.statusBar().showMessage("Background removed.", 3000)

    def _run_palette_extract_on_selection(self):
        item = self._selected_image_item()
        if item is None:
            QMessageBox.information(
                self,
                "No image selected",
                "Select an image on the canvas first, then click Palette.",
            )
            return
        asset = next(
            (a for a in self._document.assets if a.id == item.asset_ref), None
        )
        if asset is None:
            return
        try:
            from PIL import Image
            import io as _io
            raw = base64.b64decode(asset.data)
            pil = Image.open(_io.BytesIO(raw))
            colors = extract_palette(pil, k=5)
        except Exception as e:
            QMessageBox.warning(self, "Palette failed", str(e))
            return
        self._show_palette_dialog(colors)

    def _show_palette_dialog(self, colors: list):
        from PySide6.QtWidgets import QDialog, QHBoxLayout, QLabel, QVBoxLayout

        dlg = QDialog(self)
        dlg.setWindowTitle("Color palette")
        v = QVBoxLayout(dlg)
        v.addWidget(QLabel("Click a swatch to copy its hex code."))
        row = QHBoxLayout()
        for hexc in colors:
            swatch = QLabel(hexc)
            swatch.setFixedSize(96, 64)
            swatch.setStyleSheet(
                f"background:{hexc}; color: white; padding: 6px;"
                " border: 1px solid #222;"
            )
            swatch.setAlignment(Qt.AlignCenter)
            swatch.mousePressEvent = (
                lambda _ev, c=hexc: (
                    QGuiApplication.clipboard().setText(c),
                    self.statusBar().showMessage(f"Copied {c}", 2000),
                )
            )
            row.addWidget(swatch)
        v.addLayout(row)
        dlg.exec()

    # ---------------- eyedropper ----------------
    def _on_color_picked(self, hex_color: str):
        QGuiApplication.clipboard().setText(hex_color)
        self.toolbar.revert_to_select_silent()
        self.scene.set_active_tool("select")
        self.statusBar().showMessage(
            f"Sampled {hex_color} (copied to clipboard)", 4000
        )

    # ---------------- drag and drop ----------------
    def dragEnterEvent(self, event):
        if self._drop_has_image(event.mimeData()):
            event.acceptProposedAction()
        else:
            super().dragEnterEvent(event)

    def dragMoveEvent(self, event):
        if self._drop_has_image(event.mimeData()):
            event.acceptProposedAction()
        else:
            super().dragMoveEvent(event)

    def dropEvent(self, event):
        mime = event.mimeData()
        if mime.hasImage():
            img = QImage(mime.imageData())
            if not img.isNull():
                self.insert_image_from_qimage(img)
                event.acceptProposedAction()
                return
        if mime.hasUrls():
            inserted = False
            for url in mime.urls():
                if not url.isLocalFile():
                    continue
                p = Path(url.toLocalFile())
                if p.suffix.lower() in _IMAGE_EXTS:
                    self.insert_image_from_path(p)
                    inserted = True
            if inserted:
                event.acceptProposedAction()
                return
        super().dropEvent(event)

    @staticmethod
    def _drop_has_image(mime) -> bool:
        if mime is None:
            return False
        if mime.hasImage():
            return True
        if mime.hasUrls():
            for url in mime.urls():
                if url.isLocalFile():
                    if Path(url.toLocalFile()).suffix.lower() in _IMAGE_EXTS:
                        return True
        return False

    # ---------------- page ops ----------------
    def _add_page(self):
        new_page = PageModel(
            id=f"page_{len(self._document.pages) + 1}",
            size={"w": 1080, "h": 1080, "unit": "px", "dpi": 144},
            background={"type": "solid", "color": "#ffffff"},
            items=[],
        )
        cmd = AddPageCommand(self._document, new_page)
        self.undo_stack.push(cmd)
        self.pages_panel.set_document(self._document)
        self._refresh_scene()

    def _remove_page(self, index: int):
        if not (0 <= index < len(self._document.pages)):
            return
        if len(self._document.pages) <= 1:
            QMessageBox.information(
                self, "Cannot remove", "A document must have at least one page."
            )
            return
        cmd = RemovePageCommand(self._document, index)
        self.undo_stack.push(cmd)
        self.pages_panel.set_document(self._document)
        self._refresh_scene()

    def _on_page_selected(self, index: int):
        if not (0 <= index < len(self._document.pages)):
            return
        self._document.current_page_index = index
        self._refresh_scene()
        self._refresh_layers()

    # ---------------- layer ops ----------------
    def _on_layer_selected(self, item_id: str):
        for qitem in self.scene.items():
            if qitem.data(0) == item_id:
                self.scene.clearSelection()
                qitem.setSelected(True)
                return

    def _on_layer_deleted(self, item_id: str):
        if self._document.current_page_index < 0:
            return
        cmd = RemoveItemCommand(
            self._document, self._document.current_page_index, item_id
        )
        self.undo_stack.push(cmd)
        self._refresh_scene()
        self._refresh_layers()

    def _on_layer_moved(self, item_id: str, delta: int):
        if self._document.current_page_index < 0:
            return
        cmd = MoveItemZCommand(
            self._document, self._document.current_page_index, item_id, delta
        )
        self.undo_stack.push(cmd)
        self._refresh_scene()
        self._refresh_layers()

    # ---------------- helpers ----------------
    def _refresh_all(self):
        self.pages_panel.set_document(self._document)
        self._refresh_scene()
        self._refresh_layers()

    def _refresh_scene(self):
        if self._document.current_page_index < 0:
            self.scene.clear()
            return
        page = self._document.current_page()
        assets_by_id = {a.id: a for a in self._document.assets}
        self.scene.load_page(page, assets_by_id)

    def _refresh_layers(self):
        if self._document.current_page_index < 0:
            self.layers_panel.set_page(None)
            return
        self.layers_panel.set_page(self._document.current_page())

    def _refresh_title(self):
        name = (self._current_path.name if self._current_path else "Untitled.mcv")
        self.setWindowTitle(f"Klip - {name}")

    _SHAPE_HINTS = {
        Tool.RECT: "Click on canvas to drop a rectangle. Press Esc to cancel.",
        Tool.ELLIPSE: "Click on canvas to drop an ellipse. Press Esc to cancel.",
        Tool.POLYGON: "Click on canvas to drop a polygon. Press Esc to cancel.",
        Tool.LINE: "Click on canvas to drop a line. Press Esc to cancel.",
        Tool.TEXT: "Click on canvas to drop a text box. Press Esc to cancel.",
    }

    def _on_tool_changed(self, tool: Tool):
        if tool in self._SHAPE_HINTS:
            self.scene.set_active_tool(tool.value)
            self.statusBar().showMessage(self._SHAPE_HINTS[tool])
            return
        if tool == Tool.SELECT:
            self.scene.set_active_tool("select")
            self.statusBar().showMessage("Select & move", 2000)
            return
        if tool == Tool.HAND:
            self.scene.set_active_tool("hand")
            self.statusBar().showMessage("Pan tool", 2000)
            return
        if tool == Tool.PICK:
            self.scene.set_active_tool("pick")
            self.statusBar().showMessage(
                "Eyedropper armed. Click anywhere on canvas to sample a color."
            )
            return
        # One-shot action tools — fire immediately, revert to SELECT silently
        # so the action's own status message stays visible.
        if tool == Tool.IMAGE:
            self._insert_image_dialog()
        elif tool == Tool.FONT:
            self._install_font_dialog()
        elif tool == Tool.BG_REMOVE:
            self._run_bg_remove_on_selection()
        elif tool == Tool.EXTRACT:
            self._run_palette_extract_on_selection()
        elif tool == Tool.CLIP:
            self.statusBar().showMessage(
                "Clip (image inside shape) — coming in a later update.", 4000
            )
        self.scene.set_active_tool("select")
        self.toolbar.revert_to_select_silent()

    def _on_item_added(self, item_id: str):
        self.pages_panel.refresh()
        self._refresh_layers()

    def _on_scene_selection_changed(self):
        sel = self.scene.selectedItems()
        if sel:
            iid = sel[0].data(0)
            if iid:
                self.layers_panel.select_by_id(iid)
