namespace AdsPortalV2.Models;

public sealed record CategoryDto(int Id, string Name, int? ParentId);
public sealed record UpsertCategoryDto(string Name, int? ParentId);
