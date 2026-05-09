from pathlib import Path
from typing import Optional

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QFileDialog, QMainWindow, QMessageBox, QStatusBar

from .canvas.handles import HandleOverlay
from .canvas.scene import KlipScene
from .canvas.view import KlipView
from .document.document import Document
from .document.io import load_document, save_document
from .document.schema import DocumentModel, PageModel
from .toolbar.toolbar import KlipToolbar, Tool


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Klip")
        self.resize(1200, 800)

        self._document = Document(name="Untitled")
        self._current_path: Optional[Path] = None

        self.scene = KlipScene(self)
        self.view = KlipView(self.scene, self)
        self.setCentralWidget(self.view)

        self._handles = HandleOverlay(self.scene)
        self._handles.attach()

        self.toolbar = KlipToolbar(self)
        self.addToolBar(Qt.TopToolBarArea, self.toolbar)
        self.toolbar.tool_changed.connect(self._on_tool_changed)

        self.setStatusBar(QStatusBar())
        self.statusBar().showMessage("Ready")

        self._build_menus()
        self._new_document()

    def load_document_model(self, model: DocumentModel) -> None:
        self._document = Document.from_schema(model)
        self._refresh_scene()
        self._refresh_title()

    def _build_menus(self):
        m_file = self.menuBar().addMenu("&File")
        m_file.addAction("&New", self._new_document)
        m_file.addAction("&Open...", self._open)
        m_file.addAction("&Save", self._save)
        m_file.addAction("Save &As...", self._save_as)
        m_file.addSeparator()
        m_file.addAction("E&xit", self.close)

    def _new_document(self):
        self._document = Document(name="Untitled")
        self._document.add_page(PageModel(
            id="p1",
            size={"w": 1080, "h": 1080, "unit": "px", "dpi": 144},
            background={"type": "solid", "color": "#ffffff"},
            items=[],
        ))
        self._current_path = None
        self._refresh_scene()
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
        self._refresh_scene()
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

    def _refresh_scene(self):
        if self._document.current_page_index < 0:
            self.scene.clear()
            return
        page = self._document.current_page()
        assets_by_id = {a.id: a for a in self._document.assets}
        self.scene.load_page(page, assets_by_id)

    def _refresh_title(self):
        name = (self._current_path.name if self._current_path else "Untitled.mcv")
        self.setWindowTitle(f"Klip - {name}")

    def _on_tool_changed(self, tool: Tool):
        self.scene.set_active_tool(tool.value)
        self.statusBar().showMessage(f"Tool: {tool.value}", 2000)
