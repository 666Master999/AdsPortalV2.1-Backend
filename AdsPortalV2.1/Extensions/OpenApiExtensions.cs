using AdsPortalV2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AdsPortalV2.Extensions;

public sealed class DefaultErrorResponsesFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        AddResponse(operation, context, "400", "Bad Request");
        AddResponse(operation, context, "403", "Forbidden");
        AddResponse(operation, context, "404", "Not Found");

        if (HasAuthorize(context))
            AddResponse(operation, context, "401", "Unauthorized");
    }

    private static void AddResponse(OpenApiOperation operation, OperationFilterContext context, string statusCode, string description)
    {
        if (operation.Responses.ContainsKey(statusCode))
            return;

        operation.Responses[statusCode] = new OpenApiResponse
        {
            Description = description,
            Content =
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(ApiError), context.SchemaRepository)
                }
            }
        };
    }

    private static bool HasAuthorize(OperationFilterContext context)
    {
        if (context.MethodInfo.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any())
            return false;

        return context.MethodInfo.GetCustomAttributes(true).OfType<AuthorizeAttribute>().Any()
            || context.MethodInfo.DeclaringType?.GetCustomAttributes(true).OfType<AuthorizeAttribute>().Any() == true;
    }
}

