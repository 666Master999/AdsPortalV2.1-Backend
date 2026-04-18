using AdsPortalV2.Entities;

namespace AdsPortalV2.Models;

public sealed record CategoryDto(int Id, string Name, int? ParentId, bool IsLeaf, string? Path);
public sealed record CategoryTreeDto(int Id, string Name, int? ParentId, bool IsLeaf, string? Path, IReadOnlyCollection<CategoryAttributeDto> Attributes, IReadOnlyCollection<CategoryTreeDto> Children);
public sealed record CategoryViewDto(CategoryDto Category, IReadOnlyCollection<int> Path, IReadOnlyCollection<CategoryDto> Children, IReadOnlyCollection<CategoryAttributeDto> Filters);
public sealed record UpsertCategoryDto(string Name, int? ParentId);

public sealed record CategoryAttributeOptionDto(int Id, int AttributeId, string Value);
public sealed record CategoryAttributeDto(int Id, int CategoryId, string Slug, string Name, AttributeType Type, bool IsRequired, bool IsFilter, IReadOnlyCollection<CategoryAttributeOptionDto> Options);
public sealed record UpsertCategoryAttributeDto(string Slug, string Name, AttributeType Type, bool IsRequired, bool IsFilter, IReadOnlyCollection<string>? Options);

public sealed record AdAttributeValueDto(int AttributeId, string Name, AttributeType Type, string Value);
public sealed record UpsertAdAttributeValueDto(int AttributeId, string Value);
