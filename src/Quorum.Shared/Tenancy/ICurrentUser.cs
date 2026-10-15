namespace Quorum.Shared.Tenancy;

/// <summary>The signed-in user of the current request.</summary>
public interface ICurrentUser
{
    /// <summary>The user's id, or <see langword="null"/> for anonymous requests.</summary>
    Guid? UserId { get; }
}
