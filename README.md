# jellyfin4k – Anime4K für Jellyfin

Ein Jellyfin-Plugin, das Animes mit [Anime4K](https://github.com/bloc97/Anime4K) in Echtzeit hochrechnet –
mit eigenem Menü im Player, das sich vollständig mit der **Fernbedienung** bedienen lässt.

Es gibt zwei Arten, Anime4K zu nutzen:

| | **Dieses Gerät (GPU)** | **Server (für TV)** |
|---|---|---|
| Wo wird gerechnet? | Grafikkarte des Geräts, auf dem du schaust (WebGL2 im Browser) | Grafikkarte des **Jellyfin-Servers** (FFmpeg + libplacebo) |
| Was kommt an? | Original-Video, wird lokal hochgerechnet | Fertig hochgerechnetes Video (1080p / 1440p / 4K) |
| Ideal für | PC, Laptop, Desktop-App | Fernseher, Sticks, Handys – alles mit schwacher GPU |
| Clients | Jellyfin-Webclient (Browser, LG webOS, Jellyfin Media Player) | **Alle** Jellyfin-Clients (auch Android TV, Samsung Tizen, …) |

Mit dem Server-Modus rechnet also dein Computer (der Jellyfin-Server) Anime4K, und auf dem Fernseher
siehst du trotzdem das hochgerechnete Bild.

## Funktionen

- **Anime4K v4.0.1** mit allen offiziellen Modi: A, B, C, A+A, B+B, C+A
- Drei Qualitätsstufen: *Schnell* (M/S-Netze), *Hohe Qualität* (VL/M) und *Ultra* (UL/L)
- **Button im Video-Player** (✨ `auto_awesome`) mit Einstellungsmenü
- **Fernbedienung**: Steuerkreuz, OK und Zurück funktionieren im Menü, dazu Farbtasten
- **Vergleichsansicht** (links Original, rechts Anime4K) und **Statistik** (Auflösung, Bilder/s, GPU-Zeit)
- **Server-Upscaling pro Gerät** – direkt im Player einschaltbar oder im Dashboard für jedes Gerät
- Untertitel bleiben sichtbar: Textuntertitel werden weiter vom Client dargestellt, Bilduntertitel (PGS/DVD)
  werden beim Server-Upscaling scharf in die hochgerechnete Auflösung eingebrannt
- Spulen, Fortsetzen, Audiospur-Wechsel und Fortschritt funktionieren wie gewohnt

## Bedienung im Player

Während der Wiedergabe erscheint unten in der Steuerleiste der Anime4K-Button. Alternativ:

| Taste | Fernbedienung | Tastatur |
|---|---|---|
| Menü öffnen/schließen | 🔴 **Rot** | `Umschalt + A` |
| Anime4K an/aus | 🟢 **Grün** | `Strg + 0` (aus) |
| Nächster Modus | 🟡 **Gelb** | `Strg + 1` … `Strg + 6` (A, B, C, A+A, B+B, C+A) |
| Vergleich an/aus | 🔵 **Blau** | – |

Im Menü: **▲ ▼** Eintrag wählen · **◀ ▶** Wert ändern · **OK** bestätigen · **Zurück** schließt.

Menüeinträge:

- **Anime4K** – *Aus*, *Dieses Gerät (GPU)* oder *Server (für TV)*
- **Modus** – A (unscharfe Quellen, die meisten 1080p-Animes), B (leicht unscharf, mehr Artefakte),
  C (scharfe Quellen), A+A, B+B, C+A
- **Qualität** – Schnell / Hohe Qualität / Ultra
- **Ausgabeauflösung** (nur Server) – 1080p, 1440p oder 2160p
- **Vergleich**, **Statistik**
- **Übernehmen & Wiedergabe neu starten** – erscheint, wenn sich Server-Einstellungen geändert haben.
  Die Wiedergabe startet an derselben Stelle neu, jetzt mit dem Stream vom Server.

## Fernseher

- **LG webOS** und andere Geräte, die den Jellyfin-Webclient vom Server laden: Das Anime4K-Menü ist direkt im
  Player verfügbar (rote Taste). Dort *Server (für TV)* wählen und übernehmen.
- **Android TV, Fire TV, Samsung Tizen, Kodi, Infuse, …**: Diese Apps bringen einen eigenen Player mit.
  Im Dashboard unter **Anime4K → Server-Upscaling pro Gerät** das Gerät auswählen, Modus/Auflösung einstellen,
  speichern – ab der nächsten Wiedergabe kommt dort das hochgerechnete Video an.

So funktioniert es technisch: Für aktivierte Geräte schreibt das Plugin die `PlaybackInfo`-Antwort des Servers
so um, dass der Client einen HLS-Stream des Plugins abspielt. Dieser wird on demand von FFmpeg erzeugt
(`libplacebo` mit den Anime4K-Shadern, danach Hardware- oder Software-Encoding). Springen startet die Kodierung an
der neuen Stelle neu; ist FFmpeg weit genug voraus, wird es pausiert (Linux/macOS).

## Installation

### Über die Repository-URL (empfohlen, mit automatischen Updates)

1. Jellyfin-Dashboard → **Plugins** → **Repositories** (bzw. *Kataloge* → Zahnrad) → **+**
2. Name: `Anime4K`, URL:

   ```
   https://github.com/NotJuu/jellyfin4k/releases/latest/download/manifest.json
   ```

3. Speichern, dann unter **Katalog** das Plugin **Anime4K** auswählen und installieren.
4. Jellyfin neu starten und im Browser die Jellyfin-Seite neu laden (Strg+F5).

Jellyfin wählt automatisch den passenden Build (Jellyfin 12.x oder 10.11).

### Manuell

1. Die passende ZIP aus den [Releases](../../releases) herunterladen:
   - `anime4k-jellyfin-12.zip` für Jellyfin 12.x
   - `anime4k-jellyfin-10.11.zip` für Jellyfin 10.11
2. Im Jellyfin-Datenverzeichnis einen Ordner `plugins/Anime4K` anlegen und `Jellyfin.Plugin.Anime4K.dll` hineinkopieren
   (z. B. `/var/lib/jellyfin/plugins/Anime4K/`, Docker: `/config/plugins/Anime4K/`,
   Windows: `%ProgramData%\Jellyfin\Server\plugins\Anime4K\`).
3. Jellyfin neu starten und die Jellyfin-Seite im Browser neu laden.

### Neue Version veröffentlichen

Einen Tag `vX.Y.Z` auf `main` pushen (oder auf GitHub ein Release mit diesem Tag anlegen). Der Workflow
baut beide Varianten, erstellt das Release mit den ZIP-Dateien und der `manifest.json` – die Repository-URL
oben zeigt danach automatisch auf die neue Version.

Selbst bauen (benötigt das .NET 10 SDK):

```bash
cd Jellyfin.Plugin.Anime4K
dotnet build -c Release
```

Für Jellyfin 10.11:

```bash
cd Jellyfin.Plugin.Anime4K
dotnet restore -p:JellyfinVersion=10.11.11 -p:TargetFramework=net9.0
dotnet build --no-restore -c Release -p:JellyfinVersion=10.11.11 -p:TargetFramework=net9.0
```

## Voraussetzungen

### Browser-Modus (Dieses Gerät)

- Browser mit **WebGL2** und `EXT_color_buffer_float` (alle aktuellen Chrome-, Edge-, Firefox- und Safari-Versionen)
- Eine einigermaßen aktuelle GPU. Richtwerte für 1080p → 4K: *Schnell* läuft auf integrierten GPUs,
  *Hohe Qualität* braucht eine Mittelklasse-Grafikkarte, *Ultra* eine High-End-Karte.

### Server-Modus

- FFmpeg mit **libplacebo** und **Vulkan** – das mitgelieferte `jellyfin-ffmpeg` (ab Version 6) erfüllt das
- Eine GPU im Server mit Vulkan-Treiber (NVIDIA, AMD, Intel)
- Für 4K in Echtzeit unbedingt einen **Hardware-Encoder** verwenden (NVENC, Quick Sync, VAAPI, AMF, VideoToolbox).
  Bei „Automatisch“ wird der Encoder aus den Jellyfin-Einstellungen zur Hardwarebeschleunigung übernommen.

**Docker:** Der Container braucht Zugriff auf die GPU **und** den Vulkan-Treiber, z. B.

- NVIDIA: `--gpus all -e NVIDIA_DRIVER_CAPABILITIES=all` (das `all` enthält `graphics`, das für Vulkan nötig ist)
- Intel/AMD: `--device /dev/dri:/dev/dri`

Im Dashboard unter **Anime4K → Diagnose → Server-Upscaling testen** prüft das Plugin FFmpeg, libplacebo,
Vulkan und den Encoder mit einem kurzen Testvideo. Meldet der Test *„Found no suitable device“*,
im Feld **Vulkan-Gerät** `0` eintragen.

## Einstellungen im Dashboard

**Dashboard → Plugins → Anime4K**

- Player-Integration und Server-Upscaling ein/aus, ob normale Benutzer Server-Upscaling selbst einschalten dürfen,
  optional nur für Titel mit Genre „Anime“/„Animation“
- Standardmodus, -qualität und -auflösung
- Server-Upscaling pro Gerät (mit Modus, Qualität, Auflösung)
- Encoder, Qualität (CRF/CQ/QP), maximale Bitrate, Preset, Segmentlänge, Audio, Vulkan-/VAAPI-Gerät,
  maximale Anzahl gleichzeitiger Streams
- Diagnose: Testlauf und Übersicht laufender Anime4K-Streams inkl. FFmpeg-Befehl und Log

## Tests

```bash
# WebGL2-Implementierung gegen libplacebo (Referenz) für alle Modi und Qualitätsstufen
node tests/shader-parity.cjs

# End-to-End gegen einen laufenden Jellyfin-Server mit installiertem Plugin
JF_URL=http://localhost:8096 JF_USER=admin JF_PASS=admin JF_ITEM=<episoden-id> node tests/e2e-web.cjs
```

`shader-parity.cjs` vergleicht das Ergebnis der Browser-Implementierung mit dem von FFmpeg/libplacebo
(PSNR, Grenzwert 35 dB; erreicht werden 54–57 dB, also praktisch identisch).

## Lizenz

Plugin: MIT, siehe [LICENSE](LICENSE).
Anime4K-Shader: © 2019-2021 bloc97, MIT, siehe
[Shaders/LICENSE-Anime4K.txt](Jellyfin.Plugin.Anime4K/Shaders/LICENSE-Anime4K.txt).
