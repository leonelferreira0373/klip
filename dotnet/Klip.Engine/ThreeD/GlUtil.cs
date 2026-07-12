using System;
using Silk.NET.OpenGL;

namespace Klip.Engine.ThreeD;

public static class GlUtil
{
    public static uint Program(GL gl, string vs, string fs)
    {
        uint v = Compile(gl, ShaderType.VertexShader, vs);
        uint f = Compile(gl, ShaderType.FragmentShader, fs);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, v);
        gl.AttachShader(p, f);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("link: " + gl.GetProgramInfoLog(p));
        gl.DeleteShader(v);
        gl.DeleteShader(f);
        return p;
    }

    private static uint Compile(GL gl, ShaderType t, string src)
    {
        uint s = gl.CreateShader(t);
        gl.ShaderSource(s, src);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException(t + ": " + gl.GetShaderInfoLog(s));
        return s;
    }
}
