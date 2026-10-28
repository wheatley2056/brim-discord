# Discord-Plugin für Brim

Bringt Discord in die Notch: deinen Sprachkanal mit allen, die gerade sprechen, und neue Nachrichten.

- **Sprachkanal in der Notch:** Bist du in einem Sprachkanal, zeigt die Notch kompakt das Mikrofon und wer spricht, z. B. „Anna spricht“, sonst „Zocken · 4“. Spricht jemand, schimmert die Notch in Discord-Blau. Das Mikrofon wird rot, wenn du stumm oder taub bist. Aufgeklappt siehst du alle im Kanal (grüner Punkt = spricht) und die Knöpfe **Stumm**, **Taub** und **Auflegen**.
- **Über Spielen:** Im Vollbild bleibt eine kleine Pille mit dem, der gerade spricht (Brim ab 2.6.0, Recht „Overlay“).
- **Nachrichten:** Neue Discord-Nachrichten blenden sich kurz ein („Anna · #allgemein · Freunde“ und der Text) und werden gezählt („3 neu“), bis du Discord öffnest oder „Gelesen“ drückst.
- **Widget „Discord-Sprachkanal“** (klein, mittel, groß, breit): Kanal, wer spricht bzw. wer drin ist, und die Knöpfe.
- **Widget „Discord-Nachrichten“** (groß, mittel, breit, ganz): die neuesten Nachrichten; ein Klick öffnet Discord.

## Woher die Daten kommen

- **Nachrichten** liest Brim aus den Windows-Benachrichtigungen der Discord-App. Dafür muss in Brim unter Einstellungen → Kalender und Benachrichtigungen der Zugriff auf Benachrichtigungen eingerichtet sein, und Discord muss Desktop-Benachrichtigungen zeigen (Discord → Einstellungen → Benachrichtigungen). Discord schickt keine Benachrichtigung, solange sein Fenster vorne ist. Es kommt also genau das an, was Discord dir auch als Windows-Meldung zeigt.
- **Sprachkanal, Stumm und Taub** kommen über die lokale RPC-Schnittstelle des Discord-Clients (dieselbe, die z. B. Stream-Deck-Plugins nutzen). Das Plugin spricht nur mit dem Discord auf deinem PC und, einmal beim Verbinden, mit `discord.com`, um das Token abzuholen.

## Sprachkanal einrichten (einmal, ca. 3 Minuten)

Discord gibt die Sprach-Schnittstelle nur an Apps, die es freigeschaltet hat, **oder an den Besitzer der App**. Deshalb legst du dir eine eigene, private Discord-App an. Sie ist nur ein Eintrag im Developer Portal, kein Bot, und niemand außer dir sieht sie.

1. <https://discord.com/developers/applications> öffnen, mit deinem Discord-Konto anmelden, **New Application**, Name z. B. „Brim“, anlegen.
2. Links **OAuth2**:
   - **Client ID** kopieren.
   - **Client Secret** → *Reset Secret* → kopieren.
   - Unter **Redirects** `http://localhost` eintragen und speichern. (Die Adresse wird nie aufgerufen, Discord verlangt sie aber.)
3. In Brim → Einstellungen → Plugins → Discord **Client-ID** und **Client-Secret** eintragen.
4. Discord zeigt jetzt die Frage, ob „Brim“ auf Discord zugreifen darf → **Autorisieren**. Das passiert nur einmal; danach verbindet sich das Plugin von selbst, sobald Discord läuft.

Hast du die Frage weggeklickt, kommt sie nicht von selbst wieder. Im Widget „Discord-Sprachkanal“ dann **Mit Discord verbinden** drücken.

Das Token liegt verschlüsselt (Windows-DPAPI, nur dein Konto kann es lesen) in `%LocalAppData%\Brim\PluginData\de.brim.discord\token.bin`. Löschst du die Datei, fragt Discord beim nächsten Start neu. Das Client-Secret steht wie die Schlüssel anderer Plugins in Brims `settings.json` in deinem Benutzerprofil.

Ohne Client-ID und Secret funktionieren die Nachrichten trotzdem; nur der Sprachkanal fehlt.

## Rechte

Brim fragt beim Einschalten nach **„Benachrichtigungen“** (die Windows-Meldungen der App „Discord“ gehen an das Plugin statt an Brims eigene Vorschau) und **„Netzwerk“** (Token von `discord.com`). Optional nutzt es **„Overlay“** für die Pille über Spielen.

## Einstellungen

Brim → Einstellungen → Plugins → Discord:

- **Neue Nachrichten in der Notch zählen** (an)
- **Neue Nachricht kurz in der Notch einblenden** (an)
- **Sprachkanal in der Notch zeigen** (an)
- **Client-ID** und **Client-Secret** deiner Discord-App (siehe oben)
- **Lichtschein, wenn jemand spricht** (an)
- **Wer spricht, als kleine Pille über Spielen im Vollbild** (an)

## Installieren

**Aus der Bibliothek:** Brim → Einstellungen → Plugins → Bibliothek → Discord → Installieren.

**Fertig gebaut:** `brim-discord.zip` aus dem neuesten Release nach `%LocalAppData%\Brim\Plugins\discord` entpacken, Brim neu starten und im Tab **Plugins** „Discord“ einschalten.

**Selbst bauen:** Das Plugin nutzt `Brim.Sdk` aus dem Brim-Repository. Beide Repositories nebeneinander klonen:

```powershell
git clone https://github.com/wheatley2056/brim.git
git clone https://github.com/wheatley2056/brim-discord.git
cd brim-discord
dotnet publish src/Discord -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Brim\Plugins\discord"
```

Liegt Brim woanders: `-p:BrimRoot=C:\Pfad\zu\brim`.

## Wenn etwas nicht klappt

Das Protokoll liegt in `%LocalAppData%\Brim\logs\plugins\de.brim.discord.log`.

- **„Discord ist nicht geöffnet“:** Der Discord-Client (nicht der Browser) muss laufen. Discord PTB und Canary gehen auch.
- **„Discord kennt diese Client-ID nicht“:** Client-ID aus OAuth2 im Developer Portal neu kopieren.
- **„Client-ID oder Client-Secret stimmt nicht“:** Secret im Developer Portal zurücksetzen und neu eintragen.
- **„In der Discord-App fehlt der Redirect http://localhost“:** siehe Schritt 2.
- **Die Frage zum Autorisieren kommt nicht oder Discord meldet „Not authenticated or invalid scope“:** Du musst mit dem Discord-Konto angemeldet sein, dem die App gehört (oder es unter *App Testers* eintragen).
- **Keine Nachrichten:** Benachrichtigungen in Brim und in Discord prüfen (siehe oben); Discord meldet nichts, solange sein Fenster im Vordergrund ist.
