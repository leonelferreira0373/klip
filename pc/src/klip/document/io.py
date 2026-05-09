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
