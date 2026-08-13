using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
public class GameManagementPlugin : GenericPlugin
{
    private readonly IPlayniteAPI _playniteAPI;
    private readonly StorageInfo _storageInfo;
    private readonly ILogger<GameManagementPlugin> _logger;
    private ResourceDictionary? _localizationResources; // nullable para evitar CS8618

    private string StoragePath => Path.Combine(GetPluginUserDataPath(), "storage.json");

    public override Guid Id => Guid.Parse("a37e0963-91ac-4432-be2a-69e366c44726");

    public GameManagementPlugin(IPlayniteAPI playniteAPI) : base(playniteAPI)
    {
        _playniteAPI = playniteAPI;
        _logger = CustomLogger.GetLogger<GameManagementPlugin>(nameof(GameManagementPlugin));

        AssemblyLoader.ValidateReferencedAssemblies(_logger);

        _storageInfo = new StorageInfo(_playniteAPI);
        _storageInfo.LoadFromFile(StoragePath);

        // Carrega os recursos de localização
        LoadLocalizationResources();
    }

    /// <summary>
    /// Carrega o dicionário de recursos de localização com base no idioma configurado no Playnite.
    /// Fallback para en_US se o idioma atual não estiver disponível.
    /// </summary>
    private void LoadLocalizationResources()
    {
        try
        {
            // Obtém o diretório onde a DLL do plugin está instalada
            string pluginDirectory = Path.GetDirectoryName(GetType().Assembly.Location)!;
            var localizationFolder = Path.Combine(pluginDirectory, "Localization");

            // Obtém o código de idioma configurado no Playnite (ex: "pt_BR", "en_US")
            var culture = _playniteAPI.ApplicationSettings.Language ?? "en_US";
            var resourceFile = Path.Combine(localizationFolder, $"{culture}.xaml");

            // Se o arquivo específico não existir, tenta o fallback para en_US
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

            // Opcional: adiciona ao Application.Resources para uso em outras partes
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

    /// <summary>
    /// Obtém uma string localizada do dicionário de recursos.
    /// Fallback para o valor padrão ou para a própria chave.
    /// </summary>
    private string GetLocalizedString(string key, string defaultValue)
    {
        if (_localizationResources != null && _localizationResources.Contains(key))
        {
            return _localizationResources[key] as string ?? defaultValue;
        }
        return defaultValue;
    }

    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        // Verifica se todos os jogos selecionados são da biblioteca "Playnite" (adicionados manualmente)
        var allPlayniteGames = args.Games?.All(g => g.Source?.Name == "Playnite") ?? false;

        if (allPlayniteGames)
        {
            // Obtém o texto "Uninstall" localizado
            var uninstallText = GetLocalizedString("GameManagement_Uninstall", "Uninstall");

            yield return new GameMenuItem
            {
                Action = UninstallGameMenuAction,
                Description = uninstallText
            };
        }
        // A opção "Uninstall and Remove" foi removida
    }

    private void UninstallGameMenuAction(GameMenuItemActionArgs args)
    {
        UninstallGames(args);
    }

    private List<Game> UninstallGames(GameMenuItemActionArgs args)
    {
        var games = args.Games;
        if (games is null || !games.Any()) return new List<Game>();

        // Obtém textos localizados para a confirmação
        var title = GetLocalizedString("GameManagement_ConfirmationTitle", "Confirmation");
        var messageTemplate = GetLocalizedString("GameManagement_ConfirmationMessage", 
            "Do you really want to uninstall {0} game(s)?");
        var message = string.Format(messageTemplate, games.Count);

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

                if (game.InstallationStatus != InstallationStatus.Installed
                    || string.IsNullOrWhiteSpace(game.InstallDirectory)
                    || !Directory.Exists(game.InstallDirectory))
                {
                    _logger.LogError("Game {Name} is not installed!", game.Name);
                    continue;
                }

                Directory.Delete(game.InstallDirectory, true);
                game.IsInstalled = false;
                actuallyUninstalledGames.Add(game);
                // As estatísticas de armazenamento NÃO são removidas aqui (conforme solicitado)
            }

            _storageInfo.SaveToFile(StoragePath);
        }, new GlobalProgressOptions(
            string.Format(GetLocalizedString("GameManagement_ProgressTitle", "Uninstalling {0} games"), games.Count), 
            true));

        return actuallyUninstalledGames;
    }

    private readonly CancellationTokenSource _source = new();

    public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
    {
        Task.Run(() =>
        {
            _storageInfo.UpdateStorageInfoForAllNewGames();
            _storageInfo.SaveToFile(StoragePath);
        }, _source.Token);
    }

    public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
    {
        if (_source.IsCancellationRequested) return;
        _source.Cancel();
        _source.Dispose();
    }

    public override void OnGameInstalled(OnGameInstalledEventArgs args)
    {
        _storageInfo.AddStorageInfo(args.Game);
        _storageInfo.SaveToFile(StoragePath);
    }

    public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
    {
        _storageInfo.RemoveStorageInfo(args.Game);
        _storageInfo.SaveToFile(StoragePath);
    }

    public override IEnumerable<SidebarItem> GetSidebarItems()
    {
        yield return new SidebarItem
        {
            Title = GetLocalizedString("GameManagement_SidebarTitle", "View Storage Statistics"),
            Type = SiderbarItemType.View,
            Visible = true,
            Opened = () => new StorageStatisticsView
            {
                DataContext = _storageInfo
            }
        };
    }
}
