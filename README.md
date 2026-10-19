# Potion Tracker

Potion Tracker is a Dalamud plugin that shows locally observed party Medicated status onsets for each tracked combat pull.

## Install

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add and enable this repository URL:
   `https://raw.githubusercontent.com/Shintaxo/FFXIV-Potion-Tracker/main/pluginmaster.json`
3. Open `/xlplugins`, find **Potion Tracker**, and install it.

## Use

Open `/potions` to view the latest tracked pull. Open **Settings** there (or **Open Config** in the plugin manager) to choose between the default **Savage + Ultimate + Extreme** mode and **Every combat**.

Only Medicated onsets observable by your client are counted. No detected onset does **not** prove a player did not use a potion.
