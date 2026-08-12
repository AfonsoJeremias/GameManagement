using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Extensions.Common;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Other;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GameManagement;

[UsedImplicitly]
public class GameManagementPlugin : LibraryPlugin
{
    private readonly IPlayniteAPI _playniteAPI;
    private readonly ILogger<GameManagementPlugin> _logger;

    public override Guid Id => Guid.Parse("a37e0963-91ac-4432-be2a-69e366c44726");
    public override string Name { get; } = "Game Management";

    public GameManagementPlugin(IPlayniteAPI playniteAPI) : base(playniteAPI)
    {
        _playniteAPI = playniteAPI;
        _logger = CustomLogger.GetLogger<GameManagementPlugin>(nameof(GameManagementPlugin));

        AssemblyLoader.ValidateReferencedAssemblies(_logger);

        Properties = new LibraryPluginProperties
        {
            HasSettings = false,
            HasCustomizedGameImport = false
        };
    }

    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        // Apenas jogos adicionados manualmente (Source == null)
        var allPlayniteGames = args.Games.All(g => g.Source == null);

        if (allPlayniteGames && args.Games.Any())
        {
            yield return new GameMenuItem
            {
                Action = UninstallGameMenuAction,
                Description = "Uninstall"
            };
        }
    }

    private void UninstallGameMenuAction(GameMenuItemActionArgs args)
    {
        UninstallGames(args);
    }

    private List<Game> UninstallGames(GameMenuItemActionArgs args)
    {
        var games = args.Games;
        if (games is null || !games.Any()) return new List<Game>();

        var result = _playniteAPI.Dialogs.ShowMessage(
            $"Do you really want to uninstall {games.Count} game(s)?",
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return new List<Game>();

        _logger.LogInformation("Uninstalling {Count} game(s)", games.Count);

        var actuallyUninstalledGames = new List<Game>(games.Count);

        _playniteAPI.Dialogs.ActivateGlobalProgress(progressArgs =>
        {
            progressArgs.ProgressMaxValue = games.Count;
            progressArgs.CurrentProgressValue = 0;

            foreach (var game in games)
            {
                if (progressArgs.CancelToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Uninstallation canceled");
                    return;
                }

                progressArgs.CurrentProgressValue++;
                progressArgs.Text = $"Uninstalling {game.Name}";

                if (game.InstallationStatus != InstallationStatus.Installed ||
                    string.IsNullOrWhiteSpace(game.InstallDirectory) ||
                    !Directory.Exists(game.InstallDirectory))
                {
                    _logger.LogError("Game {Name} is not installed!", game.Name);
                    continue;
                }

                Directory.Delete(game.InstallDirectory, true);
                game.IsInstalled = false;
                actuallyUninstalledGames.Add(game);
            }
        }, new GlobalProgressOptions($"Uninstalling {games.Count} game(s)", true));

        return actuallyUninstalledGames;
    }

    public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        => Enumerable.Empty<GameMetadata>();

    public override LibraryMetadataProvider GetMetadataDownloader()
        => null!;
}
