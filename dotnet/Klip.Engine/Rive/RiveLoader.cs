using System;
using System.Collections.Generic;

namespace Klip.Engine.Rive;

/// <summary>
/// Parses a .riv byte stream into a RiveDocument (artboards + animations), faithful to
/// rive-runtime's File::import → RuntimeHeader::read → readRuntimeObject. Field types are
/// resolved from the built-in key table; unknown keys fall back to the file's ToC bitmap.
/// </summary>
public static class RiveLoader
{
    public static RiveDocument Load(byte[] data)
    {
        var r = new RiveReader(data);

        // ---- header ----
        if (r.ReadByte() != 'R' || r.ReadByte() != 'I' || r.ReadByte() != 'V' || r.ReadByte() != 'E')
            throw new InvalidOperationException("não é um ficheiro .riv (fingerprint)");
        var doc = new RiveDocument
        {
            MajorVersion = (int)r.ReadVarUint(),
            MinorVersion = (int)r.ReadVarUint(),
            FileId = r.ReadVarUint(),
        };

        // ToC: property-key list until 0
        var tocKeys = new List<int>();
        while (true) { int k = (int)r.ReadVarUint(); if (k == 0) break; tocKeys.Add(k); }

        // ToC field-type bitmap: 2 bits/prop, but only 4 props per uint32 word (upper 24 bits wasted)
        var toc = new Dictionary<int, int>();
        int currentInt = 0, currentBit = 8;
        foreach (var k in tocKeys)
        {
            if (currentBit == 8)
            {
                currentInt = r.ReadByte() | (r.ReadByte() << 8) | (r.ReadByte() << 16) | (r.ReadByte() << 24);
                currentBit = 0;
            }
            toc[k] = (currentInt >> currentBit) & 3;
            currentBit += 2;
        }

        // ---- object stream ----
        var objects = new List<RiveObject>();
        while (!r.End)
        {
            int typeKey;
            try { typeKey = (int)r.ReadVarUint(); }
            catch (EndOfStreamRive) { break; }

            var obj = new RiveObject(typeKey);
            bool ok = true;
            while (true)
            {
                int pk;
                try { pk = (int)r.ReadVarUint(); }
                catch (EndOfStreamRive) { ok = false; break; }
                if (pk == 0) break;   // object terminator

                int ft = FieldType(pk);
                if (ft < 0 && toc.TryGetValue(pk, out var tf)) ft = tf;
                if (ft < 0) { ok = false; break; }   // unknown & not in ToC → cannot align, stop

                object val = ft switch
                {
                    0 => (object)r.ReadVarUint(),           // uint
                    1 => r.ReadString(),                    // string
                    2 => r.ReadFloat32(),                   // double (float32 on wire)
                    3 => r.ReadColorU32(),                  // color
                    4 => (object)(r.ReadByte() == 1),       // bool
                    5 => r.ReadBytes((int)r.ReadVarUint()), // bytes (varuint len + raw)
                    _ => r.ReadVarUint(),
                };
                obj.Props[pk] = val;
            }
            if (!ok && obj.Props.Count == 0) break;
            objects.Add(obj);
        }

        BuildArtboards(doc, objects);
        return doc;
    }

    /// <summary>Complete property key → field type from core_registry.hpp (0 uint,1 string,2 double,3 color,4 bool,5 bytes). -1 = unknown → fall back to file ToC.</summary>
    private static int FieldType(int propertyKey)
        => RiveFieldTypes.Map.TryGetValue(propertyKey, out var ft) ? ft : -1;

