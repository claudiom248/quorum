using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Quorum.Shared.Results;

namespace Quorum.Shared.Endpoints;

/// <summary>
/// The keyset position of a list: the name and the id of the last item sent, base64url encoded. The list kind is inside, so a cursor
/// taken from another list is rejected instead of silently skipping rows.
/// </summary>
public static class ListCursor
{
    /// <summary>Longer than a name of any list plus the rest of the position, base64url encoded: refused before it is decoded.</summary>
    public const int MaxLength = 1024;

    public static Error Invalid { get; } =
        Error.Validation("list.invalid_cursor", "The cursor is not valid for this list: start again from the first page.");

    public static string Encode(string listKind, string name, Guid id) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Position(listKind, name, id)));

    /// <summary>The position, or <see langword="null"/> inside the result when there is no cursor (the first page).</summary>
    public static Result<(string Name, Guid Id)?> Decode(string listKind, string? cursor)
    {
        if (cursor is null)
        {
            return Result<(string Name, Guid Id)?>.Success(null);
        }

        if (cursor.Length > MaxLength)
        {
            return Invalid;
        }

        try
        {
            var position = JsonSerializer.Deserialize<Position>(Base64Url.DecodeFromChars(cursor));
            if (position is { Name: not null } && position.Kind == listKind)
            {
                return Result<(string Name, Guid Id)?>.Success((position.Name, position.Id));
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or DecoderFallbackException)
        {
            // Falls through to the same answer as a cursor of another list.
        }

        return Invalid;
    }

    private sealed record Position(string Kind, string Name, Guid Id);
}
