# Klip — Design Specification

**Date:** 2026-05-07
**Status:** Design approved, ready for implementation plan
**Owner:** Leonel Ferreira

---

## 1. Summary

Klip is a Canva-style design app that runs natively on **Windows (.exe)** and **Android (.apk)** as two cooperating apps with feature parity. It supports multi-page documents, layered objects (text, shapes, images), image-inside-shape clip masks, on-device AI background removal, color picker and palette extraction, custom font installation, and 4K original-quality export. PC and Android exchange files directly over Wi-Fi or USB via ADB, with no cloud, no account, and no internet required.

---

## 2. Goals

- **Two native apps, identical features.** Same `.mcv` document opens identically on PC and Android.
- **Fully offline.** App works without internet. Sync uses ADB over LAN/hotspot/USB.
- **Reuse what exists.** Use the BiRefNet/U2Net ONNX models already cached at `C:\Users\leone\.u2net\`. Reuse the ADB sync pattern from `bgremover-sync`.
- **Simple, fast iteration.** Python+PySide6 on PC for the same fast PyInstaller workflow as `bgremover-pc`. Kotlin+Compose on Android for native touch UX.
- **4K original-quality export.** No downscaling, no compression artifacts at export time.

## 3. Non-Goals (v1)

- iOS, web, or macOS builds.
- Cloud sync, accounts, login, or multi-user collaboration.
- Vector path editing (bezier curves, node tools) — raster-first with vector text/shapes only.
- Animation, video, or motion graphics.
- Templates gallery, stock images, online assets.
- Auto-update mechanism (manual reinstall for now).

---

## 4. Tech Stack

| Concern | PC | Android |
|---|---|---|
| Language | Python 3.9 | Kotlin 1.9+ |
| UI framework | PySide6 (Qt for Python) | Jetpack Compose, Material 3 |
| Canvas | `QGraphicsScene` + `QGraphicsView` | Compose `Canvas` + custom drawing |
| Image processing | Pillow + `rembg` | onnxruntime-android + Android Bitmap |
| AI runtime | onnxruntime (already installed 1.19.2) | onnxruntime-android (Gradle dep) |
| AI model | BiRefNet (`birefnet-general.onnx`, ~880 MB) | U2Net (`u2net.onnx`, ~170 MB, bundled in APK assets) |
| Build | PyInstaller (one-folder mode → ~50 MB .exe) | Gradle (assembleRelease → ~200 MB APK with model) |
| File transfer | `subprocess` → adb push/pull | (passive; reads/writes to `/sdcard/Klip/inbox\|outbox`) |
| Document format | `.mcv` JSON (shared schema) | `.mcv` JSON (shared schema) |

**Already installed on dev machine:** Python 3.9.13, onnxruntime, rembg, Pillow, numpy, PyInstaller, Android SDK, Android Studio, ADB. **Only PySide6 needs `pip install`.**

---

## 5. Architecture

### 5.1 PC App — high-level

```
klip-pc/
├── src/klip/
│   ├── main.py                 # Qt application entry
│   ├── app.py                  # MainWindow + global state
│   ├── document/               # Document, Page, items, schema, IO
│   │   ├── document.py
│   │   ├── page.py
│   │   ├── items/              # TextItem, ShapeItem, ImageItem, GroupItem
│   │   ├── schema.py           # Pydantic models for .mcv
│   │   └── io.py               # save/load .mcv
│   ├── canvas/
│   │   ├── scene.py            # QGraphicsScene subclass
│   │   ├── view.py             # QGraphicsView with zoom/pan
│   │   ├── handles.py          # Selection + transform handles
│   │   └── render.py           # Render at native resolution for export
│   ├── tools/                  # one module per toolbar tool
│   │   ├── select_tool.py
│   │   ├── text_tool.py
│   │   ├── shape_tool.py
│   │   ├── image_tool.py
│   │   ├── bgrm_tool.py        # wraps rembg
│   │   ├── clip_tool.py        # image-inside-shape
│   │   ├── picker_tool.py      # eyedropper
│   │   ├── extractor_tool.py   # k-means palette
│   │   └── font_tool.py        # font install
│   ├── panels/
│   │   ├── pages_panel.py
│   │   ├── layers_panel.py
│   │   ├── properties_panel.py
│   │   └── phone_panel.py      # ADB sync UI
│   ├── sync/
│   │   ├── adb.py              # subprocess wrapper around adb.exe
│   │   ├── pusher.py           # PC → phone push
│   │   ├── puller.py           # phone → PC pull (background poll)
│   │   └── device.py           # device discovery + status polling
│   ├── ai/
│   │   └── bg_remover.py       # rembg + onnxruntime wrapper
│   ├── color/
│   │   ├── picker.py           # global mouse-position pixel sampling
│   │   └── extractor.py        # k-means via numpy
│   ├── fonts/
│   │   └── installer.py        # Windows font registration
│   ├── export/
│   │   └── exporter.py         # PNG/JPG/SVG/PDF rendering
│   └── undo/
│       └── stack.py            # QUndoStack wrapper + commands
├── assets/                     # default fonts, app icons (none in v1)
├── tests/
├── pyinstaller.spec
├── requirements.txt
└── README.md
```

### 5.2 Android App — high-level

```
klip-android/
├── app/
│   ├── src/main/
│   │   ├── kotlin/com/klip/
│   │   │   ├── MainActivity.kt
│   │   │   ├── ui/
│   │   │   │   ├── KlipApp.kt          # NavHost, theme
│   │   │   │   ├── canvas/             # CanvasScreen + custom Canvas drawing
│   │   │   │   ├── tools/              # tool implementations
│   │   │   │   ├── panels/             # LayersSheet, PropsSheet, RecentsSheet
│   │   │   │   └── components/         # FAB, toolbar, dialogs
│   │   │   ├── document/
│   │   │   │   ├── Document.kt
│   │   │   │   ├── Page.kt
│   │   │   │   ├── items/              # TextItem, ShapeItem, ImageItem
│   │   │   │   ├── Schema.kt           # kotlinx-serialization for .mcv
│   │   │   │   └── DocumentIO.kt
│   │   │   ├── ai/
│   │   │   │   └── BgRemover.kt        # onnxruntime-android + U2Net
│   │   │   ├── sync/
│   │   │   │   └── InboxWatcher.kt     # poll /sdcard/Klip/inbox
│   │   │   ├── color/
│   │   │   │   ├── Picker.kt
│   │   │   │   └── Extractor.kt
│   │   │   ├── export/
│   │   │   │   └── Exporter.kt
│   │   │   └── undo/
│   │   │       └── UndoStack.kt
│   │   ├── assets/
│   │   │   └── u2net.onnx              # bundled model
│   │   └── res/
│   ├── build.gradle.kts
│   └── proguard-rules.pro
├── gradle/
└── settings.gradle.kts
```

### 5.3 Shared schema folder

```
klip/schema/
├── mcv-schema.json             # JSON Schema for .mcv format
└── README.md                   # Versioning rules, breaking-change protocol
```

Both apps validate documents against this schema on load.

---

## 6. Document model (`.mcv`)

A `.mcv` file is gzipped JSON with the following shape:

```jsonc
{
  "version": 1,
  "name": "sneaker-poster",
  "createdAt": "2026-05-07T14:00:00Z",
  "modifiedAt": "2026-05-07T14:32:11Z",
  "fonts": [                            // fonts referenced by this doc
    { "family": "Inter", "weight": 700, "source": "system" }
  ],
  "assets": [                           // embedded image assets, base64
    { "id": "asset_xyz", "mime": "image/png", "data": "..." }
  ],
  "pages": [
    {
      "id": "page_1",
      "size": { "w": 1080, "h": 1080, "unit": "px", "dpi": 144 },
      "background": { "type": "solid", "color": "#ffffff" },
      "items": [
        {
          "id": "item_1",
          "type": "image",
          "transform": { "x": 140, "y": 72, "w": 200, "h": 200, "rotation": 0, "opacity": 1 },
          "z": 2,
          "assetRef": "asset_xyz",
          "clipMask": { "shape": "ellipse", "fillRule": "nonzero" },
          "effects": [
            { "type": "bgRemoved", "model": "birefnet-general", "edgeFeather": 2 }
          ]
        },
        {
          "id": "item_2",
          "type": "text",
          "transform": { "x": 50, "y": 800, "w": 980, "h": 80, "rotation": 0, "opacity": 1 },
          "z": 4,
          "text": "FRESH KICKS",
          "font": { "family": "Inter", "weight": 700, "size": 72 },
          "color": "#ffffff",
          "align": "center",
          "letterSpacing": -1
        }
      ]
    }
  ]
}
```

**Item types:** `text`, `shape` (`rect`/`ellipse`/`polygon`/`line`), `image`, `group`.
**Coordinate system:** origin top-left, pixels, page-local. Rotation in degrees clockwise.
**Versioning:** `version: 1` on every doc; loaders fail loudly on unknown versions to avoid silent data loss.

---

## 7. Feature breakdown

### 7.1 Selection & transform
QGraphicsScene's built-in z-ordering, hit-testing, and transforms. 8-handle bounding box around selection (4 corners + 4 edges) plus rotation handle above. Shift-drag = aspect lock; Alt-drag = duplicate.

### 7.2 Text
`QGraphicsTextItem` subclass. Inline editing on double-click. Font / size / weight / color / alignment / letter-spacing in Properties panel.

### 7.3 Shapes (rect, ellipse, polygon, line)
Custom `QGraphicsItem` subclasses. Each emits a `QPainterPath`. Properties: fill, stroke (color + width + style), corner-radius (rect), sides (polygon), caps + arrowheads (line).

### 7.4 Image insert
`QGraphicsPixmapItem` wrapping a `QPixmap`. Asset stored once in document `assets[]`, referenced by `assetRef`. Supported input: PNG, JPG, JPEG, WEBP, HEIC, BMP, GIF, TIFF.

### 7.5 BG remover
On click of the BG Remover tool with an image selected: spawn a `QThread` running `rembg.remove(pillow_image, session=new_session("birefnet-general"))`. Replace the image asset with the transparent PNG result. Stored in the item's `effects: [{ type: "bgRemoved", model: "..." }]` so we can re-run with a different model later.

**Android:** same pattern using `onnxruntime-android` directly with `u2net.onnx` from APK assets. Pre/post-processing matches the Python rembg pipeline exactly so output is identical (within model-difference tolerance).

### 7.6 Image inside shape (clip mask)
Select image AND a shape → click clip tool. Encode as the image item's `clipMask` field (shape data). At render time, set `QGraphicsItem` clip path to the shape's path. Clip is non-destructive — change the shape later and the clip follows.

### 7.7 Color picker (eyedropper)
Activate eyedropper → cursor changes → on click, sample the pixel at cursor location from the rasterized canvas (`QGraphicsScene.render()` to a single-pixel `QImage`). Apply to current selection's fill (or stroke if Alt held). Hex value displayed live in status bar during hover.

### 7.8 Color extractor (palette)
Select an image → click extractor → run `numpy`-based k-means with k=5 on a downsampled (256×256) version of the image's RGB pixels. Produce 5 swatches. Drag a swatch onto a fill / stroke / canvas background to apply.

### 7.9 Font installer
Browse for `.ttf` / `.otf` → copy to `%LOCALAPPDATA%\Microsoft\Windows\Fonts\` and register with `AddFontResourceEx` via `ctypes`. Refresh font list. Already-installed fonts auto-detected via `QFontDatabase`. **Android:** load custom fonts from `/sdcard/Klip/fonts/` using Compose's `Font(File(...))`.

### 7.10 Multi-page
`Document` holds `List<Page>`. Each `Page` has its own `QGraphicsScene` instance. Switching pages swaps the scene shown by `QGraphicsView`. Pages panel shows live thumbnails (rendered offscreen at 80×60 via `scene.render()`).

### 7.11 Undo/redo
`QUndoStack`. One `QUndoCommand` per atomic mutation: AddItemCmd, RemoveItemCmd, MoveItemCmd, ResizeItemCmd, SetPropertyCmd, AddPageCmd, etc. Coalesce continuous drags into one command (start/finish hooks).

### 7.12 Export (4K original quality)
For each page: create an offscreen `QImage` at the page's pixel dimensions × DPR. `scene.render(painter)`. Save with `QImage.save()` (PNG/JPG) or generate SVG via `QSvgGenerator`, PDF via `QPdfWriter`. **No downscaling at any stage.** Vector items (text, shapes) render at full vector quality; raster items render at their native pixel resolution.

---

## 8. ADB Sync (Phone Panel)

### 8.1 Filesystem layout

```
/sdcard/Klip/
├── inbox/         # PC pushes here; Android polls and shows in "Recents"
├── outbox/        # Android writes here on export; PC auto-pulls
├── docs/          # .mcv files for Android-edited documents
└── fonts/         # custom fonts the user installed via Klip
```

### 8.2 PC side (Phone Panel)
- **Device polling:** `adb devices` every 3 s. Shows status (connected / not connected / unauthorized).
- **Push:** drag/drop or paste/browse → `adb push <local> /sdcard/Klip/inbox/`.
- **Pull (auto):** every 5 s, `adb shell ls /sdcard/Klip/outbox/` — for any file not yet pulled, `adb pull`. Track pulled files by name+mtime to avoid duplicates.
- **UI:** Phone tab in right sidebar. Device card · drop zone · recent transfers list with direction arrows.

### 8.3 Android side
- **Inbox watcher:** `WorkManager` periodic task every 5 s scanning `/sdcard/Klip/inbox/`. New files appear in "Recents" tab.
- **Export:** `Save → Send to PC` writes to `/sdcard/Klip/outbox/`. The `bgremover-sync` learning: PC auto-pulls within seconds.

### 8.4 Permissions
Android: `READ_MEDIA_IMAGES`, `MANAGE_EXTERNAL_STORAGE` (or scoped storage with `DocumentFile` for `/sdcard/Klip`).

### 8.5 Transports
- **Wi-Fi LAN** (any router, no internet needed) — already works on user's setup.
- **PC hotspot or phone hotspot** (truly offline) — same code path; ADB doesn't care.
- **USB cable** — fallback when no wireless. Same code path.
- **Bluetooth — explicitly out of scope.** ~1–2 Mbps is too slow for 4K image transfers.

---

## 9. UI Specification

### 9.1 PC main window

```
┌─────────────────────────────────────────────────────────────────┐
│  Klip — sneaker.mcv *                                  [_][□][×]│ Title bar
├─────────────────────────────────────────────────────────────────┤
│ File  Edit  View  Layer  Object  Image  Filters  Help          │ Menu
├─────────────────────────────────────────────────────────────────┤
│ ⬚ ✋ │ T ▭ ○ ⬠ ／ │ 🖼 ✂ ◉ │ 💧 🎨 A+ │ ↶ ↷                       │ Toolbar
├──────────┬───────────────────────────────────┬──────────────────┤
│ PAGES  + │                                   │ ┌─────┬────────┐ │
│ ┌──────┐ │                                   │ │Props│Phone ●│ │ Tabs
│ │  1●  │ │                                   │ ├─────┴────────┤ │
│ └──────┘ │                                   │ │              │ │
│ ┌──────┐ │       (canvas with checkers       │ │  Properties  │ │
│ │  2   │ │        + 380×380 page +           │ │     OR       │ │
│ └──────┘ │        selection handles)         │ │   Phone      │ │
│ ┌──────┐ │                                   │ │   panel      │ │
│ │  3   │ │                                   │ │              │ │
│ └──────┘ │                                   │ │              │ │
│          │                                   │ │              │ │
│ LAYERS + │                                   │ │              │ │
│ ─ T text │                                   │ │              │ │
│ ─ ◉ img  │                                   │ │              │ │
│ ─ ▭ rect │                                   │ │              │ │
└──────────┴───────────────────────────────────┴──────────────────┤
│ Page 1/3 · 1080×1080 · ● Phone · 4K ready    [−====●——]  35%   │ Status
└─────────────────────────────────────────────────────────────────┘
```

### 9.2 Android main screen

```
┌─────────────────────────┐
│ 9:41          📶 📡 🔋 │ Status
├─────────────────────────┤
│ ≡  Klip          ⤴  ⋮  │ App bar
├─────────────────────────┤
│ Canvas │ Layers │Recents│ Tabs
├─────────────────────────┤
│                         │
│      ┌───────────┐      │
│      │           │      │
│      │  canvas   │      │
│      │           │      │
│      └───────────┘      │
│                         │
│       ●○○ pages         │
│                         │
│  ✓ Synced from PC       │ Toast (auto-dismiss)
├─────────────────────────┤
│ ⬚ T ▭ [+] 💧 🎨 ≣      │ Bottom toolbar + FAB
└─────────────────────────┘
```

### 9.3 Tooltips & help
Every PC toolbar button has a 280 px tooltip with: name + shortcut + 1–2 sentence description + optional inline diagram. Android: long-press for tooltip.

---

## 10. Project layout

```
C:\Users\leone\klip\
├── pc\                  # PySide6 app (klip-pc)
├── android\             # Compose app (klip-android)
├── schema\              # shared .mcv JSON Schema
├── docs\
│   ├── design.md        # this file
│   ├── plan.md          # implementation plan (next step)
│   └── decisions.md     # architecture decision log
├── .gitignore
├── README.md
└── LICENSE              # MIT (default; change before public release if desired)
```

Single git repo. **No code or imports from GEOSSTORE, BAZA, Landing Editor, or any other project.**

---

## 11. Setup / dependencies

### PC
**Already installed on this machine:** Python 3.9.13, onnxruntime 1.19.2, rembg 2.0.61, Pillow 11.3.0, numpy 2.0.2, PyInstaller 6.20.0.
**To install:** PySide6 (~150 MB combined with shiboken6).
**Models:** referenced from existing `~/.u2net/birefnet-general.onnx` cache. Optionally bundle in PyInstaller spec for distribution to other machines.

### Android
**Already installed:** Android Studio, Android SDK, ADB.
**Gradle dependencies:**
- `androidx.compose.bom:2024.x` (Compose Material 3)
- `com.microsoft.onnxruntime:onnxruntime-android:1.18+`
- `androidx.work:work-runtime-ktx` (inbox watcher)
- `org.jetbrains.kotlinx:kotlinx-serialization-json` (.mcv parsing)
- `com.google.accompanist:accompanist-permissions`

**Bundled assets:** `app/src/main/assets/u2net.onnx` (~170 MB).

### Bootstrap commands (run when implementation begins)

```powershell
# PC side
cd C:\Users\leone\klip
git init
python -m venv pc\.venv
pc\.venv\Scripts\activate
pip install PySide6
# (onnxruntime, rembg, Pillow, numpy, PyInstaller already global)

