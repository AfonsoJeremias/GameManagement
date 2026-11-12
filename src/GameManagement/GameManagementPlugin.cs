using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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

    private class UninstallPlan
    {
        public Game Game = null!;
        public UninstallTargetType TargetType;
        public string TargetPath = "";
        public GameAction? ActionToRemove;
    }

    private enum UninstallTargetType
    {
        InstallDirectory,
        ActionFolder,
        Rom
    }

    private List<Game> UninstallGames(GameMenuItemActionArgs args)
    {
        var games = args.Games;
        if (games is null || !games.Any()) return new List<Game>();

        var previousSelectedId = _playniteAPI.MainView.SelectedGames?.FirstOrDefault()?.Id;
        var filteredSnapshot = _playniteAPI.MainView.FilteredGames?.Select(g => g.Id).ToList() ?? new List<Guid>();

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

        // FASE 1: recolher decisões (diálogos na UI thread)
        var plans = new List<UninstallPlan>();
        foreach (var game in games)
        {
            try
            {
                var plan = BuildUninstallPlan(game);
                if (plan != null) plans.Add(plan);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build uninstall plan for {Name}", game.Name);
            }
        }

        if (plans.Count == 0)
        {
            _logger.LogInformation("No uninstall plans to execute");
            return new List<Game>();
        }

        // FASE 2: executar eliminações (background, sem diálogos)
        var actuallyUninstalledGames = new List<Game>();
        var updatedGames = new List<Game>(plans.Count);

        _playniteAPI.Dialogs.ActivateGlobalProgress(progressArgs =>
        {
            progressArgs.ProgressMaxValue = plans.Count;
            progressArgs.CurrentProgressValue = 0;

            foreach (var plan in plans)
            {
                if (progressArgs.CancelToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Uninstallation has been canceled");
                    return;
                }

                progressArgs.CurrentProgressValue += 1;
                progressArgs.Text = string.Format(
                    GetLocalizedString("GameManagement_ProgressText", "Uninstalling {0}"),
                    plan.Game.Name);

                bool fullyUninstalled = ExecutePlan(plan);
                if (fullyUninstalled) actuallyUninstalledGames.Add(plan.Game);
                updatedGames.Add(plan.Game);
            }
        }, new GlobalProgressOptions(
            string.Format(GetLocalizedString("GameManagement_ProgressTitle", "Uninstalling {0} games"), plans.Count),
            true));

        // FASE 3: persistir alterações
        if (updatedGames.Count > 0)
        {
            _playniteAPI.Database.Games.Update(updatedGames);
            _logger.LogInformation("Updated {Count} game(s) in database", updatedGames.Count);
        }

        // FASE 4: trigger ISU se houve desinstalação completa
        if (actuallyUninstalledGames.Count > 0)
        {
            TriggerInstallationStatusUpdater();
        }

        // FASE 5: auto-selecionar próximo jogo
        AutoSelectNextGame(previousSelectedId, filteredSnapshot);

        return actuallyUninstalledGames;
    }

    private UninstallPlan? BuildUninstallPlan(Game game)
    {
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

        foreach (var action in game.GameActions?.ToList() ?? new List<GameAction>())
        {
            var folder = GetFolderToDeleteFromAction(game, action);
            if (folder == null) continue;

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

        if (options.Count == 0)
        {
            return BuildRomPlan(game);
        }

        string? selected;
        if (options.Count == 1)
        {
            var confirm = _playniteAPI.Dialogs.ShowMessage(
                string.Format(GetLocalizedString("GameManagement_ConfirmSingleTarget",
                    "Delete the following target for '{0}'?\n\n{1}"), game.Name, options[0]),
                GetLocalizedString("GameManagement_ConfirmationTitle", "Confirmation"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return null;
            selected = options[0];
        }
        else
        {
            selected = ShowSelectionDialog(
                GetLocalizedString("GameManagement_SelectTargetTitle", "Select what to delete"),
                string.Format(GetLocalizedString("GameManagement_SelectTarget",
                    "Choose which element of '{0}' to delete:"), game.Name),
                options);

            if (string.IsNullOrEmpty(selected))
                return null;
        }

        if (selected == uninstallOption)
        {
            return new UninstallPlan
            {
                Game = game,
                TargetType = UninstallTargetType.InstallDirectory,
                TargetPath = installDir!
            };
        }

        if (string.IsNullOrEmpty(selected))
            return null;

        if (!actionsByOption.TryGetValue(selected!, out var actionToDelete))
            return null;

        var folderToDelete = GetFolderToDeleteFromAction(game, actionToDelete);
        if (folderToDelete == null) return null;

        return new UninstallPlan
        {
            Game = game,
            TargetType = UninstallTargetType.ActionFolder,
            TargetPath = folderToDelete,
            ActionToRemove = actionToDelete
        };
    }

    private UninstallPlan? BuildRomPlan(Game game)
    {
        var romPath = game.Roms?.FirstOrDefault()?.Path;
        if (string.IsNullOrWhiteSpace(romPath)) return null;

        try
        {
            var expanded = _playniteAPI.ExpandGameVariables(game, romPath);
            var full = Path.GetFullPath(expanded);
            if (!File.Exists(full)) return null;

            return new UninstallPlan
            {
                Game = game,
                TargetType = UninstallTargetType.Rom,
                TargetPath = full
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve ROM for {Name}", game.Name);
            return null;
        }
    }

    private bool ExecutePlan(UninstallPlan plan)
    {
        try
        {
            switch (plan.TargetType)
            {
                case UninstallTargetType.InstallDirectory:
                    Directory.Delete(plan.TargetPath, true);
                    plan.Game.IsInstalled = false;
                    _logger.LogInformation("Uninstalled {Name} from {Path}", plan.Game.Name, plan.TargetPath);
                    return true;

                case UninstallTargetType.ActionFolder:
                    if (!string.IsNullOrEmpty(plan.TargetPath) && Directory.Exists(plan.TargetPath))
                        Directory.Delete(plan.TargetPath, true);

                    if (plan.ActionToRemove != null && plan.Game.GameActions != null)
                        plan.Game.GameActions.Remove(plan.ActionToRemove);

                    _logger.LogInformation("Deleted action folder {Folder} for {Name}",
                        plan.TargetPath, plan.Game.Name);
                    return false;

                case UninstallTargetType.Rom:
                    File.Delete(plan.TargetPath);
                    plan.Game.IsInstalled = false;
                    _logger.LogInformation("Deleted ROM {Path} for {Name}", plan.TargetPath, plan.Game.Name);
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute uninstall plan for {Name}", plan.Game.Name);
        }
        return false;
    }

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
                return Path.GetDirectoryName(fullPath);

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

    private string? ShowSelectionDialog(string title, string message, List<string> options)
    {
        var app = Application.Current;
        if (app?.Dispatcher == null) return null;

        string? result = null;

        app.Dispatcher.Invoke(() =>
        {
            var window = new Window
            {
                Title = title,
                Width = 620,
                Height = 420,
                WindowStartupLocation = app.MainWindow != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Owner = app.MainWindow,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = message,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(textBlock, 0);
            grid.Children.Add(textBlock);

            var listBox = new ListBox { Margin = new Thickness(10) };
            foreach (var opt in options) listBox.Items.Add(opt);
            listBox.SelectedIndex = 0;
            Grid.SetRow(listBox, 1);
            grid.Children.Add(listBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10)
            };

            var okButton = new Button
            {
                Content = "OK",
                Width = 90,
                Margin = new Thickness(5),
                IsDefault = true
            };
            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 90,
                Margin = new Thickness(5),
                IsCancel = true
            };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            Grid.SetRow(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            window.Content = grid;

            okButton.Click += (s, e) =>
            {
                result = listBox.SelectedItem as string;
                window.Close();
            };
            cancelButton.Click += (s, e) => window.Close();

            window.ShowDialog();
        });

        return result;
    }

    private void TriggerInstallationStatusUpdater()
    {
        try
        {
            var isu = _playniteAPI.Addons?.Plugins?.FirstOrDefault(p =>
            {
                var typeName = p.GetType().Name ?? string.Empty;
                var assemblyName = p.GetType().Assembly.GetName().Name ?? string.Empty;

                return typeName.IndexOf("InstallationStatusUpdater", StringComparison.OrdinalIgnoreCase) >= 0
                    || assemblyName.IndexOf("InstallationStatusUpdater", StringComparison.OrdinalIgnoreCase) >= 0;
            });

            if (isu == null)
            {
                _logger.LogDebug("Installation Status Updater not installed – skipping");
                return;
            }

            _logger.LogInformation("Triggering Installation Status Updater…");

            var type = isu.GetType();
            var candidates = new[]
            {
                "UpdateAllGames", "UpdateStatusAllGames", "UpdateAllGamesStatus",
                "CheckAllGames", "UpdateAll", "CheckAll",
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
                catch
                {
                }
            }

            _logger.LogWarning("No suitable update method found on Installation Status Updater");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger Installation Status Updater");
        }
    }

    private void AutoSelectNextGame(Guid? previousSelectedId, List<Guid> filteredSnapshot)
    {
        var app = Application.Current;
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
                        for (int i = idx + 1; i < filteredSnapshot.Count; i++)
                        {
                            if (installedIds.Contains(filteredSnapshot[i]))
                            {
                                toSelect = filteredSnapshot[i];
                                break;
                            }
                        }
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
