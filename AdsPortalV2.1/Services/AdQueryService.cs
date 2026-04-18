using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Linq.Expressions;
using System.Globalization;
using System.Text;
using System;

namespace AdsPortalV2.Services;

public class AdQueryService(AppDbContext db, IMemoryCache cache, ILogger<AdQueryService> logger)
{
    private readonly AdQueryFilterBuilder filterBuilder = new(db, cache);
    private readonly AdSearchProvider searchProvider = new();
    private readonly AdRankingService rankingService = new();
    private readonly AdPaginationStrategy paginationStrategy = new(db);

    private const int MaxPageSize = 50;
    private const int MaxSearchTextLength = 100;
    private const int MaxSearchTokens = 10;
    private const int MaxSearchFallbackStages = 4;
    private const int SearchCountProbeLimit = 5001;
    private const int FavoriteQueryChunkSize = 1000;
    private const int FreshnessWindowDay1 = 1;
    private const int FreshnessWindowDay3 = 3;
    private const int FreshnessWindowDay7 = 7;
    private const double FreshnessBoostDay1 = 1.5;
    private const double FreshnessBoostDay3 = 1.0;
    private const double FreshnessBoostDay7 = 0.5;

    private static readonly IReadOnlyDictionary<string, Func<IQueryable<Ad>, bool, IOrderedQueryable<Ad>>> Sorters =
        new Dictionary<string, Func<IQueryable<Ad>, bool, IOrderedQueryable<Ad>>>(StringComparer.OrdinalIgnoreCase)
        {
            [AdFieldNames.Title] = static (query, descending) => descending ? query.OrderByDescending(ad => ad.Title) : query.OrderBy(ad => ad.Title),
            [AdFieldNames.Price] = static (query, descending) => descending ? query.OrderByDescending(ad => ad.Price) : query.OrderBy(ad => ad.Price),
            [AdFieldNames.CreatedAt] = static (query, descending) => descending ? query.OrderByDescending(ad => ad.CreatedAt) : query.OrderBy(ad => ad.CreatedAt),
            [AdFieldNames.UpdatedAt] = static (query, descending) => descending ? query.OrderByDescending(ad => ad.UpdatedAt) : query.OrderBy(ad => ad.UpdatedAt),
            [AdFieldNames.Views] = static (query, descending) => descending ? query.OrderByDescending(ad => ad.ViewsCount) : query.OrderBy(ad => ad.ViewsCount),
            [AdFieldNames.Favorites] = static (query, descending) => descending ? query.OrderByDescending(ad => ad.FavoritesCount) : query.OrderBy(ad => ad.FavoritesCount),
        };

    private static (DateTime createdAt, int id)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        var parts = cursor.Split('_');
        if (parts.Length != 2)
            return null;

        if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
            return null;

        if (!int.TryParse(parts[1], out var id))
            return null;

