namespace AdsPortalV2.Services;

public interface IMessageMiddleware
{
    Task InvokeAsync(MessageContext context, Func<Task> next);
}
