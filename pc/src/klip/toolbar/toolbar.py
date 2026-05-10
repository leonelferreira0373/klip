from enum import Enum

from PySide6.QtCore import Signal
from PySide6.QtGui import QAction
from PySide6.QtWidgets import QToolBar


class Tool(Enum):
    SELECT = "select"
    HAND = "hand"
    TEXT = "text"
    RECT = "rect"
    ELLIPSE = "ellipse"
    POLYGON = "polygon"
    LINE = "line"
    IMAGE = "image"
    BG_REMOVE = "bg_remove"
    CLIP = "clip"
    PICK = "pick"
    EXTRACT = "extract"
    FONT = "font"


_ICONS = {
    Tool.SELECT: "Sel", Tool.HAND: "Pan",
    Tool.TEXT: "T", Tool.RECT: "Rect", Tool.ELLIPSE: "Oval",
    Tool.POLYGON: "Poly", Tool.LINE: "Line",
    Tool.IMAGE: "Img", Tool.BG_REMOVE: "BG", Tool.CLIP: "Clip",
    Tool.PICK: "Eyedrop", Tool.EXTRACT: "Palette", Tool.FONT: "Font+",
}

_TIPS = {
    Tool.SELECT: "Select & Move (V)",
    Tool.HAND: "Pan (H)",
    Tool.TEXT: "Text (T)",
    Tool.RECT: "Rectangle (R)",
    Tool.ELLIPSE: "Ellipse (E)",
    Tool.POLYGON: "Polygon (P)",
    Tool.LINE: "Line (L)",
    Tool.IMAGE: "Insert Image (I)",
    Tool.BG_REMOVE: "BG Remover",
    Tool.CLIP: "Image inside shape",
    Tool.PICK: "Color picker (K)",
    Tool.EXTRACT: "Color extractor",
    Tool.FONT: "Install font",
}


class KlipToolbar(QToolBar):
    tool_changed = Signal(Tool)

    def __init__(self, parent=None):
        super().__init__("Tools", parent)
        self._active = Tool.SELECT
        self._actions: dict = {}
        for t in Tool:
            act = QAction(_ICONS[t], self)
            act.setToolTip(_TIPS[t])
            act.setCheckable(True)
            act.triggered.connect(lambda checked, tool=t: self.set_active_tool(tool))
            self.addAction(act)
            self._actions[t] = act
        self._actions[Tool.SELECT].setChecked(True)

    @property
    def active_tool(self) -> Tool:
        return self._active

    def set_active_tool(self, tool: Tool) -> None:
        if tool == self._active:
            return
        self._actions[self._active].setChecked(False)
        self._actions[tool].setChecked(True)
        self._active = tool
        self.tool_changed.emit(tool)

    def revert_to_select_silent(self) -> None:
        """Toggle UI back to SELECT without firing tool_changed (for programmatic
        revert after one-shot action tools, so we don't clobber a status message)."""
        if self._active == Tool.SELECT:
            return
        self._actions[self._active].setChecked(False)
        self._actions[Tool.SELECT].setChecked(True)
        self._active = Tool.SELECT
