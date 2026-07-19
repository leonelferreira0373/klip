# KLIP Product Studio — Fase 1 (PBR + IBL) — Design

**Data:** 2026-07-19
**Autor:** Leonel + Claude (brainstorming)
**Estado:** aprovado o design; aguarda plano de implementação (writing-plans)

## 1. Objetivo

Trazer **product visualization foto-realista** para dentro do KLIP: renders realistas de produto e close-ups hiper-detalhados (estilo "PSD de product design") para os ativos do Leonel — logos (GS), cartões, etiquetas, embalagem em relevo. Sem depender de ferramentas externas (Blender); é feature própria e vendável do KLIP.

## 2. Âmbito

**Faseado.** Esta spec cobre a **Fase 1**.

- **Fase 1 (esta spec):** renderizar a geometria **extrudida** que o KLIP já sabe criar (via `Extruder`), com **PBR + IBL (HDRI) + estúdio + macro/DoF**, materiais por **presets + sliders**, e **still final de alta qualidade** (4K/CMYK). Preview em tempo real.
- **Fase 1.5 (futuro):** suporte a **mapas de textura** próprios (albedo/roughness/metal/normal/AO).
- **Fase 2 (futuro):** **importar/gerar malhas 3D reais** (glTF/OBJ ou IA imagem→3D) — sneakers, garrafas, caixas — a alimentar o mesmo pipeline PBR.

**Fora de âmbito (Fase 1):** importar malhas, mapas de textura próprios, path tracing, SSS, cáusticas, múltiplos produtos em cena complexa.

## 3. Estado atual do KLIP (ground truth do explorador)

Código em `C:\Users\leone\klip\dotnet`. Relevante:

**Já existe e é reutilizável:**
- Contexto **GL+Skia partilhado** num surface offscreen (`Klip.Engine\GpuSession.cs`, GLFW oculto 1×1).
- **FBO offscreen + depth + readback (`glReadPixels`) + composição Skia** por ordem de camada (`Klip.Engine\ThreeD\MeshPass.cs`, `Hybrid3D.cs`; via zero-copy em `Scene3D.cs`/`GlComposite.cs`).
- **Fila de render 3D thread-affine** (`Hybrid3D.cs:30-61`, thread `klip-3d`).
- **Câmara perspetiva animável** (`Klip.Model\Model.cs:287` `CameraRig`: eye/target/FOV keyframáveis; `Mat4.cs` LookAt/Perspective, clip-z GL).
- **Extrusor 2D→3D** robusto (`Klip.Engine\ThreeD\Extruder.cs`: path → Clipper2 union/inset → LibTessDotNet caps → paredes/bevel). Vértice atual = **pos+normal** (flat).
- **Cor sRGB↔linear correta** (`Klip.Model\ColorMath.cs`), **ICC sRGB→CMYK** (`Klip.Engine\CmykExport.cs`, Magick.NET), **export 4K vetor-reraster** (`Renderer.cs:18-29`, `EngineExport.cs`), **MP4/GIF via ffmpeg** (`Mp4Exporter.cs`).
- **Registo de verbos AI/MCP** (`Klip.App\Ai\ActionRegistry.cs`, `ControlServer.cs`, `McpStdioBridge.cs`) — já há verbos de export.

**Falta (a construir):** sistema de materiais, texturas/UVs/tangentes, PBR (só há Blinn-Phong de 1 luz em `MeshPass.cs:94-114`), IBL/HDRI, tonemapping (output nem é sRGB-encoded), luzes/sombras, DoF, alvos HDR float, AA melhor que 2× SSAA, renderer offline por acumulação. **Bug a corrigir:** o 3D no export está preso a 2×base (`Hybrid3D.cs:84`), logo stills 3D não são 4K reais.

## 4. Abordagem escolhida

