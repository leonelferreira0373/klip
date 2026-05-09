"""Adapter between schema models and Qt graphics items."""
from typing import Mapping

from PySide6.QtWidgets import QGraphicsItem

from ..schema import (
    AssetModel,
    ImageItemModel,
    ItemModel,
    ShapeItemModel,
    TextItemModel,
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
            ItemAdapter._apply_common(qitem, model)
            return qitem
        return ItemAdapter.create_qitem(model)

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
