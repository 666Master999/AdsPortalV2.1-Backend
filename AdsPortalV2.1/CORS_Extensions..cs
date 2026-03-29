namespace AdsPortalV2
{
    // CorsExtensions.cs
    public static class CORS_Extensions
    {
        public static IServiceCollection AddCustomCors(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddCors(options =>
            {
                options.AddPolicy("AllowFrontend", policy =>
                {
                    var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                                  ?? new[] { "http://localhost:5173", "https://localhost:5173" };

                    policy.WithOrigins(origins)
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .AllowCredentials();
                });
            });
            return services;
        }
    }
}
