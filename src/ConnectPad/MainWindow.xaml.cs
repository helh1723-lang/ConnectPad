using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConnectPad;

public partial class MainWindow : Window
{
    private static readonly Brush NormalStatusBrush = new SolidColorBrush(Color.FromRgb(95, 99, 104));
    private static readonly Brush ErrorStatusBrush = new SolidColorBrush(Color.FromRgb(176, 0, 32));
    private readonly ToolPaths _paths = ToolPaths.Current;
    private readonly AdbService _adb;
    private readonly ScrcpyService _scrcpy;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _adb = new AdbService(_paths);
        _scrcpy = new ScrcpyService(_paths);
        Loaded += async (_, _) => await RefreshDevicesAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshDevicesAsync(forceUsbRescan: true);

    private async Task RefreshDevicesAsync(bool announce = true, bool forceUsbRescan = false)
    {
        if (!_adb.IsAvailable)
        {
            SetStatus("缺少 ADB 文件", true);
            SetBusy(false);
            return;
        }

        SetBusy(true);
        if (announce)
        {
            SetStatus("正在检测设备…");
        }

        var (result, devices) = await _adb.ListDevicesAsync(forceUsbRescan);
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(device => device.IsReady) ?? devices.FirstOrDefault();

        if (!result.Success)
        {
            SetStatus($"ADB 失败：{ShortError(result)}", true);
        }
        else if (announce)
        {
            SetStatus(devices.Count switch
            {
                0 => "未检测到设备",
                _ when devices.Any(device => device.IsReady) => $"已检测到 {devices.Count} 台设备",
                _ when devices.Any(device => device.State.Equals("unauthorized", StringComparison.OrdinalIgnoreCase)) => "请在设备上允许 USB 调试",
                _ => "设备不可用"
            }, devices.Count > 0 && devices.All(device => !device.IsReady));
        }

        SetBusy(false);
    }

    private async void PairButton_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = PairEndpointBox.Text.Trim();
        var pairingCode = PairCodeBox.Password;
        if (!InputValidation.IsEndpoint(endpoint))
        {
            SetStatus("配对地址格式错误", true);
            return;
        }

        if (!InputValidation.IsPairingCode(pairingCode))
        {
            SetStatus("请输入六位配对码", true);
            return;
        }

        SetBusy(true);
        SetStatus("正在配对…");
        var result = await _adb.PairAsync(endpoint, pairingCode);
        PairCodeBox.Clear();
        if (result.Success)
        {
            await RefreshDevicesAsync(announce: false);
            SetStatus("配对成功");
        }
        else
        {
            SetStatus($"配对失败：{ShortError(result)}", true);
        }

        SetBusy(false);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = ConnectEndpointBox.Text.Trim();
        if (!InputValidation.IsEndpoint(endpoint))
        {
            SetStatus("连接地址格式错误", true);
            return;
        }

        SetBusy(true);
        SetStatus("正在连接…");
        var result = await _adb.ConnectAsync(endpoint);
        if (result.Success && !result.Output.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshDevicesAsync(announce: false);
            DeviceBox.SelectedItem = DeviceBox.Items.Cast<AdbDevice>()
                .FirstOrDefault(device => device.Serial.Equals(endpoint, StringComparison.OrdinalIgnoreCase))
                ?? DeviceBox.SelectedItem;
            SetStatus("连接成功");
        }
        else
        {
            SetStatus($"连接失败：{ShortError(result)}", true);
        }

        SetBusy(false);
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStartButton();

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not AdbDevice { IsReady: true } device)
        {
            SetStatus("请选择可用设备", true);
            return;
        }

        if (!_scrcpy.IsAvailable)
        {
            SetStatus("缺少 scrcpy 文件", true);
            return;
        }

        SetBusy(true);
        var profile = ScrcpyVideoProfile.UsbDefault;
        WifiLinkInfo? wifi = null;
        string? fallbackReason = null;

        if (device.IsWireless)
        {
            SetStatus("正在检测无线链路…");
            var wifiResult = await _adb.GetWifiStatusAsync(device.Serial);
            if (wifiResult.Success)
            {
                wifi = WifiStatusParser.Parse(wifiResult.Output);
            }

            SetStatus("正在检测视频编码器…");
            var encoderProbe = await _scrcpy.ProbeHardwareH265Async(device.Serial);
            if (encoderProbe.Success && encoderProbe.Encoder is not null)
            {
                profile = ScrcpyVideoProfile.WirelessH265(encoderProbe.Encoder);
            }
            else
            {
                profile = ScrcpyVideoProfile.WirelessH264;
                fallbackReason = encoderProbe.Success
                    ? "未检测到硬件 H.265，已回退"
                    : "编码器检测失败，已回退";
            }
        }

        SetStatus($"正在启动 {profile.StatusLabel}…");
        Action<CommandResult> onExit = streamResult =>
            Dispatcher.Invoke(() => SetStatus(streamResult.Success
                ? "投屏已结束"
                : $"投屏异常：{ShortText(ScrcpyService.LastLine(streamResult))}", !streamResult.Success));

        var result = await _scrcpy.StartAsync(device.Serial, profile, onExit);
        if (!result.Started && profile.IsH265)
        {
            profile = ScrcpyVideoProfile.WirelessH264;
            fallbackReason = "H.265 启动失败，已回退";
            SetStatus("H.265 启动失败，正在回退 H.264…");
            result = await _scrcpy.StartAsync(device.Serial, profile, onExit);
        }

        SetBusy(false);
        SetStatus(result.Started
            ? StreamStatus(profile, wifi?.Band, fallbackReason)
            : $"启动失败：{ShortText(result.Error)}", !result.Started);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        PairButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy;
        DeviceBox.IsEnabled = !busy;
        UpdateStartButton();
    }

    private void UpdateStartButton() =>
        StartButton.IsEnabled = !_busy && DeviceBox.SelectedItem is AdbDevice { IsReady: true };

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError ? ErrorStatusBrush : NormalStatusBrush;
    }

    private static string ShortError(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => !value.StartsWith('*')) ?? "未知错误";

        if (line.Contains("cannot connect", StringComparison.OrdinalIgnoreCase))
        {
            return "请检查地址和同一 Wi-Fi";
        }

        if (line.Contains("authenticate", StringComparison.OrdinalIgnoreCase))
        {
            return "请检查配对码";
        }

        return line.Length <= 100 ? line : line[..100];
    }

    private static string ShortText(string text) => text.Length <= 100 ? text : text[..100];

    private static string StreamStatus(ScrcpyVideoProfile profile, string? band, string? fallbackReason)
    {
        var details = new List<string> { "投屏已启动", profile.StatusLabel };
        if (!string.IsNullOrWhiteSpace(band))
        {
            details.Add(band);
        }

        if (!string.IsNullOrWhiteSpace(fallbackReason))
        {
            details.Add(fallbackReason);
        }

        return string.Join(" · ", details);
    }
}
