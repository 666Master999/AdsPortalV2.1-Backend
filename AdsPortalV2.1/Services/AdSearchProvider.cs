using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace AdsPortalV2.Services;

public sealed class AdSearchProvider
{
    private const int MaxSearchTokens = 10;
    private const int MaxSearchFallbackStages = 4;
    private const int StageCandidateLimit = 2000;

    public string[] GetSearchTokens(string search, out bool useOr)
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

    public string BuildFtsQuery(string[] tokens, bool useOr)
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

    public async Task<IQueryable<Ad>> BuildSearchCandidatesQueryAsync(
        IQueryable<Ad> baseQuery,
        string[] tokens,
        CancellationToken cancellationToken)
    {
        if (tokens.Length == 0)
            return baseQuery;

        var ids = new HashSet<int>();
        foreach (var (stageTokens, stageUseOr) in BuildSearchStages(tokens))
        {
            var stageQuery = ApplySearchFilter(baseQuery, stageTokens, stageUseOr);
            try
            {
                var stageIds = await stageQuery
                    .OrderByDescending(ad => ad.CreatedAt)
                    .ThenByDescending(ad => ad.Id)
                    .Select(ad => ad.Id)
                    .Take(StageCandidateLimit)
                    .ToListAsync(cancellationToken);

                ids.UnionWith(stageIds);
            }
            catch
            {
            }
        }

        return ids.Count == 0
            ? baseQuery.Where(_ => false)
            : baseQuery.Where(ad => ids.Contains(ad.Id));
    }

    private static IEnumerable<(string[] Tokens, bool UseOr)> BuildSearchStages(string[] tokens)
    {
        var stages = new List<(string[] Tokens, bool UseOr)>
        {
            (tokens, false),
            (tokens, true)
        };

        var withoutNumbers = tokens.Where(token => !token.All(char.IsDigit)).ToArray();
        if (withoutNumbers.Length > 0 && withoutNumbers.Length < tokens.Length)
        {
            stages.Add((withoutNumbers, false));
            stages.Add((withoutNumbers, true));
        }

        for (var size = tokens.Length - 1; size >= 1; size--)
        {
            var relaxed = tokens.Take(size).ToArray();
            stages.Add((relaxed, false));
            stages.Add((relaxed, true));
        }

        return stages.Take(MaxSearchFallbackStages);
    }

    private static IQueryable<Ad> ApplySearchFilter(IQueryable<Ad> query, string[] tokens, bool useOr)
    {
        if (tokens.Length == 0)
            return query;

        return query.Where(BuildSearchPredicate(tokens, useOr));
    }

    private static Expression<Func<Ad, bool>> BuildSearchPredicate(string[] tokens, bool useOr)
    {
        var ad = Expression.Parameter(typeof(Ad), "ad");
        Expression? combined = null;

        foreach (var token in tokens)
        {
            var term = BuildTokenMatchExpression(ad, ToContainsPattern(token));
            combined = combined is null
                ? term
                : useOr
                    ? Expression.OrElse(combined, term)
                    : Expression.AndAlso(combined, term);
        }

        return Expression.Lambda<Func<Ad, bool>>(combined ?? Expression.Constant(false), ad);
    }

    private static Expression BuildTokenMatchExpression(ParameterExpression ad, string pattern)
    {
        var title = Expression.Property(ad, nameof(Ad.Title));
        var description = Expression.Property(ad, nameof(Ad.Description));

        return Expression.OrElse(
            BuildLikeNullSafeExpression(title, pattern),
            BuildLikeNullSafeExpression(description, pattern));
    }

    private static Expression BuildLikeNullSafeExpression(Expression stringMember, string pattern)
    {
        var notNull = Expression.NotEqual(stringMember, Expression.Constant(null, typeof(string)));
        var like = BuildLikeExpression(stringMember, pattern);
        return Expression.AndAlso(notNull, like);
    }

    private static Expression BuildLikeExpression(Expression stringMember, string pattern)
    {
        var functions = Expression.Property(null, typeof(EF), nameof(EF.Functions));
        var likeMethod = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string)])!;

        return Expression.Call(likeMethod, functions, stringMember, Expression.Constant(pattern));
    }

    private static string ToContainsPattern(string value) => $"%{EscapeLikePattern(value)}%";

    private static string EscapeLikePattern(string value)
        => value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);

    private static string EscapeFtsToken(string token)
        => token.Replace("\"", "\"\"");

    private static string NormalizeFtsToken(string token)
        => new(token.Where(ch => char.IsLetterOrDigit(ch) || ch is '\'' or '-' or '+' or '#' or '.').ToArray());
}
