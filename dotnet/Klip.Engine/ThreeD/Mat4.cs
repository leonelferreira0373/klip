using System;
using System.Numerics;

namespace Klip.Engine.ThreeD;

/// <summary>Column-major 4x4 matrices (GL convention, clip-z in [-1,1]) as float[16].
/// Rolled by hand to avoid System.Numerics' D3D [0,1] clip-z mismatch with OpenGL.</summary>
public static class Mat4
{
    public static float[] Identity()
    {
        var m = new float[16];
        m[0] = m[5] = m[10] = m[15] = 1f;
        return m;
    }

    public static float[] Multiply(float[] a, float[] b) // a*b, column-major
    {
        var r = new float[16];
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
            {
                float s = 0;
                for (int k = 0; k < 4; k++) s += a[k * 4 + row] * b[c * 4 + k];
                r[c * 4 + row] = s;
            }
        return r;
    }

    public static float[] Perspective(float fovY, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovY / 2f);
        var m = new float[16];
        m[0] = f / aspect;
        m[5] = f;
        m[10] = (far + near) / (near - far);
        m[11] = -1f;
        m[14] = (2f * far * near) / (near - far);
        return m;
    }

    public static float[] LookAt(Vector3 eye, Vector3 center, Vector3 up)
    {
        var f = Vector3.Normalize(center - eye);
        var s = Vector3.Normalize(Vector3.Cross(f, up));
        var u = Vector3.Cross(s, f);
        var m = new float[16];
        m[0] = s.X; m[4] = s.Y; m[8] = s.Z; m[12] = -Vector3.Dot(s, eye);
        m[1] = u.X; m[5] = u.Y; m[9] = u.Z; m[13] = -Vector3.Dot(u, eye);
        m[2] = -f.X; m[6] = -f.Y; m[10] = -f.Z; m[14] = Vector3.Dot(f, eye);
        m[15] = 1f;
        return m;
    }

    public static float[] RotationY(float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        var m = Identity();
        m[0] = c; m[8] = s; m[2] = -s; m[10] = c;
        return m;
    }

    public static float[] RotationX(float a)   // pitch (inclinar p/ trás/frente)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        var m = Identity();
        m[5] = c; m[9] = -s; m[6] = s; m[10] = c;
        return m;
    }

    public static float[] RotationZ(float a)   // roll (giro no plano da face — leque de cartões)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        var m = Identity();
        m[0] = c; m[4] = -s; m[1] = s; m[5] = c;
        return m;
    }

    public static float[] Translation(float x, float y, float z)
    {
        var m = Identity();
        m[12] = x; m[13] = y; m[14] = z;
        return m;
    }

    public static float[] Scale(float s)
    {
        var m = Identity();
        m[0] = s; m[5] = s; m[10] = s;
        return m;
    }
}
