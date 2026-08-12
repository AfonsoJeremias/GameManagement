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
public class GameManagementPlugin : LibraryPlugin
{
    private readonly IPlayniteAPI _playniteAPI;
    private readonly ILogger<GameManagementPlugin> _logger;

    // GUID obrigatório (mantido o mesmo)
    public override Guid Id => Guid.Parse("a37e0963-91ac-4432-be2a-69e366c44726");

    // Nome da biblioteca (obrigatório para LibraryPlugin)
    public override string Name { get; } = "Game Management";

    public GameManagementPlugin(IPlayniteAPI playniteAPI) : base(playniteAPI)
    {
        _playniteAPI = playniteAPI;
        _logger = CustomLogger.GetLogger<GameManagementPlugin>(nameof(GameManagementPlugin));

        AssemblyLoader.ValidateReferencedAssemblies(_logger);

        // Configurações da biblioteca (opcional, mas recomendado)
        Properties = new LibraryPluginProperties
        {
            HasSettings = false,
            HasCustomizedGameImport = false
        };
    }

    // ============================================================
    // 1. GetGameMenuItems: apenas "Uninstall" para jogos "Playnite"
    // ============================================================
    public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
    {
        // Verifica se todos os jogos selecionados são da biblioteca "Playnite"
        var allPlayniteGames = args.Games.All(g =>
            g.Library?.Equals("Playnite", StringComparison.OrdinalIgnoreCase) == true);

        // Se houver pelo menos um jogo e todos forem Playnite, exibe a opção
        if (allPlayniteGames && args.Games.Any())
        {
            yield return new GameMenuItem
            {
                Action = UninstallGameMenuAction,
                Description = "Uninstall"  // ou "Desinstalar" se preferir
            };
        }

        // A opção "Uninstall and Remove" foi removida
    }

    // ============================================================
    // 2. Lógica de desinstalação (inalterada)
    // ============================================================
    private void UninstallGameMenuAction(GameMenuItemActionArgs args)
    {
        UninstallGames(args);
    }

    private List<Game> UninstallGames(GameMenuItemActionArgs args)
    {
        var games = args.Games;
        if (games is null || !games.Any()) return new List<Game>();

        // Caixa de diálogo de confirmação
        var result = _playniteAPI.Dialogs.ShowMessage(
            $"Do you really want to uninstall {games.Count} game(s)?",
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return new List<Game>();
        }

        _logger.LogInformation("Uninstalling {Count} game(s)", games.Count.ToString());

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
                progressArgs.Text = $"Uninstalling {game.Name}";

                // Verifica se o jogo está realmente instalado
                if (game.InstallationStatus != InstallationStatus.Installed
                    || string.IsNullOrWhiteSpace(game.InstallDirectory)
                    || !Directory.Exists(game.InstallDirectory))
                {
                    _logger.LogError("Game {Name} is not installed!", game.Name);
                    continue;
                }

                // ⚠️ EXCLUSÃO PERMANENTE (não vai para a Lixeira)
                Directory.Delete(game.InstallDirectory, true);

                // Marca o jogo como não instalado
                game.IsInstalled = false;
                actuallyUninstalledGames.Add(game);
            }

        }, new GlobalProgressOptions($"Uninstalling {games.Count} game(s)", true));

        return actuallyUninstalledGames;
    }

    // ============================================================
    // 3. Métodos obrigatórios da classe LibraryPlugin
    // ============================================================

    // Retorna uma lista vazia, pois este plugin não importa jogos
    public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
    {
        return Enumerable.Empty<GameMetadata>();
    }

    // Sem provedor de metadados
    public override LibraryMetadataProvider GetMetadataDownloader()
    {
        return null;
    }

    // ============================================================
    // 4. Eventos removidos (sem StorageInfo)
    // ============================================================

    // OnGameInstalled, OnGameUninstalled e OnApplicationStarted/Stopped
    // foram removidos porque não há mais StorageInfo para gerenciar.

    // ============================================================
    // 5. SidebarItems removido (sem StorageStatisticsView)
    // ============================================================

    // O método GetSidebarItems foi removido.
}
