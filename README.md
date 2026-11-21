# jellyfin4k – Anime4K for Jellyfin

A Jellyfin plugin that upscales anime in real time with [Anime4K](https://github.com/bloc97/Anime4K).
It adds its own menu to the video player, and the menu works fully with a **TV remote**.

You can use Anime4K in two ways:

| | **This device (GPU)** | **Server (for TVs)** |
|---|---|---|
| Where it runs | The GPU of the device you are watching on (WebGL2 in the browser) | The GPU of the **Jellyfin server** (FFmpeg + libplacebo) |
| What the client receives | The original video, upscaled locally | Video that is already upscaled (1080p / 1440p / 4K) |
| Best for | PCs, laptops, the desktop app | TVs, streaming sticks, phones, any device with a weak GPU |
| Clients | Jellyfin web client (browser, LG webOS, Jellyfin Media Player) | **Every** Jellyfin client (including Android TV and Samsung Tizen) |

In server mode, your computer (the Jellyfin server) runs Anime4K and your TV shows the upscaled picture.

## Features

- **Anime4K v4.0.1** with all official modes: A, B, C, A+A, B+B, C+A
- Three quality levels: *Fast* (M/S networks), *High quality* (VL/M) and *Ultra* (UL/L)
- **Button in the video player** (✨ `auto_awesome`) that opens a settings menu
- **TV remote support**: D-pad, OK and Back work in the menu, plus the colour buttons
- **Comparison view** (original on the left, Anime4K on the right) and a **statistics overlay** (resolution, frames per second, GPU time)
- **Server upscaling per device**: switch it on from the player or for any device in the dashboard
- Subtitles stay visible. Text subtitles are still rendered by the client. In server mode, image subtitles (PGS/DVD) are burned in sharply at the upscaled resolution.
- Seeking, resuming, switching audio tracks and progress reporting work as usual

## Using it in the player

During playback, the Anime4K button appears in the player's control bar. You can also use these keys:

| Action | Remote | Keyboard |
|---|---|---|
| Open/close the menu | 🔴 **Red** | `Shift + A` |
| Turn Anime4K on/off | 🟢 **Green** | `Ctrl + 0` (off) |
| Next mode | 🟡 **Yellow** | `Ctrl + 1` … `Ctrl + 6` (A, B, C, A+A, B+B, C+A) |
| Comparison on/off | 🔵 **Blue** | – |

In the menu: **▲ ▼** select an entry · **◀ ▶** change the value · **OK** confirm · **Back** close.

Menu entries:

- **Anime4K**: *Off*, *This device (GPU)* or *Server (for TV)*
- **Mode**: A (blurry sources, most 1080p anime), B (slightly blurry, more compression artifacts), C (sharp sources), A+A, B+B, C+A
- **Quality**: Fast / High quality / Ultra
- **Output resolution** (server only): 1080p, 1440p or 2160p
- **Comparison**, **Statistics**
- **Apply & restart playback**: shown when server settings have changed. Playback restarts at the same position, now with the stream from the server.

> The player menu is currently shown in German.

## TVs

- **LG webOS** and other devices that load the Jellyfin web client from the server get the Anime4K menu directly in the player (red button). Choose *Server (for TV)* and apply.
- **Android TV, Fire TV, Samsung Tizen, Kodi, Infuse and similar apps** use their own player. In the dashboard, go to **Anime4K → Server upscaling per device**, select the device, set mode and resolution, and save. The next playback on that device is upscaled.

How it works: for enabled devices, the plugin rewrites the server's `PlaybackInfo` response so the client plays an HLS stream from the plugin. FFmpeg creates that stream on demand: `libplacebo` runs the Anime4K shaders, then the video is encoded in hardware or software. Seeking restarts encoding at the new position. When FFmpeg is far enough ahead of playback, it is paused (Linux/macOS only).

## Installation

### Via repository URL (recommended, with automatic updates)

1. Jellyfin dashboard → **Plugins** → **Repositories** (or *Catalog* → gear icon) → **+**
2. Name: `Anime4K`, URL:

   ```
   https://github.com/NotJuu/jellyfin4k/releases/latest/download/manifest.json
   ```

3. Save, then open the **Catalog**, select **Anime4K** and install it.
4. Restart Jellyfin and reload the Jellyfin page in your browser (Ctrl+F5).

Jellyfin picks the right build automatically (Jellyfin 12.x or 10.11).

### Manual

1. Download the matching ZIP from the [releases](../../releases):
   - `anime4k-jellyfin-12.zip` for Jellyfin 12.x
   - `anime4k-jellyfin-10.11.zip` for Jellyfin 10.11
2. In the Jellyfin data directory, create a folder `plugins/Anime4K` and copy `Jellyfin.Plugin.Anime4K.dll` into it
   (for example `/var/lib/jellyfin/plugins/Anime4K/`, Docker: `/config/plugins/Anime4K/`,
   Windows: `%ProgramData%\Jellyfin\Server\plugins\Anime4K\`).
3. Restart Jellyfin and reload the Jellyfin page in your browser.

### Publishing a new version

Push a tag `vX.Y.Z` on `main`, or create a GitHub release with that tag. The workflow builds both variants and creates the release with the ZIP files and `manifest.json`. The repository URL above then points to the new version automatically.

### Building it yourself

Requires the .NET 10 SDK:

```bash
cd Jellyfin.Plugin.Anime4K
dotnet build -c Release
```

For Jellyfin 10.11:

```bash
cd Jellyfin.Plugin.Anime4K
dotnet restore -p:JellyfinVersion=10.11.11 -p:TargetFramework=net9.0
dotnet build --no-restore -c Release -p:JellyfinVersion=10.11.11 -p:TargetFramework=net9.0
```

## Requirements

### Browser mode (this device)

- A browser with **WebGL2** and `EXT_color_buffer_float` (all current versions of Chrome, Edge, Firefox and Safari)
- A reasonably modern GPU. Rough guide for 1080p → 4K: *Fast* runs on integrated GPUs, *High quality* needs a mid-range graphics card, *Ultra* needs a high-end card.

### Server mode

- FFmpeg with **libplacebo** and **Vulkan**. The bundled `jellyfin-ffmpeg` (version 6 or newer) has both.
- A GPU in the server with a Vulkan driver (NVIDIA, AMD or Intel)
- For 4K in real time, use a **hardware encoder** (NVENC, Quick Sync, VAAPI, AMF or VideoToolbox). With "Automatic", the plugin uses the encoder from Jellyfin's hardware acceleration settings.

**Docker:** the container needs access to the GPU **and** its Vulkan driver, for example:

- NVIDIA: `--gpus all -e NVIDIA_DRIVER_CAPABILITIES=all` (`all` includes `graphics`, which Vulkan needs)
- Intel/AMD: `--device /dev/dri:/dev/dri`

In the dashboard, **Anime4K → Diagnostics → Test server upscaling** checks FFmpeg, libplacebo, Vulkan and the encoder with a short test video. If the test reports *"Found no suitable device"*, enter `0` in the **Vulkan device** field.

## Dashboard settings

**Dashboard → Plugins → Anime4K**

- Turn the player integration and server upscaling on or off, choose whether regular users may switch on server upscaling themselves, and optionally limit it to titles with the genre "Anime" or "Animation"
- Default mode, quality and resolution
- Server upscaling per device (with mode, quality and resolution)
- Encoder, quality (CRF/CQ/QP), maximum bitrate, preset, segment length, audio, Vulkan/VAAPI device, maximum number of simultaneous streams
- Diagnostics: a test run and a list of running Anime4K streams with the FFmpeg command and log

> The dashboard page is currently in German.

## Tests

```bash
# Compares the WebGL2 implementation with libplacebo (reference) for all modes and quality levels
node tests/shader-parity.cjs

# End-to-end test against a running Jellyfin server with the plugin installed
JF_URL=http://localhost:8096 JF_USER=admin JF_PASS=admin JF_ITEM=<episode-id> node tests/e2e-web.cjs
```

`shader-parity.cjs` compares the output of the browser implementation with FFmpeg/libplacebo (PSNR, threshold 35 dB). The results are 54–57 dB, which is practically identical.

## License

Plugin: MIT, see [LICENSE](LICENSE).
Anime4K shaders: © 2019-2021 bloc97, MIT, see
[Shaders/LICENSE-Anime4K.txt](Jellyfin.Plugin.Anime4K/Shaders/LICENSE-Anime4K.txt).
