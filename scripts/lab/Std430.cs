using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// std430 layout-correct buffer writer. Hand-packing GLSL std430 param buffers as a flat
/// run of 4-byte floats is a recurring footgun: vec2 members align to 8 bytes and vec4
/// members/arrays align to 16, so GLSL inserts padding a tight C# writer omits — the
/// offsets silently drift and the shader reads scrambled data (this caused the multi-layer
/// "no clouds" bug 3×). This writer applies the SAME alignment rules as std430, so the C#
/// side and the GLSL struct cannot drift. Declare fields in the SAME ORDER as the GLSL
/// struct; the alignment is handled here.
///
/// std430 rules implemented: float=align4, vec2=align8, vec3/vec4=align16, struct/array
/// member alignment is the element's alignment (caller uses Vec4 per array element).
public sealed class Std430Writer
{
    // Backing store is a reusable byte[] written via BitConverter.TryWriteBytes — NO per-float
    // allocation (the old List<byte> + AddRange(GetBytes()) churned a 4-byte array per float,
    // ~hundreds per frame on the cloud param buffers → GC stutter). Grows by doubling.
    private byte[] _buf = new byte[256];
    private int _len;

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) { return; }
        int cap = _buf.Length * 2;
        while (cap < _len + extra) { cap *= 2; }
        Array.Resize(ref _buf, cap);
    }
    private void Align(int a)
    {
        int rem = _len % a;
        if (rem != 0) { int pad = a - rem; Ensure(pad); for (int i = 0; i < pad; i++) { _buf[_len++] = 0; } }
    }
    private void Raw(float v) { Ensure(4); BitConverter.TryWriteBytes(_buf.AsSpan(_len), v); _len += 4; }

    public Std430Writer F(float v)   { Align(4); Raw(v); return this; }
    public Std430Writer Vec2(float x, float y) { Align(8); Raw(x); Raw(y); return this; }
    public Std430Writer Vec2(Vector2 v) => Vec2(v.X, v.Y);
    public Std430Writer Vec4(float x, float y, float z, float w) { Align(16); Raw(x); Raw(y); Raw(z); Raw(w); return this; }
    public Std430Writer Vec4(Vector3 v, float w) => Vec4(v.X, v.Y, v.Z, w);
    public Std430Writer Vec4(Color c) => Vec4(c.R, c.G, c.B, 0f);

    /// Write a run of vec4s from a flat float[] (length must be a multiple of 4). Each vec4
    /// is 16-aligned; the array as a whole starts 16-aligned (vec4[] stride rule).
    public Std430Writer Vec4Array(float[] flat)
    {
        Align(16);
        for (int i = 0; i + 3 < flat.Length; i += 4) { Raw(flat[i]); Raw(flat[i + 1]); Raw(flat[i + 2]); Raw(flat[i + 3]); }
        return this;
    }

    /// Final bytes, padded up to a 16-byte multiple (std430 buffer size rounding).
    public byte[] ToArray()
    {
        Align(16);
        var r = new byte[_len];
        Array.Copy(_buf, r, _len);
        return r;
    }
}
