using System.Diagnostics;
using System.IO;
using System.Text;

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

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Model) ? Serial : Model;
            var connection = Serial.Contains(':') ? "Wi-Fi" : "USB";
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

    public async Task<(CommandResult Result, IReadOnlyList<AdbDevice> Devices)> ListDevicesAsync()
    {
        var result = await ProcessRunner.RunAsync(paths.Adb, ["devices", "-l"]);
        return (result, result.Success ? AdbDeviceParser.Parse(result.Output) : []);
    }

    public Task<CommandResult> PairAsync(string endpoint, string pairingCode) =>
        ProcessRunner.RunAsync(paths.Adb, ["pair", endpoint], pairingCode + Environment.NewLine, 30);

    public Task<CommandResult> ConnectAsync(string endpoint) =>
        ProcessRunner.RunAsync(paths.Adb, ["connect", endpoint], timeoutSeconds: 30);
}

internal static class ScrcpyProfile
{
    public static IReadOnlyList<string> BuildArguments(string serial) =>
    [
        $"--serial={serial}",
        "--video-codec=h264",
        "--max-fps=60",
        "--max-size=1920",
        "--no-audio",
        "--keep-active",
        "--window-title=ConnectPad"
    ];
}

internal sealed class ScrcpyService(ToolPaths paths)
{
    public bool IsAvailable => File.Exists(paths.Scrcpy);

    public async Task<(bool Started, string Error)> StartAsync(
        string serial,
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

        foreach (var argument in ScrcpyProfile.BuildArguments(serial))
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

        if (await Task.WhenAny(exitTask, Task.Delay(1200)) == exitTask)
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
            Expect(devices[1].DisplayName.Contains("Wi-Fi"), "Wi-Fi parse");
            Expect(!devices[2].IsReady && devices[2].DisplayName.Contains("未授权"), "state parse");
            Expect(InputValidation.IsEndpoint("192.168.1.8:37123"), "IPv4 endpoint");
            Expect(InputValidation.IsEndpoint("[fe80::1]:37123"), "IPv6 endpoint");
            Expect(!InputValidation.IsEndpoint("192.168.1.8"), "missing port");
            Expect(!InputValidation.IsEndpoint("host:70000"), "invalid port");
            Expect(InputValidation.IsPairingCode("123456"), "pairing code");
            Expect(!InputValidation.IsPairingCode("12345x"), "invalid pairing code");

            var arguments = ScrcpyProfile.BuildArguments("ABC123");
            Expect(arguments.Contains("--serial=ABC123"), "serial argument");
            Expect(arguments.Contains("--video-codec=h264"), "codec argument");
            Expect(arguments.Contains("--max-fps=60"), "fps argument");
            Expect(arguments.Contains("--max-size=1920"), "size argument");
            Expect(arguments.Contains("--no-audio"), "audio argument");
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
