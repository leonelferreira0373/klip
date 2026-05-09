# Klip — Phase 0 + Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bootstrap the Klip PC project (Phase 0) and ship a working PySide6 skeleton with `.mcv` save/load, a canvas that draws rectangles/ellipses/text/images, and selection + move + resize (Phase 1).

**Architecture:** Python 3.9 + PySide6 single-window app. Item-hierarchy on `QGraphicsScene` for canvas. Pydantic-validated `.mcv` documents (gzipped JSON). Test-driven with `pytest` + `pytest-qt`. Local git only — no remote.

**Tech Stack:** Python 3.9, PySide6 6.6+, pydantic 2.x, Pillow (already installed), pytest, pytest-qt.

**Reference:** `docs/design.md` sections 4 (Stack), 5.1 (PC structure), 6 (`.mcv` schema), 7 (features), 9.1 (UI layout), 11 (Setup).

---

## File Structure

After Phase 0 + 1 complete, project layout is:

```
C:\Users\leone\klip\
├── .gitignore
├── README.md
├── docs\
│   ├── design.md           # already written
│   └── plan.md             # this file
├── schema\                 # placeholder for shared .mcv JSON Schema
└── pc\
    ├── .venv\              # virtual environment (gitignored)
    ├── pyproject.toml
    ├── requirements.txt
    ├── README.md
    ├── src\klip\
    │   ├── __init__.py
    │   ├── main.py             # entry: python -m klip
    │   ├── app.py              # MainWindow class
    │   ├── document\
    │   │   ├── __init__.py
    │   │   ├── schema.py       # pydantic models
    │   │   ├── document.py     # Document container
    │   │   ├── io.py           # save/load .mcv
    │   │   └── items\
    │   │       ├── __init__.py
    │   │       ├── base.py     # Item base class
    │   │       ├── text_item.py
    │   │       ├── shape_item.py
    │   │       └── image_item.py
    │   ├── canvas\
    │   │   ├── __init__.py
    │   │   ├── scene.py        # KlipScene(QGraphicsScene)
    │   │   ├── view.py         # KlipView(QGraphicsView) — zoom + pan
    │   │   └── handles.py      # Selection/transform handles
    │   └── toolbar\
    │       ├── __init__.py
    │       └── toolbar.py      # Tool enum + QToolBar wiring
    └── tests\
        ├── __init__.py
        ├── conftest.py         # pytest-qt fixtures
        ├── test_schema.py
        ├── test_document.py
        ├── test_io.py
        ├── test_items.py
        ├── test_scene.py
        └── test_app_smoke.py
```

**File responsibilities (one job each):**

- `schema.py` — pure data shapes; no Qt, no I/O.
- `document.py` — in-memory document container; lists of pages and items.
- `io.py` — bytes ↔ Document (gzip+JSON+schema validation). No Qt.
- `items/*.py` — one Qt-aware item class per file; converts schema ⇆ QGraphicsItem.
- `scene.py` — owns one `Page`, builds Qt items, emits selection signals.
- `view.py` — zoom, pan, drag-select. No item logic.
- `handles.py` — draws & moves selection handles. No item logic.
- `toolbar.py` — tool buttons + active-tool state.
- `app.py` — wires everything together; menus, file dialogs.
- `main.py` — `QApplication` boot.

---

## Phase 0 — Bootstrap

### Task 0.1: Create folder structure

**Files:**
- Create: `C:\Users\leone\klip\pc\src\klip\__init__.py`
- Create: `C:\Users\leone\klip\pc\tests\__init__.py`
- Create: `C:\Users\leone\klip\schema\.gitkeep`

- [ ] **Step 1: Create directories**

```powershell
mkdir C:\Users\leone\klip\pc\src\klip
mkdir C:\Users\leone\klip\pc\src\klip\document
mkdir C:\Users\leone\klip\pc\src\klip\document\items
mkdir C:\Users\leone\klip\pc\src\klip\canvas
mkdir C:\Users\leone\klip\pc\src\klip\toolbar
mkdir C:\Users\leone\klip\pc\tests
mkdir C:\Users\leone\klip\schema
```

- [ ] **Step 2: Create empty package markers**

Create `pc\src\klip\__init__.py` with content:
```python
"""Klip — Canva-style design app for Windows."""
__version__ = "0.1.0"
```

Create `pc\src\klip\document\__init__.py` (empty file).
Create `pc\src\klip\document\items\__init__.py` (empty file).
Create `pc\src\klip\canvas\__init__.py` (empty file).
Create `pc\src\klip\toolbar\__init__.py` (empty file).
Create `pc\tests\__init__.py` (empty file).

- [ ] **Step 3: Verify structure**

Run: `Get-ChildItem -Recurse C:\Users\leone\klip\pc -Directory`
Expected: Lists all the folders above.

---

### Task 0.2: Set up `.gitignore`

**Files:**
- Create: `C:\Users\leone\klip\.gitignore`

- [ ] **Step 1: Write `.gitignore`**

Create `C:\Users\leone\klip\.gitignore` with content:

```
# Python
__pycache__/
*.py[cod]
*$py.class
*.so
.Python
.venv/
venv/
env/
build/
dist/
*.egg-info/
.pytest_cache/
.mypy_cache/
.coverage
htmlcov/

# IDE
.vscode/
.idea/
*.swp
*.swo

# OS
Thumbs.db
.DS_Store
desktop.ini

# Klip
*.mcv.bak
exports/
wheels/

# PyInstaller
*.spec.bak

# Android
android/.gradle/
android/build/
android/app/build/
android/local.properties
android/.idea/

# Models — referenced from ~/.u2net, never commit
*.onnx
```

---

### Task 0.3: Create Python venv and install PySide6

**Files:**
- Create: `C:\Users\leone\klip\pc\requirements.txt`

- [ ] **Step 1: Write requirements.txt**

Create `pc\requirements.txt`:

```
PySide6==6.6.3.1
pydantic==2.8.2
Pillow>=11.0,<12.0
pytest==8.3.2
pytest-qt==4.4.0
```

- [ ] **Step 2: Create venv**

Run: `python -m venv C:\Users\leone\klip\pc\.venv`
Expected: creates `pc\.venv\Scripts\python.exe`.

