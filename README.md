# Klip

Canva-style design app for Windows and Android, ADB-synced, fully offline.

See [docs/design.md](docs/design.md) for the design specification.
See [docs/plan.md](docs/plan.md) for the implementation plan.

## Status

- [x] Phase 0 — bootstrap
- [x] Phase 1 — document & canvas foundation
- [x] Phase 2 — layers, multi-page UI, undo, export
- [x] Phase 3 — AI features (BG remover, picker, extractor, fonts)
- [ ] Phase 4 — ADB sync
- [ ] Phase 5 — Android foundation
- [ ] Phase 6 — Android features + sync
- [x] Phase 7 (PC) — PyInstaller .exe with bundled BiRefNet model
- [ ] Phase 7 (Android) — signed .apk

## PC dev quickstart

```powershell
cd pc
.venv\Scripts\activate
python -m klip.main
```

Run tests:

```powershell
pytest -v
```

## Build the Windows .exe

```powershell
cd pc
.venv\Scripts\activate
python -m PyInstaller --noconfirm --clean build\klip.spec
```

Output at `pc/dist/Klip/Klip.exe` (one-folder bundle, ~1.1 GB — includes the
BiRefNet ONNX model). The icon at `pc/build/icon.ico` is a placeholder; replace
it with brand art any time and rebuild.

## What works after Phase 1

- New / Open / Save / Save As `.mcv` files (gzipped JSON)
- Multi-page document model
- Click-to-drop rectangle, ellipse, text on canvas
- Image insert (loaded from .mcv assets)
- Selection with 8 corner/edge handles
- Move via drag
- Pan + zoom (Ctrl+wheel)
