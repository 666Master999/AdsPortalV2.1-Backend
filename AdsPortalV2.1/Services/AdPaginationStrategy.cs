using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AdsPortalV2.Services;

public sealed class AdPaginationStrategy(AppDbContext db)
{
    public async Task<PagedResultDto<AdListItemDto>> BuildOffsetPageAsync(
        IQueryable<Ad> orderedQuery,
        int totalCount,
        int page,
        int pageSize,
        Func<IQueryable<Ad>, CancellationToken, Task<List<AdListItemDto>>> materializeAsync,
        CancellationToken cancellationToken)
    {
        var pages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);
        page = Math.Min(page, pages);
        var offset = (page - 1) * pageSize;
        var items = await materializeAsync(orderedQuery.Skip(offset).Take(pageSize), cancellationToken);
        return new PagedResultDto<AdListItemDto>(items, totalCount, page, pageSize, pages);
    }

    public async Task<PagedResultDto<AdListItemDto>> BuildKeysetPageAsync(
        IQueryable<Ad> query,
        bool descending,
        int pageSize,
        (DateTime createdAt, int id) cursor,
        Func<IQueryable<Ad>, CancellationToken, Task<List<AdListItemDto>>> materializeAsync,
        CancellationToken cancellationToken)
    {
        if (!descending)
            throw new NotSupportedException("Keyset pagination supports only descending order by CreatedAt.");

        var q = query.Where(ad => ad.CreatedAt < cursor.createdAt || (ad.CreatedAt == cursor.createdAt && ad.Id < cursor.id))
            .OrderByDescending(ad => ad.CreatedAt)
            .ThenByDescending(ad => ad.Id);

        var items = await materializeAsync(q.Take(pageSize + 1), cancellationToken);
        var hasMore = items.Count > pageSize;
        var pageItems = items.Take(pageSize).ToList();

        string? nextCursor = null;
        if (hasMore && pageItems.Count > 0)
        {
            var last = pageItems.Last();
            nextCursor = $"{last.CreatedAt.ToString("o", CultureInfo.InvariantCulture)}_{last.Id}";
        }

        return new PagedResultDto<AdListItemDto>(pageItems, -1, 1, pageSize, 1, nextCursor, hasMore);
    }

    public async Task<PagedResultDto<AdListItemDto>> BuildRelevancePageAsync(
        IQueryable<Ad> query,
        string ftsQuery,
        int page,
        int pageSize,
        int offset,
        AdsQuery q,
        HashSet<int>? expandedLocations,
        int[] categoryIds,
        IReadOnlyCollection<AdAttributeFilterDto> attributeFilters,
        AdStatus? status,
        Func<IQueryable<Ad>, CancellationToken, Task<List<AdListItemDto>>> materializeAsync,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ftsQuery))
            return new PagedResultDto<AdListItemDto>([], 0, page, pageSize, 1);

        var (countSql, countArgs) = BuildFtsCountSql(ftsQuery, expandedLocations, categoryIds, attributeFilters, q, status);
        var totalCount = await db.Database.SqlQueryRaw<int>(countSql, countArgs).FirstAsync(cancellationToken);

        var pages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);
        page = Math.Max(1, Math.Min(page, pages));
        offset = (page - 1) * pageSize;

        var (pageSql, pageArgs) = BuildFtsPageSql(ftsQuery, expandedLocations, categoryIds, attributeFilters, q, status, offset, pageSize);
        var pageHits = await db.FtsResults
            .FromSqlRaw(pageSql, pageArgs)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (pageHits.Count == 0)
            return new PagedResultDto<AdListItemDto>([], totalCount, page, pageSize, pages);

        var pageSlice = pageHits.Take(pageSize).ToArray();
        var pageIds = pageSlice.Select(x => x.Id).ToArray();
        var rankMap = pageSlice.ToDictionary(x => x.Id, x => x.Rank);

        if (pageIds.Length == 0)
            return new PagedResultDto<AdListItemDto>([], totalCount, page, pageSize, pages);

        var items = await materializeAsync(query.Where(ad => pageIds.Contains(ad.Id)), cancellationToken);
        var pageItems = items
            .OrderByDescending(item => rankMap[item.Id])
            .ThenByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .ToList();

        return new PagedResultDto<AdListItemDto>(pageItems, totalCount, page, pageSize, pages);
    }

    public async Task<HashSet<int>> GetFtsCandidateIdsAsync(
        string ftsQuery,
        AdsQuery q,
        HashSet<int>? expandedLocations,
        int[] categoryIds,
        IReadOnlyCollection<AdAttributeFilterDto> attributeFilters,
        AdStatus? status,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ftsQuery) || limit <= 0)
            return [];

        var (pageSql, pageArgs) = BuildFtsPageSql(ftsQuery, expandedLocations, categoryIds, attributeFilters, q, status, 0, limit);
        var hits = await db.FtsResults
            .FromSqlRaw(pageSql, pageArgs)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return hits.Select(x => x.Id).ToHashSet();
    }

    public async Task<Dictionary<int, int>> GetFtsCandidateRanksAsync(
        string ftsQuery,
        AdsQuery q,
        HashSet<int>? expandedLocations,
        int[] categoryIds,
        IReadOnlyCollection<AdAttributeFilterDto> attributeFilters,
        AdStatus? status,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ftsQuery) || limit <= 0)
            return [];

        var (pageSql, pageArgs) = BuildFtsPageSql(ftsQuery, expandedLocations, categoryIds, attributeFilters, q, status, 0, limit);
        var hits = await db.FtsResults
            .FromSqlRaw(pageSql, pageArgs)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return hits.ToDictionary(x => x.Id, x => x.Rank);
    }

    private static (string Sql, object[] Args) BuildFtsCountSql(
        string ftsQuery,
        HashSet<int>? expandedLocations,
        int[] categoryIds,
        IReadOnlyCollection<AdAttributeFilterDto> attributeFilters,
        AdsQuery q,
        AdStatus? status)
    {
        var sb = new StringBuilder();
        var args = new List<object>();

        sb.AppendLine("SELECT CAST(COUNT(*) AS int) AS Value");
        sb.AppendLine("FROM CONTAINSTABLE(dbo.Ads, (Title, Description), {0}, LANGUAGE 0) AS ft");
        sb.AppendLine("INNER JOIN dbo.Ads AS a ON a.Id = ft.[KEY]");
        args.Add(ftsQuery);
        sb.Append("WHERE 1=1");
        AppendFtsFilters(sb, args, expandedLocations, categoryIds, attributeFilters, q, status);

        return (sb.ToString(), args.ToArray());
    }

    private static (string Sql, object[] Args) BuildFtsPageSql(
        string ftsQuery,
        HashSet<int>? expandedLocations,
        int[] categoryIds,
        IReadOnlyCollection<AdAttributeFilterDto> attributeFilters,
        AdsQuery q,
        AdStatus? status,
        int offset,
        int pageSize)
    {
        var sb = new StringBuilder();
        var args = new List<object>();

        sb.AppendLine("SELECT a.Id AS Id, ft.[RANK] AS Rank");
        sb.AppendLine("FROM dbo.Ads AS a");
        sb.AppendLine("INNER JOIN CONTAINSTABLE(dbo.Ads, (Title, Description), {0}, LANGUAGE 0) AS ft");
        sb.AppendLine("    ON a.Id = ft.[KEY]");
        args.Add(ftsQuery);
        sb.Append("WHERE 1=1");
        AppendFtsFilters(sb, args, expandedLocations, categoryIds, attributeFilters, q, status);
        sb.AppendLine("ORDER BY ft.[RANK] DESC, a.CreatedAt DESC, a.Id DESC");
        sb.Append($" OFFSET {{{args.Count}}} ROWS FETCH NEXT {{{args.Count + 1}}} ROWS ONLY");
        args.Add(offset);
        args.Add(pageSize + 1);

        return (sb.ToString(), args.ToArray());
    }

    private static void AppendFtsFilters(
        StringBuilder sb,
        List<object> args,
        HashSet<int>? expandedLocations,
        int[] categoryIds,
        IReadOnlyCollection<AdAttributeFilterDto> attributeFilters,
        AdsQuery q,
        AdStatus? status)
    {
        if (categoryIds.Length > 0)
            sb.Append($" AND a.CategoryId IS NOT NULL AND a.CategoryId IN ({string.Join(", ", categoryIds.Select(id => AddSqlParameter(args, id)))})");

        foreach (var filter in attributeFilters)
        {
            var attributePlaceholders = filter.AttributeIds.Select(attributeId => AddSqlParameter(args, attributeId));
            var valuePlaceholders = filter.Values.Select(value => AddSqlParameter(args, value));
            sb.Append($" AND EXISTS (SELECT 1 FROM dbo.AdAttributeValues av WHERE av.AdId = a.Id AND av.AttributeId IN ({string.Join(", ", attributePlaceholders)}) AND av.Value IN ({string.Join(", ", valuePlaceholders)}))");
        }

        if (expandedLocations is { Count: > 0 })
            sb.Append($" AND a.LocationId IN ({string.Join(", ", expandedLocations.Select(id => AddSqlParameter(args, id)))})");

        if (!string.IsNullOrWhiteSpace(q.Type))
            sb.Append($" AND a.ListingType = {AddSqlParameter(args, q.Type)}");

        if (q.PriceFrom.HasValue)
            sb.Append($" AND a.Price >= {AddSqlParameter(args, q.PriceFrom.Value)}");

        if (q.PriceTo.HasValue)
            sb.Append($" AND a.Price <= {AddSqlParameter(args, q.PriceTo.Value)}");

        if (q.DateFrom.HasValue)
            sb.Append($" AND a.CreatedAt >= {AddSqlParameter(args, q.DateFrom.Value.ToDateTime(TimeOnly.MinValue))}");

        if (q.DateTo.HasValue)
            sb.Append($" AND a.CreatedAt < {AddSqlParameter(args, q.DateTo.Value.AddDays(1).ToDateTime(TimeOnly.MinValue))}");

        if (q.UserId.HasValue)
            sb.Append($" AND a.UserId = {AddSqlParameter(args, q.UserId.Value)}");

        if (status.HasValue)
            sb.Append($" AND a.Status = {AddSqlParameter(args, (int)status.Value)}");
    }

    private static string AddSqlParameter(List<object> args, object value)
    {
        args.Add(value ?? DBNull.Value);
        return $"{{{args.Count - 1}}}";
    }
}