        return (createdAt, id);
    }

    private async Task<PagedResultDto<AdListItemDto>> BuildKeysetPageAsync(
        IQueryable<Ad> query,
        string[] tokens,
        bool useOr,
        bool descending,
        int pageSize,
        (DateTime createdAt, int id) cursor,
        int? currentUserId,
        bool hasViewHidden,
        CancellationToken cancellationToken)
    {
        // Only support CreatedAt DESC keyset here.
        if (!descending)
            throw new NotSupportedException("Keyset pagination supports only descending order by CreatedAt.");
        var q = query.Where(ad => ad.CreatedAt < cursor.createdAt || (ad.CreatedAt == cursor.createdAt && ad.Id < cursor.id))
                     .OrderByDescending(ad => ad.CreatedAt).ThenByDescending(ad => ad.Id);

        var dtoQuery = ProjectToDto(q, currentUserId, hasViewHidden).AsNoTracking();
        var items = await dtoQuery.Take(pageSize + 1).ToListAsync(cancellationToken);

        var hasMore = items.Count > pageSize;
        var pageItems = items.Take(pageSize).ToList();

        // Ensure favorite flags are applied for keyset results as well
        await ApplyFavoriteFlagsAsync(pageItems, currentUserId, cancellationToken);

        string? nextCursor = null;
        if (hasMore && pageItems.Count > 0)
        {
            var last = pageItems.Last();
            nextCursor = $"{last.CreatedAt.ToString("o", CultureInfo.InvariantCulture)}_{last.Id}";
        }

        // When using cursor-based pagination total is not known/used by frontend — return -1 to indicate unknown
        return new PagedResultDto<AdListItemDto>(pageItems, -1, 1, pageSize, 1, nextCursor, hasMore);
    }

    public async Task<PagedResultDto<AdListItemDto>> BuildQueryAsync(
        IQueryable<Ad> query,
        AdsQuery q,
        int[] locationIds,
        int[] categoryIds,
        IReadOnlyCollection<AdAttributeFilterDto> attributeFilters,
        AdStatus? status,
        int page,
        int pageSize,
        string sortKey,
        bool descending,
        int? currentUserId,
        bool hasViewHidden,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (filteredQuery, expandedLocations) = await filterBuilder.BuildBaseQueryAsync(
            query,
            q,
            locationIds,
            categoryIds,
            attributeFilters,
            status,
            cancellationToken);

        var searchText = q.Search;
        if (!string.IsNullOrWhiteSpace(searchText) && searchText.Length > MaxSearchTextLength)
            searchText = searchText[..MaxSearchTextLength];

        var cursor = ParseCursor(q.Cursor);
        var useKeyset = cursor is not null && string.IsNullOrWhiteSpace(searchText) && (string.IsNullOrWhiteSpace(q.Sort) || q.Sort.Equals(AdFieldNames.CreatedAt, StringComparison.OrdinalIgnoreCase));
        Func<IQueryable<Ad>, CancellationToken, Task<List<AdListItemDto>>> materializeAsync = async (source, ct) =>
        {
            var items = await ProjectToDto(source, currentUserId, hasViewHidden).ToListAsync(ct);
            await ApplyFavoriteFlagsAsync(items, currentUserId, ct);
            return items;
        };

        if (string.IsNullOrWhiteSpace(searchText))
        {
            // browse mode: if cursor provided and sort is CreatedAt (or default), use keyset
            if (useKeyset)
                return await paginationStrategy.BuildKeysetPageAsync(filteredQuery, descending, pageSize, cursor.Value, materializeAsync, cancellationToken);

            var countAll = await filteredQuery.CountAsync(cancellationToken);
            var ordered = ApplySort(filteredQuery, sortKey, descending);
            return await paginationStrategy.BuildOffsetPageAsync(ordered, countAll, page, pageSize, materializeAsync, cancellationToken);
        }



        var tokens = searchProvider.GetSearchTokens(searchText, out var useOr);
        if (tokens.Length == 0)
        {
            var countAll = await filteredQuery.CountAsync(cancellationToken);
            var ordered = ApplySort(filteredQuery, sortKey, descending);
            return await paginationStrategy.BuildOffsetPageAsync(ordered, countAll, page, pageSize, materializeAsync, cancellationToken);
        }

        var requestedOffset = (page - 1) * pageSize;
        var ftsQuery = searchProvider.BuildFtsQuery(tokens, useOr);

        if (useKeyset)
            return await paginationStrategy.BuildKeysetPageAsync(filteredQuery, descending, pageSize, cursor.Value, materializeAsync, cancellationToken);

        // Search mode: collect LIKE fallback candidates and merge them with FTS candidates.
        var likeCandidates = await searchProvider.BuildSearchCandidatesQueryAsync(filteredQuery, tokens, cancellationToken);
        var candidateIds = new HashSet<int>(await likeCandidates
            .Select(ad => ad.Id)
            .Take(SearchCountProbeLimit)
            .ToListAsync(cancellationToken));

        Dictionary<int, int>? ftsRankMap = null;

        // Parse original requested category ids from query string (preserve exact requested ids before controller may have expanded them).
        int[]? requestedCategoryIds = null;
        if (!string.IsNullOrWhiteSpace(q.Category))
        {
            var parts = q.Category.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = new List<int>();
            foreach (var p in parts)
            {
                if (int.TryParse(p, out var id)) parsed.Add(id);
            }
            if (parsed.Count > 0)
                requestedCategoryIds = parsed.Distinct().ToArray();
        }

        if (!string.IsNullOrWhiteSpace(ftsQuery) && await IsFtsEnabledAsync())
        {
            ftsRankMap = await paginationStrategy.GetFtsCandidateRanksAsync(
                ftsQuery,
                q,
                expandedLocations,
                categoryIds,
                attributeFilters,
                status,
                SearchCountProbeLimit,
                cancellationToken);

            candidateIds.UnionWith(ftsRankMap.Keys);
        }

        if (candidateIds.Count == 0)
            return new PagedResultDto<AdListItemDto>([], 0, page, pageSize, 1);

        var filtered = filteredQuery.Where(ad => candidateIds.Contains(ad.Id));
        // Determine how many candidates to probe based on query length (longer queries -> wider probe)
        var multiplier = tokens.Length <= 2 ? 5 : tokens.Length <= 4 ? 8 : 12;
        var scanSize = Math.Min(1000, pageSize * multiplier);

        // First, load lightweight projection (Id, Title, Description, Price, CategoryId) to rank quickly.
        var lightCandidates = await filtered
            .Select(ad => new { ad.Id, ad.Title, ad.Description, ad.Price, ad.CategoryId })
            .Take(scanSize)
            .ToListAsync(cancellationToken);

        // Map to minimal DTO for ranking (include description and price to improve ranking quality)
        var minimalDtos = lightCandidates.Select(x => new AdListItemDto { Id = x.Id, Title = x.Title ?? string.Empty, Description = x.Description, Price = x.Price, CategoryId = x.CategoryId }).ToList();

        var minimalRanked = rankingService.RankCandidates(
            minimalDtos,
            tokens,
            searchText,
            ftsRankMap,
            requestedCategoryIds,
            categoryIds.Length > 0 ? categoryIds : null);

        // Decide how many top ids to materialize fully (balanced by pageSize and tokens)
        var topTake = Math.Min(1000, pageSize * multiplier);
        var topIds = minimalRanked.Select(x => x.Id).Take(topTake).ToArray();
        var searchCandidates = await materializeAsync(filtered.Where(ad => topIds.Contains(ad.Id)), cancellationToken);
        var rankedCandidates = rankingService.RankCandidates(
            searchCandidates,
            tokens,
            searchText,
            ftsRankMap,
            requestedCategoryIds,
            categoryIds.Length > 0 ? categoryIds : null);

        var totalCount = rankedCandidates.Count;
        var pagesSearch = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);
        page = Math.Min(page, pagesSearch);
        var offset = (page - 1) * pageSize;

        var pageItems = rankedCandidates
            .Skip(offset)
            .Take(pageSize)
            .ToList();
        return new PagedResultDto<AdListItemDto>(pageItems, totalCount, page, pageSize, pagesSearch);
    }

    private static IQueryable<Ad> ApplySort(IQueryable<Ad> query, string sortKey, bool descending)
    {
        var sort = Sorters.TryGetValue(sortKey, out var selector)
            ? selector
            : Sorters[AdFieldNames.Favorites];

        var ordered = sort(query, descending);

        if (!sortKey.Equals(AdFieldNames.CreatedAt, StringComparison.OrdinalIgnoreCase))
            ordered = ordered.ThenByDescending(ad => ad.CreatedAt).ThenByDescending(ad => ad.Id);
        else
            ordered = descending ? ordered.ThenByDescending(ad => ad.Id) : ordered.ThenBy(ad => ad.Id);

        return ordered;
    }

    private IQueryable<AdListItemDto> ProjectToDto(IQueryable<Ad> query, int? currentUserId, bool hasViewHidden)
    {
        return query.Select(ad => new AdListItemDto
        {
            Id = ad.Id,
            Title = ad.Title,
            Description = ad.Description,
            Price = ad.Price,
            IsNegotiable = ad.IsNegotiable,
            CategoryId = ad.CategoryId,
            LocationId = ad.LocationId,
            Location = ad.Location == null ? null : new LocationRef(ad.Location.Type, ad.Location.Id, ad.Location.Name),
            ListingType = ad.ListingType,
            CreatedAt = ad.CreatedAt,
            UpdatedAt = ad.UpdatedAt,
            UserId = ad.UserId,
            ViewsCount = ad.ViewsCount,
            FavoritesCount = ad.FavoritesCount,
            MainImagePath = FilePathHelpers.EnsurePublicPath(ad.MainImage == null ? null : ad.MainImage.FilePath),
            IsFavorite = false,
            ModerationStatus = hasViewHidden || (currentUserId.HasValue && ad.UserId == currentUserId.Value) ? (AdStatus?)ad.Status : null
        });
    }

    private async Task ApplyFavoriteFlagsAsync(List<AdListItemDto> items, int? currentUserId, CancellationToken cancellationToken)
    {
        if (!currentUserId.HasValue || items.Count == 0)
            return;

        var favoriteIds = await GetFavoriteIdsAsync(currentUserId, items.Select(x => x.Id), cancellationToken);

        var favSet = favoriteIds;
        foreach (var item in items)
            item.IsFavorite = favSet.Contains(item.Id);
    }

    private static string BuildFtsQuery(string[] tokens, bool useOr)
    {
        var normalized = tokens
            .Select(NormalizeFtsToken)
            .Where(t => t.Length >= 2)
            .ToArray();

        if (normalized.Length == 0)
            return string.Empty;

        var op = useOr ? "OR" : "AND";
        return string.Join($" {op} ", normalized.Select(t => $"\"{EscapeFtsToken(t)}*\""));
    }

    private static string EscapeFtsToken(string token)
        => token.Replace("\"", "\"\"");

    private static string NormalizeFtsToken(string token)
        => new(token.Where(ch => char.IsLetterOrDigit(ch) || ch is '\'' or '-' or '+' or '#' or '.').ToArray());

    private async Task<int> ExecuteScalarIntAsync(string sqlTemplate, object[] args, CancellationToken cancellationToken)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();

        // Replace {0}, {1} placeholders with parameter names @p0, @p1
        var commandText = sqlTemplate;
        for (int i = 0; i < args.Length; i++)
        {
            commandText = commandText.Replace("{" + i + "}", "@p" + i);
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + i;
            p.Value = args[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        cmd.CommandText = commandText;

        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(cancellationToken);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private async Task<bool> IsFtsEnabledAsync()
    {
        var cacheKey = $"fts:enabled:v1:{db.Database.GetDbConnection().Database}";
        return await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            try
            {
                var installed = await db.Database
                    .SqlQueryRaw<int>("SELECT CONVERT(int, FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'))")
                    .FirstAsync();

                if (installed != 1)
                    return false;

                var indexed = await db.Database
                    .SqlQueryRaw<int>("SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Ads')) THEN 1 ELSE 0 END")
                    .FirstAsync();

                return indexed == 1;
            }
            catch (Exception ex)
            {
                // Log unexpected exception for visibility
                try { logger.LogError(ex, "Failed to determine full-text availability."); } catch { }
                return false;
            }
        });
    }

    public async Task<HashSet<int>> GetFavoriteIdsAsync(int? currentUserId, IEnumerable<int> adIds, CancellationToken cancellationToken = default)
    {
        if (!currentUserId.HasValue)
            return [];

        var ids = adIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];

        var result = new HashSet<int>();
        foreach (var chunk in ids.Chunk(FavoriteQueryChunkSize))
        {
            var favorites = await db.UserFavoriteAds
                .AsNoTracking()
                .Where(f => f.UserId == currentUserId.Value && chunk.Contains(f.AdId))
                .Select(f => f.AdId)
                .ToListAsync(cancellationToken);

            result.UnionWith(favorites);
        }

        return result;
    }
}
