using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Extensions.Common;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GameManagement;

[UsedImplicitly]
public class GameManagementPlugin : GenericPlugin
{
    private readonly IPlayniteAPI _playniteAPI;
    private readonly ILogger<GameManagementPlugin> _logger;
    private ResourceDictionary? _localizationResources;

    public override Guid Id => Guid.Parse("a37e0963-91ac-4432-be2a-69e366c44726");

    public GameManagementPlugin(IPlayniteAPI playniteAPI) : base(playniteAPI)
    {
        _playniteAPI = playniteAPI;
        _logger = CustomLogger.GetLogger<GameManagementPlugin>(nameof(GameManagementPlugin));
        LoadLocalizationResources();
    }

    #region Localization

    private void LoadLocalizationResources()
    {
        try
        {
            string pluginDirectory = Path.GetDirectoryName(GetType().Assembly.Location)!;
            var localizationFolder = Path.Combine(pluginDirectory, "Localization");

            var culture = _playniteAPI.ApplicationSettings.Language ?? "en_US";
            var resourceFile = Path.Combine(localizationFolder, $"{culture}.xaml");

            if (!File.Exists(resourceFile))
            {
                resourceFile = Path.Combine(localizationFolder, "en_US.xaml");
                if (!File.Exists(resourceFile))
                {
                    _logger.LogWarning("Localization file not found, using default English strings.");
                    return;
                }
            }

            _localizationResources = new ResourceDictionary
            {
                Source = new Uri(resourceFile, UriKind.Absolute)
            };

            if (!Application.Current.Resources.MergedDictionaries.Contains(_localizationResources))
            {
                Application.Current.Resources.MergedDictionaries.Add(_localizationResources);
            }

            _logger.LogInformation("Loaded localization from {ResourceFile}", resourceFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load localization resources");
        }
    }

    private string GetLocalizedString(string key, string defaultValue)
    {
        if (_localizationResources != null && _localizationResources.Contains(key))
        {
            return _localizationResources[key] as string ?? defaultValue;
        }
        return defaultValue;
    }

    #endregion

    #region Menu Items (Context Menu)

    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        var allPlayniteGames = args.Games?.All(g => g.PluginId == Guid.Empty) ?? false;

        if (allPlayniteGames)
        {
            var uninstallText = GetLocalizedString("GameManagement_Uninstall", "Uninstall");

            yield return new GameMenuItem
            {
                Action = UninstallGameMenuAction,
                Description = uninstallText
            };
        }
    }

    private void UninstallGameMenuAction(GameMenuItemActionArgs args)
    {
        UninstallGames(args);
    }

    #endregion

    #region Uninstall Actions (Standard Playnite Button)

    public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
    {
        var game = args.Game;

        if (game.PluginId != Guid.Empty)
            yield break;

        bool hasRom = game.Roms?.Any() == true;
        bool hasInstallDir = game.InstallationStatus == InstallationStatus.Installed &&
                             !string.IsNullOrEmpty(game.InstallDirectory);

        if (!hasRom && !hasInstallDir)
            yield break;

        yield return new CustomUninstallController(game, this);
    }

    #endregion

    #region Uninstall Logic Core

    public List<Game> UninstallGames(GameMenuItemActionArgs args)
    {
        var games = args.Games;
        if (games is null || !games.Any())
            return new List<Game>();

        return UninstallGamesCore(games, showConfirmation: true, showProgress: true);
    }

