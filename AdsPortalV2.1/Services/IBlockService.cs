using System.Threading.Tasks;

namespace AdsPortalV2.Services;

public interface IBlockService
{
    // Returns true if participants are allowed to send messages to each other
    Task<bool> CanSendAsync(int userA, int userB);

    // Returns true if participants of a conversation are allowed to send messages
    Task<bool> CanSendAsync(int conversationId);
}
