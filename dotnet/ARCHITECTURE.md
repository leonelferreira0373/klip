# KLIP on .NET — Architecture & Migration Blueprint

**Decision (2026-07-11):** rewrite KLIP on **C#/.NET 10** for maximum real power. Rationale: for a GPU
engine the power lives in the GPU + shaders (language-independent); C#/.NET gives full native GPU
access (SkiaSharp + Silk.NET), Native-AOT single-exe, no GIL, and a P/Invoke escape hatch to C++
for hot loops — the max power-to-screen with the best velocity, unifying with the LUGATECH .NET
stack. The Python app (`C:\Users\leone\klip\pc`) stays as the executable **reference spec** during
migration; nothing is lost — the AI-first design (MCP command bus) is language-agnostic.

## Target stack
| Concern | Choice | Why |
|---|---|---|
| Runtime | **.NET 10** (Native AOT for release) | single self-contained `.exe`, no interpreter, no bundling pain |
| 2D render / effects | **SkiaSharp** (Skia GPU backend) | gaussian blur, image filters, GPU vector — the effects engine, mostly built |
| Low-level GPU | **Silk.NET** (D3D12/Vulkan) + `pyside6-qsb`→ HLSL/SPIR-V | custom compositor, morph on GPU, shader passes |
| Shell / UI | **Avalonia** (Skia-native, cross-platform) | its render layer *is* Skia → seamless with the engine; WPF = Windows-only fallback |
| Geometry booleans | **Clipper2** (native C# — original) | replaces the pyclipr binding |
| Vectorize | potrace/mkbitmap **binaries** (call as-is) | no port needed |
| Background removal | **onnxruntime** (.NET) + BiRefNet | first-class C# |
| Color / CMYK | SkiaSharp color mgmt + LittleCMS.NET | ICC print-ready |
| AI-first | **ModelContextProtocol** (.NET MCP SDK) | command bus + `klip-mcp` as an AOT exe (trivial, no python) |
| Voice (later) | **Piper** (bundled, offline) | free, CPU, decent |

## Solution layout (`C:\Users\leone\klip\dotnet`)
```
Klip.sln
  Klip.Engine        // render-graph, compositor, effects, morph, timeline eval — SkiaSharp/Silk.NET (no UI)
  Klip.Model         // document schema (records + System.Text.Json), .mcv IO, undo commands
  Klip.App           // Avalonia shell: canvas, panels, command bus, transport
  Klip.Mcp           // control server + action registry + klip-mcp.exe (AOT) — AI-first
  Klip.Cli           // in-app Claude Code backend (port of ai/claude_cli.py)
  Klip.Tests         // headless engine + model tests
```

## Module map (Python → C#)
| Python (reference) | C# home |
|---|---|
| `document/schema.py` (pydantic) | `Klip.Model` records + `System.Text.Json` |
| `document/geometry.py`, `curves.py`, `booleans.py` | `Klip.Engine.Geometry` (+ native Clipper2) |
| `canvas/scene.py`, `view.py` | `Klip.App` Avalonia canvas (Skia draw) |
| `mcp/registry.py` (26 actions), `server.py`, `mcp_stdio.py` | `Klip.Mcp` (registry + control HTTP + AOT stdio bridge) |
| `ai/claude_cli.py` (CLI backend) | `Klip.Cli` |
| `export.py`, `color/cmyk.py`, `ai/bg_remover.py` | `Klip.Engine.Export`, `.Color`, `.BgRemove` |

## Migration order — ENGINE-CORE-FIRST (not big-bang)
1. **Engine core (greenfield, proves the stack)** — `Klip.Engine`: timeline eval + SkiaSharp GPU compositor + separable gaussian blur + CPU path-morph + MP4 export (grab framebuffer → ffmpeg).
2. **Model + shell** — port `Klip.Model`; stand up `Klip.App` (Avalonia) editing surface around the engine; wire the command bus + undo.
3. **AI-first** — `Klip.Mcp` (control server + registry + AOT `klip-mcp`) and `Klip.Cli` → the in-app Claude Code drives the C# app exactly as today.
4. **Parity + power** — CMYK, vectorize, bg-remover, brushes, 2.5D kit, effects stack, morph→GPU, the pro-logo methodology tools.

The Python app keeps running throughout — zero downtime; it's the spec we port against.

## First milestone (one vertical slice, not a toy)
A comp with `duration`/`fps` holding a logo layer whose path **morphs A→B** across the timeline while
a **keyframed gaussian blur** animates its radius — played at 60fps through the SkiaSharp GPU
compositor and **exported to MP4**. Runs through every real seam: `Klip.Model` (comp fps + MorphTrack +
Effect) → `Klip.Engine` timeline eval → compositor + blur shader → transport → MP4. Proves GPU + one
real effect + one real morph + timeline playback + export, end to end.

## Risks & mitigations
- **GPU context / software fallback** silently killing perf → assert hardware D3D at startup, fail loud.
- **Shader authoring + AOT** → bake shaders at build; keep the shader set small (separable blur + full-screen color pass cover most).
- **Rewrite drift** → engine-core-first + the Python app as a live oracle keeps scope honest; port module-by-module with headless tests.
- **Don't reinvent the rasterizer** → SkiaSharp owns pixels; Clipper2 owns booleans; we own the render-graph + morph, nothing lower.
