from pathlib import Path

import pytest

from klip.fonts import installer


def test_unsupported_extension_rejected(tmp_path: Path):
    fake = tmp_path / "notafont.png"
    fake.write_bytes(b"x")
    with pytest.raises(ValueError):
        installer.install_user_font(fake)


def test_missing_file_raises(tmp_path: Path):
    with pytest.raises(FileNotFoundError):
        installer.install_user_font(tmp_path / "nope.ttf")


def test_list_returns_path_objects():
    fonts = installer.list_installed_user_fonts()
    assert all(isinstance(p, Path) for p in fonts)
