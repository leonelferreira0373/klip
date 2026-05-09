# Klip — Project Status & Resumption Guide

**Last updated:** 2026-05-09 (end of day)
**Where we left off:** PC .exe shipping with image import. Toolbar UX bug to fix tomorrow.

---

## 🟢 Quick resume 2026-05-10

When you come back, just say:

> "Resume Klip — toolbar bug"

Open bug:
- User reports toolbar buttons (Select / Rect / Ellipse / Polygon / Line / Text)
  "don't seem to actually do anything." Either:
  (a) tool-change signal not reaching `KlipScene.set_active_tool` in the
      frozen build, or
  (b) UX confusion — current flow is *click tool → click canvas to drop*; user
      probably expected the button itself to add a shape, or to show pressed-state.
- First check: open the .exe, click Rect, then click the canvas. If a rect
  appears, it's UX (b) → fix with checkable toolbar buttons + "click on canvas
  to add" status hint. If nothing, it's (a) → check signal wiring in app.py
  `_on_tool_changed` and confirm `KlipScene.handle_canvas_click` returns a
  model when `_active_tool` is set.
- Also worth checking: dock-panel buttons (add-page, delete-layer) and any
  other clickable that the user might have meant.

## ⏭️ What was just shipped today (2026-05-09)
1. Resumed mid-Phase-3 (memory was stale — said Phase 0+1 blocked, actually
   already through Phase 2).
2. Fixed venv: `pyvenv.cfg` `include-system-site-packages = true` so AI deps
   from system Python are visible to the venv (no re-downloads needed).
3. Committed + tagged `phase-3` (AI bg remover, color picker/extractor, fonts).
4. Wrote `pc/build/klip.spec` + `make_icon.py` + `version_info.txt`.
5. Built `pc/dist/Klip/Klip.exe` (~1.1 GB, BiRefNet bundled, windowed).
   Tag: `phase-7-pc`.
6. Added image import (Ctrl+I menu, drag-and-drop, Ctrl+V paste). +7 tests,
   62 total passing. Rebuilt the .exe.

## ⏭️ Roadmap reminder
- Phase 4 — ADB sync (not started)
- Phase 5/6 — Android Compose .apk (not started — needs Gradle internet sync)
- Phase 7 (Android) — signed .apk

---

## 📜 Original resume sequence (kept for reference, no longer relevant)

