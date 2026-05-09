from typing import Optional

from PySide6.QtCore import QSize, Qt, Signal
from PySide6.QtGui import QColor, QPainter, QPixmap
from PySide6.QtWidgets import (
    QHBoxLayout,
    QLabel,
    QListWidget,
    QListWidgetItem,
    QPushButton,
    QVBoxLayout,
    QWidget,
)

from ..canvas.scene import KlipScene
from ..document.document import Document


THUMB_SIZE = QSize(96, 72)


class PagesPanel(QWidget):
    """Left-side panel showing pages as thumbnails."""

    page_selected = Signal(int)
    page_added = Signal()
    page_removed = Signal(int)

    def __init__(self, parent=None):
        super().__init__(parent)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(4, 4, 4, 4)
        layout.setSpacing(4)

        layout.addWidget(QLabel("Pages"))

        self._list = QListWidget(self)
        self._list.setIconSize(THUMB_SIZE)
        self._list.setUniformItemSizes(True)
        self._list.itemSelectionChanged.connect(self._on_selection_changed)
        layout.addWidget(self._list, 1)

        btns = QHBoxLayout()
        self._add = QPushButton("+")
        self._del = QPushButton("✕")
        for b in (self._add, self._del):
            b.setFixedWidth(32)
            btns.addWidget(b)
        btns.addStretch(1)
        layout.addLayout(btns)

        self._add.clicked.connect(self.page_added.emit)
        self._del.clicked.connect(self._remove_selected)

        self._document: Optional[Document] = None

    def set_document(self, document: Optional[Document]) -> None:
        self._document = document
        self.refresh()

    def refresh(self) -> None:
        self._list.blockSignals(True)
        self._list.clear()
        if self._document is None:
            self._list.blockSignals(False)
            return
        for index, page in enumerate(self._document.pages):
            li = QListWidgetItem(f"Page {index + 1}")
            pixmap = self._render_thumb(page)
            li.setIcon(pixmap)
            self._list.addItem(li)
        if self._document.current_page_index >= 0:
            self._list.setCurrentRow(self._document.current_page_index)
        self._list.blockSignals(False)

    def _render_thumb(self, page) -> QPixmap:
        scene = KlipScene()
        assets = {a.id: a for a in (self._document.assets if self._document else [])}
        scene.load_page(page, assets)
        target = QPixmap(THUMB_SIZE)
        target.fill(QColor(page.background.get("color", "#ffffff")))
        painter = QPainter(target)
        try:
            painter.setRenderHint(QPainter.Antialiasing)
            painter.setRenderHint(QPainter.SmoothPixmapTransform)
            scene.render(painter, target.rect(), scene.sceneRect())
        finally:
            painter.end()
        return target

    def _on_selection_changed(self):
        idx = self._list.currentRow()
        if idx < 0:
            return
        self.page_selected.emit(idx)

    def _remove_selected(self):
        idx = self._list.currentRow()
        if idx < 0:
            return
        self.page_removed.emit(idx)
