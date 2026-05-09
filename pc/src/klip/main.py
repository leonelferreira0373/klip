import os
import sys
from pathlib import Path


def _bootstrap_frozen_env() -> None:
    if not getattr(sys, "frozen", False):
        return
    bundle = Path(sys._MEIPASS)
    bundled_models = bundle / "models"
    if bundled_models.is_dir():
        os.environ.setdefault("U2NET_HOME", str(bundled_models))


_bootstrap_frozen_env()

from PySide6.QtWidgets import QApplication

from klip.app import MainWindow


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("Klip")
    app.setApplicationVersion("0.1.0")
    app.setOrganizationName("Klip")
    window = MainWindow()
    window.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
