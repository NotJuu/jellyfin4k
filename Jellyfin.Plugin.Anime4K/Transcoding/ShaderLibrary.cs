using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Anime4K.Transcoding;

/// <summary>
/// Anime4K presets (identical to the official mpv presets) and access to the bundled GLSL shaders.
/// </summary>
public static class ShaderLibrary
{
    /// <summary>
    /// Available modes.
    /// </summary>
    public static readonly IReadOnlyList<ModeInfo> Modes =
    [
        new("A", "Modus A", "Stark unscharfe / verwaschene Quellen (die meisten 1080p-Animes)"),
        new("B", "Modus B", "Leicht unscharfe Quellen, stärkere Kompressionsartefakte"),
        new("C", "Modus C", "Scharfe Quellen mit wenig Unschärfe (z. B. 720p-Neuproduktionen)"),
        new("AA", "Modus A+A", "Wie A, aber stärkere Rekonstruktion"),
        new("BB", "Modus B+B", "Wie B, aber stärkere Rekonstruktion"),
        new("CA", "Modus C+A", "Wie C, mit zusätzlicher Restaurierung")
    ];

    /// <summary>
    /// Available quality presets.
    /// </summary>
    public static readonly IReadOnlyList<ModeInfo> Qualities =
    [
        new("Fast", "Schnell", "Für schwächere GPUs (M/S-Netze)"),
        new("HQ", "Hohe Qualität", "Für Mittelklasse- und High-End-GPUs (VL/M-Netze)"),
        new("Ultra", "Ultra", "Nur für sehr starke GPUs (UL/L-Netze)")
    ];

    /// <summary>
    /// Supported output heights for server side upscaling.
    /// </summary>
    public static readonly IReadOnlyList<int> TargetHeights = [1080, 1440, 2160];

    private static readonly ConcurrentDictionary<string, string> _bundleCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Normalizes a mode identifier.
    /// </summary>
    /// <param name="mode">Requested mode.</param>
    /// <returns>A valid mode id.</returns>
    public static string NormalizeMode(string? mode)
    {
        var m = (mode ?? string.Empty).Replace("+", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        return Modes.Any(x => x.Id == m) ? m : "A";
    }

    /// <summary>
    /// Normalizes a quality identifier.
    /// </summary>
    /// <param name="quality">Requested quality.</param>
    /// <returns>A valid quality id.</returns>
    public static string NormalizeQuality(string? quality)
    {
        var q = Qualities.FirstOrDefault(x => string.Equals(x.Id, quality?.Trim(), StringComparison.OrdinalIgnoreCase));
        return q?.Id ?? "HQ";
    }

    /// <summary>
    /// Normalizes an output height.
    /// </summary>
    /// <param name="height">Requested height.</param>
    /// <returns>A supported height.</returns>
    public static int NormalizeHeight(int? height)
    {
        if (height is null or <= 0)
        {
            return 2160;
        }

        return TargetHeights.OrderBy(h => Math.Abs(h - height.Value)).First();
    }

    /// <summary>
    /// Returns the shader file names for a mode/quality combination.
    /// </summary>
    /// <param name="mode">Mode id.</param>
    /// <param name="quality">Quality id.</param>
    /// <returns>List of shader file names in execution order.</returns>
    public static IReadOnlyList<string> GetShaderFiles(string mode, string quality)
    {
        mode = NormalizeMode(mode);
        quality = NormalizeQuality(quality);

        // big = first (main) networks, small = second stage networks; same as the official mpv presets.
        var (big, small) = quality switch
        {
            "Fast" => ("M", "S"),
            "Ultra" => ("UL", "L"),
            _ => ("VL", "M"),
        };

        const string Clamp = "Anime4K_Clamp_Highlights.glsl";
        const string Down2 = "Anime4K_AutoDownscalePre_x2.glsl";
        const string Down4 = "Anime4K_AutoDownscalePre_x4.glsl";
        string Restore(string s) => $"Anime4K_Restore_CNN_{s}.glsl";
        string RestoreSoft(string s) => $"Anime4K_Restore_CNN_Soft_{s}.glsl";
        string Upscale(string s) => $"Anime4K_Upscale_CNN_x2_{s}.glsl";
        string UpscaleDenoise(string s) => $"Anime4K_Upscale_Denoise_CNN_x2_{s}.glsl";

        return mode switch
        {
            "B" => [Clamp, RestoreSoft(big), Upscale(big), Down2, Down4, Upscale(small)],
            "C" => [Clamp, UpscaleDenoise(big), Down2, Down4, Upscale(small)],
            "AA" => [Clamp, Restore(big), Upscale(big), Restore(small), Down2, Down4, Upscale(small)],
            "BB" => [Clamp, RestoreSoft(big), Upscale(big), Down2, Down4, RestoreSoft(small), Upscale(small)],
            "CA" => [Clamp, UpscaleDenoise(big), Down2, Down4, Restore(small), Upscale(small)],
            _ => [Clamp, Restore(big), Upscale(big), Down2, Down4, Upscale(small)],
        };
    }

    /// <summary>
    /// Returns one concatenated mpv/libplacebo user shader for a mode/quality combination.
    /// </summary>
    /// <param name="mode">Mode id.</param>
    /// <param name="quality">Quality id.</param>
    /// <returns>Shader source.</returns>
    public static string GetBundle(string mode, string quality)
    {
        mode = NormalizeMode(mode);
        quality = NormalizeQuality(quality);
        return _bundleCache.GetOrAdd(mode + "|" + quality, _ =>
        {
            var sb = new StringBuilder();
            sb.Append("// Anime4K bundle: mode ").Append(mode).Append(", quality ").Append(quality).Append('\n');
            sb.Append("// Anime4K by bloc97 - MIT License - https://github.com/bloc97/Anime4K\n\n");
            foreach (var file in GetShaderFiles(mode, quality))
            {
                sb.Append(ReadShader(file).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                sb.Append("\n\n");
            }

            return sb.ToString();
        });
    }

    /// <summary>
    /// Writes the bundle for a mode/quality combination to a directory (once) and returns the file path.
    /// </summary>
    /// <param name="directory">Target directory.</param>
    /// <param name="mode">Mode id.</param>
    /// <param name="quality">Quality id.</param>
    /// <returns>Path of the shader file.</returns>
    public static string WriteBundle(string directory, string mode, string quality)
    {
        var content = GetBundle(mode, quality);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12].ToLowerInvariant();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"anime4k_{NormalizeMode(mode)}_{NormalizeQuality(quality)}_{hash}.glsl");
        if (!File.Exists(path))
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }

        return path;
    }

    private static string ReadShader(string file)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = typeof(ShaderLibrary).Namespace!.Replace(".Transcoding", ".Shaders.", StringComparison.Ordinal) + file;
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("Anime4K shader not found: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// A mode or quality option.
    /// </summary>
    /// <param name="Id">Identifier.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Description">Description.</param>
    public sealed record ModeInfo(string Id, string Name, string Description);
}
