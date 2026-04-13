namespace AdsPortalV2.Services;

public class MessageRejectedException : Exception
{
    public string Code { get; }

    public MessageRejectedException(string code, string? message = null) : base(message ?? code)
    {
        Code = code;
    }
}
