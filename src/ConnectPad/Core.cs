using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ConnectPad;

internal sealed record ToolPaths(string Adb, string Scrcpy)
{
    public static ToolPaths Current
    {
        get
        {
            var toolDirectory = Path.Combine(AppContext.BaseDirectory, "tools", "scrcpy");
            return new(
                Path.Combine(toolDirectory, "adb.exe"),
                Path.Combine(toolDirectory, "scrcpy.exe"));
        }
    }
}

internal sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}

internal static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? standardInput = null,
        int timeoutSeconds = 15)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["ADB_LIBUSB"] = "1";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new(-1, string.Empty, "进程启动失败");
            }
        }
        catch (Exception exception)
        {
            return new(-1, string.Empty, exception.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the timeout and Kill().
            }

            return new(-1, await outputTask, "操作超时");
        }

        return new(process.ExitCode, await outputTask, await errorTask);
    }
}

internal sealed record AdbDevice(string Serial, string State, string Model)
{
    public bool IsReady => State.Equals("device", StringComparison.OrdinalIgnoreCase);
    public bool IsWireless => Serial.Contains(':');

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Model) ? Serial : Model;
            var connection = IsWireless ? "Wi-Fi" : "USB";
            var state = State.ToLowerInvariant() switch
            {
                "device" => string.Empty,
                "unauthorized" => " · 未授权",
                "offline" => " · 离线",
                _ => $" · {State}"
            };
            return $"{name} · {connection}{state}";
        }
    }
}

internal sealed record WifiLinkInfo(
    int? FrequencyMhz,
    int? RssiDbm,
    int? TxLinkSpeedMbps,
    double? RetryPercent,
    double? LossPercent)
{
    public string? Band => FrequencyMhz switch
    {
        >= 2400 and < 2500 => "2.4 GHz",
        >= 4900 and < 5925 => "5 GHz",
        >= 5925 and <= 7125 => "6 GHz",
        _ => null
    };
}

internal static class WifiStatusParser
{
    public static WifiLinkInfo? Parse(string output)
    {
        var frequency = ParseInt(output, @"\bFrequency:\s*(\d+)\s*MHz");
        var rssi = ParseInt(output, @"\bRSSI:\s*(-?\d+)");
        var txLinkSpeed = ParseInt(output, @"\bTx Link speed:\s*(\d+)\s*Mbps")
                          ?? ParseInt(output, @"\bLink speed:\s*(\d+)\s*Mbps");

        var successful = ParseDouble(output, @"\bsuccessfulTxPacketsPerSecond:\s*([\d.eE+-]+)");
        var retried = ParseDouble(output, @"\bretriedTxPacketsPerSecond:\s*([\d.eE+-]+)");
        var lost = ParseDouble(output, @"\blostTxPacketsPerSecond:\s*([\d.eE+-]+)");
        if (successful is null || retried is null || lost is null)
        {
            successful = ParseDouble(output, @"\bsuccessfulTxPackets:\s*([\d.eE+-]+)");
            retried = ParseDouble(output, @"\bretriedTxPackets:\s*([\d.eE+-]+)");
            lost = ParseDouble(output, @"\blostTxPackets:\s*([\d.eE+-]+)");
        }

        double? retryPercent = null;
        double? lossPercent = null;
        if (successful is not null && retried is not null && lost is not null)
        {
            var total = successful.Value + retried.Value + lost.Value;
            if (total > 0)
            {
                retryPercent = retried.Value / total * 100;
                lossPercent = lost.Value / total * 100;
            }
        }

        if (frequency is null
            && rssi is null
            && txLinkSpeed is null
            && retryPercent is null
            && lossPercent is null)
        {
            return null;
        }

        return new(frequency, rssi, txLinkSpeed, retryPercent, lossPercent);
    }

