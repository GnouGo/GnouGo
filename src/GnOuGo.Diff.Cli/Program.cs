using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;

var rootCommand = new RootCommand("GnOuGo.Diff CLI - Outil de test pour insérer des données d'exemple");

var urlOption = new Option<string>(
    "--url",
    getDefaultValue: () => "http://localhost:5100",
    description: "URL de l'API GnOuGo.Diff"
);

// Commande pour insérer des données de test
var seedCommand = new Command("seed", "Insère un jeu de données d'exemple");
seedCommand.AddOption(urlOption);

seedCommand.SetHandler(async (string url) =>
{
    Console.WriteLine("🌱 Insertion de données d'exemple dans GnOuGo.Diff...");
    Console.WriteLine($"   URL: {url}");
    Console.WriteLine();

    using var client = new HttpClient { BaseAddress = new Uri(url) };

    // Exemple 1: Configuration d'application
    await SeedAppConfig(client);
    
    // Exemple 2: Profil utilisateur
    await SeedUserProfile(client);
    
    // Exemple 3: Configuration de pricing
    await SeedPricingConfig(client);

    Console.WriteLine();
    Console.WriteLine("✅ Données insérées avec succès!");
    Console.WriteLine($"   Ouvrez {url} dans votre navigateur pour visualiser.");
}, urlOption);

rootCommand.AddCommand(seedCommand);

return await rootCommand.InvokeAsync(args);

static async Task SeedAppConfig(HttpClient client)
{
    Console.WriteLine("📝 Insertion de configurations d'application...");
    
    var configs = new[]
    {
        new
        {
            entityType = "AppConfiguration",
            entityId = "api-config",
            currentValue = JsonSerializer.Serialize(new
            {
                apiVersion = "v1",
                timeout = 30,
                maxRetries = 3,
                enableCaching = true,
                endpoints = new
                {
                    primary = "https://api.example.com",
                    fallback = "https://api-backup.example.com"
                }
            }),
            author = "admin@example.com"
        },
        new
        {
            entityType = "AppConfiguration",
            entityId = "api-config",
            currentValue = JsonSerializer.Serialize(new
            {
                apiVersion = "v1",
                timeout = 60, // Modifié
                maxRetries = 5, // Modifié
                enableCaching = true,
                enableMetrics = true, // Nouveau
                endpoints = new
                {
                    primary = "https://api.example.com",
                    fallback = "https://api-backup.example.com"
                }
            }),
            author = "john.doe@example.com"
        },
        new
        {
            entityType = "AppConfiguration",
            entityId = "api-config",
            currentValue = JsonSerializer.Serialize(new
            {
                apiVersion = "v2", // Modifié
                timeout = 60,
                maxRetries = 5,
                enableCaching = false, // Modifié
                enableMetrics = true,
                enableLogging = true, // Nouveau
                endpoints = new
                {
                    primary = "https://api-v2.example.com", // Modifié
                    fallback = "https://api-backup.example.com",
                    monitoring = "https://monitoring.example.com" // Nouveau
                }
            }),
            author = "alice.smith@example.com"
        }
    };

    foreach (var config in configs)
    {
        await Task.Delay(1000); // Délai pour avoir des timestamps différents
        var response = await client.PostAsJsonAsync("/api/revisions", config);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"   ✓ Révision créée par {config.author}");
    }
}

static async Task SeedUserProfile(HttpClient client)
{
    Console.WriteLine("👤 Insertion de profils utilisateur...");
    
    var profiles = new[]
    {
        new
        {
            entityType = "UserProfile",
            entityId = "user-123",
            currentValue = JsonSerializer.Serialize(new
            {
                userId = "user-123",
                name = "Jean Dupont",
                email = "jean.dupont@example.com",
                role = "developer",
                permissions = new[] { "read", "write" },
                preferences = new
                {
                    theme = "dark",
                    language = "fr"
                }
            }),
            author = "system"
        },
        new
        {
            entityType = "UserProfile",
            entityId = "user-123",
            currentValue = JsonSerializer.Serialize(new
            {
                userId = "user-123",
                name = "Jean Dupont",
                email = "jean.dupont@newmail.com", // Modifié
                role = "senior-developer", // Modifié
                permissions = new[] { "read", "write", "admin" }, // Modifié
                preferences = new
                {
                    theme = "dark",
                    language = "fr",
                    notifications = true // Nouveau
                }
            }),
            author = "admin@example.com"
        },
        new
        {
            entityType = "UserProfile",
            entityId = "user-123",
            currentValue = JsonSerializer.Serialize(new
            {
                userId = "user-123",
                name = "Jean Dupont-Martin", // Modifié
                email = "jean.dupont@newmail.com",
                role = "senior-developer",
                permissions = new[] { "read", "write", "admin" },
                preferences = new
                {
                    theme = "light", // Modifié
                    language = "en", // Modifié
                    notifications = true,
                    timezone = "Europe/Paris" // Nouveau
                },
                lastLogin = DateTime.UtcNow.ToString("O") // Nouveau
            }),
            author = "jean.dupont@newmail.com"
        }
    };

    foreach (var profile in profiles)
    {
        await Task.Delay(1000);
        var response = await client.PostAsJsonAsync("/api/revisions", profile);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"   ✓ Révision créée par {profile.author}");
    }
}

static async Task SeedPricingConfig(HttpClient client)
{
    Console.WriteLine("💰 Insertion de configurations de pricing...");
    
    var configs = new[]
    {
        new
        {
            entityType = "PricingConfiguration",
            entityId = "prod-pricing",
            currentValue = JsonSerializer.Serialize(new
            {
                currency = "EUR",
                basePlan = new
                {
                    name = "Starter",
                    price = 9.99,
                    features = new[] { "Basic support", "5 users" }
                },
                proPlan = new
                {
                    name = "Pro",
                    price = 29.99,
                    features = new[] { "Priority support", "Unlimited users", "Advanced analytics" }
                }
            }),
            author = "pricing-team@example.com"
        },
        new
        {
            entityType = "PricingConfiguration",
            entityId = "prod-pricing",
            currentValue = JsonSerializer.Serialize(new
            {
                currency = "EUR",
                basePlan = new
                {
                    name = "Starter",
                    price = 12.99, // Modifié
                    features = new[] { "Basic support", "10 users" } // Modifié
                },
                proPlan = new
                {
                    name = "Pro",
                    price = 29.99,
                    features = new[] { "Priority support", "Unlimited users", "Advanced analytics" }
                },
                enterprisePlan = new // Nouveau
                {
                    name = "Enterprise",
                    price = 99.99,
                    features = new[] { "24/7 support", "Unlimited users", "Custom analytics", "SLA" }
                }
            }),
            author = "john.manager@example.com"
        }
    };

    foreach (var config in configs)
    {
        await Task.Delay(1000);
        var response = await client.PostAsJsonAsync("/api/revisions", config);
        response.EnsureSuccessStatusCode();
        Console.WriteLine($"   ✓ Révision créée par {config.author}");
    }
}

