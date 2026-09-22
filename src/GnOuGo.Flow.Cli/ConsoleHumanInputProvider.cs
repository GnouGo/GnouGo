using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Cli;

/// <summary>
/// Console-based human input provider. Prompts the user on stdout
/// and reads responses from stdin. Used when running workflows in the CLI.
/// </summary>
public sealed class ConsoleHumanInputProvider : IHumanInputProvider
{
    public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════╗");
        Console.WriteLine("║  🙋 HUMAN INPUT REQUIRED                    ║");
        Console.WriteLine("╚══════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"  {request.Prompt}");

        if (request.Context != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine();
            Console.WriteLine("  Context:");
            Console.WriteLine($"  {request.Context.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");
            Console.ResetColor();
        }

        if (request.Choices is { Count: > 0 })
        {
            Console.WriteLine();
            Console.WriteLine("  Choices:");
            for (var i = 0; i < request.Choices.Count; i++)
                Console.WriteLine($"    [{i + 1}] {request.Choices[i]}");
            WriteAbandonChoice(request.AllowAbandon);

            Console.Write("  Enter choice number or text: ");
            var line = Console.ReadLine()?.Trim() ?? "";
            if (request.AllowAbandon && IsAbandonInput(line))
                return Task.FromResult<JsonNode?>(BuildAbandonResponse());

            // If the user enters a number, map it to the choice
            if (int.TryParse(line, out var idx) && idx >= 1 && idx <= request.Choices.Count)
                line = request.Choices[idx - 1];

            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["response"] = line,
                ["source"] = "console",
                [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
            });
        }

        if (request.Fields is { Count: > 0 })
        {
            Console.WriteLine();
            if (request.AllowAbandon)
            {
                Console.WriteLine($"  {LocalizedLabel("Enter [A] at any field to abandon this request.", "Saisissez [A] dans n’importe quel champ pour abandonner cette demande.")}");
                Console.WriteLine();
            }
            var result = new JsonObject
            {
                [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
            };
            foreach (var field in request.Fields)
            {
                var label = field.Description ?? field.Name;
                var defaultHint = field.Default != null ? $" [{field.Default}]" : "";

                if (field.Options is { Count: > 0 })
                {
                    Console.WriteLine($"  {label}{defaultHint}:");
                    for (var optionIndex = 0; optionIndex < field.Options.Count; optionIndex++)
                    {
                        var option = field.Options[optionIndex];
                        var definition = field.OptionDefinitions?.FirstOrDefault(candidate => string.Equals(
                            candidate.Value,
                            option,
                            StringComparison.Ordinal));
                        var recommended = definition?.Recommended == true
                            ? $" ({LocalizedLabel("Recommended", "Recommandé")})"
                            : "";
                        Console.WriteLine($"    [{optionIndex + 1}] {option}{recommended}");
                        if (!string.IsNullOrWhiteSpace(definition?.Description))
                            Console.WriteLine($"        {definition.Description}");
                    }
                    if (field.AllowCustomAnswer)
                        Console.WriteLine($"    [{field.Options.Count + 1}] {LocalizedLabel("Other", "Autre")}");
                    Console.Write("  Enter option number or answer: ");
                }
                else
                {
                    Console.Write($"  {label}{defaultHint}: ");
                }

                var value = Console.ReadLine()?.Trim() ?? "";
                if (request.AllowAbandon && IsAbandonInput(value))
                    return Task.FromResult<JsonNode?>(BuildAbandonResponse());
                if (field.Options is { Count: > 0 }
                    && int.TryParse(value, out var optionNumber))
                {
                    if (optionNumber >= 1 && optionNumber <= field.Options.Count)
                    {
                        value = field.Options[optionNumber - 1];
                    }
                    else if (field.AllowCustomAnswer && optionNumber == field.Options.Count + 1)
                    {
                        Console.Write($"  {LocalizedLabel("Other answer", "Autre réponse")}: ");
                        value = Console.ReadLine()?.Trim() ?? "";
                        if (request.AllowAbandon && IsAbandonInput(value))
                            return Task.FromResult<JsonNode?>(BuildAbandonResponse());
                    }
                }
                if (string.IsNullOrEmpty(value) && field.Default != null)
                    value = field.Default;

                result[field.Name] = field.Type switch
                {
                    "integer" => int.TryParse(value, out var i) ? JsonValue.Create(i) : JsonValue.Create(value),
                    "number" => double.TryParse(value, out var n) ? JsonValue.Create(n) : JsonValue.Create(value),
                    "boolean" => JsonValue.Create(value.Equals("true", StringComparison.OrdinalIgnoreCase)
                                                  || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase)),
                    _ => JsonValue.Create(value)
                };
            }

            result["source"] = "console";
            return Task.FromResult<JsonNode?>(result);
        }

        // Free-form text input
        Console.Write("  > ");
        var text = Console.ReadLine()?.Trim() ?? "";

        // Try parsing as JSON first
        try
        {
            var parsed = JsonNode.Parse(text);
            if (parsed is JsonObject parsedObject)
            {
                parsedObject[HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit;
                return Task.FromResult<JsonNode?>(parsedObject);
            }

            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["response"] = parsed?.DeepClone(),
                ["source"] = "console",
                [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
            });
        }
        catch
        {
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["response"] = text,
                ["source"] = "console",
                [HumanInputContract.ActionProperty] = HumanInputContract.ActionSubmit
            });
        }
    }

    private static void WriteAbandonChoice(bool allowAbandon)
    {
        if (allowAbandon)
            Console.WriteLine($"    [A] {LocalizedLabel("Abandon", "Abandonner")}");
    }

    private static string LocalizedLabel(string english, string french) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase)
            ? french
            : english;

    private static bool IsAbandonInput(string value) =>
        value.Equals("a", StringComparison.OrdinalIgnoreCase)
        || value.Equals("abandon", StringComparison.OrdinalIgnoreCase);

    private static JsonObject BuildAbandonResponse() => new()
    {
        [HumanInputContract.ActionProperty] = HumanInputContract.ActionAbandon,
        ["source"] = "console"
    };
}
