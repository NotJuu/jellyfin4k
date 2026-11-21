using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Anime4K.Configuration;
using Jellyfin.Plugin.Anime4K.Transcoding;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Anime4K.Api;

/// <summary>
/// General Anime4K API: client script, shaders, per-device settings, diagnostics.
/// </summary>
[ApiController]
[Route("Anime4K")]
public class Anime4KController : ControllerBase
{
    private readonly TranscodeManager _manager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IServerConfigurationManager _configurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="Anime4KController"/> class.
    /// </summary>
    /// <param name="manager">Transcode manager.</param>
    /// <param name="authorizationContext">Authorization context.</param>
    /// <param name="configurationManager">Configuration manager.</param>
    public Anime4KController(TranscodeManager manager, IAuthorizationContext authorizationContext, IServerConfigurationManager configurationManager)
    {
        _manager = manager;
        _authorizationContext = authorizationContext;
        _configurationManager = configurationManager;
    }

    /// <summary>
    /// Returns the client script that is injected into the web client.
    /// </summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.anime4k.js");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        Response.Headers.CacheControl = "no-cache";
        return Content(reader.ReadToEnd(), "application/javascript", Encoding.UTF8);
    }

    /// <summary>
    /// Returns the concatenated Anime4K shaders (mpv user shader format) of a mode.
    /// </summary>
    /// <param name="mode">Mode (A, B, C, AA, BB, CA).</param>
    /// <param name="quality">Quality (Fast, HQ, Ultra).</param>
    /// <returns>GLSL source.</returns>
    [HttpGet("Shaders/Bundle")]
    [AllowAnonymous]
    [Produces("text/plain")]
    public ActionResult GetShaderBundle([FromQuery] string? mode, [FromQuery] string? quality)
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(ShaderLibrary.GetBundle(mode ?? "A", quality ?? "HQ"), "text/plain", Encoding.UTF8);
    }

    /// <summary>
    /// Returns options and the settings of the calling device.
    /// </summary>
    /// <returns>Options.</returns>
    [HttpGet("Options")]
    [Authorize]
    public async Task<ActionResult<OptionsDto>> GetOptions()
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var config = Plugin.Instance!.Configuration;
        var isAdmin = User.IsInRole("Administrator");
        return new OptionsDto
        {
            ServerUpscalingEnabled = config.EnableServerUpscaling,
            CanChangeServerUpscaling = config.EnableServerUpscaling && (isAdmin || config.AllowUsersServerUpscaling),
            IsAdmin = isAdmin,
            DeviceId = auth.DeviceId,
            Modes = ShaderLibrary.Modes,
            Qualities = ShaderLibrary.Qualities,
            TargetHeights = ShaderLibrary.TargetHeights,
            DefaultMode = ShaderLibrary.NormalizeMode(config.DefaultMode),
            DefaultQuality = ShaderLibrary.NormalizeQuality(config.DefaultQuality),
            DefaultTargetHeight = ShaderLibrary.NormalizeHeight(config.DefaultTargetHeight),
            Device = config.FindDevice(auth.DeviceId),
            ActiveJob = _manager.GetJobs().Any(j => j.DeviceId == auth.DeviceId && !j.Exited)
        };
    }

    /// <summary>
    /// Updates the server upscaling settings of the calling device.
    /// </summary>
    /// <param name="body">Settings.</param>
    /// <returns>The stored settings.</returns>
    [HttpPost("Devices/Current")]
    [Authorize]
    public async Task<ActionResult<DeviceSetting>> UpdateCurrentDevice([FromBody] DeviceSetting body)
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (string.IsNullOrEmpty(auth.DeviceId))
        {
            return BadRequest("No device id.");
        }

        var plugin = Plugin.Instance!;
        var config = plugin.Configuration;
        if (!config.EnableServerUpscaling || !(User.IsInRole("Administrator") || config.AllowUsersServerUpscaling))
        {
            return Forbid();
        }

        var device = config.FindDevice(auth.DeviceId);
        if (device is null)
        {
            device = new DeviceSetting { DeviceId = auth.DeviceId };
            config.Devices.Add(device);
        }

        device.DeviceName = string.IsNullOrEmpty(auth.Device) ? device.DeviceName : auth.Device;
        device.AppName = string.IsNullOrEmpty(auth.Client) ? device.AppName : auth.Client;
        device.ServerUpscaling = body.ServerUpscaling;
        device.Mode = ShaderLibrary.NormalizeMode(body.Mode);
        device.Quality = ShaderLibrary.NormalizeQuality(body.Quality);
        device.TargetHeight = ShaderLibrary.NormalizeHeight(body.TargetHeight);
        plugin.SaveConfiguration();

        if (!device.ServerUpscaling)
        {
            _manager.StopJobs(null, auth.DeviceId);
        }

        return device;
    }

    /// <summary>
    /// Returns diagnostics (admin only).
    /// </summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetStatus()
    {
        var encoder = _manager.GetEncoder();
        var options = _configurationManager.GetEncodingOptions();
        return new
        {
            FfmpegPath = _manager.GetFfmpegPath(),
            Encoder = encoder.Name,
            JellyfinHardwareAcceleration = options.HardwareAccelerationType.ToString(),
            TranscodeDirectory = _manager.RootDirectory,
            Jobs = _manager.GetJobs()
        };
    }

    /// <summary>
    /// Runs a short test encode with libplacebo + Anime4K + the configured encoder (admin only).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result.</returns>
    [HttpPost("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> RunTest(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var encoder = _manager.GetEncoder();
        var shader = ShaderLibrary.WriteBundle(_manager.ShaderDirectory, config.DefaultMode, "Fast");
        var swFormat = encoder.Family == "x26x" ? "yuv420p" : "nv12";
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning" };
        if (encoder.Family == "vaapi")
        {
            args.AddRange(["-vaapi_device", string.IsNullOrWhiteSpace(config.VaapiDevice) ? (string.IsNullOrWhiteSpace(_configurationManager.GetEncodingOptions().VaapiDevice) ? "/dev/dri/renderD128" : _configurationManager.GetEncodingOptions().VaapiDevice) : config.VaapiDevice]);
        }

        if (!string.IsNullOrWhiteSpace(config.VulkanDevice))
        {
            args.AddRange(["-init_hw_device", "vulkan=a4kvk:" + config.VulkanDevice.Trim(), "-filter_hw_device", "a4kvk"]);
        }

        var filter = $"libplacebo=w=1280:h=720:custom_shader_path='{FfmpegCommandBuilder.EscapeFilterPath(shader)}':format={swFormat}";
        if (encoder.Family == "vaapi")
        {
            filter += ",format=nv12,hwupload";
        }

        args.AddRange(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-t", "2", "-vf", filter, "-c:v", encoder.Name, "-f", "null", "-"]);

        var psi = new ProcessStartInfo(_manager.GetFfmpegPath())
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi)!;
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                return new { Success = false, Encoder = encoder.Name, Output = "Timeout nach 120 s." };
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            return new
            {
                Success = process.ExitCode == 0,
                Encoder = encoder.Name,
                Seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
                Output = string.IsNullOrWhiteSpace(stderr) ? "OK" : stderr
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new { Success = false, Encoder = encoder.Name, Output = ex.Message };
        }
    }

    /// <summary>
    /// Options returned to clients.
    /// </summary>
    public class OptionsDto
    {
        /// <summary>Gets or sets a value indicating whether server upscaling is enabled.</summary>
        public bool ServerUpscalingEnabled { get; set; }

        /// <summary>Gets or sets a value indicating whether the user may change server upscaling for this device.</summary>
        public bool CanChangeServerUpscaling { get; set; }

        /// <summary>Gets or sets a value indicating whether the user is an administrator.</summary>
        public bool IsAdmin { get; set; }

        /// <summary>Gets or sets the device id of the caller.</summary>
        public string? DeviceId { get; set; }

        /// <summary>Gets or sets the modes.</summary>
        public IReadOnlyList<ShaderLibrary.ModeInfo> Modes { get; set; } = [];

        /// <summary>Gets or sets the qualities.</summary>
        public IReadOnlyList<ShaderLibrary.ModeInfo> Qualities { get; set; } = [];

        /// <summary>Gets or sets the target heights.</summary>
        public IReadOnlyList<int> TargetHeights { get; set; } = [];

        /// <summary>Gets or sets the default mode.</summary>
        public string DefaultMode { get; set; } = "A";

        /// <summary>Gets or sets the default quality.</summary>
        public string DefaultQuality { get; set; } = "HQ";

        /// <summary>Gets or sets the default target height.</summary>
        public int DefaultTargetHeight { get; set; } = 2160;

        /// <summary>Gets or sets the settings of the calling device.</summary>
        public DeviceSetting? Device { get; set; }

        /// <summary>Gets or sets a value indicating whether an Anime4K transcode is running for the device.</summary>
        public bool ActiveJob { get; set; }
    }
}
