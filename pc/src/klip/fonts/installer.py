"""Per-user font installer for Windows.

Installs .ttf/.otf to %LOCALAPPDATA%\\Microsoft\\Windows\\Fonts\\ and
registers via Win32 AddFontResourceW so the font is immediately usable in
any Qt-based app without admin privileges.
"""
from __future__ import annotations

import ctypes
import os
import shutil
from pathlib import Path
from typing import List


_USER_FONTS = Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft" / "Windows" / "Fonts"
_USER_FONTS_KEY = (
    r"Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts"
)


def install_user_font(src: Path) -> Path:
    """Copy `src` font into the per-user fonts dir, register it, return new path.

    Raises FileNotFoundError if src missing or unsupported extension.
    """
    src = Path(src)
    if not src.exists():
        raise FileNotFoundError(src)
    if src.suffix.lower() not in (".ttf", ".otf"):
        raise ValueError(f"unsupported font format: {src.suffix}")

    _USER_FONTS.mkdir(parents=True, exist_ok=True)
    dst = _USER_FONTS / src.name
    if not dst.exists():
        shutil.copy2(src, dst)

    # Register the font with the running session
    gdi32 = ctypes.windll.gdi32
    res = gdi32.AddFontResourceW(str(dst))
    if res == 0:
        # The font may already be registered; not fatal.
        pass

    # Best-effort: write a per-user registry entry so the font persists across reboots
    try:
        import winreg
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, _USER_FONTS_KEY) as key:
            display = src.stem + " (TrueType)" if src.suffix.lower() == ".ttf" else src.stem + " (OpenType)"
            winreg.SetValueEx(key, display, 0, winreg.REG_SZ, str(dst))
    except Exception:
        pass

    # Notify other apps
    try:
        HWND_BROADCAST = 0xFFFF
        WM_FONTCHANGE = 0x001D
        ctypes.windll.user32.SendMessageW(HWND_BROADCAST, WM_FONTCHANGE, 0, 0)
    except Exception:
        pass

    return dst


def list_installed_user_fonts() -> List[Path]:
    if not _USER_FONTS.exists():
        return []
    return sorted(
        p for p in _USER_FONTS.iterdir()
        if p.is_file() and p.suffix.lower() in (".ttf", ".otf")
    )
