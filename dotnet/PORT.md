# KLIP → one app, in .NET/C#

**Decision (2026-07-11, final):** there is ONE KLIP, the .NET app. Everything the Python KLIP
(`C:\Users\leone\klip\pc`) does is ported to C#; the Python app is then **deleted**.

**Delete timing (safety):** do NOT delete the Python app until the .NET app reaches parity — it is
both the only working product today and the reference we port from. Port → verify parity → delete.

## Already in .NET (ahead of Python)
Engine: 2D + 2.5D transforms, real 3D + bevel + camera + lighting, motion timeline (keyframes/easing),
path morph, motion trails, gradients (linear/radial), drop shadow, specular, MP4 export. Avalonia
editor shell rendering the engine (PNG boundary).

## Parity port (each item = Python has it → build in .NET)
1. ✅ **Document save/load** — `.klip` = gzip JSON of layers (StorageProvider pickers). (pages/multi-doc later)
2. ✅ (v1) **Editor interaction** — select, move-drag, corner resize handle, rotate ±15° buttons,
   snap-to-grid toggle, undo/redo (snapshot stack, Ctrl+Z/Y), Delete key. (rotate handle + pan/zoom later)
3. ✅ (v1) **Panels** — layers list (select/reorder/duplicate/delete), inspector readout, toolbar. (palette/pages later)
4. ✅ (v1) **Creation tools** — star/circle/rect/squircle buttons, text→vector outlines (wordmarks),
   **boolean subtract/union/intersect (Clipper2, verified)**, **PowerClip (verified)**, gradient presets.
   (freehand path, golden scaffold, full color picker later)
5. ✅ (core) **AI-first** — C# command bus (`Ai/ActionRegistry`, 14 actions) + control server (`Ai/ControlServer`,
   HttpListener /health /manifest /call) writing the SAME `%TEMP%\klip_mcp.json` → the existing klip-mcp.exe
   bridge + MCP registration drive the .NET editor as-is. Verified live over the bus (squircle+text+powerclip+
   gradient+export → bus_proof.png). REMAINING: in-app Claude Code chat panel (port of ai_panel/claude_cli).
6. **Specialists** — CMYK (ICC), vectorize (reuse potrace binary), background removal (ONNX .NET),
   **font install**, **custom-font / glyph editing**, **PowerClip** (content clipped inside a shape).
7. **New power surfaced** — 3D / motion / bevel tools exposed in the editor + timeline UI.
8. **Single `.exe`** — Native AOT / self-contained single-file, bundling native libs + ffmpeg.
9. **Verify parity → delete the Python KLIP.**

New requests (PowerClip, boolean-subtract UI, custom fonts, kerning, snap-to-grid, advanced gradients,
intentional geometric logos, user-supplied fonts to force-install) all land inside this port, in .NET.

## Mega-roadmap (Leonel, 2026-07-12 — "most powerful engines" is rule #1)
- ✅ Real 3D camera (animatable dolly/truck/fov) + advanced keyframing (cubic-bezier) + trim-path
  line-draw + stroke engine + rulers + **infinite whiteboard canvas** (wheel zoom, middle-drag pan,
  re-render per zoom = true crisp vectors, never scaled PNG) + **AI built-in skills prompt**.
- 🔄 **Rive runtime embed** (play .riv): build saga — needs ClangCL toolset (installing); then C wrapper + P/Invoke.
- ⏳ 3D **generation** engine (text→3D asset pipeline; grow from Extruder + meshes).
- ⏳ Hyper-advanced text: full typography controls + **custom typeface creation** (glyph editor → font export).
- ⏳ Advanced image manipulation (filters/adjustments/perspective warp of images).
- ⏳ **Advanced timeline UI** (tracks/keyframes/curve editor panel in the editor).
- ⏳ **WebRTC same-wifi file transfer** (snapshare protocol heritage, native .NET).
- ⏳ **Background-removal model** (ONNX BiRefNet — model already at ~/.u2net) + **voice transcription
  (whisper.cpp/onnx) + voice generation (Piper)** bundled.
- ⏳ Advanced **brushes** (variable-width stroker), **textures**, **rotoscoping** (frame-by-frame masks).
