namespace Quorum.Web.Client;

/// <summary>
/// What the preview panel renders (spec 014, D-5): a content descriptor with no knowledge of where the content came
/// from. The fenced code block of an assistant message is the first producer; later phases produce the same
/// descriptor from artifacts or <c>ui://</c> resources without touching the panel, the viewer or the consent rules.
/// </summary>
public sealed record PreviewContent(PreviewKind Kind, string Text, string? Title = null);
