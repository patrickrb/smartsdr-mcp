# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is the FlexLib API v3.9.19.37287 codebase for FlexRadio Systems software-defined radio (SDR) control library. The project provides C# libraries for communicating with FlexRadio hardware and managing radio operations.

## Project Structure

The solution consists of five main components:

- **FlexLib**: Core radio communication library with classes for Radio, Slice, Panadapter, Waterfall, and various hardware interfaces
- **Vita**: VITA 49 protocol implementation for data streaming (FFT, waterfall, audio, meter data)  
- **Util**: Utility classes including network helpers, audio mixers, and data structures
- **UiWpfFramework**: WPF MVVM framework with observable collections, commands, and dialog services
- **ComPortPTT**: Sample COM port PTT (Push-to-Talk) application demonstrating library usage

## Build Commands

This is a .NET solution that can be built with:
- `dotnet build` - Build all projects
- `dotnet build --configuration Release` - Release build
- `msbuild ComPortPTT/ComPortPTT.sln` - Build using MSBuild

Target frameworks: .NET 4.6.2 and .NET 8.0 Windows

## Architecture Notes

### Core Communication Pattern
- **Radio.cs**: Central class managing radio connection, command communication, and hardware state
- **API.cs**: Static entry point for radio discovery and program initialization
- **TcpCommandCommunication.cs/TlsCommandCommunication.cs**: Handle command protocol over TCP/TLS
- **VitaSocket.cs**: Manages VITA 49 UDP data streams

### Key Design Patterns
- MVVM pattern used throughout with ObservableObject base class
- Command pattern via RelayCommand for UI actions  
- Observer pattern for radio status and data updates
- Concurrent collections for thread-safe radio management

### Data Flow
1. Radio discovery via UDP broadcast (Discovery.cs)
2. TCP/TLS command connection establishment  
3. VITA 49 UDP streams for real-time data (audio, FFT, waterfall, meters)
4. Event-driven updates propagated through observable properties

### Hardware Abstraction
- **Slice.cs**: Represents receiver/transmitter channel
- **Panadapter.cs**: Spectrum display data management
- **Waterfall.cs**: Waterfall display data management  
- **Meter.cs**: Real-time meter data handling
- **USB*.cs**: Various USB cable interface implementations

This API provides both low-level hardware control and higher-level abstractions suitable for building radio control applications.