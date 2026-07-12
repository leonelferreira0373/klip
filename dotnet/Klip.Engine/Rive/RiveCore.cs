using System;
using System.Collections.Generic;

namespace Klip.Engine.Rive;

/// <summary>
/// A deserialized Rive core object: a type key + a bag of property values keyed by property key.
/// The whole .riv format is a stream of these; typed views (Artboard, Shape…) interpret known keys.
/// This decouples PARSING (generic) from INTERPRETATION (needs the key tables).
/// </summary>
public sealed class RiveObject
{
    public int TypeKey { get; }
    public Dictionary<int, object> Props { get; } = new();
    public int LocalId { get; set; }   // index within the artboard's object list

    public RiveObject(int typeKey) => TypeKey = typeKey;

    public double D(int key, double def = 0) => Props.TryGetValue(key, out var v) ? Convert.ToDouble(v) : def;
    public uint U(int key, uint def = 0) => Props.TryGetValue(key, out var v) ? Convert.ToUInt32(v) : def;
    public int I(int key, int def = 0) => Props.TryGetValue(key, out var v) ? Convert.ToInt32(v) : def;
    public bool B(int key, bool def = false) => Props.TryGetValue(key, out var v) ? Convert.ToBoolean(v) : def;
    public string S(int key, string def = "") => Props.TryGetValue(key, out var v) ? (string)v : def;
    public bool Has(int key) => Props.ContainsKey(key);
}

/// <summary>The field-type index → how the value is read from the stream. Filled from the ToC extraction.</summary>
public enum RiveFieldType { Uint = 0, String = 1, Double = 2, Color = 3, Bool = 4, Bytes = 5, Id = 6 }

/// <summary>A parsed .riv document: the object stream split into artboards.</summary>
public sealed class RiveDocument
{
    public int MajorVersion, MinorVersion;
    public ulong FileId;
    public List<RiveArtboard> Artboards { get; } = new();
    public RiveArtboard? First => Artboards.Count > 0 ? Artboards[0] : null;
}

/// <summary>One artboard: its object list (shapes/paths/paints/nodes) + its animations.</summary>
public sealed class RiveArtboard
{
    public RiveObject Root = null!;              // the Artboard core object
    public List<RiveObject> Objects { get; } = new();
    public List<RiveAnimation> Animations { get; } = new();

    public double Width, Height, OriginX = 0, OriginY = 0;
    public string Name = "";
}

/// <summary>A linear animation: fps, duration in frames, loop mode, and its keyed objects.</summary>
public sealed class RiveAnimation
{
    public string Name = "";
    public int Fps = 60;
    public int DurationFrames;
    public int LoopValue;                        // 0 oneShot, 1 loop, 2 pingPong
    public double Speed = 1;
    public List<RiveKeyedObject> KeyedObjects { get; } = new();

    public double DurationSeconds => Fps > 0 ? (double)DurationFrames / Fps : 0;
}

/// <summary>Keyframes for one object's properties.</summary>
public sealed class RiveKeyedObject
{
    public int ObjectId;                         // local id of the target object
    public List<RiveKeyedProperty> Properties { get; } = new();
}

public sealed class RiveKeyedProperty
{
    public int PropertyKey;
    public List<RiveKeyFrame> KeyFrames { get; } = new();
}

/// <summary>One keyframe: time (frame), value, and interpolation to the NEXT keyframe.</summary>
public struct RiveKeyFrame
{
    public double Frame;
    public double Value;                         // double or packed color (uint) as double bits
    public uint ColorValue;
    public bool IsColor;
    public int InterpolationType;                // 0 hold, 1 linear, 2 cubic
    public int InterpolatorRef;                  // object index of the CubicInterpolator (-1 none)
    public double[]? Cubic;                      // [x1,y1,x2,y2] when InterpolationType==cubic
}
