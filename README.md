# Mandela VPN Guard

Mandela VPN Guard is a Windows application for connecting to and monitoring the Mandela VPN. The installer includes the application and its .NET runtime; no separate .NET installation is required.

## Requirements

- Windows 10 or later on an x64-compatible computer
- Administrator approval during installation and when running the application
- A valid Mandela VPN account and access to the VPN service

## Installation

1. Run `MandelaVpnGuard-Setup-x64.exe`.
2. Approve the Windows administrator prompt.
3. Follow the setup instructions. A desktop shortcut is optional; a Start Menu shortcut is created by default.
4. The application opens after setup. It will use saved VPN credentials when available. If credentials are missing or no longer work, enter your username and password in the app.

The installer is not digitally signed. Windows may show a publisher or reputation warning before allowing it to run.

## Using the application

- When connected, select **Disconnect** to end the VPN connection.
- Minimize the window to keep the application available on the taskbar. Selecting the window's **Close** button also minimizes it.
- Select **Exit & Close Port** to exit the application and restore the network settings it changed.
- Enable **Start with Windows** in the application to launch it automatically when you sign in.
- If a saved login cannot reconnect, the app displays the credential fields so you can enter an updated password.

## Network settings

While running, the guard changes the Windows DNS policy and disables IPv6 bindings on physical network adapters. It saves the previous settings and restores them when the application exits normally or is uninstalled. Keep the application installed if you want its recovery data available after an unexpected shutdown.

## Uninstallation

Open **Settings > Apps > Installed apps**, select **Mandela VPN**, and choose **Uninstall**. Administrator approval may be required.

## Support

For assistance, contact [phemelosecwalo@sekozal.app](mailto:phemelosecwalo@sekozal.app).
