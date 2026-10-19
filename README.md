# Potion Tracker — lokale Party-Potion-Auswertung

Stand: 2026-10-19. Separates Dalamud-Dev-Plugin. Es sendet **nichts an andere Spieler oder ins Netzwerk**, speichert keine Aufzeichnungen und wird durch diesen Quellcode nicht installiert oder als Dalamud-Plugin veröffentlicht. Ergebnisse bleiben nur bis zum Entladen im Speicher.

## Verhalten

- Das Plugin beobachtet automatisch die Party einschließlich der Eigenperson (bei Solo-Spiel nur die Eigenperson). Die Eigenperson wird über Content-ID, Actor-ID und nötigenfalls eindeutigen Namen plus Heimatwelt einem Party-Eintrag zugeordnet; fehlt sie in der Party-Liste, wird sie ergänzt. Auch bei fehlendem `GameObject` eines Party-Eintrags wird ein Actor über `EntityId` gesucht. Jede erkannte Person steht beim Pullstart nur einmal in Auswertung, Chat-Nenner und Tabelle. Ein nicht lesbarer (`null`) Party-Slot hat keine verlässliche Identität und wird nicht als zusätzliche Person gezählt; insbesondere dupliziert er die ergänzte Eigenperson nicht. Es braucht keine Zielauswahl.
- Ein Pull beginnt/endet, wenn die **lokale** `InCombat`-Bedingung auf ja/nein wechselt. Das ist ein Proxy, kein sicherer Boss-Pull oder Wipe/Kill.
- Nur ein bei sichtbarem, zur Party passendem Battle-Actor beobachteter Status-49-Übergang **absent → present** zählt als Potion-Indiz. Das Verschwinden zählt nicht. Ein bereits bei der ersten Beobachtung vorhandener Status ist kein Ereignis. 4:30 Minuten sind ausschließlich ein Plausibilitäts-/Dedup-Fenster für erneute 0→1-Übergänge; daraus werden nie zusätzliche Ereignisse errechnet.
- Ein beobachteter 0→1-Übergang bis 30 Sekunden vor lokalem Pullstart wird als Prepot diesem Pull zugeordnet, sofern er nach dem vorherigen erkannten Kampfende liegt. Zeiten sind relativ zum lokalen Pullstart; alle Ereignisse davor erscheinen in der Tabelle als `Prepull`, danach z. B. `4:36`. Gleichzeitige Frame-Beobachtungen haben keine beweisbare Reihenfolge.
- Nach erkanntem Kampfende erscheint **nur im eigenen Chat** eine Zeile wie `Potion onsets detected for 3/8 players. /potions for details`. Der Nenner ist die Zahl eindeutiger Party-Personen einschließlich Eigenperson beim Pullstart; der Zähler umfasst Personen mit mindestens einem beobachteten, zugeordneten 0→1-Übergang. `/potions` öffnet die Tabelle mit Class/Job-Icon, Name, Potion, Amount und Timing. Die Reihenfolge ist strikt Tank, Healer, Melee, PhysRange, Caster, dann unbekannte Klassen/Jobs; innerhalb jeder Rolle bleibt die Party-Reihenfolge vom Pullstart erhalten. Die Basisklassen GLA/MRD, CNJ, PGL/LNC/ROG, ARC sowie THM/ACN sind ihren jeweiligen Rollen zugeordnet. Begrenzte Jobs BLU und BST werden, falls vorhanden, als Caster bzw. Melee einsortiert. `/ptrdiag` bleibt als Alias erhalten.
- In der Potion-Spalte bedeutet `✓`: mindestens ein Potion-Beginn wurde erkannt. `X` bedeutet ausschließlich: **kein Potion-Beginn erkannt**; es ist keine Behauptung, dass tatsächlich keine Potion benutzt wurde. Auch bei unvollständiger Beobachtung (z. B. bereits aktiver Status bei der ersten Beobachtung, Actor-/Sampling-Lücke, Partywechsel oder später Pluginstart) steht `X` statt `?`. `Amount` zeigt die Zahl beobachteter Potion-Beginne, bei keinem erkannten Beginn `0`; bei Beobachtungslücken kann die Zahl eine Untergrenze sein. Status 49 ist ein Buff-Indiz, kein direkter Item-Beweis. Die Beobachtungsqualität wird weiterhin intern erfasst, aber nicht als separates UI-Urteil angezeigt.
- Es gibt aktuell **keine Duty-Whitelist** im Projekt. Die Auswertung nutzt nur den lokalen Combat-Proxy in der aktuellen Party.

