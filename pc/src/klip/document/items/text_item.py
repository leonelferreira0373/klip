from PySide6.QtCore import Qt
from PySide6.QtGui import QColor, QFont, QTextOption
from PySide6.QtWidgets import QGraphicsTextItem

from ..schema import TextItemModel


class _TextQItem(QGraphicsTextItem):
    def __init__(self, model: TextItemModel):
        super().__init__(model.text)
        font = QFont(model.font_family, int(model.font_size))
        font.setWeight(QFont.Weight(model.font_weight))
        font.setLetterSpacing(QFont.AbsoluteSpacing, model.letter_spacing)
        self.setFont(font)
        self.setDefaultTextColor(QColor(model.color))
        self.setTextWidth(model.transform.w)
        if model.align != "left":
            doc = self.document()
            opt = QTextOption()
            opt.setAlignment({
                "center": Qt.AlignCenter,
                "right": Qt.AlignRight,
            }[model.align])
            doc.setDefaultTextOption(opt)


def build_text_item(model: TextItemModel) -> QGraphicsTextItem:
    return _TextQItem(model)