    private List<Game> UninstallGamesCore(IEnumerable<Game> games, bool showConfirmation, bool showProgress)
    {
        var gameList = games.ToList();
        if (!gameList.Any())
            return new List<Game>();

        if (showConfirmation)
        {
            var title = GetLocalizedString("GameManagement_ConfirmationTitle", "Confirmation");
            string message;
            if (gameList.Count == 1)
            {
                message = GetLocalizedString("GameManagement_ConfirmationMessage",
                    "Do you really want to uninstall this game?");
            }
            else
            {
                var template = GetLocalizedString("GameManagement_ConfirmationMessages",
                    "Do you really want to uninstall these {0} games?");
                message = string.Format(template, gameList.Count);
            }

            var result = _playniteAPI.Dialogs.ShowMessage(message, title,
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return new List<Game>();
        }

        _logger.LogInformation("Uninstalling {Count} game(s)", gameList.Count);

        var actuallyUninstalledGames = new List<Game>(gameList.Count);

        if (showProgress)
        {
            _playniteAPI.Dialogs.ActivateGlobalProgress(progressArgs =>
            {
                progressArgs.ProgressMaxValue = gameList.Count;
                progressArgs.CurrentProgressValue = 0;

                foreach (var game in gameList)
                {
                    if (progressArgs.CancelToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Uninstallation has been canceled");
                        return;
                    }

                    progressArgs.CurrentProgressValue += 1;
                    progressArgs.Text = string.Format(
                        GetLocalizedString("GameManagement_ProgressText", "Uninstalling {0}"),
                        game.Name);

                    if (TryUninstallSingleGame(game, out var result) && result != null)
                        actuallyUninstalledGames.Add(result);
                }
            }, new GlobalProgressOptions(
                string.Format(GetLocalizedString("GameManagement_ProgressTitle", "Uninstalling {0} games"), gameList.Count),
                true));
        }
        else
        {
            foreach (var game in gameList)
            {
                if (TryUninstallSingleGame(game, out var result) && result != null)
                    actuallyUninstalledGames.Add(result);
            }
        }

        return actuallyUninstalledGames;
    }

    private bool TryUninstallSingleGame(Game game, out Game? uninstalledGame)
    {
        uninstalledGame = null;
        _logger.LogDebug("Uninstalling {Name}", game.Name);

        bool deleted = false;

        string? romPath = null;
        if (game.Roms != null && game.Roms.Any())
        {
            romPath = game.Roms.FirstOrDefault()?.Path;
        }

        if (!string.IsNullOrWhiteSpace(romPath))
        {
            string resolvedRomPath = _playniteAPI.ExpandGameVariables(game, romPath);
            try
            {
                resolvedRomPath = Path.GetFullPath(resolvedRomPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve ROM path {Path} for game {Name}", resolvedRomPath, game.Name);
            }

            if (File.Exists(resolvedRomPath))
            {
                try
                {
                    File.Delete(resolvedRomPath);
                    game.IsInstalled = false;
                    _playniteAPI.Database.Games.Update(game);
                    uninstalledGame = game;
                    _logger.LogInformation("Successfully deleted ROM file {Path} for {Name}", resolvedRomPath, game.Name);
                    deleted = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete ROM file {Path} for game {Name}", resolvedRomPath, game.Name);
                }
            }
            else
            {
                _logger.LogWarning("ROM file {Path} does not exist for {Name}", resolvedRomPath, game.Name);
            }
        }

        if (!deleted)
        {
            if (game.InstallationStatus != InstallationStatus.Installed ||
                string.IsNullOrWhiteSpace(game.InstallDirectory))
            {
                _logger.LogError("Game {Name} is not installed or has no install directory!", game.Name);
                return false;
            }

            string resolvedPath = _playniteAPI.ExpandGameVariables(game, game.InstallDirectory);

            try
            {
                resolvedPath = Path.GetFullPath(resolvedPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve path {Path} for game {Name}", resolvedPath, game.Name);
                return false;
            }

            if (!Directory.Exists(resolvedPath))
            {
                _logger.LogError("Game {Name} install directory does not exist: {Path}", game.Name, resolvedPath);
                return false;
            }

            try
            {
                Directory.Delete(resolvedPath, true);
                game.IsInstalled = false;
                _playniteAPI.Database.Games.Update(game);
                uninstalledGame = game;
                _logger.LogInformation("Successfully uninstalled {Name} from {Path}", game.Name, resolvedPath);
                deleted = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete directory {Path} for game {Name}", resolvedPath, game.Name);
            }
        }

        return deleted;
    }

    #endregion

    #region Custom Uninstall Controller

    private class CustomUninstallController : UninstallController
{
    private readonly Game _game;
    private readonly GameManagementPlugin _plugin;

    public CustomUninstallController(Game game, GameManagementPlugin plugin) : base(game)
    {
        _game = game;
        _plugin = plugin;
        Name = "Gerenciado pelo Playnite";
    }

    public override void Uninstall(UninstallActionArgs args)
    {
        try
        {
            var result = _plugin.UninstallGamesCore(new[] { _game }, showConfirmation: false, showProgress: false);

            if (result.Contains(_game))
            {
                // CORRETO: usa InvokeOnUninstalled() para sinalizar sucesso
                InvokeOnUninstalled();
            }
            else
            {
                // CORRETO: lança uma exceção para sinalizar falha
                throw new Exception("A desinstalação falhou ou foi cancelada.");
            }
        }
        catch (Exception ex)
        {
            // Relança a exceção para que o Playnite a trate como falha
            throw;
        }
    }
}
