from typing import Optional

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import (
    QListWidget,
    QListWidgetItem,
    QVBoxLayout,
    QWidget,
    QLabel,
    QHBoxLayout,
    QPushButton,
)

from ..document.schema import PageModel


_ITEM_ICON = {
    "text": "T",
    "shape": "[]",
    "image": "Img",
}


class LayersPanel(QWidget):
    """Right-side panel showing the layer (item) stack of the current page."""

    layer_selected = Signal(str)  # item id
    layer_deleted = Signal(str)
    layer_moved = Signal(str, int)  # item id, delta (+1 up, -1 down)

    def __init__(self, parent=None):
        super().__init__(parent)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(4, 4, 4, 4)
        layout.setSpacing(4)

        layout.addWidget(QLabel("Layers"))

        self._list = QListWidget(self)
        self._list.setSelectionMode(QListWidget.SingleSelection)
        self._list.itemSelectionChanged.connect(self._on_selection_changed)
        layout.addWidget(self._list, 1)

        btns = QHBoxLayout()
        self._up = QPushButton("↑")
        self._down = QPushButton("↓")
        self._del = QPushButton("✕")
        for b in (self._up, self._down, self._del):
            b.setFixedWidth(32)
            btns.addWidget(b)
        btns.addStretch(1)
        layout.addLayout(btns)

        self._up.clicked.connect(lambda: self._move_selected(+1))
        self._down.clicked.connect(lambda: self._move_selected(-1))
        self._del.clicked.connect(self._delete_selected)

        self._page: Optional[PageModel] = None

    def set_page(self, page: Optional[PageModel]) -> None:
        self._page = page
        self.refresh()

    def refresh(self) -> None:
        self._list.blockSignals(True)
        self._list.clear()
        if self._page is None:
            self._list.blockSignals(False)
            return
        # Top of list = highest z, bottom = lowest z (matches Photoshop convention)
        items_sorted = sorted(self._page.items, key=lambda i: -i.z)
        for model in items_sorted:
            label = self._label_for(model)
            li = QListWidgetItem(label)
            li.setData(Qt.UserRole, model.id)
            self._list.addItem(li)
        self._list.blockSignals(False)

    def _label_for(self, model) -> str:
        icon = _ITEM_ICON.get(model.type, "?")
        if model.type == "text":
            text = model.text[:20] if model.text else "(text)"
            return f"{icon}  {text}"
        if model.type == "shape":
            return f"{icon}  {model.shape}"
        if model.type == "image":
            return f"{icon}  image"
        return f"{icon}  {model.type}"

    def select_by_id(self, item_id: str) -> None:
        for i in range(self._list.count()):
            li = self._list.item(i)
            if li.data(Qt.UserRole) == item_id:
                self._list.blockSignals(True)
                self._list.setCurrentRow(i)
                self._list.blockSignals(False)
                return
        # if not found, clear selection
        self._list.blockSignals(True)
        self._list.setCurrentRow(-1)
        self._list.blockSignals(False)

    def _on_selection_changed(self):
        cur = self._list.currentItem()
        if cur is None:
            return
        self.layer_selected.emit(cur.data(Qt.UserRole))

    def _move_selected(self, delta: int):
        cur = self._list.currentItem()
        if cur is None:
            return
        self.layer_moved.emit(cur.data(Qt.UserRole), delta)

    def _delete_selected(self):
        cur = self._list.currentItem()
        if cur is None:
            return
        self.layer_deleted.emit(cur.data(Qt.UserRole))
