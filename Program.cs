using FootyScores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        // Set up configuration
        var configuration = context.Configuration;
        FootyConfiguration.ApiUrl = configuration["SCORES_URL"]
            ?? throw new InvalidOperationException("SCORES_URL configuration is required");

        // Set cache times from configuration
        if (int.TryParse(configuration["CLIENT_CACHE_SECS"], out var clientCacheSecs))
        {
            FootyConfiguration.ClientCacheSeconds = clientCacheSecs;
        }

        if (int.TryParse(configuration["SERVER_CACHE_SECS"], out var serverCacheSecs))
        {
            FootyConfiguration.ServerCacheSeconds = serverCacheSecs;
        }

        // Add HTTP client factory
        services.AddHttpClient();

        // Add memory caching
        services.AddMemoryCache();

        // Data
        services.AddScoped<IFootyDataService, FootyDataService>();
    })
    .Build();

host.Run();