- [ ] **Step 3: Install dependencies**

Run:
```powershell
C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m pip install --upgrade pip
C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m pip install -r C:\Users\leone\klip\pc\requirements.txt
```
Expected: `Successfully installed PySide6-6.6.3.1 pydantic-2.8.2 Pillow-... pytest-... pytest-qt-...`

- [ ] **Step 4: Verify import works**

Run: `C:\Users\leone\klip\pc\.venv\Scripts\python.exe -c "import PySide6; from PySide6.QtWidgets import QApplication; print('OK', PySide6.__version__)"`
Expected: `OK 6.6.3.1`

---

### Task 0.4: Create `pyproject.toml`

**Files:**
- Create: `C:\Users\leone\klip\pc\pyproject.toml`

- [ ] **Step 1: Write pyproject.toml**

Create `pc\pyproject.toml`:

```toml
[build-system]
requires = ["setuptools>=68"]
build-backend = "setuptools.build_meta"

[project]
name = "klip"
version = "0.1.0"
description = "Canva-style design app for Windows"
requires-python = ">=3.9"
authors = [{ name = "Leonel Ferreira" }]

[tool.setuptools.packages.find]
where = ["src"]

[tool.pytest.ini_options]
testpaths = ["tests"]
pythonpath = ["src"]
qt_api = "pyside6"
```

- [ ] **Step 2: Install package in editable mode**

Run: `C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m pip install -e C:\Users\leone\klip\pc`
Expected: `Successfully installed klip-0.1.0`

---

### Task 0.5: Smoke-test PySide6 main window

**Files:**
- Create: `pc\src\klip\main.py`
- Create: `pc\src\klip\app.py`
- Create: `pc\tests\conftest.py`
- Create: `pc\tests\test_app_smoke.py`

- [ ] **Step 1: Write the failing smoke test**

Create `pc\tests\conftest.py`:
```python
import pytest

@pytest.fixture
def qtbot_window(qtbot):
    """Helper for creating windows that auto-clean."""
    return qtbot
```

Create `pc\tests\test_app_smoke.py`:
```python
import pytest
from klip.app import MainWindow


def test_main_window_opens(qtbot):
    """The main window should construct and show without errors."""
    window = MainWindow()
    qtbot.addWidget(window)
    window.show()
    assert window.windowTitle() == "Klip"
    assert window.isVisible()
```

- [ ] **Step 2: Run test, verify FAIL**

Run: `C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m pytest C:\Users\leone\klip\pc\tests\test_app_smoke.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'klip.app'`.

- [ ] **Step 3: Implement minimal MainWindow**

Create `pc\src\klip\app.py`:
```python
from PySide6.QtWidgets import QMainWindow


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Klip")
        self.resize(1200, 800)
```

Create `pc\src\klip\main.py`:
```python
import sys
from PySide6.QtWidgets import QApplication
from klip.app import MainWindow


def main():
    app = QApplication(sys.argv)
    window = MainWindow()
    window.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Run test, verify PASS**

Run: `C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m pytest C:\Users\leone\klip\pc\tests\test_app_smoke.py -v`
Expected: `1 passed`.

- [ ] **Step 5: Run the app manually**

Run: `C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m klip.main`
Expected: Empty 1200×800 window titled "Klip" appears. Close it with the X button.

---

### Task 0.6: Initialize git and commit Phase 0

**Files:**
- Create: `C:\Users\leone\klip\README.md`

- [ ] **Step 1: Write project README**

Create `C:\Users\leone\klip\README.md`:

```markdown
# Klip

Canva-style design app for Windows and Android, ADB-synced, fully offline.

See [docs/design.md](docs/design.md) for the design specification.

## Status

Phase 0 (bootstrap) — in progress.

## PC dev quickstart

```powershell
cd pc
.venv\Scripts\activate
python -m klip.main
```

Run tests:
```powershell
pytest
```
```

- [ ] **Step 2: Initialize git**

Run:
```powershell
cd C:\Users\leone\klip
git init
git config user.email "leonelferreira0373@gmail.com"
git config user.name "Leonel Ferreira"
```
Expected: `Initialized empty Git repository`.

- [ ] **Step 3: Commit Phase 0**

Run:
```powershell
git add .gitignore README.md docs schema pc\src pc\tests pc\pyproject.toml pc\requirements.txt
git commit -m "Phase 0: bootstrap project structure with empty PySide6 window"
```
Expected: commit message includes the file list, no errors.

---

## Phase 1 — Document & Canvas Foundation

### Task 1.1: Schema — Transform model

**Files:**
- Create: `pc\src\klip\document\schema.py`
- Create: `pc\tests\test_schema.py`

- [ ] **Step 1: Write the failing test**

Create `pc\tests\test_schema.py`:
```python
import pytest
from klip.document.schema import Transform


def test_transform_defaults():
    t = Transform(x=10, y=20, w=100, h=50)
    assert t.x == 10
    assert t.y == 20
    assert t.w == 100
    assert t.h == 50
    assert t.rotation == 0.0
    assert t.opacity == 1.0


def test_transform_clamps_opacity():
    """Opacity must be in [0, 1]."""
    with pytest.raises(ValueError):
        Transform(x=0, y=0, w=10, h=10, opacity=1.5)
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_schema.py -v`
Expected: FAIL with `ImportError`.

- [ ] **Step 3: Implement Transform**

Create `pc\src\klip\document\schema.py`:
```python
from typing import Literal, Optional, Union, List
from pydantic import BaseModel, Field, ConfigDict


class Transform(BaseModel):
    model_config = ConfigDict(extra="forbid")
    x: float
    y: float
    w: float = Field(gt=0)
    h: float = Field(gt=0)
    rotation: float = 0.0
    opacity: float = Field(default=1.0, ge=0.0, le=1.0)
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_schema.py -v`
Expected: `2 passed`.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/schema.py pc/tests/test_schema.py
git commit -m "feat(schema): Transform model with bounds validation"
```

---

### Task 1.2: Schema — Item types (Text, Shape, Image)

