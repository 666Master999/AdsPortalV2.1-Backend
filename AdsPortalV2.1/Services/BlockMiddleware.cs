using Microsoft.Extensions.DependencyInjection;

namespace AdsPortalV2.Services;

public class BlockMiddleware : IMessageMiddleware
{
    private readonly IBlockService _blockService;

    public BlockMiddleware(IBlockService blockService)
    {
        _blockService = blockService;
    }

    public async Task InvokeAsync(MessageContext context, Func<Task> next)
    {
        var canSend = await _blockService.CanSendAsync(context.SenderId, context.ConversationId);
        if (!canSend)
        {
            context.IsRejected = true;
            context.ErrorCode = "blocked";
            return;
        }

        await next();
    }
}
