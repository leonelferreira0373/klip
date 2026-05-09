import math

from PySide6.QtCore import Qt, QRectF
from PySide6.QtGui import QBrush, QColor, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QGraphicsItem

from ..schema import ShapeItemModel


class _ShapeQItem(QGraphicsItem):
    def __init__(self, model: ShapeItemModel):
        super().__init__()
        self._model = model
        self._rect = QRectF(0, 0, model.transform.w, model.transform.h)

    def boundingRect(self) -> QRectF:
        return self._rect

    def paint(self, painter: QPainter, option, widget=None):
        painter.setRenderHint(QPainter.Antialiasing)
        if self._model.fill:
            painter.setBrush(QBrush(QColor(self._model.fill)))
        else:
            painter.setBrush(Qt.NoBrush)
        if self._model.stroke and self._model.stroke_width > 0:
            painter.setPen(QPen(QColor(self._model.stroke), self._model.stroke_width))
        else:
            painter.setPen(Qt.NoPen)

        if self._model.shape == "rect":
            r = self._model.corner_radius
            if r > 0:
                painter.drawRoundedRect(self._rect, r, r)
            else:
                painter.drawRect(self._rect)
        elif self._model.shape == "ellipse":
            painter.drawEllipse(self._rect)
        elif self._model.shape == "line":
            painter.drawLine(self._rect.topLeft(), self._rect.bottomRight())
        elif self._model.shape == "polygon":
            path = QPainterPath()
            self._build_polygon_path(path)
            painter.drawPath(path)

    def _build_polygon_path(self, path: QPainterPath):
        cx, cy = self._rect.center().x(), self._rect.center().y()
        rx, ry = self._rect.width() / 2, self._rect.height() / 2
        n = max(3, self._model.sides)
        for i in range(n):
            angle = (2 * math.pi * i / n) - (math.pi / 2)
            x = cx + rx * math.cos(angle)
            y = cy + ry * math.sin(angle)
            if i == 0:
                path.moveTo(x, y)
            else:
                path.lineTo(x, y)
        path.closeSubpath()


def build_shape_item(model: ShapeItemModel) -> QGraphicsItem:
    return _ShapeQItem(model)
