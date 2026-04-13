using System.Linq;

namespace AdsPortalV2.Services;

public class MessagePipeline
{
    private readonly IEnumerable<IMessageMiddleware> _middlewares;

    public MessagePipeline(IEnumerable<IMessageMiddleware> middlewares)
    {
        _middlewares = middlewares;
    }

    public async Task ExecuteAsync(MessageContext context)
    {
        Func<Task> handler = () => Task.CompletedTask;

        foreach (var middleware in _middlewares.Reverse())
        {
            var next = handler;
            handler = () => middleware.InvokeAsync(context, next);
        }

        await handler();
    }
}
