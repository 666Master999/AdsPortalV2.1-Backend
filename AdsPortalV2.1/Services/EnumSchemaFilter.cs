using System;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AdsPortalV2.Services
{
    public class EnumSchemaFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type.IsEnum && schema?.Enum != null)
            {
                schema.Enum.Clear();

                foreach (var name in Enum.GetNames(context.Type))
                {
                    var camel = char.ToLowerInvariant(name[0]) + name.Substring(1);
                    schema.Enum.Add(new OpenApiString(camel));
                }
            }
        }
    }
}
