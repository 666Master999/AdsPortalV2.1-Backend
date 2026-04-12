namespace AdsPortalV2.Models;

public class UpdateUserDto
{
    public string? UserLogin { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public string? UserPhoneNumber { get; set; }
    public string? AvatarPath { get; set; }
    public string? Password { get; set; }
}
