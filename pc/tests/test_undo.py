from klip.document.document import Document
from klip.document.schema import PageModel, ShapeItemModel, Transform
from klip.undo.commands import (
    AddPageCommand,
    MoveItemZCommand,
    RemoveItemCommand,
    RemovePageCommand,
)


def _doc_with_page():
    d = Document(name="x")
    d.add_page(PageModel(
        id="p1",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[],
    ))
    return d


def _make_rect(item_id="r1", z=0):
    return ShapeItemModel(
        id=item_id,
        transform=Transform(x=0, y=0, w=10, h=10),
        z=z, shape="rect", fill="#000",
    )


def test_remove_item_command_round_trip():
    d = _doc_with_page()
    rect = _make_rect()
    d.pages[0].items.append(rect)
    cmd = RemoveItemCommand(d, 0, "r1")
    cmd.redo()
    assert len(d.pages[0].items) == 0
    cmd.undo()
    assert len(d.pages[0].items) == 1
    assert d.pages[0].items[0].id == "r1"


def test_move_item_z_command():
    d = _doc_with_page()
    d.pages[0].items.append(_make_rect("a", z=2))
    cmd = MoveItemZCommand(d, 0, "a", +1)
    cmd.redo()
    assert d.pages[0].items[0].z == 3
    cmd.undo()
    assert d.pages[0].items[0].z == 2


def test_add_page_command():
    d = _doc_with_page()
    new = PageModel(
        id="p2",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[],
    )
    cmd = AddPageCommand(d, new)
    cmd.redo()
    assert len(d.pages) == 2
    assert d.current_page_index == 1
    cmd.undo()
    assert len(d.pages) == 1


def test_remove_page_command_preserves_other_pages():
    d = _doc_with_page()
    d.add_page(PageModel(
        id="p2",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[_make_rect()],
    ))
    cmd = RemovePageCommand(d, 0)
    cmd.redo()
    assert len(d.pages) == 1
    assert d.pages[0].id == "p2"
    cmd.undo()
    assert len(d.pages) == 2
    assert d.pages[0].id == "p1"
