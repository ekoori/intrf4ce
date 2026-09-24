using Intrf4ce.Core.Model;

namespace Intrf4ce.App.Stage;

/// <summary>What "Ask about this" refers to: a part, an element, or a whole slide.</summary>
/// <param name="Target">A focus target (<c>knee.acl</c>, or an element id); null for the slide.</param>
/// <param name="Label">How the user would name it: a callout's label, a photo's caption.</param>
/// <param name="Region">[x, y, w, h] as fractions of a picture, when the user drew a box on one.</param>
public sealed record AskSubject(string SlideId, string? ElementId, string? Target, string Label, double[]? Region = null);

/// <summary>The slide now being shown, and whether its narration should start.</summary>
public readonly record struct ShownSlide(Slide? Slide, bool Play);
