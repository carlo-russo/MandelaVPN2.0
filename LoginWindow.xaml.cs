using System.Media;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace MandelaVpnGuard;

public partial class LoginWindow : Window
{
    private readonly string _vpnName;
    private readonly bool _autoDialOnOpen;
    private readonly DispatcherTimer _dialTimer;
    private DateTime _dialStart;
    private bool _suppressStartupEvent;
    private bool _allowClose;
    private const int DialTimeoutSeconds = 12;

    public event EventHandler? DisconnectRequested;

    public LoginWindow(string vpnName, bool initiallyConnected, bool autoDialOnOpen, bool showCredentials)
    {
        InitializeComponent();

        _vpnName = vpnName;
        _autoDialOnOpen = autoDialOnOpen;

        TxtUser.Text = NativeRas.GetSavedUsername(_vpnName);

        _suppressStartupEvent = true;
        ChkStartup.IsChecked = StartupManager.IsEnabled();
        _suppressStartupEvent = false;

        ShowInTaskbar = true;
        UpdateWatchdogStatus(initiallyConnected);
        CredentialsPanel.Visibility = showCredentials ? Visibility.Visible : Visibility.Collapsed;

        _dialTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _dialTimer.Tick += DialTimer_Tick;

        Loaded += (_, _) =>
        {
            Activate();
            TxtPass.Focus();
            if (_autoDialOnOpen)
                BeginReconnect();
        };
        Closed += (_, _) =>
        {
            _dialTimer.Stop();
            if (NativeRas.IsDialing)
                NativeRas.HangUpAndClosePort(_vpnName);
        };
    }

    private void StartDial()
    {
        if (NativeRas.IsDialing)
            return;

        if (NativeRas.IsVpnConnected(_vpnName))
        {
            UpdateWatchdogStatus(connected: true);
            return;
        }

        BtnConnect.IsEnabled = false;
        BtnConnect.Content = "Connecting...";
        TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        TxtStatus.Text = "Connecting...";
        TxtStatus.Visibility = Visibility.Visible;

        _dialStart = DateTime.UtcNow;
        NativeRas.StartDialAsync(_vpnName, TxtUser.Text.Trim(), TxtPass.Password);
        _dialTimer.Start();
    }

    private void DialTimer_Tick(object? sender, EventArgs e)
    {
        if (TryShowConnected())
            return;

        if (!NativeRas.IsDialing)
        {
            if (TryShowConnected())
                return;

            _dialTimer.Stop();
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            TxtStatus.Text = string.IsNullOrWhiteSpace(NativeRas.ErrorMessage)
                ? "Couldn't connect. Check your password or connection."
                : $"Couldn't connect: {NativeRas.ErrorMessage}";
            BtnConnect.IsEnabled = true;
            BtnConnect.Content = "Connect";
            CredentialsPanel.Visibility = Visibility.Visible;
            TxtPass.Clear();
            TxtPass.Focus();
            return;
        }

        double elapsed = (DateTime.UtcNow - _dialStart).TotalSeconds;
        if (elapsed >= DialTimeoutSeconds)
        {
            _dialTimer.Stop();
            NativeRas.HangUpAndClosePort(_vpnName);
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            TxtStatus.Text = "Connection timed out. Check your password or connection.";
            BtnConnect.IsEnabled = true;
            BtnConnect.Content = "Connect";
            CredentialsPanel.Visibility = Visibility.Visible;
            TxtPass.Focus();
            return;
        }

        TxtStatus.Text = $"Connecting... ({elapsed:F1}s)";
    }

    private bool TryShowConnected()
    {
        if (!NativeRas.DialSucceeded && !NativeRas.IsVpnConnected(_vpnName))
            return false;

        _dialTimer.Stop();
        UpdateWatchdogStatus(connected: true);
        BtnConnect.IsEnabled = true;
        BtnConnect.Content = "Connect";
        TxtPass.Clear();
        CredentialsPanel.Visibility = Visibility.Collapsed;
        return true;
    }

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(BtnConnect.Content?.ToString(), "Disconnect", StringComparison.Ordinal))
            DisconnectRequested?.Invoke(this, EventArgs.Empty);
        else
            StartDial();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }

    public void BeginReconnect()
    {
        CredentialsPanel.Visibility = Visibility.Collapsed;
        TxtPass.Clear();
        StartDial();
    }

    public void SetManuallyDisconnected()
    {
        UpdateWatchdogStatus(connected: false);
        SubText.Text = "Disconnected.";
        TxtStatus.Text = "Disconnected.";
        CredentialsPanel.Visibility = Visibility.Collapsed;
    }

    public void AllowApplicationExit() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            ShowInTaskbar = true;
            WindowState = WindowState.Minimized;
            return;
        }

        base.OnClosing(e);
    }

    public void UpdateWatchdogStatus(bool connected, bool isTrip = false, string tripStamp = "")
    {
        if (connected)
        {
            OuterBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            BadgeBorder.Background = new SolidColorBrush(Color.FromRgb(6, 55, 42));
            BadgeText.Foreground = new SolidColorBrush(Color.FromRgb(110, 231, 183));
            BadgeText.Text = "MANDELA VPN • CONNECTED";
            SubText.Text = "Connected.";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(110, 231, 183));
            TxtStatus.Text = "Connected.";
            TxtStatus.Visibility = Visibility.Visible;
            CredentialsPanel.Visibility = Visibility.Collapsed;
            BtnConnect.IsEnabled = true;
            BtnConnect.Content = "Disconnect";
            return;
        }

        OuterBorder.BorderBrush = new SolidColorBrush(isTrip
            ? Color.FromRgb(239, 68, 68)
            : Color.FromRgb(56, 189, 248));
        BadgeBorder.Background = new SolidColorBrush(isTrip
            ? Color.FromRgb(63, 21, 27)
            : Color.FromRgb(23, 37, 84));
        BadgeText.Foreground = new SolidColorBrush(isTrip
            ? Color.FromRgb(252, 165, 165)
            : Color.FromRgb(147, 197, 253));
        BadgeText.Text = isTrip
            ? "CONNECTION LOST"
            : "MANDELA VPN";

        if (isTrip)
        {
            SubText.Text = $"Connection lost at {tripStamp}. Reconnecting...";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            TxtStatus.Text = "Reconnecting...";
            SystemSounds.Hand.Play();
        }
        else
        {
            SubText.Text = "Connect with your saved login or enter your password.";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            TxtStatus.Text = "Not connected.";
        }
        TxtStatus.Visibility = Visibility.Visible;
        BtnConnect.IsEnabled = true;
        BtnConnect.Content = "Connect";
        if (isTrip)
            CredentialsPanel.Visibility = Visibility.Visible;
    }

    private void ChkStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupEvent)
            return;

        try
        {
            StartupManager.SetEnabled(ChkStartup.IsChecked == true);
        }
        catch (Exception ex)
        {
            _suppressStartupEvent = true;
            ChkStartup.IsChecked = !ChkStartup.IsChecked;
            _suppressStartupEvent = false;
            MessageBox.Show(this, ex.Message, "Startup setting failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
