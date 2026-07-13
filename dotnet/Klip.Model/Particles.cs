using System;
using System.Collections.Generic;

namespace Klip.Model;

/// <summary>
/// Emissor de partículas — DETERMINÍSTICO: cada partícula é função pura de (Seed, índice, tempo).
/// birth_i = i/Rate; viva sse birth ≤ t &lt; birth+Lifetime. Rate/Lifetime/Speed/SpreadDeg/Gravity/
/// SpawnRadius/ParticleScale são Tracks (keyframáveis); Direction/Spin/Color/Fade são escalares.
/// </summary>
public sealed record ParticleSpec(
    int Seed = 12345,
    Track? Rate = null,            // partículas/seg (null → 30)
    Track? Lifetime = null,        // vida em seg (null → 1.0)
    Track? Speed = null,           // px/s velocidade inicial (null → 120)
    Track? SpreadDeg = null,       // meia-abertura do cone em ± graus (null → 30; 180 = omni)
    Track? Gravity = null,         // px/s² para BAIXO (+Y) (null → 0)
    double DirectionDeg = -90,     // direção central (0=+x/direita, -90=cima)
    double SpinDegPerSec = 0,      // rotação base por segundo
    uint ColorA = 0xFFFFFFFFu,     // cor extremo A (ambas default brancas → usa a fill da camada)
    uint ColorB = 0xFFFFFFFFu,     // cor extremo B
    Track? SpawnRadius = null,     // raio (px) de dispersão inicial (null → 0)
    Track? ParticleScale = null,   // multiplicador de escala do sprite (null → 1)
    double FadeIn = 0.10,          // fração inicial da vida a subir 0→1
    double FadeOut = 0.30,         // fração final da vida a descer 1→0
    bool ColorByLife = true,       // true: lerp A→B por vida; false: por hash (multicor)
    double SpinSpread = 0,         // ± variação do spin (deg/s)
    int Cap = 2000);               // máx. partículas vivas (custo)

/// <summary>Estado renderizável de uma partícula (offset local em px, escala, rotação, opacidade, cor).</summary>
public readonly record struct Particle(double OffsetX, double OffsetY, double Scale, double RotationDeg, double Opacity, uint Color);

/// <summary>Simulação PURA e determinística (sem Random/Date, sem estado entre frames).</summary>
public static class ParticleSim
{
    private static class Ch { public const int Angle = 1, Speed = 2, JitterR = 3, JitterA = 4, Spin = 5, Color = 6, Life = 7; }

    internal static double Rand(int seed, int i, int ch)
    {
        unchecked
        {
            int n = seed * 374761393 + i * 668265263 + ch * 2147483647;
            n = (n ^ (n >> 13)) * 1274126177; n ^= n >> 16;   // avalanche antes do mix do motor
            return Track.HashInt(n);                            // [-1,1)
        }
    }
    internal static double Rand01(int seed, int i, int ch) => Rand(seed, i, ch) * 0.5 + 0.5;   // [0,1)

    /// <summary>Preenche outBuf com as partículas VIVAS em t (determinístico). fallbackColor = cor se A==B==branco.</summary>
    public static void Emit(ParticleSpec s, double t, List<Particle> outBuf, uint fallbackColor)
    {
        outBuf.Clear();
        if (t < 0) return;
        double rate = Math.Max(0, s.Rate?.Eval(t) ?? 30);
        if (rate <= 1e-9) return;
        double life0 = Math.Max(1e-4, s.Lifetime?.Eval(t) ?? 1.0);
        int iMax = (int)Math.Floor(t * rate + 1e-9);
        int iMin = Math.Max(0, (int)Math.Ceiling((t - life0) * rate - 1e-9));
        bool defColor = s.ColorA == 0xFFFFFFFFu && s.ColorB == 0xFFFFFFFFu;
        uint cA = defColor ? fallbackColor : s.ColorA;
        uint cB = defColor ? fallbackColor : s.ColorB;
        const double DEG = Math.PI / 180.0;

        for (int i = iMin; i <= iMax; i++)
        {
            double birth = i / rate;
            double life = Math.Max(1e-4, s.Lifetime?.Eval(birth) ?? 1.0);
            double age = t - birth;
            if (age < 0 || age >= life) continue;
            double frac = age / life;

            double spread = s.SpreadDeg?.Eval(birth) ?? 30;
            double ang = (s.DirectionDeg + spread * Rand(s.Seed, i, Ch.Angle)) * DEG;
            double speed = s.Speed?.Eval(birth) ?? 120;
            double vx = Math.Cos(ang) * speed, vy = Math.Sin(ang) * speed;
            double grav = s.Gravity?.Eval(birth) ?? 0;                    // +Y = baixo
            double jr = (s.SpawnRadius?.Eval(birth) ?? 0) * Rand01(s.Seed, i, Ch.JitterR);
            double ja = Math.PI * Rand(s.Seed, i, Ch.JitterA);
            double x = Math.Cos(ja) * jr + vx * age;
            double y = Math.Sin(ja) * jr + vy * age + 0.5 * grav * age * age;
            double scale = s.ParticleScale?.Eval(birth) ?? 1.0;
            double rot = (s.SpinDegPerSec + s.SpinSpread * Rand(s.Seed, i, Ch.Spin)) * age;

            double op = 1.0;
            if (s.FadeIn > 1e-6 && frac < s.FadeIn) op = frac / s.FadeIn;
            else if (s.FadeOut > 1e-6 && frac > 1 - s.FadeOut) op = (1 - frac) / s.FadeOut;
            op = Math.Clamp(op, 0, 1);

            double mix = s.ColorByLife ? frac : Rand01(s.Seed, i, Ch.Color);
            uint col = ColorMath.Lerp(cA, cB, mix);

            outBuf.Add(new Particle(x, y, scale, rot, op, col));
            if (outBuf.Count >= s.Cap) return;
        }
    }
}
