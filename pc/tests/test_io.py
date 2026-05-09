import gzip
import json
from pathlib import Path

import pytest

from klip.document.io import load_document, save_document
from klip.document.schema import DocumentModel, PageModel


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
    with pytest.raises(ValueError):
        load_document(path)