**A — Renderer PBR+IBL próprio em GLSL, sobre o pipeline GL+Skia existente.** Reutiliza toda a canalização; técnica-padrão de motores para este look; escala para a Fase 2 (importar malhas alimenta o mesmo passe). Rejeitadas: **B** (lib/motor C# externo — luta com a arquitetura single-context, dependências), **C** (path tracer offline — enorme, lento, preview≠final, exagero para logos/cartões).

## 5. Arquitetura — componentes novos

Todos em `Klip.Engine\ThreeD\` salvo indicação. Reutilizam `GpuSession` (contexto) e o padrão thread/readback do `Hybrid3D`.

- **`PbrPass.cs`** — passe forward num **FBO HDR float (RGBA16F)** + depth. Shader **Cook-Torrance/GGX** (workflow metallic-roughness). Substitui o Blinn-Phong para camadas "produto". Amostra o IBL + luzes analíticas + sombra.
- **`HdriLoader.cs`** — carrega HDRI equiretangular `.hdr`/`.exr` → pixels float (via Magick.NET, dep existente).
- **`IblProbe.cs`** — equiret→**cubemap**; gera **mapa de irradiância** (difuso), **especular pré-filtrado** (mip-chain por roughness) e **BRDF-LUT** (2D, pré-computado uma vez e em cache). É o núcleo do realismo.
- **`Tonemap.cs`** — passe full-screen HDR→**AgX**→sRGB 8-bit, com **exposição**. (AgX; ACES como alternativa comentada.)
- **`DofPass.cs`** — post-process: circle-of-confusion a partir de foco+abertura → bokeh por profundidade. Para os close-ups macro.
- **Chão + sombra** — plano de estúdio no passe 3D + **shadow map** da luz principal (assenta o produto). Ambiente (env) desenhado como fundo do passe 3D para reflexos corretos.
- **Acumulação offline** — modo "render still": acumula **N amostras** com jitter sub-pixel da câmara (e amostragem de DoF/luz de área) num buffer HDR → AA limpo + IBL/DoF sem ruído; resolve na **resolução real do export** (corrige o cap de 2×).
- **AA tempo-real** — MSAA no FBO HDR (ou SSAA moderado) para o preview.

## 6. Modelo de dados (`Klip.Model\Model.cs`)

- **Vértice**: estender de `pos+normal` para **`pos+normal+uv+tangent`** (necessário p/ anisotropia do metal escovado e p/ mapas na Fase 1.5). `Extruder` passa a gerar UVs planares (frente pela bounding-box do path; laterais wrap simples) + tangentes.
- **`PbrMaterial`** (por camada/objeto): `Preset { BrushedMetal, Gold, Chrome, Glass, Pearl, MattePlastic, GlossyPlastic }`, `BaseColorArgb`, `Roughness`, `Metalness`, `Clearcoat`, `ClearcoatRoughness`, `IOR` (vidro), `Tint`. Presets definem defaults; sliders sobrepõem.
- **`Studio3D`** (por Comp): `HdriId`, `EnvRotation`, `EnvIntensity`, `Exposure`, `Tonemap`, `Dof { FocusDistance, Aperture }`, luz-chave (dir/intensidade). Campos keyframáveis (via `Track`) onde faz sentido — dá **turntable de borla** depois, reusando a câmara animável.
- Camada "produto" = camada com `PbrMaterial` (evolução do `Extrude3D` atual, que fica como caminho legado/Blinn-Phong).

## 7. Fluxo de dados

`Layer.path` → `Extruder` (+UV+tangent) → mesh cache → **`PbrPass`** (FBO HDR; GGX + IBL[irradiância/pré-filtrado/BRDF-LUT do HDRI escolhido] + luz + shadow map) → [still: **acumula N amostras** jitteradas] → **`Tonemap` (AgX)** → **`DofPass`** → resolve sRGB → readback → **composição Skia** na ordem da camada → export existente (**PNG / 4K / CMYK / MP4**).

## 8. Sub-fases (entrega incremental)

- **1a** — FBO HDR float + shader GGX PBR + 1 luz direcional/área + AgX tonemap (sem IBL). Já melhor que o Blinn-Phong.
- **1b** — IBL completo (HDRI→cubemap→irradiância+pré-filtrado+BRDF-LUT) + seletor de HDRI. **O salto de realismo.**
- **1c** — plano+sombra + DoF + acumulação offline + **fix do cap 4K 3D**. Qualidade de still final.
- **1d** — painel UI (presets+sliders+HDRI+DoF+"Render still") + verbos MCP. Utilizável ponta-a-ponta.

## 9. UI (`Klip.App`)

Painel "Estúdio / Produto": dropdown de **material** + sliders (roughness/metalness/tint/clearcoat); seletor de **HDRI** + rotação/intensidade/exposição; **DoF** (foco/abertura); tonemap; botão **"Render still"** (acumulação offline → alimenta o export PNG/4K/CMYK). Preview atualiza ao vivo ao mexer nos parâmetros.

## 10. IA / MCP (`ActionRegistry`)

Novos verbos, no padrão existente: `set_material {layer, preset, params}`, `set_studio {hdri, rotation, exposure, dof…}`, `render_product_still {resolution, samples, out}`. Permite dirigir renders de produto por linguagem natural.

## 11. Erros / fallbacks

- GL/shader-compile ou falha de carregamento de HDRI → **fallback ao passe Blinn-Phong existente** (nada parte). Falha de HDRI → env neutro por defeito.
- Erros de GPU já são expostos via `Hybrid3D.GpuError` / `get_state` — estender para os novos passes.
- Sem placa capaz de RGBA16F → cair para RGBA8 (aviso) mantendo o pipeline funcional.

## 12. Testes (`Klip.Tests`)

- **Golden-image:** render de {material×HDRI×câmara} conhecidos vs referência (diff perceptual com tolerância).
- **Unitário:** valores da BRDF-LUT vs referência; curva AgX; matemática do CoC (DoF); geração de UV/tangente.
- **Regressão:** render do logo GS em cada preset + export 4K + CMYK, confirmando **zero regressões** no 2D/3D existente.

## 13. Dependências / assets

- **HDRIs de estúdio** embutidos: 4-6, 1-2K, CC0 (Poly Haven). Bundle no app.
- **Sem novas deps pesadas:** Magick.NET (já presente) lê `.hdr`/`.exr`; Silk.NET.OpenGL (presente) faz tudo o resto. (SharpGLTF/Assimp só entram na **Fase 2**.)

## 14. Critério de sucesso (Fase 1)

> Pego no logo GS → escolho "metal escovado" → escolho um HDRI de estúdio → ajusto o DoF → exporto um still **4K** (e **CMYK**) que parece **foto de produto real** — sem partir nada do 2D/3D existente do KLIP.
