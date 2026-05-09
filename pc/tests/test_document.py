from klip.document.document import Document
from klip.document.schema import DocumentModel, PageModel


def _page():
    return PageModel(
        id=f"p_{id(object())}",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#ffffff"},
        items=[],
    )


def test_document_starts_empty():
    d = Document(name="untitled")
    assert d.name == "untitled"
    assert d.pages == []
    assert d.current_page_index == -1


def test_add_page_sets_current():
    d = Document(name="x")
    p = _page()
    d.add_page(p)
    assert d.pages == [p]
    assert d.current_page_index == 0


def test_remove_page_updates_current():
    d = Document(name="x")
    p1, p2 = _page(), _page()
    d.add_page(p1)
    d.add_page(p2)
    d.remove_page(0)
    assert len(d.pages) == 1
    assert d.current_page_index == 0


def test_remove_only_page_resets_index():
    d = Document(name="x")
    d.add_page(_page())
    d.remove_page(0)
    assert d.pages == []
    assert d.current_page_index == -1


def test_to_schema():
    d = Document(name="x")
    d.add_page(_page())
    model = d.to_schema()
    assert isinstance(model, DocumentModel)
    assert model.name == "x"
    assert len(model.pages) == 1


def test_from_schema():
    model = DocumentModel(
        version=1, name="y",
        pages=[_page()],
    )
    d = Document.from_schema(model)
    assert d.name == "y"
    assert len(d.pages) == 1
    assert d.current_page_index == 0
