using AdsPortalV2.Data;
using AdsPortalV2.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdsPortalV2.Services;

public class EfUserRepository : IUserRepository
{
    private readonly AppDbContext _db;

    public EfUserRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<User?> GetByIdAsync(int userId) =>
        _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);

    public Task<List<User>> GetByIdsAsync(IEnumerable<int> ids) =>
        _db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync();
}
