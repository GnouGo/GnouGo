﻿﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace DocIngestor.Cli.Configuration;

/// <summary>
/// Charge la configuration depuis appsettings.json.
/// </summary>
public static class ConfigurationLoader
{
    public static AppSettings LoadConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        var configuration = builder.Build();
        var settings = new AppSettings();
        
        configuration.GetSection("DocIngestor").Bind(settings.DocIngestor);
        configuration.GetSection("OpenAi").Bind(settings.OpenAi);
        configuration.GetSection("Ollama").Bind(settings.Ollama);
        configuration.GetSection("OpenTelemetry").Bind(settings.OpenTelemetry);

        return settings;
    }
}


