from typing import List, Literal, Optional, Union
from pydantic import BaseModel, ConfigDict, Field


class Transform(BaseModel):
    model_config = ConfigDict(extra="forbid")
    x: float
    y: float
    w: float = Field(gt=0)
    h: float = Field(gt=0)
    rotation: float = 0.0
    opacity: float = Field(default=1.0, ge=0.0, le=1.0)


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
    sides: int = 5


class ImageItemModel(_ItemBase):
    type: Literal["image"] = "image"
    asset_ref: str
    clip_mask: Optional[dict] = None
    effects: List[dict] = Field(default_factory=list)


ItemModel = Union[TextItemModel, ShapeItemModel, ImageItemModel]


class PageModel(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str
    size: dict
    background: dict
    items: List[ItemModel] = Field(default_factory=list)


class AssetModel(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str
    mime: str
    data: str


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
