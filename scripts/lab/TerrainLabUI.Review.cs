using Godot;

namespace WG16.Lab;

/// Host glue for LabReviewController (decomposition Phase 3d). The eye-gate review LOGIC (keys 1-9 + the
/// T-key terrain A/B cycle + the night gate) moved to LabReviewController.cs. This partial keeps only what
/// must live on the Node: the [Export] ReviewMode (set by scenes/review.tscn, also read by _Process to yield
/// the number keys) and the _UnhandledInput lifecycle override, which forwards to the controller.
public partial class TerrainLabUI : Control
{
    [Export] public bool ReviewMode = false;

    private LabReviewController _review = null!;
    private void InitReview() =>
        _review = new LabReviewController(this, this, _sky, _lighting, _terrain, _nightGate, v => _timeRunning = v, ReviewMode);

    public override void _UnhandledInput(InputEvent @event) => _review?.HandleInput(@event);
}
