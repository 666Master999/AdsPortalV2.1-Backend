using System.ComponentModel.DataAnnotations;

namespace AdsPortalV2.Entities;

public class Ad
{
    // Идентификаторы
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? CategoryId { get; set; }

    // Основная информация
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public decimal? Price { get; set; }

    // Дополнительная информация
    public string? City { get; set; }
    public string? Type { get; set; }

    // Временные метки
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Мягкое удаление и модерация
    public bool IsDeleted { get; set; }
    public ModerationStatus ModerationStatus { get; set; }

    // Навигационные свойства
    public Category? Category { get; set; }
    public User? User { get; set; }
    public ICollection<AdImage> Images { get; set; } = new List<AdImage>();
}