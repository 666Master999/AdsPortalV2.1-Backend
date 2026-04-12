using AdsPortalV2.Entities;
using AdsPortalV2.Models;
using System.Text.Json;

namespace AdsPortalV2.Services;

public interface IAdImagePatchService
{
    void Apply(
        Ad ad,
        JsonElement raw,
        ICollection<string> updated,
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors);

}
