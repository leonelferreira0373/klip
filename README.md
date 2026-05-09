# Klip

Canva-style design app for Windows and Android, ADB-synced, fully offline.

See [docs/design.md](docs/design.md) for the design specification.
See [docs/plan.md](docs/plan.md) for the implementation plan.

## Status

- [x] Phase 0 — bootstrap
- [x] Phase 1 — document & canvas foundation
- [ ] Phase 2 — layers, multi-page UI, undo, export
- [ ] Phase 3 — AI features (BG remover, clip, picker, extractor, fonts)
- [ ] Phase 4 — ADB sync
- [ ] Phase 5 — Android foundation
- [ ] Phase 6 — Android features + sync
- [ ] Phase 7 — polish + package

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

## What works after Phase 1

- New / Open / Save / Save As `.mcv` files (gzipped JSON)
- Multi-page document model
- Click-to-drop rectangle, ellipse, text on canvas
- Image insert (loaded from .mcv assets)
- Selection with 8 corner/edge handles
- Move via drag
- Pan + zoom (Ctrl+wheel)
