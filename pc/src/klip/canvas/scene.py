import uuid
from typing import Mapping, Optional

from PySide6.QtCore import QRectF
from PySide6.QtGui import QBrush, QColor
from PySide6.QtWidgets import QGraphicsScene

from ..document.items.base import ItemAdapter
from ..document.schema import (
    AssetModel,
    PageModel,
    ShapeItemModel,
    TextItemModel,
    Transform,
)


class KlipScene(QGraphicsScene):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._page: Optional[PageModel] = None
        self._active_tool: Optional[str] = None

    def load_page(self, page: PageModel, assets: Mapping[str, AssetModel]) -> None:
        self.clear()
        self._page = page
        self.setSceneRect(QRectF(0, 0, page.size["w"], page.size["h"]))
        bg_color = page.background.get("color", "#ffffff")
        self.setBackgroundBrush(QBrush(QColor(bg_color)))
        for model in page.items:
            qitem = ItemAdapter.create_qitem_with_assets(model, assets)
            self.addItem(qitem)

    @property
    def page(self) -> Optional[PageModel]:
        return self._page

    def set_active_tool(self, tool_value: str) -> None:
        self._active_tool = tool_value

    def handle_canvas_click(self, scene_pos):
        """Drop a new item at scene_pos based on the active tool."""
        if self._active_tool is None or self._active_tool in ("select", "hand"):
            return None
        new_model = None
        if self._active_tool == "rect":
            new_model = ShapeItemModel(
                id=f"item_{uuid.uuid4().hex[:8]}",
                transform=Transform(
                    x=scene_pos.x() - 50, y=scene_pos.y() - 50, w=100, h=100
                ),
                z=len(self.items()),
                shape="rect",
                fill="#cccccc",
                stroke="#333333",
                stroke_width=1,
            )
        elif self._active_tool == "ellipse":
            new_model = ShapeItemModel(
                id=f"item_{uuid.uuid4().hex[:8]}",
                transform=Transform(
                    x=scene_pos.x() - 50, y=scene_pos.y() - 50, w=100, h=100
                ),
                z=len(self.items()),
                shape="ellipse",
                fill="#cccccc",
                stroke="#333333",
                stroke_width=1,
            )
        elif self._active_tool == "text":
            new_model = TextItemModel(
                id=f"item_{uuid.uuid4().hex[:8]}",
                transform=Transform(
                    x=scene_pos.x(), y=scene_pos.y(), w=200, h=40
                ),
                z=len(self.items()),
                text="Text",
                font_family="Segoe UI",
                font_size=24,
                color="#000000",
            )
        if new_model is None:
            return None
        if self._page is not None:
            self._page.items.append(new_model)
        qitem = ItemAdapter.create_qitem(new_model)
        self.addItem(qitem)
        return new_model
