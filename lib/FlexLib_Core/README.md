# FlexLib Core - Cross-Platform FlexRadio API

[![.NET Version](https://img.shields.io/badge/.NET-9.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/9.0)
[![Platform](https://img.shields.io/badge/platform-cross--platform-brightgreen.svg)](https://github.com/brianbruff/FlexLib_Core)

## Overview

FlexLib Core is a **cross-platform port** of FlexRadio Systems' FlexLib API library, ported from **.NET Framework 4.6.2 to .NET 9** with all **Windows dependencies removed** for full cross-platform compatibility.

This library provides comprehensive control of FlexRadio software-defined radio (SDR) hardware, enabling radio discovery, command communication, and real-time data streaming across **Windows, macOS, and Linux** platforms.

## 🚀 Key Features

- **Cross-Platform**: Runs on Windows, macOS, and Linux
- **.NET 9**: Modern .NET runtime with enhanced performance
- **Zero Windows Dependencies**: No WPF, PresentationCore, or Windows-specific libraries
- **Full Radio Control**: Complete FlexRadio hardware management
- **Real-Time Data**: VITA 49 protocol for FFT, waterfall, audio, and meter data
- **Network Communication**: TCP/TLS command protocols and UDP data streams

## 🔄 Port Details

### Original Architecture
- **Framework**: .NET Framework 4.6.2 + .NET 8.0-windows
- **UI Framework**: WPF with extensive Windows dependencies
- **Platform**: Windows-only

### Ported Architecture
- **Framework**: .NET 9.0 (cross-platform)
- **UI Framework**: Framework-agnostic (ready for Blazor, MAUI, etc.)
- **Platform**: Cross-platform (Windows, macOS, Linux)

### Dependencies Removed
- ✅ **WPF Framework** (`UseWpf`, PresentationCore, PresentationFramework, WindowsBase)
- ✅ **UiWpfFramework** (replaced with cross-platform MVVM classes)
- ✅ **System.Windows** dependencies (Media.Color → string, etc.)
- ✅ **Windows-specific targeting** (`EnableWindowsTargeting`)

### Cross-Platform Replacements Added
- ✅ **Cross-platform MVVM**: `ObservableObject`, `PropertySupport`
- ✅ **Type Converters**: `EnumDescriptionTypeConverter`
- ✅ **String-based Colors**: Replaced `System.Windows.Media.Color`

## 🏗️ Project Structure

The solution consists of five main components:

- **FlexLib**: Core radio communication library (.NET 9)
- **Vita**: VITA 49 protocol implementation for data streaming
- **Util**: Utility classes including network helpers and data structures  
- **UiWpfFramework**: Legacy WPF framework (preserved for reference)
- **ComPortPTT**: Sample COM port PTT application

## 🛠️ Building

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Compatible with Windows, macOS, and Linux

### Build Commands
```bash
# Build all projects
dotnet build

# Build specific project
dotnet build FlexLib/FlexLib.csproj

# Release build
dotnet build --configuration Release
```

### Target Framework
- **FlexLib**: `net9.0` (cross-platform)
- **Legacy components**: `net462` (Windows compatibility)

## 🖥️ Platform Compatibility

| Platform | Status | Notes |
|----------|--------|-------|
| **Windows** | ✅ Fully Supported | Original platform compatibility maintained |
| **macOS** | ✅ Fully Supported | Tested and verified on macOS |
| **Linux** | ✅ Expected to work | .NET 9 cross-platform compatibility |

## 🔮 Future UI Options

With Windows dependencies removed, FlexLib Core is ready for modern cross-platform UI frameworks:

- **Blazor Server/WebAssembly**: Web-based radio control interfaces
- **MAUI**: Native cross-platform desktop and mobile apps
- **Avalonia UI**: Cross-platform XAML-based applications
- **Web APIs**: RESTful services with real-time SignalR integration

## 📡 Radio Communication

### Core Communication Pattern
- **Radio.cs**: Central radio management and state
- **API.cs**: Radio discovery and initialization
- **TcpCommandCommunication/TlsCommandCommunication**: Command protocols
- **VitaSocket.cs**: VITA 49 UDP data streams

### Data Flow
1. **Discovery**: UDP broadcast radio discovery
2. **Connection**: TCP/TLS command channel establishment  
3. **Streaming**: VITA 49 UDP real-time data (audio, FFT, waterfall, meters)
4. **Events**: Real-time updates via observable properties

## 🤝 Contributing

This is a community-driven port focused on cross-platform compatibility. Contributions welcome for:

- Additional platform testing and validation
- UI framework integrations (Blazor, MAUI, Avalonia)
- Performance optimizations
- Documentation improvements

## 📄 License

This project maintains compatibility with FlexRadio Systems' original licensing terms. Please refer to the original FlexLib documentation for licensing details.

## 🙏 Acknowledgments

- **FlexRadio Systems**: Original FlexLib API and comprehensive SDR hardware ecosystem
- **Community Contributors**: Cross-platform porting and testing efforts

---

**Ready for the next generation of cross-platform FlexRadio applications! 🚀**