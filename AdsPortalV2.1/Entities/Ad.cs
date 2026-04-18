using System.ComponentModel.DataAnnotations;
using AdsPortalV2.Services;

namespace AdsPortalV2.Entities;

public class Ad : Entity
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? CategoryId { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    [MaxLength(2000)]
    public string? Description { get; set; }

    public decimal? Price { get; set; }

    [MaxLength(50)]
    public string? ListingType { get; set; }
    public bool IsNegotiable { get; set; }

    public int LocationId { get; set; }
    public Location? Location { get; set; }
    public int? MainImageId { get; set; }
    public AdImage? MainImage { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public AdStatus Status { get; set; } = AdStatus.PendingModeration;
    public string? RejectionReason { get; set; }
    public DateTime? DeletedAt { get; set; }

    public int ViewsCount { get; set; }
    public int FavoritesCount { get; set; }

    public Category? Category { get; set; }
    public User? User { get; set; }
    public ICollection<AdImage> Images { get; set; } = [];
    public ICollection<AdAttributeValue> AttributeValues { get; set; } = [];
    public ICollection<Conversation> Conversations { get; set; } = [];

    public void Approve(int actorId, string actorName)
    {
        var oldStatus = Status;
        Status = AdStatus.Active;
        RejectionReason = null;
        UpdatedAt = DateTime.UtcNow;
        Raise(new AdApproved(Id, actorId, actorName, oldStatus.ToString(), Status.ToString()));
    }

    public void Reject(int actorId, string reason, string actorName)
    {
        var oldStatus = Status;
        Status = AdStatus.Rejected;
        RejectionReason = reason;
        UpdatedAt = DateTime.UtcNow;
        Raise(new AdRejected(Id, actorId, actorName, reason, oldStatus.ToString(), Status.ToString()));
    }

    public void QueueImageDeletion(IEnumerable<string> filePaths)
    {
        var paths = filePaths?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        if (paths.Length == 0)
            return;

        Raise(new FileDeletionRequested(paths));
    }

    // Public helper to record that this ad was created. This raises a domain event that will be
    // persisted to the Outbox during SaveChanges pipeline. We intentionally don't set the AdId here
    // because identity is assigned by EF; MarkCreated should be called after identity assignment
    // so that the event contains the real Ad.Id.
    public void MarkCreated(int authorId)
    {
        Raise(new AdCreated(Id, authorId));
    }
}