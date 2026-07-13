using System;
using System.Linq;
using System.Threading.Tasks;
using Klip.Engine;
using Klip.Model;

// KLIP demo authoring driver — CENDAP-style vertical motion-graphics promo.
// Build a Comp in code (white bg, brand-red, kinetic typography, spring drops) → export MP4.
internal static class Demo
{
    const int W = 1080, H = 1920;
    const double CX = 0;              // PosX/PosY são OFFSET a partir do centro do comp (não top-left)
    const uint RED = 0xFFE4162B, WHITE = 0xFFFFFFFF, INK = 0xFF14161B;

    // Map-pin silhouette (head centred at local 0,0; tail point at 0,+230).
    const string PIN =
        "M -130,0 C -130,-72 -72,-130 0,-130 C 72,-130 130,-72 130,0 " +
        "C 130,78 44,150 0,230 C -44,150 -130,78 -130,0 Z";

    static string FAM = "Segoe UI";
    static readonly string Scratch =
        @"C:\Users\leone\AppData\Local\Temp\claude\C--\79f0fc87-55f6-402b-a0ef-3884390cc77c\scratchpad";

    // ---- track helpers ------------------------------------------------------
    static Track K(params (double t, double v, Easing e)[] ks)
        => new Track(ks.Select(k => new Keyframe(k.t, k.v, k.e)).ToArray());
    static Track C(double v) => Track.Const(v);
    static Track Fade(double tIn, double hold, double outT, double gone)
        => K((tIn, 0, Easing.EaseOut), (hold, 1, Easing.Linear), (outT, 1, Easing.EaseIn), (gone, 0, Easing.Linear));
    static Track Pop(double tIn, double from = 0.72, double settle = 0.5)
        => K((tIn, from, Easing.EaseOutBack), (tIn + settle, 1, Easing.Linear));
    static Track SlideY(double tIn, double y, double dist = 64, double settle = 0.5)
        => K((tIn, y + dist, Easing.EaseOutBack), (tIn + settle, y, Easing.Linear));

    static string T(string s, float size) =>
        TextShape.TextPathD(s, size, FAM, true) ?? TextShape.TextPathD(s, size, "Segoe UI", true)!;

    static async Task<int> Main(string[] args)
    {
        try { await FontRegistry.Shared.LoadAsync("Poppins"); FAM = "Poppins"; Console.WriteLine("font: Poppins (Google)"); }
        catch (Exception e) { Console.WriteLine("Poppins download failed (" + e.Message + ") → Segoe UI"); }

        var comp = Build();

        if (args.Contains("probe"))
        {
            foreach (var t in new[] { 0.9, 1.6, 3.1, 3.6, 5.0, 5.5, 6.8, 7.6 })
                Mp4Exporter.ProbePng(comp, t, System.IO.Path.Combine(Scratch, $"probe_{t:0.0}.png"));
            Console.WriteLine("probes written to scratchpad");
            return 0;
        }

        if (args.Contains("particles"))
        {
            var pcomp = BuildParticles();
            int pf = (int)Math.Round(pcomp.Duration * pcomp.Fps);
            Console.WriteLine($"rendering confetti {pf} frames…");
            Mp4Exporter.Export(pcomp, @"C:\Users\leone\Downloads\KLIP_particles_demo.mp4", "ffmpeg");
            foreach (var t in new[] { 0.4, 0.8, 1.2, 1.7, 2.2, 2.8 })
                Mp4Exporter.ProbePng(pcomp, t, System.IO.Path.Combine(Scratch, $"pcl_{t:0.0}.png"));
            Console.WriteLine("confetti probes + mp4 written");
            return 0;
        }

        string outPath = @"C:\Users\leone\Downloads\KLIP_CENDAP_demo.mp4";
        int frames = (int)Math.Round(comp.Duration * comp.Fps);
        Console.WriteLine($"rendering {frames} frames @ {comp.Width}x{comp.Height} {comp.Fps}fps ...");
        int last = -1;
        Mp4Exporter.Export(comp, outPath, "ffmpeg", onProgress: p =>
        {
            int pc = (int)(p * 100); if (pc != last && pc % 5 == 0) { Console.Write($"\r{pc}%   "); last = pc; }
        });
        Console.WriteLine($"\nDONE → {outPath}");
        return 0;
    }

