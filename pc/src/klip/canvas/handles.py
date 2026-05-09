from typing import List

from PySide6.QtGui import QBrush, QColor, QPen
from PySide6.QtWidgets import QGraphicsItem, QGraphicsRectItem, QGraphicsScene

HANDLE_SIZE = 8
HANDLE_COLOR = QColor("#4d80ff")
HANDLE_FILL = QColor("#ffffff")


class _Handle(QGraphicsRectItem):
    def __init__(self):
        r = HANDLE_SIZE
        super().__init__(-r / 2, -r / 2, r, r)
        self.setBrush(QBrush(HANDLE_FILL))
        self.setPen(QPen(HANDLE_COLOR, 2))
        self.setZValue(10000)
        self.setFlag(QGraphicsItem.ItemIgnoresTransformations)


class HandleOverlay:
    def __init__(self, scene: QGraphicsScene):
        self._scene = scene
        self._handles: List[_Handle] = []

    def attach(self):
        self._scene.selectionChanged.connect(self.refresh)

    def handles(self) -> List[_Handle]:
        return self._handles

    def refresh(self):
        for h in self._handles:
            self._scene.removeItem(h)
        self._handles.clear()
        sel = self._scene.selectedItems()
        if not sel:
            return
        bounds = sel[0].sceneBoundingRect()
        positions = [
            (bounds.left(), bounds.top()),
            (bounds.center().x(), bounds.top()),
            (bounds.right(), bounds.top()),
            (bounds.left(), bounds.center().y()),
            (bounds.right(), bounds.center().y()),
            (bounds.left(), bounds.bottom()),
            (bounds.center().x(), bounds.bottom()),
            (bounds.right(), bounds.bottom()),
        ]
        for x, y in positions:
            h = _Handle()
            h.setPos(x, y)
            self._scene.addItem(h)
            self._handles.append(h)
