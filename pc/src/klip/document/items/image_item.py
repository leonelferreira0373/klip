import base64

from PySide6.QtCore import QRectF
from PySide6.QtGui import QPainter, QPixmap
from PySide6.QtWidgets import QGraphicsItem

from ..schema import AssetModel, ImageItemModel


class _ImageQItem(QGraphicsItem):
    def __init__(self, model: ImageItemModel, asset: AssetModel):
        super().__init__()
        self._model = model
        self._rect = QRectF(0, 0, model.transform.w, model.transform.h)
        pm = QPixmap()
        pm.loadFromData(base64.b64decode(asset.data))
        self._pixmap = pm

    def boundingRect(self) -> QRectF:
        return self._rect

    def paint(self, painter: QPainter, option, widget=None):
        painter.setRenderHint(QPainter.SmoothPixmapTransform)
        painter.drawPixmap(self._rect, self._pixmap, QRectF(self._pixmap.rect()))


def build_image_item(model: ImageItemModel, asset: AssetModel) -> QGraphicsItem:
    return _ImageQItem(model, asset)
