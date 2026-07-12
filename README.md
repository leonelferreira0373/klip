# KLIP Animator

An AI-first native design and motion-graphics editor for Windows — a single self-contained `.exe`, no installer, no runtimes.

Built in C#/.NET 10 with Avalonia + SkiaSharp. The whole editor is driven by an HTTP command bus, so the same actions power both the UI and an embedded AI that can compose, render, *look at its own output*, and refine — like a motion designer.

## Features

- **After Effects-style expression language** — real JavaScript on any property: `wiggle`, `loopOut`, `linear`, `valueAtTime`, springs.
- **Real 3D** — extrude + bevel, lit, with a keyframable camera (dolly / orbit / truck). Vector, crisp at 4K.
- **Built-in browser + vision** — search, click any image to pull it onto the canvas; the AI sees the page.
- **Voice → text on-device** — transcribe audio/video locally (Whisper), no cloud.
- **Lossless export** — MP4, GIF, SVG, Lottie, and CMYK TIFF with ICC profile for print.
- **Native Rive + Lottie runtimes** — custom C# runtimes for `.riv` and bodymovin `.json`.
- **Anchor points, parenting/nulls, trim-paths, path booleans, gradients, timeline editor.**

## Download

Grab the latest single `.exe` from **[Releases](https://github.com/leonelferreira0373/klip/releases/latest)** — Windows 10/11, ~70 MB, no dependencies. Double-click and create.

## Build from source

```bash
cd dotnet
dotnet build -c Release
# single-file self-contained publish:
dotnet publish Klip.App/Klip.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -o out
```

Requires the .NET 10 SDK. See [`dotnet/ARCHITECTURE.md`](dotnet/ARCHITECTURE.md) for the engine/app layout.

## AI credits

KLIP runs Sonnet, Haiku, or KLIP AI. Bring your own Anthropic key (BYOK, stored locally in `%APPDATA%\Klip\ai.json`), or top up credits — pay by transfer and send the receipt inside the app; each receipt is verified by its EMIS digital signature.

## Structure

- `dotnet/` — the .NET solution (`Klip.App` UI, `Klip.Engine` render/animation engine, `Klip.Model`).
- `landing/` — the marketing site (static, deployed to Vercel).

---

© Ferreira Korp · Made in Angola.
