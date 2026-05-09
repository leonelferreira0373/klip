from pathlib import Path
from typing import Optional

from PySide6.QtCore import Qt
from PySide6.QtGui import QKeySequence, QUndoStack
from PySide6.QtWidgets import (
    QDockWidget,
    QFileDialog,
    QMainWindow,
    QMessageBox,
    QStatusBar,
)

from .canvas.handles import HandleOverlay
from .canvas.scene import KlipScene
from .canvas.view import KlipView
from .document.document import Document
from .document.io import load_document, save_document
from .document.schema import DocumentModel, PageModel
from .export.exporter import export_page
from .panels.layers_panel import LayersPanel
from .panels.pages_panel import PagesPanel
from .toolbar.toolbar import KlipToolbar, Tool
from .undo.commands import (
    AddPageCommand,
    MoveItemZCommand,
    RemoveItemCommand,
    RemovePageCommand,
)


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Klip")
        self.resize(1280, 820)

        self._document = Document(name="Untitled")
        self._current_path: Optional[Path] = None

        self.scene = KlipScene(self)
        self.scene.item_added.connect(self._on_item_added)
        self.view = KlipView(self.scene, self)
        self.setCentralWidget(self.view)

        self._handles = HandleOverlay(self.scene)
        self._handles.attach()
        self.scene.selectionChanged.connect(self._on_scene_selection_changed)

        self.toolbar = KlipToolbar(self)
        self.addToolBar(Qt.TopToolBarArea, self.toolbar)
        self.toolbar.tool_changed.connect(self._on_tool_changed)

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

    def _on_tool_changed(self, tool: Tool):
        self.scene.set_active_tool(tool.value)
        self.statusBar().showMessage(f"Tool: {tool.value}", 2000)

    def _on_item_added(self, item_id: str):
        self.pages_panel.refresh()
        self._refresh_layers()

    def _on_scene_selection_changed(self):
        sel = self.scene.selectedItems()
        if sel:
            iid = sel[0].data(0)
            if iid:
                self.layers_panel.select_by_id(iid)
