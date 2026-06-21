using Godot;

namespace WG16.Lab;

/// One celestial billboard (galaxy/nebula v2). Direction-anchored 2D disc rendered in cloud_sky.gdshader.
/// Type: 0 = spiral galaxy, 1 = elliptical galaxy, 2 = nebula.
public struct Billboard
{
    public Vector3 Dir;       // unit sky direction
    public float Size;        // angular radius (radians)
    public int Type;          // 0 spiral / 1 elliptical / 2 nebula
    public Vector3 Color;     // primary/core color
    public Vector3 Color2;    // secondary/arm color
    public float Brightness;  // per-slot
    public float Rotation;    // disc spin (rad)
    public float Tilt;        // 0..1 inclination squash
    public float Arms;        // spiral arm count (galaxy)
    public float Seed;        // per-object hash seed
}
