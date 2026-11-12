using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

    #region Menu Items

    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        var allPlayniteGames = args.Games?.All(g => g.PluginId == Guid.Empty && g.IsInstalled) ?? false;

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

    private List<Game> UninstallGames(GameMenuItemActionArgs args)
    {
        var games = args.Games;
        if (games is null || !games.Any()) return new List<Game>();

        // Capturar a seleção atual ANTES de apagar (para auto-seleção)
        var previousSelectedId = _playniteAPI.MainView.SelectedGames?.FirstOrDefault()?.Id;
        var filteredSnapshot = _playniteAPI.MainView.FilteredGames?.Select(g => g.Id).ToList() ?? new List<Guid>();

        // Confirmação
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

        if (result != MessageBoxResult.Yes) return new List<Game>();

        _logger.LogInformation("Uninstalling {Count} game(s)", games.Count);

        var actuallyUninstalledGames = new List<Game>(games.Count);
        var updatedGames = new List<Game>(games.Count);

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

                progressArgs.CurrentProgressValue += 1;
                progressArgs.Text = string.Format(
                    GetLocalizedString("GameManagement_ProgressText", "Uninstalling {0}"),
                    game.Name);

                bool fullyUninstalled = UninstallSingleGame(game, progressArgs.CancelToken);
                if (fullyUninstalled)
                    actuallyUninstalledGames.Add(game);

                updatedGames.Add(game);
            }
        }, new GlobalProgressOptions(
            string.Format(GetLocalizedString("GameManagement_ProgressTitle", "Uninstalling {0} games"), games.Count),
            true));

        // Persistir alterações (ações removidas / IsInstalled alterado)
        if (updatedGames.Count > 0)
        {
            _playniteAPI.Database.Games.Update(updatedGames);
            _logger.LogInformation("Updated {Count} game(s) in database", updatedGames.Count);
        }

        // Chamar Installation Status Updater se houve desinstalação completa
        if (actuallyUninstalledGames.Count > 0)
        {
            TriggerInstallationStatusUpdater();
        }

        // Auto-selecionar próximo jogo depois que o Playnite atualizar a UI
        AutoSelectNextGame(previousSelectedId, filteredSnapshot);

        return actuallyUninstalledGames;
    }

    /// <summary>
    /// Desinstala um jogo individualmente. Retorna true se o jogo foi
    /// completamente desinstalado (InstallDirectory removido ou ROM principal apagada).
    /// </summary>
    private bool UninstallSingleGame(Game game, CancellationToken cancelToken)
    {
        // Resolver InstallDirectory
        string? installDir = null;
        if (game.InstallationStatus == InstallationStatus.Installed
            && !string.IsNullOrWhiteSpace(game.InstallDirectory))
        {
            try
            {
                var expanded = _playniteAPI.ExpandGameVariables(game, game.InstallDirectory);
                installDir = Path.GetFullPath(expanded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve install dir {Dir} for {Name}",
                    game.InstallDirectory, game.Name);
            }
        }

        // Construir opções de eliminação
        var options = new List<string>();
        var actionsByOption = new Dictionary<string, GameAction>();
        string? uninstallOption = null;

        if (installDir != null && Directory.Exists(installDir))
        {
            uninstallOption = string.Format(
                GetLocalizedString("GameManagement_DeleteFull",
                    "[UNINSTALL GAME] Delete entire installation directory: {0}"),
                installDir);
            options.Add(uninstallOption);
        }

        var actions = game.GameActions?.ToList() ?? new List<GameAction>();
        foreach (var action in actions)
        {
            var folder = GetFolderToDeleteFromAction(game, action);
            if (folder == null) continue;

            // Se a pasta da ação == InstallDirectory, é já a opção "full uninstall"
            if (installDir != null && string.Equals(folder, installDir, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Directory.Exists(folder)) continue;

            var label = string.Format(
                GetLocalizedString("GameManagement_DeleteAction",
                    "[DELETE ACTION] {0} -> folder: {1}"),
                action.Name ?? "(unnamed)", folder);
            options.Add(label);
            actionsByOption[label] = action;
        }

        // Sem opções → tentar apagar a ROM (comportamento antigo)
        if (options.Count == 0)
        {
            return TryDeleteRomFile(game);
        }

        // Só uma opção → confirmar directamente
        if (options.Count == 1)
        {
            var confirm = _playniteAPI.Dialogs.ShowMessage(
                string.Format(GetLocalizedString("GameManagement_ConfirmSingleTarget",
                    "Delete the following target for '{0}'?\n\n{1}"), game.Name, options[0]),
                GetLocalizedString("GameManagement_ConfirmationTitle", "Confirmation"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return false;
        }

        // Escolher opção
        string? selected;
        if (options.Count == 1)
        {
            selected = options[0];
        }
        else
        {
            selected = _playniteAPI.Dialogs.SelectString(
                string.Format(GetLocalizedString("GameManagement_SelectTarget",
                    "Choose which element of '{0}' to delete:"), game.Name),
                GetLocalizedString("GameManagement_SelectTargetTitle", "Select what to delete"),
                options);
        }

        if (string.IsNullOrEmpty(selected) || cancelToken.IsCancellationRequested) return false;

        // Executar
        if (selected == uninstallOption)
        {
            return DeleteInstallDirectory(game, installDir!);
        }

        if (actionsByOption.TryGetValue(selected, out var actionToDelete))
        {
            return DeleteActionFolder(game, actionToDelete);
        }

        return false;
    }

    /// <summary>
    /// Determina a "última pasta" da acção: se a acção aponta para
    /// {InstallDir}\Sonic Project Hero\Sonic - Project Hero.exe,
    /// devolve {InstallDir}\Sonic Project Hero
    /// </summary>
    private string? GetFolderToDeleteFromAction(Game game, GameAction action)
    {
        if (action.Type != GameActionType.File) return null;
        if (string.IsNullOrWhiteSpace(action.Path)) return null;

        try
        {
            var expanded = _playniteAPI.ExpandGameVariables(game, action.Path);
            if (string.IsNullOrWhiteSpace(expanded)) return null;

            var fullPath = Path.GetFullPath(expanded);

            if (File.Exists(fullPath))
                return Path.GetDirectoryName(fullPath);  // ← última pasta do ficheiro

            if (Directory.Exists(fullPath))
                return fullPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve action path {Path} for {Game}",
                action.Path, game.Name);
        }
        return null;
    }

    /// <summary>
    /// Apaga a subpasta de uma acção específica. A acção é removida
    /// (desativada) mas o jogo NÃO é marcado como desinstalado.
    /// </summary>
    private bool DeleteActionFolder(Game game, GameAction action)
    {
        var folder = GetFolderToDeleteFromAction(game, action);
        if (folder == null) return false;

        try
        {
            Directory.Delete(folder, true);
            _logger.LogInformation("Deleted action folder {Folder} for {Game}", folder, game.Name);

            // Desativar a acção (remover da lista)
            game.GameActions.Remove(action);
            _logger.LogInformation("Removed/disabled action '{Action}' from {Game}",
                action.Name, game.Name);

            return false;  // NÃO marcar jogo como desinstalado
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete action folder {Folder} for {Game}", folder, game.Name);
            return false;
        }
    }

    /// <summary>
    /// Apaga o InstallDirectory principal. Marca o jogo como desinstalado.
    /// </summary>
    private bool DeleteInstallDirectory(Game game, string installDir)
    {
        try
        {
            Directory.Delete(installDir, true);
            game.IsInstalled = false;
            _logger.LogInformation("Successfully uninstalled {Name} from {Path}", game.Name, installDir);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete directory {Path} for {Name}", installDir, game.Name);
            return false;
        }
    }

    /// <summary>
    /// Fallback para jogos sem InstallDirectory nem acções: apaga o ficheiro ROM.
    /// </summary>
    private bool TryDeleteRomFile(Game game)
    {
        var romPath = game.Roms?.FirstOrDefault()?.Path;
        if (string.IsNullOrWhiteSpace(romPath)) return false;

        try
        {
            var expanded = _playniteAPI.ExpandGameVariables(game, romPath);
            expanded = Path.GetFullPath(expanded);

            if (!File.Exists(expanded))
            {
                _logger.LogWarning("ROM file {Path} not found for {Game}", expanded, game.Name);
                return false;
            }

            File.Delete(expanded);
            game.IsInstalled = false;
            _logger.LogInformation("Deleted ROM file {Path} for {Game}", expanded, game.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete ROM file for {Game}", game.Name);
            return false;
        }
    }

    /// <summary>
    /// Tenta chamar o método "verificar estado de instalação de todos os jogos"
    /// do plugin Installation Status Updater (darklinkpower). Usa reflexão porque
    /// o nome do método pode variar entre versões.
    /// </summary>
    private void TriggerInstallationStatusUpdater()
    {
        try
        {
            var isu = _playniteAPI.Addons?.Plugins?.FirstOrDefault(p =>
                p.GetType().Name.Contains("InstallationStatusUpdater", StringComparison.OrdinalIgnoreCase)
                || (p.Name?.Contains("Installation Status", StringComparison.OrdinalIgnoreCase) == true));

            if (isu == null)
            {
                _logger.LogDebug("Installation Status Updater not installed – skipping");
                return;
            }

            _logger.LogInformation("Triggering Installation Status Updater…");

            var type = isu.GetType();
            var candidates = new[]
            {
                "UpdateAllGames", "CheckAllGames", "UpdateAll", "CheckAll",
                "UpdateInstallationStatusAllGames", "UpdateAllInstallationStatuses",
                "UpdateInstallationStatus", "CheckInstallationStatus",
            };

            foreach (var name in candidates)
            {
                try
                {
                    var method = type.GetMethod(name,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (method != null)
                    {
                        method.Invoke(isu, null);
                        _logger.LogInformation("Called {Method} on Installation Status Updater", name);
                        return;
                    }
                }
                catch { /* tentar o próximo */ }
            }

            _logger.LogWarning("No suitable update method found on Installation Status Updater");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger Installation Status Updater");
        }
    }

    /// <summary>
    /// Depois que o Playnite atualizar a lista de jogos, seleciona o "próximo"
    /// jogo (na ordem em que estava antes) que ainda esteja instalado.
    /// </summary>
    private void AutoSelectNextGame(Guid? previousSelectedId, List<Guid> filteredSnapshot)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return;

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };

        timer.Tick += (s, e) =>
        {
            timer.Stop();
            try
            {
                var installed = _playniteAPI.Database.Games
                    .Where(g => g.IsInstalled)
                    .ToList();
                if (installed.Count == 0) return;

                var installedIds = installed.Select(g => g.Id).ToHashSet();
                Guid? toSelect = null;

                if (previousSelectedId.HasValue && filteredSnapshot.Count > 0)
                {
                    var idx = filteredSnapshot.IndexOf(previousSelectedId.Value);
                    if (idx >= 0)
                    {
                        // procurar em frente
                        for (int i = idx + 1; i < filteredSnapshot.Count; i++)
                        {
                            if (installedIds.Contains(filteredSnapshot[i]))
                            {
                                toSelect = filteredSnapshot[i];
                                break;
                            }
                        }
                        // se não houver, procurar para trás
                        if (!toSelect.HasValue)
                        {
                            for (int i = idx - 1; i >= 0; i--)
                            {
                                if (installedIds.Contains(filteredSnapshot[i]))
                                {
                                    toSelect = filteredSnapshot[i];
                                    break;
                                }
                            }
                        }
                    }
                }

                toSelect ??= installed.FirstOrDefault()?.Id;

                if (toSelect.HasValue)
                {
                    _playniteAPI.MainView.SelectGame(toSelect.Value);
                    _logger.LogInformation("Auto-selected next game after uninstall");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-select next game");
            }
        };

        timer.Start();
    }

    #endregion
}