    private static int? ParseInt(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
               && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? ParseDouble(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
               && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

internal static class AdbDeviceParser
{
    public static IReadOnlyList<AdbDevice> Parse(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith('*'))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            var modelField = fields.FirstOrDefault(field => field.StartsWith("model:", StringComparison.Ordinal));
            var model = modelField is null ? string.Empty : modelField[6..].Replace('_', ' ');
            devices.Add(new(fields[0], fields[1], model));
        }

        return devices;
    }
}

internal static class InputValidation
{
    public static bool IsPairingCode(string value) =>
        value.Length == 6 && value.All(character => character is >= '0' and <= '9');

    public static bool IsEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate($"tcp://{value.Trim()}", UriKind.Absolute, out var uri))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(uri.Host)
               && uri.Port is >= 1 and <= 65535
               && uri.AbsolutePath == "/"
               && string.IsNullOrEmpty(uri.Query)
               && string.IsNullOrEmpty(uri.Fragment)
               && string.IsNullOrEmpty(uri.UserInfo);
    }
}

internal sealed class AdbService(ToolPaths paths)
{
    public bool IsAvailable => File.Exists(paths.Adb);

    public async Task<(CommandResult Result, IReadOnlyList<AdbDevice> Devices)> ListDevicesAsync(bool forceUsbRescan = false)
    {
        var result = await ProcessRunner.RunAsync(paths.Adb, ["devices", "-l"]);
        var devices = result.Success ? AdbDeviceParser.Parse(result.Output) : [];
        if (result.Success && ShouldRestartForUsb(devices, forceUsbRescan))
        {
            await ProcessRunner.RunAsync(paths.Adb, ["kill-server"]);
            result = await ProcessRunner.RunAsync(paths.Adb, ["devices", "-l"]);
            devices = result.Success ? AdbDeviceParser.Parse(result.Output) : [];
        }

        return (result, devices);
    }

    internal static bool ShouldRestartForUsb(IReadOnlyList<AdbDevice> devices, bool forceUsbRescan) =>
        devices.All(device => device.IsWireless)
        && (forceUsbRescan || devices.All(device => !device.IsReady));

    public Task<CommandResult> PairAsync(string endpoint, string pairingCode) =>
        ProcessRunner.RunAsync(paths.Adb, ["pair", endpoint], pairingCode + Environment.NewLine, 30);

    public Task<CommandResult> ConnectAsync(string endpoint) =>
        ProcessRunner.RunAsync(paths.Adb, ["connect", endpoint], timeoutSeconds: 30);

    public Task<CommandResult> GetWifiStatusAsync(string serial) =>
        ProcessRunner.RunAsync(paths.Adb, ["-s", serial, "shell", "cmd", "wifi", "status"], timeoutSeconds: 5);
}

internal sealed record ScrcpyVideoProfile(
    string Codec,
    int BitRateMbps,
    string? Encoder,
    bool SetBitRate,
    bool NoDownsizeOnError)
{
    public static ScrcpyVideoProfile UsbDefault { get; } = new("h264", 8, null, false, false);
    public static ScrcpyVideoProfile WirelessH264 { get; } = new("h264", 8, null, true, true);

    public static ScrcpyVideoProfile WirelessH265(string encoder) =>
        new("h265", 4, encoder, true, true);

    public bool IsH265 => Codec.Equals("h265", StringComparison.OrdinalIgnoreCase);
    public string StatusLabel => $"{(IsH265 ? "H.265" : "H.264")} {BitRateMbps} Mbps";
}

internal static class ScrcpyProfile
{
    public static IReadOnlyList<string> BuildArguments(string serial, ScrcpyVideoProfile profile)
    {
        var arguments = new List<string>
        {
            $"--serial={serial}",
            $"--video-codec={profile.Codec}"
        };

        if (!string.IsNullOrWhiteSpace(profile.Encoder))
        {
            arguments.Add($"--video-encoder={profile.Encoder}");
        }

        if (profile.SetBitRate)
        {
            arguments.Add($"--video-bit-rate={profile.BitRateMbps}M");
        }

        arguments.Add("--max-fps=60");
        arguments.Add("--max-size=1920");
        arguments.Add("--no-audio");
        arguments.Add("--keep-active");
        if (profile.NoDownsizeOnError)
        {
            arguments.Add("--no-downsize-on-error");
        }

        arguments.Add("--window-title=ConnectPad");
        return arguments;
    }
}

