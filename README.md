# GameManagement Plugin for Playnite

This plugin adds an **"Uninstall"** option to the game context menu for native Playnite games (those with `PluginId == Guid.Empty`).

### Features
- Deletes the game's **ROM file** first (if available).
- Falls back to deleting the **installation directory** if no ROM is found.
- Expands Playnite variables (`{PlayniteDir}`, `{InstallDir}`, etc.) and resolves absolute paths.
- Shows a confirmation dialog and a progress window during uninstallation.
- Only appears for installed games, avoiding accidental clicks on uninstalled titles.

---

**Based on the original work by [erri120](https://github.com/erri120) from [Playnite.Extensions](https://github.com/erri120/Playnite.Extensions).**

---

## Installation

You can install this plugin directly via the **Addon Database** inside Playnite.

For manual installation, download the latest release from the [Releases page](https://github.com/AfonsoJeremias/GameManagement/releases/latest) and extract the contents into your Playnite `Extensions` folder.
