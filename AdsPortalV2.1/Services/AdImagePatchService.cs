using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using System.Text.Json;

namespace AdsPortalV2.Services;

public class AdImagePatchService(
    AppDbContext db,
    IFileStorage fileStorage,
    ILogger<AdImagePatchService> logger) : IAdImagePatchService
{
    public void Apply(
        Ad ad,
        JsonElement raw,
        ICollection<string> updated,
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, AdFieldNames.Images, "Images not changed."));
            return;
        }

        if (raw.ValueKind is not JsonValueKind.Array)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Images, "Images must be an array."));
            return;
        }

        foreach (var image in raw.EnumerateArray())
            ProcessItem(ad, image, updated, skipped, errors);
    }

    private void ProcessItem(
        Ad ad,
        JsonElement image,
        ICollection<string> updated,
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        if (image.ValueKind is not JsonValueKind.Object)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Images, "Image item must be an object."));
            return;
        }

        var ctx = Parse(image);

        try
        {
            if (ctx.Delete)
                HandleDelete(ad, ctx, updated, errors);
            else if (ctx.HasId)
                HandleUpdate(ad, ctx, updated, skipped, errors);
            else
                HandleAdd(ad, ctx, updated, errors);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image patch failed for ad {AdId}", ad.Id);
            errors.Add(new PatchIssueDto(PatchErrorCodes.InternalError, AdFieldNames.Images, "Internal error while processing image."));
        }
    }

    private static ImagePatchContext Parse(JsonElement image)
    {
        var delete = image.TryGetProperty("delete", out var d) && d.ValueKind is JsonValueKind.True;
        int? id = image.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var parsedId) ? parsedId : null;
        int? sortOrder = image.TryGetProperty("sortOrder", out var sortValue) && sortValue.TryGetInt32(out var parsedSort) ? parsedSort : null;
        string? filePath = image.TryGetProperty("filePath", out var fileValue) && fileValue.ValueKind is JsonValueKind.String ? fileValue.GetString() : null;

        return new ImagePatchContext
        {
            Delete = delete,
            Id = id,
            SortOrder = sortOrder,
            FilePath = filePath
        };
    }

    private void HandleDelete(
        Ad ad,
        ImagePatchContext ctx,
        ICollection<string> updated,
        ICollection<PatchIssueDto> errors)
    {
        if (!ctx.Id.HasValue)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Images, "Image id is required for delete."));
            return;
        }

        var image = ad.Images.FirstOrDefault(i => i.Id == ctx.Id.Value);
        if (image == null)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.NotFound, AdFieldNames.Images, "Image not found."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(image.FilePath))
        {
            if (fileStorage.Exists(image.FilePath))
                fileStorage.Delete(image.FilePath);
        }

        db.AdImages.Remove(image);
        updated.Add(AdFieldNames.Images);

        if (ad.MainImageId == image.Id)
        {
            var next = ad.Images
                .Where(i => i.Id != image.Id)
                .OrderBy(i => i.SortOrder)
                .Select(i => (int?)i.Id)
                .FirstOrDefault();

            ad.MainImageId = next;
            updated.Add(AdFieldNames.MainImageId);
        }
    }

    private static void HandleUpdate(
        Ad ad,
        ImagePatchContext ctx,
        ICollection<string> updated,
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        var image = ad.Images.FirstOrDefault(i => i.Id == ctx.Id);
        if (image == null)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.NotFound, AdFieldNames.Images, "Image not found."));
            return;
        }

        if (!ctx.SortOrder.HasValue)
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, AdFieldNames.Images, "Images not changed."));
            return;
        }

        PatchHelpers.UpdateInt(ctx.SortOrder.Value, image.SortOrder, v => image.SortOrder = v, AdFieldNames.Images, updated, skipped, errors);
    }

    private void HandleAdd(
        Ad ad,
        ImagePatchContext ctx,
        ICollection<string> updated,
        ICollection<PatchIssueDto> errors)
    {
        if (string.IsNullOrWhiteSpace(ctx.FilePath))
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Images, "Invalid image data."));
            return;
        }

        if (!fileStorage.TryNormalize(ctx.FilePath, out var relativePath))
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, AdFieldNames.Images, "Invalid filePath."));
            return;
        }

        if (!fileStorage.ExistsNormalized(relativePath))
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.NotFound, AdFieldNames.Images, "File not found."));
            return;
        }

        db.AdImages.Add(new AdImage
        {
            AdId = ad.Id,
            FilePath = relativePath,
            SortOrder = ctx.SortOrder ?? 0
        });
        updated.Add(AdFieldNames.Images);
    }
}