Die Chat-Zeile enthält einen **funktionsfähigen Slash-Befehl**, keinen klickbaren Dalamud-Chatlink. Ein solcher Link wurde nicht ingame verifiziert und ist für die Grundfunktion nicht erforderlich.

Verwendete Dalamud-APIs: [IPartyList](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IPartyList/), [IPartyMember](https://dalamud.dev/api/Dalamud.Game.ClientState.Party/Interfaces/IPartyMember/), [IPlayerState](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IPlayerState/), [IObjectTable](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IObjectTable/), [IChatGui.Print](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IChatGui/#printstring-string-ushort), [ITextureProvider](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/ITextureProvider/) und [GameIconLookup](https://dalamud.dev/api/Dalamud.Interface.Textures/Structs/GameIconLookup/). Das Klassen-/Job-Mapping nutzt `ClassJob.RowId` aus [ffxiv-datamining/ClassJob.csv](https://github.com/xivapi/ffxiv-datamining/blob/master/csv/en/ClassJob.csv). Gerahmte Klassen-/Job-Icons kommen direkt aus dem Spiel über `ITextureProvider.GetFromGameIcon` mit `IconId = 62100 + ClassJob.RowId` (z. B. GLA 62101, CNJ 62106, PLD 62119, VPR 62141, PCT 62142); keine externen Bilddateien. Bei unbekannter Klasse, unbekanntem Job oder noch nicht ladbarem Icon steht `?`. Die konkrete Darstellung ist noch ingame zu prüfen.

## Build und lokale Dev-Prüfung

In der aktuellen Linux-Umgebung ist ein Terminal verfügbar, aber kein `dotnet`-SDK installiert. **Dieser Projektstand ist hier nicht kompiliert oder ingame verifiziert.** Ein älterer Windows-Build der Diagnoseversion beweist diesen Build nicht.

Für die lokale Dev-Prüfung das Repository unter Windows klonen (`git clone https://github.com/Shintaxo/FFXIV-Potion-Tracker.git`) oder einen bestehenden Klon mit `git pull` aktualisieren. PowerShell im Projektordner:

```powershell
dotnet --list-sdks
Test-Path "$env:APPDATA\XIVLauncher\addon\Hooks\dev\Dalamud.dll"
dotnet build .\PotionTrackerDiagnostic.csproj -c Debug -p:Platform=x64 2>&1 | Tee-Object .\build.log
if ($LASTEXITCODE -ne 0) { throw "Build failed; see build.log" }
Get-ChildItem .\bin -Recurse -Filter PotionTrackerDiagnostic.dll | Select-Object -ExpandProperty FullName
```

Benötigt: .NET 10 SDK und Dalamud-Dev-Dateien (SDK 15). Falls der `Test-Path` false ergibt, den Dev-Pfad prüfen oder `DALAMUD_HOME` setzen. Den DLL-Pfad in `/xlsettings` → Experimental → Dev Plugin Locations eintragen; unter `/xlplugins` → Dev Tools → Installed Dev Plugins lokal aktivieren. Keine Veröffentlichung über ein Dalamud-Plugin-Repository.

## Offene Ingame-Prüfung

Bei einem **ohnehin anstehenden** neuen Kampf prüfen: lokale Chat-Zeile genau einmal am Kampfende, `/potions`-Fenster und gerahmte Icon-Darstellung, tatsächliche Partygröße einschließlich Eigenperson (auch wenn Party-Liste sie auslässt oder ihr Actor dort `null` ist), Rollen-Sortierung einschließlich Basisklassen mit stabiler Party-Reihenfolge und deren Icon-Darstellung, beobachtete Prepot-Zeit, erneuter Statusbeginn nach etwa 4:30, sowie `X` und Amount `0` bei keinem erkannten Potion-Beginn auch nach Actor-/Sampling-Lücke oder spätem Pluginstart. Den bereits dokumentierten historischen Test nicht wiederholen; das Plugin lädt keine vergangenen Ereignisse.