    static Comp Build()
    {
        var layers = new System.Collections.Generic.List<Layer>();

        // ---- ambient soft red blobs (depth). Y = offset a partir do centro (960). ----
        layers.Add(new Layer("blob1", MorphTrack.Static(Shapes.Circle(430)), 0x14E4162B,
            PosX: C(220), PosY: K((0, -490, Easing.EaseInOut), (4, -400, Easing.EaseInOut), (8.4, -490, Easing.EaseInOut)),
            BlurRadius: C(95), Opacity: C(1)));
        layers.Add(new Layer("blob2", MorphTrack.Static(Shapes.Circle(360)), 0x10E4162B,
            PosX: C(-240), PosY: K((0, 400, Easing.EaseInOut), (4, 330, Easing.EaseInOut), (8.4, 400, Easing.EaseInOut)),
            BlurRadius: C(85), Opacity: C(1)));

        // ---- BEAT 1 — pin logo spring-drop (0.2–2.4). Rest dy=-200 (visual y≈760). ----
        var pinY = K((0.2, -1300, Easing.EaseOutBack), (1.05, -200, Easing.Linear),
                     (2.05, -200, Easing.EaseIn), (2.45, -1360, Easing.Linear));
        var pinS = K((0.2, 0.60, Easing.EaseOutBack), (0.9, 1.0, Easing.Linear));
        var pinO = Fade(0.2, 0.55, 2.05, 2.4);
        var mbPin = K((0.2, 1.4, Easing.EaseOut), (1.0, 0, Easing.Linear));   // motion-blur só durante a queda
        layers.Add(new Layer("pinBody", MorphTrack.Static(PIN), RED,
            PosX: C(CX), PosY: pinY, Scale: pinS, Opacity: pinO, Shadow: true, MotionBlur: mbPin, MotionBlurSamples: 14));
        layers.Add(new Layer("pinDot", MorphTrack.Static(Shapes.Circle(50)), WHITE,
            PosX: C(CX), PosY: pinY, Scale: pinS, Opacity: pinO, MotionBlur: mbPin, MotionBlurSamples: 14));

        // ---- BEAT 2 — "Marcar uma consulta?" kinetic type (2.15–4.4) ----
        layers.Add(new Layer("m1", MorphTrack.Static(T("Marcar uma", 116)), RED,
            PosX: C(CX), PosY: SlideY(2.15, -55), Scale: Pop(2.15, 0.8), Opacity: Fade(2.15, 2.55, 4.15, 4.45)));
        layers.Add(new Layer("m2", MorphTrack.Static(T("consulta?", 150)), RED,
            PosX: C(CX), PosY: SlideY(2.32, 110), Scale: Pop(2.32, 0.8), Opacity: Fade(2.32, 2.72, 4.15, 4.45)));
        layers.Add(new Layer("uline", MorphTrack.Static(Shapes.Capsule(240, 11)), RED,
            PosX: C(CX), PosY: C(208),
            ScaleX: K((2.55, 0.01, Easing.EaseOut), (2.95, 1, Easing.Linear)), ScaleY: C(1),
            Opacity: Fade(2.55, 2.8, 4.15, 4.4)));

        // ---- BEAT 3 — "Já está" red block slide-in from left (4.2–6.2). Block dy=+20. ----
        layers.Add(new Layer("block", MorphTrack.Static(Shapes.Superellipse(450, 205, 8)), RED,
            PosX: K((4.2, -1360, Easing.EaseOutBack), (4.78, CX, Easing.Linear)), PosY: C(20),
            Rotation: K((4.2, -7, Easing.EaseOutBack), (4.8, 0, Easing.Linear)),
            Opacity: Fade(4.2, 4.5, 6.0, 6.25), Shadow: true,
            MotionBlur: K((4.2, 1.5, Easing.EaseOut), (4.78, 0, Easing.Linear)), MotionBlurSamples: 16));
        layers.Add(new Layer("jaesta", MorphTrack.Static(T("Já está", 168)), WHITE,
            Parent: "block", PosX: C(0), PosY: C(0),
            Scale: K((4.5, 0.5, Easing.EaseOutBack), (5.05, 1, Easing.Linear)),
            Opacity: Fade(4.5, 4.85, 6.0, 6.2)));
        layers.Add(new Layer("cross", MorphTrack.Static(Shapes.Cross(46, 17)), WHITE,
            Parent: "block", PosX: C(300), PosY: C(-150),
            Scale: Pop(4.75, 0.2, 0.45), Opacity: Fade(4.75, 5.0, 6.0, 6.2)));

        // ---- BEAT 4 — end card (6.05–8.4). endPin dy=-212 (visual ≈748). ----
        var epO = Fade(6.05, 6.45, 8.3, 8.5);
        var epS = C(0.62);
        var epY = K((6.05, -260, Easing.EaseOutBack), (6.55, -212, Easing.Linear));
        layers.Add(new Layer("endPin", MorphTrack.Static(PIN), RED,
            PosX: C(CX), PosY: epY, Scale: epS, Opacity: epO, Shadow: true));
        layers.Add(new Layer("endDot", MorphTrack.Static(Shapes.Circle(50)), WHITE,
            PosX: C(CX), PosY: epY, Scale: epS, Opacity: epO));
        layers.Add(new Layer("cendap", MorphTrack.Static(T("CENDAP", 132)), RED,
            PosX: C(CX), PosY: SlideY(6.28, 50, 40), Scale: Pop(6.28, 0.84), Opacity: Fade(6.28, 6.6, 8.3, 8.5)));
        layers.Add(new Layer("url", MorphTrack.Static(T("agendacendap.com.br", 58)), INK,
            PosX: C(CX), PosY: SlideY(6.5, 160, 34), Opacity: Fade(6.5, 6.85, 8.3, 8.5)));

        return new Comp(W, H, 60, 8.5, 0xFFFFFFFF, layers, BackgroundArgb2: 0xFFFDF3F4);
    }

    // Chafariz de confete: emissor no fundo-centro, sobe e cai por gravidade, multicor + spin.
    static Comp BuildParticles()
    {
        var spec = new ParticleSpec(
            Seed: 2026,
            Rate: Track.Const(90),
            Lifetime: Track.Const(2.6),
            Speed: Track.Const(1050),
            SpreadDeg: Track.Const(28),
            Gravity: Track.Const(900),
            DirectionDeg: -90,               // dispara para CIMA; a gravidade puxa → arco
            SpinDegPerSec: 320,
            SpinSpread: 260,
            ColorA: 0xFFE4162Bu, ColorB: 0xFF2D6CDFu, ColorByLife: false,   // multicor vermelho↔azul por peça
            ParticleScale: Track.Const(1.0), FadeIn: 0.05, FadeOut: 0.25);
        var confetti = new Layer("confetti", MorphTrack.Static(Shapes.Rect(16, 10)), 0xFFFFFFFF,
            PosX: C(0), PosY: C(430), Particles: spec);   // emissor perto do fundo (offset +430 do centro)
        return new Comp(1080, 1080, 60, 3.2, 0xFFFFFFFF, new System.Collections.Generic.List<Layer> { confetti },
            BackgroundArgb2: 0xFFF3F5FF);
    }
}
