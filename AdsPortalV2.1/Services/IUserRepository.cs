using AdsPortalV2.Entities;

namespace AdsPortalV2.Services;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(int userId);
    Task<List<User>> GetByIdsAsync(IEnumerable<int> ids);
}
