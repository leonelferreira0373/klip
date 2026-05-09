"""Color picker — sample a pixel from a QImage at given coordinates."""
from typing import Optional

from PySide6.QtGui import QImage


def hex_at(img: QImage, x: int, y: int) -> Optional[str]:
    """Return '#rrggbb' for the pixel at (x, y), or None if out of bounds."""
    if x < 0 or y < 0 or x >= img.width() or y >= img.height():
        return None
    color = img.pixelColor(x, y)
    return f"#{color.red():02x}{color.green():02x}{color.blue():02x}"
