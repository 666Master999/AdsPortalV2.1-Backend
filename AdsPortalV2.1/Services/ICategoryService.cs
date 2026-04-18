using AdsPortalV2.Models;

namespace AdsPortalV2.Services;

public interface ICategoryService
{
    Task<CategoryGraph> GetGraphAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<CategoryTreeDto>> GetTreeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<CategoryAttributeDto>> GetAttributesAsync(int categoryId, bool onlyFilters = false, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, IReadOnlyCollection<CategoryAttributeDto>>> GetAttributeLookupAsync(IEnumerable<int> categoryIds, bool onlyFilters = false, CancellationToken cancellationToken = default);
    Task<CategoryViewDto?> GetViewAsync(int categoryId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<int>> GetPathIdsAsync(int categoryId, CancellationToken cancellationToken = default);
    Task RebuildAsync(CancellationToken cancellationToken = default);
}