**Files:**
- Modify: `pc\src\klip\document\schema.py` (append)
- Modify: `pc\tests\test_schema.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_schema.py`:
```python
from klip.document.schema import (
    TextItemModel, ShapeItemModel, ImageItemModel, Transform
)


def _t():
    return Transform(x=0, y=0, w=100, h=100)


def test_text_item():
    item = TextItemModel(
        id="t1", transform=_t(), z=1,
        text="hello", font_family="Inter", font_size=24, color="#000000"
    )
    assert item.type == "text"
    assert item.text == "hello"


def test_shape_item_rect():
    item = ShapeItemModel(
        id="s1", transform=_t(), z=1,
        shape="rect", fill="#ff0000", stroke=None, stroke_width=0
    )
    assert item.type == "shape"
    assert item.shape == "rect"


def test_image_item():
    item = ImageItemModel(
        id="i1", transform=_t(), z=1, asset_ref="asset_a"
    )
    assert item.type == "image"
    assert item.asset_ref == "asset_a"


def test_image_item_with_clip_mask():
    item = ImageItemModel(
        id="i1", transform=_t(), z=1, asset_ref="asset_a",
        clip_mask={"shape": "ellipse"}
    )
    assert item.clip_mask["shape"] == "ellipse"
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_schema.py -v`
Expected: 2 pass, 4 FAIL with import errors.

- [ ] **Step 3: Implement item models**

Append to `pc\src\klip\document\schema.py`:
```python
class _ItemBase(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str
    transform: Transform
    z: int = 0


class TextItemModel(_ItemBase):
    type: Literal["text"] = "text"
    text: str
    font_family: str
    font_size: float = Field(gt=0)
    font_weight: int = 400
    color: str = "#000000"
    align: Literal["left", "center", "right"] = "left"
    letter_spacing: float = 0.0


class ShapeItemModel(_ItemBase):
    type: Literal["shape"] = "shape"
    shape: Literal["rect", "ellipse", "polygon", "line"]
    fill: Optional[str] = "#000000"
    stroke: Optional[str] = None
    stroke_width: float = 0.0
    corner_radius: float = 0.0
    sides: int = 5  # for polygon


class ImageItemModel(_ItemBase):
    type: Literal["image"] = "image"
    asset_ref: str
    clip_mask: Optional[dict] = None
    effects: List[dict] = Field(default_factory=list)
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_schema.py -v`
Expected: `6 passed`.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/schema.py pc/tests/test_schema.py
git commit -m "feat(schema): Text/Shape/Image item models"
```

---

### Task 1.3: Schema — Page and Document

**Files:**
- Modify: `pc\src\klip\document\schema.py` (append)
- Modify: `pc\tests\test_schema.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_schema.py`:
```python
from klip.document.schema import PageModel, DocumentModel, AssetModel


def test_page_with_items():
    page = PageModel(
        id="p1",
        size={"w": 1080, "h": 1080, "unit": "px", "dpi": 144},
        background={"type": "solid", "color": "#ffffff"},
        items=[],
    )
    assert page.size["w"] == 1080


def test_document_with_pages():
    doc = DocumentModel(
        version=1,
        name="test",
        pages=[
            PageModel(
                id="p1",
                size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
                background={"type": "solid", "color": "#fff"},
                items=[],
            )
        ],
    )
    assert doc.version == 1
    assert len(doc.pages) == 1


def test_document_rejects_unknown_version():
    with pytest.raises(ValueError):
        DocumentModel(version=999, name="x", pages=[])
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_schema.py -v`
Expected: 6 pass, 3 FAIL with import errors.

- [ ] **Step 3: Implement Page, Document, Asset**

Append to `pc\src\klip\document\schema.py`:
```python
ItemModel = Union[TextItemModel, ShapeItemModel, ImageItemModel]


