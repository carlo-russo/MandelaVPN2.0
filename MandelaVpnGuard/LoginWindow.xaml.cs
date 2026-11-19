using System.Media;
using System.Windows;
using System.Windows.Input;
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
    private const int DialTimeoutSeconds = 12;

    public bool UserRequestedExit { get; private set; }

    public LoginWindow(string vpnName, bool isTrip, string tripStamp, bool autoDialOnOpen)
    {
        InitializeComponent();

        _vpnName = vpnName;
        _autoDialOnOpen = autoDialOnOpen;

        TxtUser.Text = NativeRas.GetSavedUsername(_vpnName);

        _suppressStartupEvent = true;
        ChkStartup.IsChecked = StartupManager.IsEnabled();
        _suppressStartupEvent = false;

        if (isTrip)
        {
            OuterBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            BadgeBorder.Background = new SolidColorBrush(Color.FromRgb(63, 21, 27));
            BadgeText.Foreground = new SolidColorBrush(Color.FromRgb(252, 165, 165));
            BadgeText.Text = "VPN TRIPPED • DNS LEAK GUARD ACTIVE";
            SubText.Text = $"Disconnected at {tripStamp}. Press Enter to reconnect with saved credentials or type a new password.";
            SystemSounds.Hand.Play();
        }

        _dialTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _dialTimer.Tick += DialTimer_Tick;

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) =>
        {
            Activate();
            TxtPass.Focus();
            if (_autoDialOnOpen)
                StartDial();
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

        BtnConnect.IsEnabled = false;
        BtnConnect.Content = "Connecting...";
        TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        TxtStatus.Text = "Initializing native RAS tunnel...";
        TxtStatus.Visibility = Visibility.Visible;

        _dialStart = DateTime.UtcNow;
        NativeRas.StartDialAsync(_vpnName, TxtUser.Text.Trim(), TxtPass.Password);
        _dialTimer.Start();
    }

    private void DialTimer_Tick(object? sender, EventArgs e)
    {
        if (NativeRas.DialSucceeded || NativeRas.IsVpnConnected(_vpnName))
        {
            _dialTimer.Stop();
            DialogResult = true;
            Close();
            return;
        }

        if (!NativeRas.IsDialing)
        {
            _dialTimer.Stop();
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            TxtStatus.Text = string.IsNullOrWhiteSpace(NativeRas.ErrorMessage)
                ? "Connection closed before completing."
                : NativeRas.ErrorMessage;
            BtnConnect.IsEnabled = true;
            BtnConnect.Content = "Reconnect Tunnel";
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
            TxtStatus.Text = $"Timed out after {DialTimeoutSeconds}s (at: {NativeRas.StateText}). Port closed; ready to retry.";
            BtnConnect.IsEnabled = true;
            BtnConnect.Content = "Reconnect Tunnel";
            TxtPass.Focus();
            return;
        }

        TxtStatus.Text = $"{NativeRas.StateText} ({elapsed:F1}s)";
    }

    private void BtnConnect_Click(object sender, RoutedEventArgs e) => StartDial();

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        UserRequestedExit = true;
        NativeRas.HangUpAndClosePort(_vpnName);
        DialogResult = false;
        Close();
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