    /// <summary>Split the flat object stream into artboards (each owns its objects + animations),
    /// mirroring the ImportStack: the artboard is index 0 of its own list; parentId indexes into it.</summary>
    private static void BuildArtboards(RiveDocument doc, List<RiveObject> objects)
    {
        RiveArtboard? ab = null;
        RiveAnimation? anim = null;
        RiveKeyedObject? ko = null;
        RiveKeyedProperty? kp = null;

        foreach (var o in objects)
        {
            switch (o.TypeKey)
            {
                case RiveKeys.Backboard:
                    continue;

                case RiveKeys.Artboard:
                    ab = new RiveArtboard { Root = o, Name = o.S(RiveKeys.NameKey) };
                    ab.Width = o.D(RiveKeys.ArtboardWidthKey);
                    ab.Height = o.D(RiveKeys.ArtboardHeightKey);
                    ab.OriginX = o.D(RiveKeys.ArtboardOriginXKey);
                    ab.OriginY = o.D(RiveKeys.ArtboardOriginYKey);
                    o.LocalId = 0;
                    ab.Objects.Add(o);   // artboard is index 0
                    doc.Artboards.Add(ab);
                    anim = null; ko = null; kp = null;
                    continue;

                case RiveKeys.LinearAnimation:
                    if (ab is null) continue;
                    anim = new RiveAnimation
                    {
                        Name = o.S(RiveKeys.AnimNameKey),
                        Fps = (int)o.U(RiveKeys.FpsKey, 60),
                        DurationFrames = (int)o.U(RiveKeys.DurationKey),
                        LoopValue = (int)o.U(RiveKeys.LoopValueKey),
                        Speed = o.D(RiveKeys.SpeedKey, 1),
                    };
                    ab.Animations.Add(anim);
                    ko = null; kp = null;
                    continue;

                case RiveKeys.KeyedObject:
                    if (anim is null) continue;
                    ko = new RiveKeyedObject { ObjectId = (int)o.U(RiveKeys.ObjectIdKey) };
                    anim.KeyedObjects.Add(ko);
                    kp = null;
                    continue;

                case RiveKeys.KeyedProperty:
                    if (ko is null) continue;
                    kp = new RiveKeyedProperty { PropertyKey = (int)o.U(RiveKeys.KeyedPropertyKey) };
                    ko.Properties.Add(kp);
                    continue;

                case RiveKeys.KeyFrameDouble:
                case RiveKeys.KeyFrameColor:
                case RiveKeys.KeyFrameId:
                    if (kp is null) continue;
                    var kf = new RiveKeyFrame
                    {
                        Frame = o.D(RiveKeys.FrameKey),
                        InterpolationType = (int)o.U(RiveKeys.InterpTypeKey),
                    };
                    // interpolatorId resolved later against the artboard object list
                    kf.InterpolatorRef = o.Has(RiveKeys.InterpolatorIdKey) ? (int)o.U(RiveKeys.InterpolatorIdKey) : -1;
                    if (o.TypeKey == RiveKeys.KeyFrameColor)
                    { kf.IsColor = true; kf.ColorValue = o.U(RiveKeys.KfColorValueKey); }
                    else if (o.TypeKey == RiveKeys.KeyFrameId)
                    { kf.Value = o.U(RiveKeys.KfIdValueKey); }
                    else
                    { kf.Value = o.D(RiveKeys.KfDoubleValueKey); }
                    kp.KeyFrames.Add(kf);
                    continue;

                default:
                    // any other object belongs to the current artboard's object list (indexed)
                    if (ab is null) continue;
                    o.LocalId = ab.Objects.Count;
                    ab.Objects.Add(o);
                    continue;
            }
        }

        // resolve interpolator cubic params from the artboard object list
        foreach (var a in doc.Artboards)
            foreach (var k in a.Animations)
                foreach (var keyedObj in k.KeyedObjects)
                    foreach (var prop in keyedObj.Properties)
                        for (int i = 0; i < prop.KeyFrames.Count; i++)
                        {
                            var kf = prop.KeyFrames[i];
                            if (kf.InterpolationType == 2 && kf.InterpolatorRef >= 0 && kf.InterpolatorRef < a.Objects.Count)
                            {
                                var ci = a.Objects[kf.InterpolatorRef];
                                if (ci.TypeKey is RiveKeys.CubicInterpolator or RiveKeys.CubicEaseInterpolator or RiveKeys.CubicValueInterpolator)
                                    kf.Cubic = new[] { ci.D(RiveKeys.CiX1), ci.D(RiveKeys.CiY1), ci.D(RiveKeys.CiX2), ci.D(RiveKeys.CiY2) };
                            }
                            prop.KeyFrames[i] = kf;
                        }
    }
}
