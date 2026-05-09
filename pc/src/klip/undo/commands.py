"""QUndoCommand subclasses for Klip mutations."""
from typing import TYPE_CHECKING, Optional

from PySide6.QtGui import QUndoCommand

from ..document.schema import ItemModel, PageModel

if TYPE_CHECKING:
    from ..document.document import Document


class AddItemCommand(QUndoCommand):
    def __init__(self, document: "Document", page_index: int, item: ItemModel):
        super().__init__(f"Add {item.type}")
        self._doc = document
        self._page_index = page_index
        self._item = item

    def redo(self):
        page = self._doc.pages[self._page_index]
        if self._item not in page.items:
            page.items.append(self._item)

    def undo(self):
        page = self._doc.pages[self._page_index]
        if self._item in page.items:
            page.items.remove(self._item)


class RemoveItemCommand(QUndoCommand):
    def __init__(self, document: "Document", page_index: int, item_id: str):
        super().__init__("Delete item")
        self._doc = document
        self._page_index = page_index
        self._item_id = item_id
        self._snapshot: Optional[ItemModel] = None
        self._index: int = -1

    def redo(self):
        page = self._doc.pages[self._page_index]
        for i, it in enumerate(page.items):
            if it.id == self._item_id:
                self._snapshot = it
                self._index = i
                del page.items[i]
                return

    def undo(self):
        if self._snapshot is None:
            return
        page = self._doc.pages[self._page_index]
        page.items.insert(self._index, self._snapshot)


class MoveItemZCommand(QUndoCommand):
    """Bring item forward / send backward by adjusting z."""

    def __init__(self, document: "Document", page_index: int, item_id: str, delta: int):
        super().__init__("Reorder layer")
        self._doc = document
        self._page_index = page_index
        self._item_id = item_id
        self._delta = delta
        self._old_z: Optional[int] = None

    def redo(self):
        item = self._find()
        if item is None:
            return
        self._old_z = item.z
        item.z = item.z + self._delta

    def undo(self):
        item = self._find()
        if item is None or self._old_z is None:
            return
        item.z = self._old_z

    def _find(self) -> Optional[ItemModel]:
        page = self._doc.pages[self._page_index]
        for it in page.items:
            if it.id == self._item_id:
                return it
        return None


class AddPageCommand(QUndoCommand):
    def __init__(self, document: "Document", page: PageModel):
        super().__init__("Add page")
        self._doc = document
        self._page = page
        self._inserted_at: int = -1

    def redo(self):
        self._doc.pages.append(self._page)
        self._inserted_at = len(self._doc.pages) - 1
        self._doc.current_page_index = self._inserted_at

    def undo(self):
        if self._inserted_at < 0:
            return
        del self._doc.pages[self._inserted_at]
        if not self._doc.pages:
            self._doc.current_page_index = -1
        else:
            self._doc.current_page_index = min(
                self._doc.current_page_index, len(self._doc.pages) - 1
            )


class RemovePageCommand(QUndoCommand):
    def __init__(self, document: "Document", page_index: int):
        super().__init__("Remove page")
        self._doc = document
        self._page_index = page_index
        self._snapshot: Optional[PageModel] = None
        self._was_current: bool = False

    def redo(self):
        if self._page_index >= len(self._doc.pages):
            return
        self._snapshot = self._doc.pages[self._page_index]
        self._was_current = (self._doc.current_page_index == self._page_index)
        del self._doc.pages[self._page_index]
        if not self._doc.pages:
            self._doc.current_page_index = -1
        else:
            self._doc.current_page_index = min(
                self._doc.current_page_index, len(self._doc.pages) - 1
            )

    def undo(self):
        if self._snapshot is None:
            return
        self._doc.pages.insert(self._page_index, self._snapshot)
        if self._was_current:
            self._doc.current_page_index = self._page_index