1. Read this file + `design.md` + `plan.md` to restore full context.
2. Run offline pip install from `C:\Users\leone\klip\wheels\`.
3. Run pytest to verify everything passes.
4. Commit Phase 0 + Phase 1.
5. Launch the app once so you can see it open.
6. Move to Phase 2.

---

## What this project is

**Klip** is a Canva-style design app:
- **PC version** — Windows .exe — Python 3.9 + PySide6 + onnxruntime + rembg
- **Android version** — .apk — Kotlin + Jetpack Compose + onnxruntime-android
- **Sync** — ADB push/pull over Wi-Fi or USB (no cloud, no internet, no auth)
- **AI bg-remover** — BiRefNet on PC, U2Net on Android (already-cached ONNX models)
- **Format** — `.mcv` (gzipped JSON), shared between platforms
- **Export** — 4K original quality (PNG, JPG, SVG, PDF)
- **Project root** — `C:\Users\leone\klip\` — fully isolated, own git repo, NO link to GEOSSTORE/BAZA/Landing-Editor.

Full design doc: **`docs/design.md`**
Full bite-sized implementation plan (Phases 0+1): **`docs/plan.md`**

---

## ✅ Done today

### Brainstorming + design (complete)
- Clarified ambition: Mini-Canva (raster + vector overlays) — not full vector editor.
- Tech stack: Python+PySide6 (PC) and Kotlin+Compose (Android). Two native codebases, shared schema.
- Confirmed BG remover: existing `~/.u2net/birefnet-general.onnx` and `u2net.onnx` files are reused.
- Confirmed sync: ADB-based, same as your existing `bgremover-sync` — no cloud, no internet.
- Confirmed offline: Wi-Fi LAN / hotspot / USB all work via ADB. Bluetooth too slow for 4K → out of scope.
- Saved app name: **Klip**.

### Design doc — `docs/design.md` (committed)
15 sections: summary, goals, non-goals, tech stack, architecture, .mcv schema, all 12 features, ADB sync details, UI specs (PC + Android), project layout, setup commands, 7 phases, testing strategy, risks, acceptance criteria.

### Implementation plan — `docs/plan.md`
Phase 0 (bootstrap, 6 tasks) + Phase 1 (foundation, 18 tasks) = 24 tasks total. Bite-sized TDD steps. Phases 2–7 will be planned after Phase 1 lands.

### Visual mockups — `mini-canva-design/.superpowers/brainstorm/...`
Three iterations (`app-mockup.html`, `v2`, `v3`). v3 includes the PC app with the Phone tab and a clean Android phone mockup.

### Code + tests written (NOT yet running because PySide6 isn't installed)

**Source files at `C:\Users\leone\klip\pc\src\klip\`:**
| File | What it does |
|---|---|
| `__init__.py` | Package marker, version 0.1.0 |
| `main.py` | Entry point: `QApplication` + `MainWindow.show()` |
| `app.py` | `MainWindow` — wires scene, view, toolbar, menus, File→New/Open/Save |
| `document/schema.py` | Pydantic models: Transform, TextItem, ShapeItem, ImageItem, Page, Document, Asset, FontRef |
| `document/document.py` | `Document` dataclass — page management, schema conversion |
| `document/io.py` | `save_document` / `load_document` — gzipped JSON .mcv |
| `document/items/base.py` | `ItemAdapter` — converts schema models → QGraphicsItem |
| `document/items/shape_item.py` | rect / ellipse / polygon / line drawing |
| `document/items/text_item.py` | Qt text item with font/size/color/alignment |
| `document/items/image_item.py` | base64-asset image rendering |
| `canvas/scene.py` | `KlipScene` — owns a Page, builds Qt items, click-to-drop |
| `canvas/view.py` | `KlipView` — Ctrl+wheel zoom, pan, mousePress hooks |
| `canvas/handles.py` | 8-point selection-handle overlay |
| `toolbar/toolbar.py` | `Tool` enum + `KlipToolbar` with active-tool tracking |

**Test files at `C:\Users\leone\klip\pc\tests\`:**
- `test_schema.py` — 9 tests for pydantic models
- `test_io.py` — 3 tests for .mcv save/load + bad-version rejection
- `test_document.py` — 6 tests for page management + schema conversion
- `test_items.py` — 5 tests for shape/text/image rendering
- `test_scene.py` — 5 tests for KlipScene + KlipView + handles
- `test_toolbar.py` — 3 tests for tool enum + signal emission
- `test_app_smoke.py` — 5 tests for MainWindow + click-to-drop + round-trip

**Total: ~36 tests written, all currently NOT runnable (PySide6 missing).**

### Project infrastructure
- `C:\Users\leone\klip\.git\` — local git repo initialized (NEVER pushed anywhere)
- `C:\Users\leone\klip\.gitignore` — Python, IDE, OS, ONNX-models excluded
- `C:\Users\leone\klip\README.md`
- `C:\Users\leone\klip\pc\pyproject.toml` — pytest config, package layout
- `C:\Users\leone\klip\pc\requirements.txt` — pinned deps
- `C:\Users\leone\klip\pc\.venv\` — Python virtualenv (pip 26.0.1, no packages installed yet)
- `C:\Users\leone\klip\schema\.gitkeep` — placeholder for shared JSON Schema

---

## ⏳ In progress (blocking)

### IDM downloads
**You're downloading these via IDM right now (or about to):**

📄 **`C:\Users\leone\klip\wheels\IDM_URLS.txt`** has all 8 URLs.

| File | Size |
|---|---|
| PySide6_Essentials-6.6.3.1 | ~70 MB ← the big one |
| shiboken6-6.6.3.1 | ~1 MB |
| pillow-10.4.0 (cp39) | ~3 MB |
| pydantic-2.8.2 | ~400 KB |
| pydantic_core-2.20.1 (cp39) | ~1.7 MB |
| annotated_types-0.7.0 | ~13 KB |
| pytest-8.3.2 | ~340 KB |
| pytest_qt-4.4.0 | ~36 KB |

**Save them all to:** `C:\Users\leone\klip\wheels\`

When done → tell me, I install offline.

---

## ⏭️ What I'll do tomorrow (immediate)

1. **Verify wheels arrived** — `Get-ChildItem C:\Users\leone\klip\wheels\*.whl`
2. **Offline install** — `pip install --no-index --find-links C:\Users\leone\klip\wheels\ -r requirements.txt`
   (Pulls in any tiny transitive deps like pluggy/iniconfig/packaging/tomli/exceptiongroup from PyPI — they're only ~50 KB combined and should download fine.)
3. **Run all tests** — `pytest -v` from `C:\Users\leone\klip\pc\`. Expected: ~36 pass.
4. **Manual smoke test** — `python -m klip.main` → verify window opens, click Rect tool, click canvas, see grey rectangle, save as .mcv, reopen, see rectangle.
5. **Commit** — `git add . && git commit -m "feat: Phase 0 + Phase 1 — bootstrap + document/canvas foundation"`
6. **Tag** — `git tag phase-1`
7. **Demo to you** — show the app running.

Estimated time tomorrow: ~30 min if downloads complete and tests pass.

---

## 📅 Full roadmap (after Phase 1)

| Phase | Scope | Plan file |
|---|---|---|
| 0 | Bootstrap (folders, venv, git, empty window) | done in `plan.md` |
| 1 | PC document + canvas + .mcv save/load + click-to-drop + selection handles | done in `plan.md` |
| **2** | **Layers panel · pages panel · QUndoStack · 4K PNG/JPG/SVG/PDF export** | `plan-phase-2.md` (to write) |
| 3 | BG remover (rembg + threading) · clip masks · color picker · palette extractor (k-means) · font installer | `plan-phase-3.md` |
| 4 | ADB sync — Phone tab in right sidebar · drop zone · auto-pull from `/sdcard/Klip/outbox` | `plan-phase-4.md` |
| 5 | Android foundation — Compose canvas · .mcv loader · layers · multi-page · undo · export (mirror Phase 1+2) | `plan-phase-5.md` |
| 6 | Android features + sync — U2Net BG · clip · picker · extractor · custom fonts · inbox watcher · end-to-end PC↔phone round-trip | `plan-phase-6.md` |
| 7 | Polish + package — icons · About dialog · error pass · PyInstaller `.exe` · signed `.apk` · manual QA | `plan-phase-7.md` |

Each phase produces working, testable software. Acceptance gate for Phase 6: design on PC → ADB-sync → edit on Android → ADB-sync back → opens identically on PC.

---

## 🔑 Key file paths to remember

```
C:\Users\leone\klip\
├── docs\
│   ├── STATUS.md         ← this file (resume guide)
│   ├── design.md         ← full spec (read first if you forget anything)
│   └── plan.md           ← Phase 0+1 bite-sized tasks
├── pc\
│   ├── src\klip\         ← all source code
│   ├── tests\            ← all tests
│   ├── .venv\            ← Python virtualenv
│   ├── requirements.txt
│   └── pyproject.toml
├── wheels\
│   └── IDM_URLS.txt      ← URLs to download via IDM
├── schema\               ← future: shared JSON Schema for .mcv
├── .gitignore
└── README.md
```

```
C:\Users\leone\.u2net\
├── birefnet-general.onnx  ← BG remover model used by PC (~880 MB)
└── u2net.onnx             ← BG remover model used by Android (~170 MB)
```

```
C:\Users\leone\bgremover-pc\dist\BGRemoverPC\BGRemoverPC.exe
   ← your existing working bg-remover .exe — reference for sync patterns

