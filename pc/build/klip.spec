# PyInstaller spec for Klip — PC build (Windows .exe, one-folder).
#
# Build:
#   cd pc
#   .venv\Scripts\activate
#   pyinstaller --noconfirm --clean build\klip.spec
#
# Output: pc\dist\Klip\Klip.exe (with all dependencies + BiRefNet model bundled).

from pathlib import Path

from PyInstaller.utils.hooks import collect_submodules, collect_data_files

block_cipher = None

PC_ROOT = Path(SPECPATH).parent.resolve()
SRC = PC_ROOT / "src"
ICON = PC_ROOT / "build" / "icon.ico"
VERSION = PC_ROOT / "build" / "version_info.txt"
HOME = Path.home()
BIREFNET = HOME / ".u2net" / "birefnet-general.onnx"

assert ICON.exists(), f"Missing icon: {ICON}"
assert VERSION.exists(), f"Missing version info: {VERSION}"
assert BIREFNET.exists(), f"Missing BiRefNet model: {BIREFNET}"


datas = []
datas.append((str(BIREFNET), "models"))
datas += collect_data_files("onnxruntime")


hiddenimports = []
hiddenimports += collect_submodules("onnxruntime")
hiddenimports += [
    "PIL._tkinter_finder",
    "klip.canvas",
    "klip.canvas.scene",
    "klip.canvas.view",
    "klip.canvas.handles",
    "klip.document",
    "klip.document.io",
    "klip.document.schema",
    "klip.document.document",
    "klip.document.items.base",
    "klip.document.items.shape_item",
    "klip.document.items.text_item",
    "klip.document.items.image_item",
    "klip.toolbar.toolbar",
    "klip.panels",
    "klip.export",
    "klip.undo",
    "klip.color.picker",
    "klip.color.extractor",
    "klip.fonts.installer",
    "klip.ai.bg_remover",
]


excludes = [
    "tkinter",
    "unittest",
    "pydoc",
    "test",
    "tests",
    "pytest",
    "_pytest",
    "pytest_qt",
    "rembg",
    "scipy",
    "scikit-image",
    "skimage",
    "cv2",
    "opencv",
    "numba",
    "llvmlite",
    "pymatting",
    "matplotlib",
    "IPython",
    "jupyter",
    "notebook",
    "PySide6.QtWebEngineCore",
    "PySide6.QtWebEngineWidgets",
    "PySide6.QtWebEngine",
    "PySide6.QtMultimedia",
    "PySide6.QtMultimediaWidgets",
    "PySide6.QtCharts",
    "PySide6.QtDataVisualization",
    "PySide6.Qt3DCore",
    "PySide6.Qt3DRender",
    "PySide6.Qt3DAnimation",
    "PySide6.Qt3DInput",
    "PySide6.Qt3DLogic",
    "PySide6.Qt3DExtras",
    "PySide6.QtBluetooth",
    "PySide6.QtNfc",
    "PySide6.QtSerialPort",
    "PySide6.QtPositioning",
    "PySide6.QtLocation",
    "PySide6.QtRemoteObjects",
    "PySide6.QtSensors",
    "PySide6.QtScxml",
    "PySide6.QtSpeech",
    "PySide6.QtTest",
]


a = Analysis(
    [str(SRC / "klip" / "main.py")],
    pathex=[str(SRC)],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=excludes,
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="Klip",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=str(ICON),
    version=str(VERSION),
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="Klip",
)