internal static class ScrcpyEncoderParser
{
    public static string? FindHardwareH265(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("--video-codec=h265", StringComparison.OrdinalIgnoreCase)
                || !line.Contains("(hw)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = Regex.Match(
                line,
                @"--video-encoder=(?:'(?<single>[^']+)'|""(?<double>[^""]+)""|(?<plain>\S+))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return new[] { "single", "double", "plain" }
                    .Select(name => match.Groups[name].Value)
                    .First(value => !string.IsNullOrWhiteSpace(value));
            }
        }

        return null;
    }
}

internal sealed class ScrcpyService(ToolPaths paths)
{
    public bool IsAvailable => File.Exists(paths.Scrcpy);

    public async Task<(bool Success, string? Encoder, string Error)> ProbeHardwareH265Async(string serial)
    {
        var result = await ProcessRunner.RunAsync(
            paths.Scrcpy,
            [$"--serial={serial}", "--list-encoders"],
            timeoutSeconds: 10);
        var output = result.Output + Environment.NewLine + result.Error;
        return result.Success
            ? (true, ScrcpyEncoderParser.FindHardwareH265(output), string.Empty)
            : (false, null, LastLine(result));
    }

    public async Task<(bool Started, string Error)> StartAsync(
        string serial,
        ScrcpyVideoProfile profile,
        Action<CommandResult> onExit)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.Scrcpy,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(paths.Scrcpy)!
        };
        startInfo.Environment["ADB_LIBUSB"] = "1";

        foreach (var argument in ScrcpyProfile.BuildArguments(serial, profile))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return (false, "进程启动失败");
            }
        }
        catch (Exception exception)
        {
            process.Dispose();
            return (false, exception.Message);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();

        var startupProbeMilliseconds = profile.IsH265 ? 5000 : 1200;
        if (await Task.WhenAny(exitTask, Task.Delay(startupProbeMilliseconds)) == exitTask)
        {
            await exitTask;
            var result = new CommandResult(process.ExitCode, await outputTask, await errorTask);
            process.Dispose();
            return (false, LastLine(result));
        }

        _ = ObserveExitAsync(process, exitTask, outputTask, errorTask, onExit);
        return (true, string.Empty);
    }

    private static async Task ObserveExitAsync(
        Process process,
        Task exitTask,
        Task<string> outputTask,
        Task<string> errorTask,
        Action<CommandResult> onExit)
    {
        try
        {
            await exitTask;
            onExit(new(process.ExitCode, await outputTask, await errorTask));
        }
        finally
        {
            process.Dispose();
        }
    }

    public static string LastLine(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                   .Select(line => line.Trim())
                   .LastOrDefault() ?? "scrcpy 已退出";
    }
}

