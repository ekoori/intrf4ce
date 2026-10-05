using Avalonia;

namespace Intrf4ce.App.Stage;

/// <summary>
/// An element that takes a drag started on it as its own — a 3D model turns under the pointer —
/// instead of the stage panning.
/// </summary>
/// <remarks>
/// Asked only once the pointer has moved far enough to be a drag, so a click on the element still
/// focuses the part under it and a double-click still does what it does everywhere else.
/// </remarks>
public interface IStageDraggable
{
    /// <summary>The pointer moved by <paramref name="delta"/> screen pixels since the last call.</summary>
    void DragBy(Vector delta);

    void DragEnded();
}
