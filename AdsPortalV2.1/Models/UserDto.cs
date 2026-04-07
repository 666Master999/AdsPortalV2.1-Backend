namespace AdsPortalV2.Models;

public sealed record UserDto(
    int Id,
    string UserLogin,
    string? UserName,
    string? UserEmail,
    string? UserPhoneNumber,
    string? AvatarPath,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    IReadOnlyCollection<string> Roles);
