# Network Adapter Manager

A lightweight Windows console application for viewing, switching, and controlling network adapters from a simple interactive menu.

Network Adapter Manager is designed for systems with multiple network interfaces — such as Ethernet, Wi-Fi, VPN, virtual, or other Windows network adapters — where you want to quickly choose which adapter should be active without navigating through Windows network settings.

## Features

- **View available network adapters**
  - Adapter name and description
  - IPv4 address
  - Enabled/disabled state
  - Internet connectivity status
  - Currently active adapter
- **Switch network adapters**
  - Select an adapter from an interactive menu
  - Enables the selected adapter
  - Disables the other detected network adapters
- **Enable or disable Internet access**
  - Enable all network adapters
  - Disable all network adapters
- **Internet connectivity detection**
  - Tests connectivity through each enabled adapter
- **Interactive console UI**
  - Keyboard navigation with `↑` / `↓`
  - `Enter` to select
  - `Esc` to go back or exit
- **Administrator detection**
  - Warns when the application is not running with administrator privileges

## Requirements

- Windows 10 or later
- .NET 10 SDK or Runtime
- Administrator privileges are recommended for enabling/disabling network adapters

> **Windows only:** The application uses Windows WMI and Windows security APIs to manage network adapters and therefore does not support Linux or macOS.

## Installation

### Run from source

Clone the repository:

```bash
git clone https://github.com/VictorZeZ/NetworkAdapterManager.git
cd NetworkAdapterManager
```

Build the application:

```bash
dotnet build
```

Run it:

```bash
dotnet run
```

For reliable adapter control, run the application from an **Administrator** terminal.

### Publish a Windows executable

To create a standalone Windows executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

The published application will be available under:

```text
bin/Release/net10.0/win-x64/publish/
```

## Usage

After launching the application, the main menu provides two operations:

```text
ADAPTER MANAGER

> Switch Network Adapter
  Enable / Disable Internet
  Exit
```

### Switch Network Adapter

Select **Switch Network Adapter** to scan the network interfaces available to Windows.

Adapters are displayed with useful information such as their name, description, IPv4 address, and Internet availability. The currently active adapter is marked as **Active**.

Selecting an adapter makes it the sole enabled network adapter by enabling the selected adapter and disabling the others.

### Enable / Disable Internet

The Internet control menu allows you to quickly change the state of all network adapters.

- If Internet access is available, the application can disable **all** network adapters.
- If no adapter currently has Internet access, the application can enable **all** network adapters.

Disabling all adapters requires confirmation before the operation is performed.

> **Warning:** Disabling all network adapters will immediately disconnect the system from networks that depend on those adapters.

## How Internet Detection Works

Network Adapter Manager uses Windows/.NET networking APIs to discover and control adapters and determine connectivity:

- **WMI (`Win32_NetworkAdapter`)** to discover adapters and enable/disable them.
- **`System.Net.NetworkInformation`** to retrieve network interface and IPv4 information.
- A short TCP connectivity probe to `1.1.1.1:443` is used to determine whether an enabled adapter can reach the Internet.

The application also determines the local IPv4 address currently selected by the operating system's routing table to identify the active adapter.

## Project Structure

```text
NetworkAdapterManager/
├── Models/
│   └── Network adapter data models
├── Services/
│   └── Adapter management and connectivity logic
├── UI/
│   └── Interactive console menus and flows
├── Program.cs
├── NetworkAdapterManager.csproj
└── NetworkAdapterManager.slnx
```

## Technology

- **C#**
- **.NET 10**
- **Windows Management Instrumentation (WMI)** via `System.Management`
- **System.Net.NetworkInformation**
- **Console-based UI**

## Permissions

Changing the enabled state of network adapters generally requires administrator privileges on Windows.

If the application is started without administrator privileges, it will display a warning and allow you to continue, but adapter operations may fail.

For the best experience, launch the application using **Run as administrator** or from an elevated terminal.

## Safety Notes

Network adapter changes are system-level operations. In particular:

- Switching adapters can disconnect existing network connections.
- Disabling all adapters removes network connectivity until adapters are enabled again.
- VPNs and virtual adapters may also be affected because the application manages the network adapters exposed by Windows.
- If you are connected remotely, disabling the adapter used by the remote session can terminate your connection.

## License

No license has currently been specified for this repository.

If you intend to distribute the project publicly, consider adding an appropriate open-source license.

## Contributing

Contributions, improvements, and bug reports are welcome. Before submitting changes, make sure the application continues to build successfully on a supported Windows environment and that network adapter operations are tested carefully.

## Author

Created by [VictorZeZ](https://github.com/VictorZeZ).
