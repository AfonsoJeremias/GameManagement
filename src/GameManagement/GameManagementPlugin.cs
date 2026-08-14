using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Extensions.Common;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO; // Para Recycle Bin
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace GameManagement;

public enum DeletionMode
{
    RecycleBin,
    Permanent
}

public class GameManagementSettings : ObservableObject
{
    private DeletionMode _deletionMode = DeletionMode.RecycleBin;

    public DeletionMode DeletionMode
    {
        get => _deletionMode;
        set => SetValue(ref _deletionMode, value);
    }
}

[UsedImplicitly]
public class GameManagementPlugin : GenericPlugin
{
    private readonly IPlayniteAPI _playniteAPI;
    private readonly ILogger<GameManagementPlugin> _logger;
    private ResourceDictionary? _localizationResources;
    private GameManagementSettings _settings;

    public override Guid Id => Guid.Parse("a37e0963-91ac-4432-be2a-69e366c44726");

    public GameManagementPlugin(IPlayniteAPI playniteAPI) : base(playniteAPI)
    {
        _playniteAPI = playniteAPI;
        _logger = CustomLogger.GetLogger<GameManagementPlugin>(nameof(GameManagementPlugin));
        LoadLocalizationResources();

        // Carrega configurações
        _settings = LoadPluginSettings<GameManagementSettings>() ?? new GameManagementSettings();
        SavePluginSettings(_settings);
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

    #region Menu Items

    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        var allPlayniteGames = args.Games?.All(g => g.Source?.Name == "Playnite") ?? false;

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

    #region Uninstall Logic

    /// <summary>
    /// Deleta um arquivo ou diretório conforme a configuração do usuário (Lixeira ou permanente).
    /// </summary>
    private void DeleteFileOrDirectory(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            if (_settings.DeletionMode == DeletionMode.RecycleBin)
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                Directory.Delete(path, true);
        }
        else
        {
            if (_settings.DeletionMode == DeletionMode.RecycleBin)
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else
                File.Delete(path);
        }
    }

    private List<Game> UninstallGames(GameMenuItemActionArgs args)
    {
        var games = args.Games;
        if (games is null || !games.Any()) return new List<Game>();

        var title = GetLocalizedString("GameManagement_ConfirmationTitle", "Confirmation");
        string message;
        if (games.Count == 1)
        {
            message = GetLocalizedString("GameManagement_ConfirmationMessage",
                "Do you really want to uninstall this game?");
        }
        else
        {
            var template = GetLocalizedString("GameManagement_ConfirmationMessages",
                "Do you really want to uninstall these {0} games?");
            message = string.Format(template, games.Count);
        }

        var result = _playniteAPI.Dialogs.ShowMessage(message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return new List<Game>();
        }

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
                    _logger.LogInformation("Uninstallation has been canceled");
                    return;
                }

                _logger.LogDebug("Uninstalling {Name}", game.Name);

                progressArgs.CurrentProgressValue += 1;
                progressArgs.Text = string.Format(
                    GetLocalizedString("GameManagement_ProgressText", "Uninstalling {0}"),
                    game.Name);

                bool deleted = false;
                string? targetPath = null;
                bool isDirectory = false;

                // ----- PRIORIDADE 1: ROM (primeiro arquivo da coleção) -----
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
                        targetPath = resolvedRomPath;
                        isDirectory = false;
                        deleted = true;
                    }
                    else
                    {
                        _logger.LogWarning("ROM file {Path} does not exist for {Name}", resolvedRomPath, game.Name);
                    }
                }

                // ----- PRIORIDADE 2: Diretório de instalação (se a ROM não foi encontrada) -----
                if (!deleted)
                {
                    if (game.InstallationStatus != InstallationStatus.Installed ||
                        string.IsNullOrWhiteSpace(game.InstallDirectory))
                    {
                        _logger.LogError("Game {Name} is not installed or has no install directory!", game.Name);
                        continue;
                    }

                    string resolvedPath = _playniteAPI.ExpandGameVariables(game, game.InstallDirectory);

                    try
                    {
                        resolvedPath = Path.GetFullPath(resolvedPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to resolve path {Path} for game {Name}", resolvedPath, game.Name);
                        continue;
                    }

                    if (Directory.Exists(resolvedPath))
                    {
                        targetPath = resolvedPath;
                        isDirectory = true;
                        deleted = true;
                    }
                    else
                    {
                        _logger.LogError("Game {Name} install directory does not exist: {Path}", game.Name, resolvedPath);
                    }
                }

                // ----- Executa a exclusão (Lixeira ou permanente) -----
                if (deleted && targetPath != null)
                {
                    try
                    {
                        DeleteFileOrDirectory(targetPath, isDirectory);
                        game.IsInstalled = false;
                        actuallyUninstalledGames.Add(game);
                        _logger.LogInformation("Successfully removed {Path}", targetPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to remove {Path}", targetPath);
                    }
                }
                else
                {
                    _logger.LogError("Game {Name} has no valid file or folder to delete", game.Name);
                }
            }
        }, new GlobalProgressOptions(
            string.Format(GetLocalizedString("GameManagement_ProgressTitle", "Uninstalling {0} games"), games.Count),
            true));

        return actuallyUninstalledGames;
    }

    #endregion

    #region Settings Support

    public override ISettings GetSettings(bool firstRunSettings)
    {
        return _settings;
    }

    public override void SetSettings(ISettings settings)
    {
        if (settings is GameManagementSettings gmSettings)
        {
            _settings = gmSettings;
            SavePluginSettings(_settings);
        }
    }

    #endregion
}