from dataclasses import dataclass, field
from typing import List

from .schema import AssetModel, DocumentModel, PageModel


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

    def to_schema(self) -> DocumentModel:
        return DocumentModel(
            version=1,
            name=self.name,
            pages=list(self.pages),
            assets=list(self.assets),
        )

    @classmethod
    def from_schema(cls, model: DocumentModel) -> "Document":
        d = cls(
            name=model.name,
            pages=list(model.pages),
            assets=list(model.assets),
        )
        d.current_page_index = 0 if model.pages else -1
        return d
