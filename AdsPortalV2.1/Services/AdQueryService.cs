using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Globalization;
using System.Text;
using System;

namespace AdsPortalV2.Services;

public class AdQueryService(AppDbContext db, IMemoryCache cache, ILogger<AdQueryService> logger)
{
    private const int MaxPageSize = 50;
    private const int MaxSearchTextLength = 100;
    private const int MaxSearchTokens = 10;
    private const int RelevanceTokenLimit = 3;
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

        HashSet<int>? expandedLocations = null;
        if (locationIds.Length > 0)
            expandedLocations = await ExpandLocationIdsAsync(locationIds, cancellationToken);

        query = ApplyBaseFilters(query.AsNoTracking(), q, expandedLocations, categoryIds, status);

        var searchText = q.Search;
        if (!string.IsNullOrWhiteSpace(searchText) && searchText.Length > MaxSearchTextLength)
            searchText = searchText[..MaxSearchTextLength];

        var cursor = ParseCursor(q.Cursor);
        var useKeyset = cursor is not null && string.IsNullOrWhiteSpace(searchText) && (string.IsNullOrWhiteSpace(q.Sort) || q.Sort.Equals(AdFieldNames.CreatedAt, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(searchText))
        {
            // browse mode: if cursor provided and sort is CreatedAt (or default), use keyset
            if (useKeyset)
                return await BuildKeysetPageAsync(query, Array.Empty<string>(), false, descending, pageSize, cursor.Value, currentUserId, hasViewHidden, cancellationToken);

            var countAll = await query.CountAsync(cancellationToken);
            var ordered = ApplySort(query, sortKey, descending);
            return await BuildPageAsync(ordered, countAll, page, pageSize, currentUserId, hasViewHidden, cancellationToken);
        }



        var tokens = GetSearchTokens(searchText, out var useOr);
        if (tokens.Length == 0)
        {
            var countAll = await query.CountAsync(cancellationToken);
            var ordered = ApplySort(query, sortKey, descending);
            return await BuildPageAsync(ordered, countAll, page, pageSize, currentUserId, hasViewHidden, cancellationToken);
        }

        var searchTokens = tokens.Select(t => t.ToLowerInvariant()).Take(RelevanceTokenLimit).ToArray();
        var requestedOffset = (page - 1) * pageSize;

        // If a cursor is provided and sort is default (CreatedAt), use keyset pagination.
        // `useKeyset` already computed above for browse; reuse it here.

        if (sortKey.Equals(AdFieldNames.Relevance, StringComparison.OrdinalIgnoreCase))
            return await BuildRelevancePageAsync(query, searchTokens, useOr, descending, page, pageSize, requestedOffset, currentUserId, hasViewHidden, q, expandedLocations, categoryIds, status, cancellationToken);

        if (useKeyset)
            return await BuildKeysetPageAsync(query, searchTokens, useOr, descending, pageSize, cursor.Value, currentUserId, hasViewHidden, cancellationToken);

        // Non-relevance search: fallback to prefix filtering and exact count
        var filtered = ApplySearchFilter(query, searchTokens, useOr);
        var countSearch = await filtered.CountAsync(cancellationToken);
        var pagesSearch = countSearch == 0 ? 1 : (int)Math.Ceiling((double)countSearch / pageSize);
        page = Math.Min(page, pagesSearch);
        var offsetSearch = (page - 1) * pageSize;

        var orderedSearch = ApplySort(filtered, sortKey, descending);
        return await BuildPageAsync(orderedSearch, countSearch, page, pageSize, currentUserId, hasViewHidden, cancellationToken);
    }

    private IQueryable<Ad> ApplyBaseFilters(
        IQueryable<Ad> query,
        AdsQuery q,
        HashSet<int>? locationIds,
        int[] categoryIds,
        AdStatus? status)
    {
        if (locationIds is { Count: > 0 })
            query = query.Where(x => locationIds.Contains(x.LocationId));

        if (categoryIds.Length > 0)
            query = query.Where(x => x.CategoryId.HasValue && categoryIds.Contains(x.CategoryId.Value));

        if (!string.IsNullOrWhiteSpace(q.Type))
            query = query.Where(x => x.ListingType == q.Type);

        if (q.PriceFrom.HasValue)
            query = query.Where(x => x.Price >= q.PriceFrom.Value);

        if (q.PriceTo.HasValue)
            query = query.Where(x => x.Price <= q.PriceTo.Value);

        if (q.DateFrom.HasValue)
            query = query.Where(x => x.CreatedAt >= q.DateFrom.Value.ToDateTime(TimeOnly.MinValue));

        if (q.DateTo.HasValue)
            query = query.Where(x => x.CreatedAt < q.DateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));

