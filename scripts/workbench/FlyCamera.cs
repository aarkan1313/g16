using Godot;

namespace WG16.Workbench;

/// Free-fly camera: RMB/LMB-look, WASD+QE move, Shift boost, wheel speed.
/// Walk mode: Workbench flips Walk and feeds speeds from PresentationParams —
/// ground traversal is data-tuned, fly stays wheel-tuned.
public partial class FlyCamera : Camera3D
{
    private float _speed = 200f;
    private Vector2 _look;

    public bool Walk;
    public float WalkSpeed = 2.2f;      // overwritten from presentation params
    public float WalkRunMult = 3f;

    public override void _UnhandledInput(InputEvent ev)
    {
        if (ev is InputEventMouseMotion m &&
            (Input.IsMouseButtonPressed(MouseButton.Right) || Input.IsMouseButtonPressed(MouseButton.Left)))
        {
            _look += m.Relative;
        }

        if (ev is InputEventMouseButton b && b.Pressed)
        {
            if (b.ButtonIndex == MouseButton.WheelUp)
            {
                _speed = Mathf.Min(_speed * 1.25f, 20000f);
            }

            if (b.ButtonIndex == MouseButton.WheelDown)
            {
                _speed = Mathf.Max(_speed / 1.25f, 5f);
            }
        }
    }

    public override void _Process(double delta)
    {
        float lookScale = Walk ? 0.15f * 0.6f : 0.15f;
        RotationDegrees = new Vector3(
            Mathf.Clamp(RotationDegrees.X - _look.Y * lookScale, -89f, 89f),
            RotationDegrees.Y - _look.X * lookScale,
            0f);
        _look = Vector2.Zero;

        Vector3 dir = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) { dir -= Transform.Basis.Z; }
        if (Input.IsKeyPressed(Key.S)) { dir += Transform.Basis.Z; }
        if (Input.IsKeyPressed(Key.A)) { dir -= Transform.Basis.X; }
        if (Input.IsKeyPressed(Key.D)) { dir += Transform.Basis.X; }
        if (Input.IsKeyPressed(Key.E)) { dir += Vector3.Up; }
        if (Input.IsKeyPressed(Key.Q)) { dir -= Vector3.Up; }

        if (dir != Vector3.Zero)
        {
            float speed = Walk ? WalkSpeed : _speed;
            float boost = Input.IsKeyPressed(Key.Shift) ? (Walk ? WalkRunMult : 5f) : 1f;
            Position += dir.Normalized() * speed * boost * (float)delta;
        }
    }
}
