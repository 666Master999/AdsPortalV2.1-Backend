namespace AdsPortalV2.Entities;

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<UserRole> UserRoles { get; set; } = [];
    public List<RolePermission> RolePermissions { get; set; } = [];
}