        if (q.UserId.HasValue)
            query = query.Where(x => x.UserId == q.UserId.Value);

        if (status.HasValue)
            query = query.Where(x => x.Status == status.Value);

        return query;
    }

    private async Task<PagedResultDto<AdListItemDto>> BuildRelevancePageAsync(
        IQueryable<Ad> query,
        string[] tokens,
        bool useOr,
        bool descending,
        int page,
        int pageSize,
        int offset,
        int? currentUserId,
        bool hasViewHidden,
        AdsQuery q,
        HashSet<int>? expandedLocations,
        int[] categoryIds,
        AdStatus? status,
        CancellationToken cancellationToken)
    {
        var ftsQuery = BuildFtsQuery(tokens, useOr);

        if (!string.IsNullOrWhiteSpace(ftsQuery) && await IsFtsEnabledAsync())
        {
            // Build WHERE clause from provided filters (category, location, status, price, dates, type, user)
            string Escape(string s) => s.Replace("'", "''");

            // Build parameterized SQL to avoid injection. We will construct a FormattableString via FormattableStringFactory.
            var countSb = new StringBuilder();
            var countArgs = new List<object>();
            countSb.AppendLine("SELECT COUNT(*)");
            countSb.AppendLine("FROM CONTAINSTABLE(dbo.Ads, (Title, Description), {0}, LANGUAGE 0) AS ft");
            countSb.AppendLine("INNER JOIN dbo.Ads AS a ON a.Id = ft.[KEY]");
            countArgs.Add(ftsQuery);
            countSb.Append("WHERE 1=1");

            if (categoryIds.Length > 0)
                countSb.Append($" AND a.CategoryId IS NOT NULL AND a.CategoryId IN ({string.Join(", ", categoryIds)})");

            if (expandedLocations is { Count: > 0 })
                countSb.Append($" AND a.LocationId IN ({string.Join(", ", expandedLocations)})");

            if (!string.IsNullOrWhiteSpace(q.Type))
            {
                countSb.Append($" AND a.ListingType = {{{countArgs.Count}}}");
                countArgs.Add(q.Type);
            }

            if (q.PriceFrom.HasValue)
            {
                countSb.Append($" AND a.Price >= {{{countArgs.Count}}}");
                countArgs.Add(q.PriceFrom.Value);
            }

            if (q.PriceTo.HasValue)
            {
                countSb.Append($" AND a.Price <= {{{countArgs.Count}}}");
                countArgs.Add(q.PriceTo.Value);
            }

            if (q.DateFrom.HasValue)
            {
                countSb.Append($" AND a.CreatedAt >= {{{countArgs.Count}}}");
                countArgs.Add(q.DateFrom.Value.ToDateTime(TimeOnly.MinValue));
            }

            if (q.DateTo.HasValue)
            {
                countSb.Append($" AND a.CreatedAt < {{{countArgs.Count}}}");
                countArgs.Add(q.DateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));
            }

            if (q.UserId.HasValue)
            {
                countSb.Append($" AND a.UserId = {{{countArgs.Count}}}");
                countArgs.Add(q.UserId.Value);
            }

            if (status.HasValue)
            {
                countSb.Append($" AND a.Status = {{{countArgs.Count}}}");
                countArgs.Add((int)status.Value);
            }

            var countSqlTemplate = countSb.ToString();
            var countSqlArgs = countArgs.ToArray();
            var totalCount = await ExecuteScalarIntAsync(countSqlTemplate, countSqlArgs, cancellationToken);

            var pages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);
            page = Math.Max(1, Math.Min(page, pages));
            offset = (page - 1) * pageSize;
            // Fetch ranked page of hits from FTS with same filters (parameterized)
            var pageSb = new StringBuilder();
            var pageArgs = new List<object>();
            pageSb.AppendLine("SELECT a.Id AS Id, ft.[RANK] AS Rank");
            pageSb.AppendLine("FROM dbo.Ads AS a");
            pageSb.AppendLine("INNER JOIN CONTAINSTABLE(dbo.Ads, (Title, Description), {0}, LANGUAGE 0) AS ft");
            pageSb.AppendLine("    ON a.Id = ft.[KEY]");
            pageArgs.Add(ftsQuery);
            pageSb.Append("WHERE 1=1");

            if (categoryIds.Length > 0)
                pageSb.Append($" AND a.CategoryId IS NOT NULL AND a.CategoryId IN ({string.Join(", ", categoryIds)})");

            if (expandedLocations is { Count: > 0 })
                pageSb.Append($" AND a.LocationId IN ({string.Join(", ", expandedLocations)})");

            if (!string.IsNullOrWhiteSpace(q.Type))
            {
                pageSb.Append($" AND a.ListingType = {{{pageArgs.Count}}}");
                pageArgs.Add(q.Type);
            }

            if (q.PriceFrom.HasValue)
            {
                pageSb.Append($" AND a.Price >= {{{pageArgs.Count}}}");
                pageArgs.Add(q.PriceFrom.Value);
            }

            if (q.PriceTo.HasValue)
            {
                pageSb.Append($" AND a.Price <= {{{pageArgs.Count}}}");
                pageArgs.Add(q.PriceTo.Value);
            }

            if (q.DateFrom.HasValue)
            {
                pageSb.Append($" AND a.CreatedAt >= {{{pageArgs.Count}}}");
                pageArgs.Add(q.DateFrom.Value.ToDateTime(TimeOnly.MinValue));
            }

            if (q.DateTo.HasValue)
            {
                pageSb.Append($" AND a.CreatedAt < {{{pageArgs.Count}}}");
                pageArgs.Add(q.DateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));
            }

            if (q.UserId.HasValue)
            {
                pageSb.Append($" AND a.UserId = {{{pageArgs.Count}}}");
                pageArgs.Add(q.UserId.Value);
            }

            if (status.HasValue)
            {
                pageSb.Append($" AND a.Status = {{{pageArgs.Count}}}");
                pageArgs.Add((int)status.Value);
            }

            pageSb.AppendLine("ORDER BY ft.[RANK] DESC, a.CreatedAt DESC, a.Id DESC");
            pageSb.Append($" OFFSET {{{pageArgs.Count}}} ROWS FETCH NEXT {{{pageArgs.Count + 1}}} ROWS ONLY");
            pageArgs.Add(offset);
            pageArgs.Add(pageSize + 1);

            var pageSqlTemplate = pageSb.ToString();
            var pageSqlArgs = pageArgs.ToArray();

            // Execute parameterized page query for FTS hits
            var pageHits = await db.FtsResults
                .FromSqlRaw(pageSqlTemplate, pageSqlArgs)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            if (pageHits.Count > 0)
            {
                var pageSlice = pageHits.Take(pageSize).ToArray();
                var pageIds = pageSlice.Select(x => x.Id).ToArray();
                var rankMap = pageSlice.ToDictionary(x => x.Id, x => x.Rank);

                if (pageIds.Length > 0)
                {
                    var items = await ProjectToDto(query.Where(ad => pageIds.Contains(ad.Id)), currentUserId, hasViewHidden).ToListAsync(cancellationToken);
                    // FTS rank is meaningful only in descending order. Always order by rank DESC.
                    var pageItems = items
                        .OrderByDescending(item => rankMap[item.Id])
                        .ThenByDescending(item => item.CreatedAt)
                        .ThenByDescending(item => item.Id)
                        .ToList();

                    await ApplyFavoriteFlagsAsync(pageItems, currentUserId, cancellationToken);

                    return new PagedResultDto<AdListItemDto>(pageItems, totalCount, page, pageSize, pages);
                }
            }
        }

        // Fallback: FTS not available or no ftsQuery -> count directly from filtered query and page normally
        var ordered = descending
            ? query.OrderByDescending(ad => ad.CreatedAt).ThenByDescending(ad => ad.Id)
            : query.OrderBy(ad => ad.CreatedAt).ThenBy(ad => ad.Id);

        var fallbackTotal = await query.CountAsync(cancellationToken);
        var fallbackPages = fallbackTotal == 0 ? 1 : (int)Math.Ceiling((double)fallbackTotal / pageSize);
        page = Math.Max(1, Math.Min(page, fallbackPages));
        offset = (page - 1) * pageSize;

        var itemsFallback = await ProjectToDto(ordered.Skip(offset).Take(pageSize), currentUserId, hasViewHidden).ToListAsync(cancellationToken);
        await ApplyFavoriteFlagsAsync(itemsFallback, currentUserId, cancellationToken);
        return new PagedResultDto<AdListItemDto>(itemsFallback, fallbackTotal, page, pageSize, fallbackPages);
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

    private static IQueryable<Ad> ApplySearchFilter(IQueryable<Ad> query, string[] tokens, bool useOr)
    {
        if (tokens.Length == 0)
            return query;

        if (useOr)
        {
            return tokens.Length switch
            {
                1 => query.Where(ad => ad.Title != null && ad.Title.StartsWith(tokens[0])),
                2 => query.Where(ad => (ad.Title != null && ad.Title.StartsWith(tokens[0])) || (ad.Title != null && ad.Title.StartsWith(tokens[1]))),
                _ => query.Where(ad => (ad.Title != null && ad.Title.StartsWith(tokens[0])) || (ad.Title != null && ad.Title.StartsWith(tokens[1])) || (ad.Title != null && ad.Title.StartsWith(tokens[2])))
            };
        }

        foreach (var token in tokens)
            query = query.Where(ad => ad.Title != null && ad.Title.StartsWith(token));

        return query;
    }

    private static string[] GetSearchTokens(string search, out bool useOr)
    {
        var raw = search.Trim();
        useOr = raw.Contains('|');

        return (useOr
                ? raw.Split('|', StringSplitOptions.RemoveEmptyEntries)
                : raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            .Select(t => t.Trim())
            .Where(t => t.Length is >= 2 and <= 50)
            .Take(MaxSearchTokens)
            .ToArray();
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

    private async Task<HashSet<int>> ExpandLocationIdsAsync(int[] ids, CancellationToken cancellationToken)
    {
        var cacheKey = $"locations:tree:v1:{db.Database.GetDbConnection().DataSource}";
        if (!cache.TryGetValue(cacheKey, out List<LocationNode>? allLocations))
        {
            allLocations = await db.Locations
                .AsNoTracking()
                .Select(l => new LocationNode { Id = l.Id, ParentId = l.ParentId })
                .ToListAsync(cancellationToken);
            cache.Set(cacheKey, allLocations, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
            });
        }

        // Cache children dictionary to avoid rebuilding it on every call
        var childrenCacheKey = "locations:children:v1";
        if (!cache.TryGetValue(childrenCacheKey, out Dictionary<int, int[]>? children))
        {
            children = allLocations
                .Where(l => l.ParentId.HasValue)
                .GroupBy(l => l.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToArray());

            cache.Set(childrenCacheKey, children, TimeSpan.FromMinutes(10));
        }

        var expanded = ids.ToHashSet();
        var frontier = new Queue<int>(ids);

        while (frontier.Count > 0)
        {
            if (!children.TryGetValue(frontier.Dequeue(), out var next))
                continue;

            foreach (var childId in next.Where(expanded.Add))
                frontier.Enqueue(childId);
        }

        return expanded;
    }

    private async Task<PagedResultDto<AdListItemDto>> BuildPageAsync(
        IQueryable<Ad> orderedQuery,
        int totalCount,
        int page,
        int pageSize,
        int? currentUserId,
        bool hasViewHidden,
        CancellationToken cancellationToken)
    {
        var pages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);
        page = Math.Min(page, pages);
        var offset = (page - 1) * pageSize;
        var items = await ProjectToDto(orderedQuery.Skip(offset).Take(pageSize), currentUserId, hasViewHidden).ToListAsync(cancellationToken);
        await ApplyFavoriteFlagsAsync(items, currentUserId, cancellationToken);
        return new PagedResultDto<AdListItemDto>(items, totalCount, page, pageSize, pages);
    }

    private sealed class LocationNode
    {
        public int Id { get; init; }
        public int? ParentId { get; init; }
    }
}