# Android side — open klip\android\ in Android Studio, sync Gradle.
# Copy U2Net model in:
copy %USERPROFILE%\.u2net\u2net.onnx android\app\src\main\assets\
```

---

## 12. Phasing (rough; detailed plan in `plan.md`)

**Phase 0 — Bootstrap.** Create folders, init git, set up Python venv, install PySide6, create empty PySide6 window, Android Studio "Hello Compose" project, both run + close cleanly.

**Phase 1 — Document & canvas foundation (PC).** Document model, .mcv save/load, single page, canvas with rect/ellipse/text/image, selection + move + resize, basic toolbar, save → open round-trips.

**Phase 2 — Layers, multi-page, undo, export (PC).** Layers panel, pages panel, undo/redo, PNG/JPG export at 4K.

**Phase 3 — The "wow" features (PC).** BG remover wired to rembg, clip masks, color picker, palette extractor, font installer.

**Phase 4 — ADB sync (PC).** Phone panel, drop zone, push/pull, recents list. End-to-end PC-to-PC sanity test using two folders.

**Phase 5 — Android foundation.** Mirror Phase 1+2 on Android. Compose canvas, .mcv loader/saver, layers, multi-page, undo, export.

**Phase 6 — Android features + sync.** BG remover with U2Net, clip masks, color picker, palette extractor, custom fonts, inbox watcher, end-to-end test: design on PC → sync → edit on Android → sync back → open on PC.

**Phase 7 — Polish + package.** Icons, About dialog, error handling pass, PyInstaller spec, Android signed APK, manual QA pass.

---

## 13. Testing strategy

- **PC unit tests** (`pytest`): document IO round-trip, schema validation, color-extraction algorithm, clip-mask geometry, font installer (mocked), ADB wrapper (mocked subprocess).
- **PC integration tests:** full app smoke test via `pytest-qt` — open app, create page, add items, save, reopen, assert content matches.
- **Android unit tests:** schema parsing, color algorithms, sync state machine.
- **Manual QA:** the cross-device round-trip is the acceptance gate — design on PC, sync, edit on Android, sync back, must open identically on PC.

---

## 14. Risks & open questions

| Risk / question | Mitigation |
|---|---|
| **PySide6 ↔ numpy 2.x compatibility** | Verify on Phase 0; downgrade numpy to 1.26 if needed. |
| **BiRefNet vs U2Net visual difference at boundaries** | Acceptable per user requirement (Android = lighter, faster, slightly less precise). Document the difference in app About. |
| **Font install requires admin?** | `AddFontResourceEx` with per-user scope works without admin. Test on Phase 3. |
| **ADB-over-Wi-Fi flakiness** | Reuse the existing `bgremover-sync` polling pattern (3 s heartbeat, auto-reconnect). Show clear "disconnected" state. |
| **APK size with bundled U2Net (~170 MB)** | Acceptable. Alternative: download on first run from a CDN — explicitly out of scope per offline requirement. |
| **HEIC support on Windows** | Pillow needs `pillow-heif` plugin; add to requirements. |
| **4K export memory** | A 4096×4096 RGBA image is 64 MB in memory; QImage handles this fine on modern machines. Document memory floor: 4 GB RAM minimum. |

---

## 15. Acceptance criteria for v1 ship

1. PC `.exe` runs on a clean Windows machine without Python installed.
2. Android `.apk` installs and runs on Android 8+ (API 26+) without errors.
3. A 1080×1080 design with text + image + shape + clip mask + bg-removed image saves to `.mcv`, transfers via ADB to phone, opens on Android with identical visual output (within rendering tolerance), edits round-trip back to PC.
4. PNG export of any page at 4K (3840×3840 max) completes in under 10 seconds on dev machine.
5. All 15 PC toolbar tools and all 9 Android toolbar tools functional.
6. App works on a Wi-Fi network with no internet (verified on PC hotspot).

---

**End of design.**
