using System.Text.Json;
using System.Text.Json.Nodes;
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

            Console.Write("  Enter choice number or text: ");
            var line = Console.ReadLine()?.Trim() ?? "";

            // If the user enters a number, map it to the choice
            if (int.TryParse(line, out var idx) && idx >= 1 && idx <= request.Choices.Count)
                line = request.Choices[idx - 1];

            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["response"] = line,
                ["source"] = "console"
            });
        }

        if (request.Fields is { Count: > 0 })
        {
            Console.WriteLine();
            var result = new JsonObject();
            foreach (var field in request.Fields)
            {
                var label = field.Description ?? field.Name;
                var defaultHint = field.Default != null ? $" [{field.Default}]" : "";

                if (field.Options is { Count: > 0 })
                {
                    Console.WriteLine($"  {label} ({string.Join(", ", field.Options)}){defaultHint}:");
                    Console.Write("  > ");
                }
                else
                {
                    Console.Write($"  {label}{defaultHint}: ");
                }

                var value = Console.ReadLine()?.Trim() ?? "";
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
            return Task.FromResult(parsed);
        }
        catch
        {
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["response"] = text,
                ["source"] = "console"
            });
        }
    }
}