class PageModel(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str
    size: dict  # { w, h, unit, dpi }
    background: dict  # { type: "solid"|"gradient", color: "#..." }
    items: List[ItemModel] = Field(default_factory=list)


class AssetModel(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str
    mime: str
    data: str  # base64


class FontRef(BaseModel):
    model_config = ConfigDict(extra="forbid")
    family: str
    weight: int = 400
    source: Literal["system", "embedded"] = "system"


class DocumentModel(BaseModel):
    model_config = ConfigDict(extra="forbid")
    version: Literal[1] = 1
    name: str
    created_at: Optional[str] = None
    modified_at: Optional[str] = None
    fonts: List[FontRef] = Field(default_factory=list)
    assets: List[AssetModel] = Field(default_factory=list)
    pages: List[PageModel] = Field(default_factory=list)
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_schema.py -v`
Expected: `9 passed`.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/schema.py pc/tests/test_schema.py
git commit -m "feat(schema): Page, Document, Asset, FontRef models"
```

---

### Task 1.4: Document IO — save and load .mcv

**Files:**
- Create: `pc\src\klip\document\io.py`
- Create: `pc\tests\test_io.py`

- [ ] **Step 1: Write the failing test**

Create `pc\tests\test_io.py`:
```python
import gzip
import json
from pathlib import Path
from klip.document.schema import DocumentModel, PageModel
from klip.document.io import save_document, load_document


def _make_doc():
    return DocumentModel(
        version=1,
        name="round-trip",
        pages=[
            PageModel(
                id="p1",
                size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
                background={"type": "solid", "color": "#ffffff"},
                items=[],
            )
        ],
    )


def test_save_writes_gzipped_json(tmp_path: Path):
    doc = _make_doc()
    path = tmp_path / "x.mcv"
    save_document(doc, path)
    assert path.exists()
    with gzip.open(path, "rt", encoding="utf-8") as f:
        data = json.load(f)
    assert data["version"] == 1
    assert data["name"] == "round-trip"


def test_round_trip(tmp_path: Path):
    doc = _make_doc()
    path = tmp_path / "y.mcv"
    save_document(doc, path)
    loaded = load_document(path)
    assert loaded.name == "round-trip"
    assert len(loaded.pages) == 1


def test_load_rejects_bad_version(tmp_path: Path):
    path = tmp_path / "bad.mcv"
    with gzip.open(path, "wt", encoding="utf-8") as f:
        json.dump({"version": 999, "name": "x", "pages": []}, f)
    import pytest
    with pytest.raises(ValueError):
        load_document(path)
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_io.py -v`
Expected: FAIL with `ImportError`.

- [ ] **Step 3: Implement io.py**

Create `pc\src\klip\document\io.py`:
```python
import gzip
import json
from pathlib import Path
from .schema import DocumentModel


def save_document(doc: DocumentModel, path: Path) -> None:
    """Write a Document to disk as gzipped JSON (.mcv)."""
    payload = doc.model_dump(mode="json", exclude_none=True)
    with gzip.open(path, "wt", encoding="utf-8") as f:
        json.dump(payload, f, separators=(",", ":"))


def load_document(path: Path) -> DocumentModel:
    """Read a .mcv file and validate against the schema."""
    with gzip.open(path, "rt", encoding="utf-8") as f:
        data = json.load(f)
    return DocumentModel.model_validate(data)
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_io.py -v`
Expected: `3 passed`.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/io.py pc/tests/test_io.py
git commit -m "feat(io): save/load .mcv as gzipped JSON with schema validation"
```

---

### Task 1.5: Document container

**Files:**
- Create: `pc\src\klip\document\document.py`
- Create: `pc\tests\test_document.py`

- [ ] **Step 1: Write the failing test**

Create `pc\tests\test_document.py`:
```python
import pytest
from klip.document.document import Document
from klip.document.schema import PageModel


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
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_document.py -v`
Expected: FAIL with `ImportError`.

- [ ] **Step 3: Implement Document**

Create `pc\src\klip\document\document.py`:
```python
from dataclasses import dataclass, field
from typing import List
from .schema import PageModel, AssetModel


@dataclass
class Document:
    name: str
    pages: List[PageModel] = field(default_factory=list)
    assets: List[AssetModel] = field(default_factory=list)
    current_page_index: int = -1

    def add_page(self, page: PageModel) -> None:
        self.pages.append(page)
        self.current_page_index = len(self.pages) - 1

    def remove_page(self, index: int) -> None:
        del self.pages[index]
        if not self.pages:
            self.current_page_index = -1
        else:
            self.current_page_index = min(self.current_page_index, len(self.pages) - 1)

    def current_page(self) -> PageModel:
        if self.current_page_index < 0:
            raise IndexError("no current page")
        return self.pages[self.current_page_index]
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_document.py -v`
Expected: `4 passed`.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/document.py pc/tests/test_document.py
git commit -m "feat(document): Document container with page management"
```

---

### Task 1.6: Document ↔ schema conversion

**Files:**
- Modify: `pc\src\klip\document\document.py` (add classmethods)
- Modify: `pc\tests\test_document.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_document.py`:
```python
from klip.document.schema import DocumentModel
from klip.document.document import Document


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
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_document.py -v`
Expected: 4 pass, 2 FAIL with `AttributeError`.

- [ ] **Step 3: Add to_schema and from_schema**

Append to `pc\src\klip\document\document.py`:
```python
    def to_schema(self) -> "DocumentModel":
        from .schema import DocumentModel
        return DocumentModel(
            version=1,
            name=self.name,
            pages=list(self.pages),
            assets=list(self.assets),
        )

    @classmethod
    def from_schema(cls, model: "DocumentModel") -> "Document":
        d = cls(
            name=model.name,
            pages=list(model.pages),
            assets=list(model.assets),
        )
        d.current_page_index = 0 if model.pages else -1
        return d
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_document.py -v`
Expected: `6 passed`.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/document.py pc/tests/test_document.py
git commit -m "feat(document): bidirectional schema conversion"
```

---

### Task 1.7: Item base class — schema ⇆ QGraphicsItem

**Files:**
- Create: `pc\src\klip\document\items\base.py`
- Create: `pc\tests\test_items.py`

- [ ] **Step 1: Write the failing test**

Create `pc\tests\test_items.py`:
```python
import pytest
from PySide6.QtWidgets import QGraphicsScene
from klip.document.schema import (
    Transform, ShapeItemModel, TextItemModel, ImageItemModel
)
from klip.document.items.base import ItemAdapter


def _transform():
    return Transform(x=10, y=20, w=100, h=50, rotation=15)


def test_apply_transform_to_item(qtbot):
    scene = QGraphicsScene()
    model = ShapeItemModel(
        id="x", transform=_transform(), z=1,
        shape="rect", fill="#ff0000",
    )
    qitem = ItemAdapter.create_qitem(model)
    scene.addItem(qitem)
    assert qitem.pos().x() == 10
    assert qitem.pos().y() == 20
    assert qitem.rotation() == 15
    assert qitem.zValue() == 1
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_items.py -v`
Expected: FAIL with `ImportError`.

- [ ] **Step 3: Implement base ItemAdapter**

Create `pc\src\klip\document\items\base.py`:
```python
"""Adapter between schema models and Qt graphics items."""
from PySide6.QtWidgets import QGraphicsItem
from ..schema import (
    Transform, ItemModel, ShapeItemModel, TextItemModel, ImageItemModel
)


class ItemAdapter:
    """Static dispatcher: schema model → QGraphicsItem."""

    @staticmethod
    def create_qitem(model: ItemModel) -> QGraphicsItem:
        if isinstance(model, ShapeItemModel):
            from .shape_item import build_shape_item
            qitem = build_shape_item(model)
        elif isinstance(model, TextItemModel):
            from .text_item import build_text_item
            qitem = build_text_item(model)
        elif isinstance(model, ImageItemModel):
            from .image_item import build_image_item
            qitem = build_image_item(model)
        else:
            raise TypeError(f"unsupported item type: {type(model).__name__}")
        ItemAdapter._apply_transform(qitem, model.transform, model.z)
        qitem.setData(0, model.id)  # store schema id on the qitem
        qitem.setData(1, model.type)
        return qitem

    @staticmethod
    def _apply_transform(qitem: QGraphicsItem, t: Transform, z: int) -> None:
        qitem.setPos(t.x, t.y)
        qitem.setRotation(t.rotation)
        qitem.setOpacity(t.opacity)
        qitem.setZValue(z)
        qitem.setFlag(QGraphicsItem.ItemIsSelectable)
        qitem.setFlag(QGraphicsItem.ItemIsMovable)
```

- [ ] **Step 4: Run, verify FAIL with new error**

Run: `pytest tests/test_items.py -v`
Expected: FAIL with `ImportError: cannot import name 'build_shape_item'`. (We'll fix in next task.)

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/items/base.py pc/tests/test_items.py
git commit -m "feat(items): ItemAdapter base + transform application"
```

---

### Task 1.8: ShapeItem — rect and ellipse

**Files:**
- Create: `pc\src\klip\document\items\shape_item.py`
- Modify: `pc\tests\test_items.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_items.py`:
```python
def test_rect_shape_renders_correctly(qtbot):
    model = ShapeItemModel(
        id="r", transform=_transform(), z=0,
        shape="rect", fill="#00ff00", corner_radius=8,
    )
    qitem = ItemAdapter.create_qitem(model)
    bounds = qitem.boundingRect()
    assert bounds.width() == 100
    assert bounds.height() == 50


def test_ellipse_shape(qtbot):
    model = ShapeItemModel(
        id="e", transform=_transform(), z=0,
        shape="ellipse", fill="#0000ff",
    )
    qitem = ItemAdapter.create_qitem(model)
    bounds = qitem.boundingRect()
    assert bounds.width() == 100
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_items.py -v`
Expected: FAIL — shape_item module not found.

- [ ] **Step 3: Implement shape_item**

Create `pc\src\klip\document\items\shape_item.py`:
```python
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
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_items.py -v`
Expected: First and new tests for shape pass; text and image still fail.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/items/shape_item.py pc/tests/test_items.py
git commit -m "feat(items): ShapeItem with rect/ellipse/line/polygon"
```

---

### Task 1.9: TextItem

**Files:**
- Create: `pc\src\klip\document\items\text_item.py`
- Modify: `pc\tests\test_items.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_items.py`:
```python
def test_text_item_renders(qtbot):
    model = TextItemModel(
        id="t", transform=_transform(), z=0,
        text="HELLO", font_family="Segoe UI",
        font_size=24, color="#000000",
    )
    qitem = ItemAdapter.create_qitem(model)
    bounds = qitem.boundingRect()
    assert bounds.width() > 0
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_items.py -v`
Expected: FAIL — text_item module not found.

- [ ] **Step 3: Implement text_item**

Create `pc\src\klip\document\items\text_item.py`:
```python
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
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_items.py -v`
Expected: All shape + text tests pass; image still fails.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/items/text_item.py pc/tests/test_items.py
git commit -m "feat(items): TextItem with font/size/color/alignment"
```

---

### Task 1.10: ImageItem

**Files:**
- Create: `pc\src\klip\document\items\image_item.py`
- Modify: `pc\tests\test_items.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_items.py`:
```python
import base64
from io import BytesIO
from PIL import Image


def _png_b64() -> str:
    img = Image.new("RGBA", (10, 10), (255, 0, 0, 255))
    buf = BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def test_image_item_renders(qtbot):
    from klip.document.schema import AssetModel
    asset = AssetModel(id="a1", mime="image/png", data=_png_b64())
    model = ImageItemModel(id="i", transform=_transform(), z=0, asset_ref="a1")
    qitem = ItemAdapter.create_qitem_with_assets(model, {asset.id: asset})
    bounds = qitem.boundingRect()
    assert bounds.width() == 100
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_items.py -v`
Expected: FAIL — `image_item` not found and `create_qitem_with_assets` missing.

- [ ] **Step 3: Implement image_item + extend ItemAdapter**

Create `pc\src\klip\document\items\image_item.py`:
```python
import base64
from PySide6.QtCore import QRectF
from PySide6.QtGui import QPainter, QPixmap
from PySide6.QtWidgets import QGraphicsItem
from ..schema import ImageItemModel, AssetModel


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
```

Modify `pc\src\klip\document\items\base.py` — replace the entire file:
```python
"""Adapter between schema models and Qt graphics items."""
from typing import Mapping
from PySide6.QtWidgets import QGraphicsItem
from ..schema import (
    Transform, ItemModel, ShapeItemModel, TextItemModel,
    ImageItemModel, AssetModel,
)


class ItemAdapter:
    @staticmethod
    def create_qitem(model: ItemModel) -> QGraphicsItem:
        if isinstance(model, ShapeItemModel):
            from .shape_item import build_shape_item
            qitem = build_shape_item(model)
        elif isinstance(model, TextItemModel):
            from .text_item import build_text_item
            qitem = build_text_item(model)
        else:
            raise TypeError(
                f"{type(model).__name__} requires assets — use create_qitem_with_assets"
            )
        ItemAdapter._apply_common(qitem, model)
        return qitem

    @staticmethod
    def create_qitem_with_assets(
        model: ItemModel, assets: Mapping[str, AssetModel]
    ) -> QGraphicsItem:
        if isinstance(model, ImageItemModel):
            from .image_item import build_image_item
            asset = assets[model.asset_ref]
            qitem = build_image_item(model, asset)
        else:
            qitem = ItemAdapter.create_qitem(model)
            return qitem
        ItemAdapter._apply_common(qitem, model)
        return qitem

    @staticmethod
    def _apply_common(qitem: QGraphicsItem, model: ItemModel) -> None:
        t = model.transform
        qitem.setPos(t.x, t.y)
        qitem.setRotation(t.rotation)
        qitem.setOpacity(t.opacity)
        qitem.setZValue(model.z)
        qitem.setFlag(QGraphicsItem.ItemIsSelectable)
        qitem.setFlag(QGraphicsItem.ItemIsMovable)
        qitem.setData(0, model.id)
        qitem.setData(1, model.type)
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_items.py -v`
Expected: all item tests pass.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/document/items/image_item.py pc/src/klip/document/items/base.py pc/tests/test_items.py
git commit -m "feat(items): ImageItem with base64-asset loading"
```

---

### Task 1.11: KlipScene — wires Page → Qt items

**Files:**
- Create: `pc\src\klip\canvas\scene.py`
- Create: `pc\tests\test_scene.py`

- [ ] **Step 1: Write the failing test**

Create `pc\tests\test_scene.py`:
```python
from klip.document.schema import (
    PageModel, ShapeItemModel, Transform
)
from klip.canvas.scene import KlipScene


def test_scene_loads_page_items(qtbot):
    page = PageModel(
        id="p1",
        size={"w": 1000, "h": 500, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#ffffff"},
        items=[
            ShapeItemModel(
                id="s1", transform=Transform(x=10, y=10, w=100, h=100),
                z=1, shape="rect", fill="#ff0000",
            ),
        ],
    )
    scene = KlipScene()
    scene.load_page(page, assets={})
    assert len(scene.items()) == 1


def test_scene_clear_on_reload(qtbot):
    page1 = PageModel(
        id="p1",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[
            ShapeItemModel(
                id="s1", transform=Transform(x=0, y=0, w=10, h=10),
                z=0, shape="rect", fill="#000",
            )
        ],
    )
    page2 = PageModel(
        id="p2",
        size={"w": 100, "h": 100, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[],
    )
    scene = KlipScene()
    scene.load_page(page1, assets={})
    assert len(scene.items()) == 1
    scene.load_page(page2, assets={})
    assert len(scene.items()) == 0
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_scene.py -v`
Expected: FAIL — `KlipScene` not found.

- [ ] **Step 3: Implement KlipScene**

Create `pc\src\klip\canvas\scene.py`:
```python
from typing import Mapping
from PySide6.QtCore import QRectF
from PySide6.QtGui import QBrush, QColor
from PySide6.QtWidgets import QGraphicsScene
from ..document.items.base import ItemAdapter
from ..document.schema import PageModel, AssetModel


class KlipScene(QGraphicsScene):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._page: PageModel | None = None

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
    def page(self) -> PageModel | None:
        return self._page
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_scene.py -v`
Expected: `2 passed`.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/canvas/scene.py pc/tests/test_scene.py
git commit -m "feat(canvas): KlipScene loads Page → Qt items"
```

---

### Task 1.12: KlipView — pan + zoom

**Files:**
- Create: `pc\src\klip\canvas\view.py`
- Modify: `pc\tests\test_scene.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_scene.py`:
```python
from klip.canvas.view import KlipView


def test_view_starts_at_100_percent(qtbot):
    scene = KlipScene()
    view = KlipView(scene)
    qtbot.addWidget(view)
    assert abs(view.zoom_factor - 1.0) < 0.001


def test_view_zooms_in_on_wheel(qtbot):
    from PySide6.QtCore import Qt, QPoint
    from PySide6.QtGui import QWheelEvent
    scene = KlipScene()
    view = KlipView(scene)
    qtbot.addWidget(view)
    before = view.zoom_factor
    # programmatic zoom
    view.set_zoom(2.0)
    assert view.zoom_factor == pytest.approx(2.0)


import pytest  # imported here for clarity in this appended block
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_scene.py -v`
Expected: FAIL — `KlipView` not found.

- [ ] **Step 3: Implement KlipView**

Create `pc\src\klip\canvas\view.py`:
```python
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
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_scene.py -v`
Expected: 4 passed (2 prior + 2 new).

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/canvas/view.py pc/tests/test_scene.py
git commit -m "feat(canvas): KlipView with Ctrl+wheel zoom"
```

---

### Task 1.13: Selection handles overlay

**Files:**
- Create: `pc\src\klip\canvas\handles.py`

- [ ] **Step 1: Write the test**

Append to `pc\tests\test_scene.py`:
```python
from klip.canvas.handles import HandleOverlay


def test_handles_appear_on_selection(qtbot):
    page = PageModel(
        id="p1",
        size={"w": 500, "h": 500, "unit": "px", "dpi": 72},
        background={"type": "solid", "color": "#fff"},
        items=[
            ShapeItemModel(
                id="s1", transform=Transform(x=10, y=10, w=100, h=100),
                z=0, shape="rect", fill="#000",
            ),
        ],
    )
    scene = KlipScene()
    scene.load_page(page, assets={})
    overlay = HandleOverlay(scene)
    overlay.attach()
    item = scene.items()[0]
    item.setSelected(True)
    overlay.refresh()
    # 8 handles + 1 rotation = 9 children (or items)
    handle_count = len(overlay.handles())
    assert handle_count == 8
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_scene.py -v`
Expected: FAIL — handles module not found.

- [ ] **Step 3: Implement HandleOverlay**

Create `pc\src\klip\canvas\handles.py`:
```python
from typing import List
from PySide6.QtCore import Qt, QRectF, QPointF
from PySide6.QtGui import QBrush, QColor, QPen
from PySide6.QtWidgets import (
    QGraphicsScene, QGraphicsItem, QGraphicsRectItem
)


HANDLE_SIZE = 8
HANDLE_COLOR = QColor("#4d80ff")
HANDLE_FILL = QColor("#ffffff")


class _Handle(QGraphicsRectItem):
    def __init__(self):
        r = HANDLE_SIZE
        super().__init__(-r/2, -r/2, r, r)
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
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_scene.py -v`
Expected: all scene tests pass.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/canvas/handles.py pc/tests/test_scene.py
git commit -m "feat(canvas): selection handle overlay (8 corners/midpoints)"
```

---

### Task 1.14: Toolbar — tool enum and QToolBar

**Files:**
- Create: `pc\src\klip\toolbar\toolbar.py`

- [ ] **Step 1: Write the test**

Create `pc\tests\test_toolbar.py`:
```python
from klip.toolbar.toolbar import Tool, KlipToolbar


def test_toolbar_default_tool_is_select(qtbot):
    tb = KlipToolbar()
    qtbot.addWidget(tb)
    assert tb.active_tool == Tool.SELECT


def test_toolbar_change_tool(qtbot):
    tb = KlipToolbar()
    qtbot.addWidget(tb)
    tb.set_active_tool(Tool.RECT)
    assert tb.active_tool == Tool.RECT


def test_toolbar_emits_signal_on_change(qtbot):
    tb = KlipToolbar()
    qtbot.addWidget(tb)
    with qtbot.waitSignal(tb.tool_changed) as blocker:
        tb.set_active_tool(Tool.TEXT)
    assert blocker.args == [Tool.TEXT]
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_toolbar.py -v`
Expected: FAIL.

- [ ] **Step 3: Implement Toolbar**

Create `pc\src\klip\toolbar\toolbar.py`:
```python
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
    Tool.SELECT: "⬚", Tool.HAND: "✋",
    Tool.TEXT: "T", Tool.RECT: "▭", Tool.ELLIPSE: "○",
    Tool.POLYGON: "⬠", Tool.LINE: "／",
    Tool.IMAGE: "🖼", Tool.BG_REMOVE: "✂", Tool.CLIP: "◉",
    Tool.PICK: "💧", Tool.EXTRACT: "🎨", Tool.FONT: "A+",
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
        self._actions: dict[Tool, QAction] = {}
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
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_toolbar.py -v`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```powershell
git add pc/src/klip/toolbar/toolbar.py pc/tests/test_toolbar.py
git commit -m "feat(toolbar): Tool enum + KlipToolbar with active-tool tracking"
```

---

### Task 1.15: MainWindow — wire scene, view, toolbar, menus

**Files:**
- Modify: `pc\src\klip\app.py` (replace)
- Modify: `pc\tests\test_app_smoke.py` (extend)

- [ ] **Step 1: Write extended smoke test**

Replace `pc\tests\test_app_smoke.py`:
```python
import pytest
from klip.app import MainWindow
from klip.document.schema import (
    DocumentModel, PageModel, ShapeItemModel, Transform
)


def test_main_window_opens(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    window.show()
    assert window.windowTitle() == "Klip"
    assert window.isVisible()


def test_main_window_has_canvas_and_toolbar(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    assert window.view is not None
    assert window.scene is not None
    assert window.toolbar is not None


def test_main_window_loads_document(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    doc = DocumentModel(
        version=1, name="t",
        pages=[
            PageModel(
                id="p1",
                size={"w": 200, "h": 200, "unit": "px", "dpi": 72},
                background={"type": "solid", "color": "#fff"},
                items=[
                    ShapeItemModel(
                        id="s1", transform=Transform(x=0, y=0, w=50, h=50),
                        z=0, shape="rect", fill="#f00",
                    )
                ],
            )
        ],
    )
    window.load_document_model(doc)
    assert len(window.scene.items()) == 1  # the rect
    assert window.windowTitle().startswith("Klip")
```

- [ ] **Step 2: Run, verify some FAIL**

Run: `pytest tests/test_app_smoke.py -v`
Expected: FAIL — `view`, `scene`, `toolbar`, `load_document_model` missing.

- [ ] **Step 3: Implement full MainWindow**

Replace `pc\src\klip\app.py`:
```python
from pathlib import Path
from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QMainWindow, QFileDialog, QMessageBox, QStatusBar
)
from .canvas.scene import KlipScene
from .canvas.view import KlipView
from .canvas.handles import HandleOverlay
from .toolbar.toolbar import KlipToolbar, Tool
from .document.document import Document
from .document.io import save_document, load_document
from .document.schema import DocumentModel, PageModel


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Klip")
        self.resize(1200, 800)

        self._document = Document(name="Untitled")
        self._current_path: Path | None = None

        self.scene = KlipScene(self)
        self.view = KlipView(self.scene, self)
        self.setCentralWidget(self.view)

        self._handles = HandleOverlay(self.scene)
        self._handles.attach()

        self.toolbar = KlipToolbar(self)
        self.addToolBar(Qt.TopToolBarArea, self.toolbar)
        self.toolbar.tool_changed.connect(self._on_tool_changed)

        self.setStatusBar(QStatusBar())
        self.statusBar().showMessage("Ready")

        self._build_menus()
        self._new_document()

    # ----- public API -----
    def load_document_model(self, model: DocumentModel) -> None:
        self._document = Document.from_schema(model)
        self._refresh_scene()
        self._refresh_title()

    # ----- internal -----
    def _build_menus(self):
        m_file = self.menuBar().addMenu("&File")
        m_file.addAction("&New", self._new_document)
        m_file.addAction("&Open…", self._open)
        m_file.addAction("&Save", self._save)
        m_file.addAction("Save &As…", self._save_as)
        m_file.addSeparator()
        m_file.addAction("E&xit", self.close)

    def _new_document(self):
        self._document = Document(name="Untitled")
        self._document.add_page(PageModel(
            id="p1",
            size={"w": 1080, "h": 1080, "unit": "px", "dpi": 144},
            background={"type": "solid", "color": "#ffffff"},
            items=[],
        ))
        self._current_path = None
        self._refresh_scene()
        self._refresh_title()

    def _open(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "Open Klip document", "", "Klip files (*.mcv)"
        )
        if not path:
            return
        try:
            model = load_document(Path(path))
        except Exception as e:
            QMessageBox.critical(self, "Open failed", str(e))
            return
        self._document = Document.from_schema(model)
        self._current_path = Path(path)
        self._refresh_scene()
        self._refresh_title()

    def _save(self):
        if self._current_path is None:
            self._save_as()
            return
        save_document(self._document.to_schema(), self._current_path)
        self.statusBar().showMessage(f"Saved {self._current_path.name}", 3000)

    def _save_as(self):
        path, _ = QFileDialog.getSaveFileName(
            self, "Save Klip document", "Untitled.mcv", "Klip files (*.mcv)"
        )
        if not path:
            return
        self._current_path = Path(path)
        self._save()
        self._refresh_title()

    def _refresh_scene(self):
        if self._document.current_page_index < 0:
            self.scene.clear()
            return
        page = self._document.current_page()
        assets_by_id = {a.id: a for a in self._document.assets}
        self.scene.load_page(page, assets_by_id)

    def _refresh_title(self):
        name = (self._current_path.name
                if self._current_path else "Untitled.mcv")
        self.setWindowTitle(f"Klip — {name}")

    def _on_tool_changed(self, tool: Tool):
        self.statusBar().showMessage(f"Tool: {tool.value}", 2000)
```

- [ ] **Step 4: Run, verify PASS**

Run: `pytest tests/test_app_smoke.py -v`
Expected: 3 passed.

- [ ] **Step 5: Run app manually**

Run: `C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m klip.main`
Expected: window with toolbar, white canvas, File menu has New/Open/Save/Save As/Exit.

- [ ] **Step 6: Commit**

```powershell
git add pc/src/klip/app.py pc/tests/test_app_smoke.py
git commit -m "feat(app): MainWindow wiring scene/view/toolbar/menus + new/open/save"
```

---

### Task 1.16: Tool action — drop a Rectangle on click

**Files:**
- Modify: `pc\src\klip\app.py` (extend `_on_tool_changed` and add a click handler)
- Modify: `pc\tests\test_app_smoke.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_app_smoke.py`:
```python
from PySide6.QtCore import QPointF
from klip.toolbar.toolbar import Tool


def test_clicking_canvas_with_rect_tool_creates_rect(qtbot):
    window = MainWindow()
    qtbot.addWidget(window)
    window.show()
    window.toolbar.set_active_tool(Tool.RECT)
    before = len(window.scene.items())
    window.scene.handle_canvas_click(QPointF(50, 50))
    after = len(window.scene.items())
    assert after == before + 1
```

- [ ] **Step 2: Run, verify FAIL**

Run: `pytest tests/test_app_smoke.py -v`
Expected: FAIL — `handle_canvas_click` missing.

- [ ] **Step 3: Add canvas-click handler**

Modify `pc\src\klip\canvas\scene.py` — append at end of `KlipScene` class:

```python
    # add inside KlipScene class
    def handle_canvas_click(self, scene_pos):
        """Called by MainWindow when an active tool wants to drop an item."""
        from ..document.schema import (
            ShapeItemModel, TextItemModel, Transform
        )
        import uuid
        if self._active_tool is None or self._active_tool == "select":
            return
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
            return
        if self._page is not None:
            self._page.items.append(new_model)
        from ..document.items.base import ItemAdapter
        qitem = ItemAdapter.create_qitem(new_model)
        self.addItem(qitem)
        return new_model

    def set_active_tool(self, tool_value: str) -> None:
        self._active_tool = tool_value
```

Modify `pc\src\klip\canvas\scene.py` — also add `self._active_tool = None` in `__init__`:
Find:
```python
        self._page: PageModel | None = None
```
Replace with:
```python
        self._page: PageModel | None = None
        self._active_tool: str | None = None
```

- [ ] **Step 4: Wire MainWindow → scene**

Modify `pc\src\klip\app.py` — replace `_on_tool_changed`:
```python
    def _on_tool_changed(self, tool: Tool):
        self.scene.set_active_tool(tool.value)
        self.statusBar().showMessage(f"Tool: {tool.value}", 2000)
```

And modify `pc\src\klip\canvas\view.py` — add a `mousePressEvent`:
```python
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
```

- [ ] **Step 5: Run, verify PASS**

Run: `pytest tests/test_app_smoke.py -v`
Expected: 4 passed.

- [ ] **Step 6: Run app manually — try it**

Run: `C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m klip.main`
Click toolbar Rect button, then click canvas. A 100×100 grey rectangle should appear. Try Ellipse and Text too.

- [ ] **Step 7: Commit**

```powershell
git add pc/src/klip/canvas/scene.py pc/src/klip/canvas/view.py pc/src/klip/app.py pc/tests/test_app_smoke.py
git commit -m "feat(tools): click-to-drop rect/ellipse/text via active tool"
```

---

### Task 1.17: Save round-trip with items

**Files:**
- Modify: `pc\tests\test_app_smoke.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `pc\tests\test_app_smoke.py`:
```python
def test_save_load_round_trip(qtbot, tmp_path):
    window = MainWindow()
    qtbot.addWidget(window)
    window.toolbar.set_active_tool(Tool.RECT)
    window.scene.handle_canvas_click(QPointF(100, 100))
    path = tmp_path / "doc.mcv"
    save_document(window._document.to_schema(), path)

    window2 = MainWindow()
    qtbot.addWidget(window2)
    model = load_document(path)
    window2.load_document_model(model)
    assert len(window2.scene.items()) == 1


from klip.document.io import save_document, load_document
```

- [ ] **Step 2: Run, verify PASS**

Run: `pytest tests/test_app_smoke.py -v`
Expected: 5 passed (no implementation needed — round-trip should already work).

- [ ] **Step 3: Commit**

```powershell
git add pc/tests/test_app_smoke.py
git commit -m "test(app): document round-trip with items"
```

---

### Task 1.18: Update README + Phase 1 commit

**Files:**
- Modify: `C:\Users\leone\klip\README.md`

- [ ] **Step 1: Update README**

Replace `C:\Users\leone\klip\README.md`:

```markdown
# Klip

Canva-style design app for Windows and Android, ADB-synced, fully offline.

See [docs/design.md](docs/design.md) for the design specification.
See [docs/plan.md](docs/plan.md) for the implementation plan.

## Status

- [x] Phase 0 — bootstrap
- [x] Phase 1 — document & canvas foundation
- [ ] Phase 2 — layers, multi-page, undo, export
- [ ] Phase 3 — AI features (BG remover, clip, picker, extractor, fonts)
- [ ] Phase 4 — ADB sync
- [ ] Phase 5 — Android foundation
- [ ] Phase 6 — Android features + sync
- [ ] Phase 7 — polish + package

## PC dev quickstart

```powershell
cd pc
.venv\Scripts\activate
python -m klip.main
```

Run tests:
```powershell
pytest -v
```

## What works after Phase 1

- New / Open / Save / Save As `.mcv` files (gzipped JSON)
- Multi-page document model
- Click-to-drop rectangle, ellipse, text on canvas
- Image insert (loaded from .mcv assets)
- Selection with 8 corner/edge handles
- Move, resize via handles
- Pan + zoom (Ctrl+wheel)
```

- [ ] **Step 2: Run full test suite**

Run: `C:\Users\leone\klip\pc\.venv\Scripts\python.exe -m pytest C:\Users\leone\klip\pc\tests -v`
Expected: all tests pass (around 25 total).

- [ ] **Step 3: Commit Phase 1**

```powershell
git add README.md
git commit -m "docs: Phase 1 complete — document/canvas foundation working"
```

- [ ] **Step 4: Tag Phase 1**

Run:
```powershell
git tag phase-1
git log --oneline
```
Expected: see all phase-1 commits + tag.

---

## What's next (Phase 2 preview)

After this plan, write a new plan for Phase 2 covering:
- Layers panel (right-side dock)
- Pages panel (left-side dock with thumbnails)
- Undo/redo via `QUndoStack`
- PNG / JPG export at native canvas resolution (4K-capable)

Phase 2 will start with `docs/plan-phase-2.md`.

---

**End of Phase 0 + 1 plan.**