internal static class CoreSelfTest
{
    public static int Run()
    {
        try
        {
            var devices = AdbDeviceParser.Parse(
                "List of devices attached\r\n" +
                "R58M1234 device product:x model:Galaxy_Tab device:x transport_id:1\r\n" +
                "192.168.1.8:42871 device product:x model:Pixel_8 device:x transport_id:2\r\n" +
                "ABC123 unauthorized usb:1-2 transport_id:3\r\n");

            Expect(devices.Count == 3, "device count");
            Expect(devices[0].Model == "Galaxy Tab" && devices[0].DisplayName.Contains("USB"), "USB parse");
            Expect(devices[1].IsWireless && devices[1].DisplayName.Contains("Wi-Fi"), "Wi-Fi parse");
            Expect(!devices[2].IsReady && devices[2].DisplayName.Contains("未授权"), "state parse");
            Expect(!AdbService.ShouldRestartForUsb(devices, true), "existing USB skips rescan");
            Expect(!AdbService.ShouldRestartForUsb([devices[1]], false), "ready Wi-Fi keeps server");
            Expect(AdbService.ShouldRestartForUsb([new("192.168.1.8:37123", "offline", string.Empty)], false), "offline Wi-Fi triggers recovery");
            Expect(AdbService.ShouldRestartForUsb([devices[1]], true), "manual USB rescan");
            Expect(AdbService.ShouldRestartForUsb([], false), "empty list triggers recovery");
            Expect(InputValidation.IsEndpoint("192.168.1.8:37123"), "IPv4 endpoint");
            Expect(InputValidation.IsEndpoint("[fe80::1]:37123"), "IPv6 endpoint");
            Expect(!InputValidation.IsEndpoint("192.168.1.8"), "missing port");
            Expect(!InputValidation.IsEndpoint("host:70000"), "invalid port");
            Expect(InputValidation.IsPairingCode("123456"), "pairing code");
            Expect(!InputValidation.IsPairingCode("12345x"), "invalid pairing code");

            var wifi = WifiStatusParser.Parse(
                "WifiInfo: RSSI: -42, Link speed: 144Mbps, Tx Link speed: 72Mbps, Frequency: 2437MHz\n" +
                "successfulTxPacketsPerSecond: 80\nretriedTxPacketsPerSecond: 10\nlostTxPacketsPerSecond: 10\n");
            var parsedWifi = wifi ?? throw new InvalidOperationException("Self-test failed: Wi-Fi status");
            Expect(parsedWifi is { Band: "2.4 GHz", FrequencyMhz: 2437, RssiDbm: -42, TxLinkSpeedMbps: 72 }, "Wi-Fi status");
            Expect(Math.Abs(parsedWifi.RetryPercent!.Value - 10) < 0.001, "Wi-Fi retry rate");
            Expect(Math.Abs(parsedWifi.LossPercent!.Value - 10) < 0.001, "Wi-Fi loss rate");
            Expect(WifiStatusParser.Parse("Wifi status unavailable") is null, "unreadable Wi-Fi status");

            var encoders =
                "--video-codec=h264 --video-encoder=c2.vendor.avc.encoder (hw) [vendor]\n" +
                "--video-codec=h265 --video-encoder=c2.android.hevc.encoder (sw)\n" +
                "--video-codec=h265 --video-encoder='c2.vendor.hevc.encoder' (hw) [vendor]\n";
            Expect(ScrcpyEncoderParser.FindHardwareH265(encoders) == "c2.vendor.hevc.encoder", "hardware H.265 encoder");
            Expect(ScrcpyEncoderParser.FindHardwareH265("--video-codec=h265 --video-encoder=c2.android.hevc.encoder (sw)") is null, "software-only H.265");

            var usbArguments = ScrcpyProfile.BuildArguments("ABC123", ScrcpyVideoProfile.UsbDefault);
            Expect(usbArguments.SequenceEqual(new[]
            {
                "--serial=ABC123",
                "--video-codec=h264",
                "--max-fps=60",
                "--max-size=1920",
                "--no-audio",
                "--keep-active",
                "--window-title=ConnectPad"
            }), "unchanged USB arguments");

            var h264Arguments = ScrcpyProfile.BuildArguments("192.168.1.8:37123", ScrcpyVideoProfile.WirelessH264);
            Expect(h264Arguments.Contains("--video-bit-rate=8M"), "wireless H.264 bitrate");
            Expect(h264Arguments.Contains("--no-downsize-on-error"), "wireless H.264 resolution lock");

            var h265Arguments = ScrcpyProfile.BuildArguments(
                "192.168.1.8:37123",
                ScrcpyVideoProfile.WirelessH265("c2.vendor.hevc.encoder"));
            Expect(h265Arguments.Contains("--video-codec=h265"), "H.265 codec");
            Expect(h265Arguments.Contains("--video-encoder=c2.vendor.hevc.encoder"), "H.265 encoder");
            Expect(h265Arguments.Contains("--video-bit-rate=4M"), "H.265 bitrate");
            Expect(h265Arguments.Contains("--max-fps=60") && h265Arguments.Contains("--max-size=1920"), "wireless frame limits");
            Expect(h265Arguments.Contains("--no-audio") && h265Arguments.Contains("--no-downsize-on-error"), "wireless quality arguments");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void Expect(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {name}");
        }
    }
}