C:\Users\leone\bgremover-sync\bgremover_sync.py
   ← your existing ADB push/pull tool — Klip will reuse this approach
```

---

## 💾 Memory entries already saved

These persist across Claude sessions in `C:\Users\leone\.claude\projects\C--Users-leone\memory\`:

- `MEMORY.md` — index, includes Klip entry
- `project_klip.md` — Klip overview with stack, models, file format, location

So even if you close Claude entirely, opening it again and saying "let's continue Klip" will pick up the project context.

---

## ⚠️ Things to NOT lose

1. **The `~/.u2net\*.onnx` model files** — don't delete; they're 1+ GB and we're referencing them.
2. **The `bgremover-pc\` folder** — it stays; we don't touch it. Reference only.
3. **The `bgremover-sync\` folder** — it stays; we don't touch it. Reference only.
4. **Local git in `C:\Users\leone\klip\.git\`** — never pushed anywhere, but holds your commits.

---

## 📞 Talk to me tomorrow with any of these

- "Resume Klip — downloads done" → I run offline install + tests
- "Resume Klip — downloads failed" → I'll troubleshoot or give alternative URLs
- "Show me the design again" → I'll re-render the v3 mockup at localhost
- "I want to change X before we continue" → we'll iterate the design before code
- "Skip ahead to phase Y" → I'll re-prioritize (not recommended; phases build on each other)

---

**End of status. Sleep well — picking up tomorrow.**
