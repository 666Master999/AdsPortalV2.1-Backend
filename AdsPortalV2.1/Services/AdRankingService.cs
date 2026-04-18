using AdsPortalV2.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AdsPortalV2.Services;

public sealed class AdRankingService
{
    public List<AdListItemDto> RankCandidates(
        IReadOnlyCollection<AdListItemDto> candidates,
        string[] tokens,
        string searchText,
        IReadOnlyDictionary<int, int>? ftsRankMap = null,
        int[]? requestedCategoryIds = null,
        int[]? expandedCategoryIds = null)
    {
        if (candidates.Count == 0)
            return [];

        return candidates
            .Select(item => new RankedSearchHit(item, ComputeSearchScore(item, tokens, searchText, ftsRankMap, requestedCategoryIds, expandedCategoryIds)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.ViewsCount)
            .ThenByDescending(x => x.Item.CreatedAt)
            .ThenByDescending(x => x.Item.Id)
            .Select(x => x.Item)
            .ToList();
    }

    public double ComputeSearchScore(
        AdListItemDto item,
        string[] tokens,
        string searchText,
        IReadOnlyDictionary<int, int>? ftsRankMap = null,
        int[]? requestedCategoryIds = null,
        int[]? expandedCategoryIds = null)
    {
        var title = item.Title ?? string.Empty;
        var description = item.Description ?? string.Empty;
        var normalizedSearchText = NormalizeSearchText(searchText);
        var score = 0d;

        if (ftsRankMap is not null && ftsRankMap.TryGetValue(item.Id, out var ftsRank))
            score += ftsRank * 0.4d;

        // Boost if ad belongs to requested category (exact match) or to its children (expanded)
        if (requestedCategoryIds is not null && requestedCategoryIds.Length > 0 && item.CategoryId.HasValue)
        {
            // if single requested category, prefer it
            if (requestedCategoryIds.Length == 1)
            {
                if (item.CategoryId.Value == requestedCategoryIds[0])
                    score += 20d;
                else if (expandedCategoryIds is not null && expandedCategoryIds.Contains(item.CategoryId.Value))
                    score += 10d;
            }
            else
            {
                if (requestedCategoryIds.Contains(item.CategoryId.Value))
                    score += 20d;
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedSearchText))
        {
            if (ContainsText(title, normalizedSearchText))
                score += 120d;
            else if (ContainsText(description, normalizedSearchText))
                score += 50d;
        }

        if (tokens.Length >= 2)
        {
            var pair = NormalizeSearchText($"{tokens[0]} {tokens[1]}");
            if (ContainsText(title, pair))
                score += 35d;
            else if (ContainsText(description, pair))
                score += 10d;
        }

        var matchedTokens = 0;
        foreach (var token in tokens)
        {
            var tokenScore = ScoreToken(title, description, token);
            if (tokenScore > 0)
                matchedTokens++;

            score += tokenScore;
        }

        score += matchedTokens * 10d;
        if (matchedTokens == tokens.Length)
            score += 20d;

        return matchedTokens == 0 ? 0d : score;
    }

    private sealed record RankedSearchHit(AdListItemDto Item, double Score);

    private static double ScoreToken(string title, string description, string token)
    {
        var normalizedToken = NormalizeSearchText(token);
        if (normalizedToken.Length == 0)
            return 0d;

        var isNumeric = normalizedToken.All(char.IsDigit);
        var score = 0d;

        if (StartsWithText(title, normalizedToken))
            score += isNumeric ? 9d : 14d;
        else if (ContainsText(title, normalizedToken))
            score += isNumeric ? 7d : 12d;

        if (ContainsText(description, normalizedToken))
            score += isNumeric ? 2d : 5d;

        return score;
    }

    private static bool ContainsText(string source, string value)
        => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool StartsWithText(string source, string value)
        => source.StartsWith(value, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSearchText(string value)
        => string.Join(' ', value
            .Trim()
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => new string(token.Where(ch => char.IsLetterOrDigit(ch) || ch is '\'' or '-' or '+' or '#' or '.' or '/').ToArray()))
            .Where(token => token.Length > 0));
}
