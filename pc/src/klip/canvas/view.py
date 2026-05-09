from PySide6.QtCore import Qt
from PySide6.QtGui import QPainter, QWheelEvent
from PySide6.QtWidgets import QGraphicsView


class KlipView(QGraphicsView):
    def __init__(self, scene, parent=None):
        super().__init__(scene, parent)
        self.setRenderHint(QPainter.Antialiasing)
        self.setRenderHint(QPainter.SmoothPixmapTransform)
        self.setDragMode(QGraphicsView.RubberBandDrag)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        self._zoom = 1.0

    @property
    def zoom_factor(self) -> float:
        return self._zoom

    def set_zoom(self, factor: float) -> None:
        clamped = max(0.1, min(8.0, factor))
        ratio = clamped / self._zoom
        self.scale(ratio, ratio)
        self._zoom = clamped

    def wheelEvent(self, event: QWheelEvent):
        if event.modifiers() & Qt.ControlModifier:
            delta = event.angleDelta().y()
            factor = 1.15 if delta > 0 else 1 / 1.15
            self.set_zoom(self._zoom * factor)
            event.accept()
        else:
            super().wheelEvent(event)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            scene_pos = self.mapToScene(event.pos())
            scene = self.scene()
            if hasattr(scene, "handle_canvas_click"):
                model = scene.handle_canvas_click(scene_pos)
                if model is not None:
                    event.accept()
                    return
        super().mousePressEvent(event)
