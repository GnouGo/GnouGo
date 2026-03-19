using DocIngestor.Cli.Commands;
using DocIngestor.Cli.Configuration;

// Charger la configuration
var config = ConfigurationLoader.LoadConfiguration();

// Parser la commande
var (cmd, rest) = CommandLineParser.ParseCommand(args);

// Exécuter la commande
int exitCode = cmd.ToLowerInvariant() switch
{
    "ingest" => await IngestCommand.RunAsync(rest, config),
    "search" => await SearchCommand.RunAsync(rest, config),
    _ => ExecuteHelp()
};

return exitCode;

static int ExecuteHelp()
{
    HelpCommand.PrintUsage();
    return 2;
}

