# SmartSDR MCP

A **Model Context Protocol (MCP) server** that lets AI assistants such as Claude control FlexRadio software-defined radios and operate CW (Morse code) QSOs semi-autonomously.

---

## Overview

SmartSDR MCP bridges your FlexRadio hardware with any MCP-compatible AI assistant. The server exposes a set of tools and live resources that allow the AI to:

- **Discover and connect** to FlexRadios on the local network
- **Control the radio** — frequency, demodulation mode, and more
- **Decode Morse code** in real-time using a custom DSP pipeline over DAX audio
- **Track QSO state** — detect CQ calls, callsigns, signal reports, and exchanges
- **Generate contextually appropriate CW replies** with AI assistance
- **Transmit safely** with a proposal-approval loop: the AI suggests, you approve, then it transmits

The design keeps humans in the loop: no RF is ever emitted without explicit approval.

---

## Features

### Radio Control
- Discover all FlexRadios on the local network (or target a specific serial number or IP)
- Connect and disconnect from the radio programmatically
- Read current frequency, mode, TX state, and CW pitch
- Set VFO frequency (e.g., `14.035` MHz for 20 m CW)
- Set demodulation mode: `CW`, `USB`, `LSB`, `AM`, `FM`

### CW (Morse Code) Decoding
- Captures audio via the FlexRadio DAX (Digital Audio eXchange) interface
- Custom DSP pipeline:
  - Band-pass filter tuned to the radio's configured CW pitch
  - AGC (automatic gain control)
  - Envelope detector and key-event extractor
  - WPM estimator (auto-detect or fixed)
  - Ham-aware rescoring to validate callsigns and common CW prosigns
- Real-time decode buffer with diagnostic telemetry (WPM, tone magnitude, noise floor)
- Decoded message history with timestamps

### QSO Tracking
- State machine that understands the CW QSO lifecycle:
  - Idle → Calling CQ / Answering CQ → Exchange → Closing → Idle
- Detects CQ calls, called/calling callsigns, signal reports (RST), and exchange fields
- Tracks your callsign against decoded traffic

### AI-Assisted Replies
- `CwGenerateReply` produces a contextual CW response based on current QSO state
- Returns a **proposal** (ID, suggested text, reason, estimated WPM, estimated duration)
- Proposals sit in a pending queue until explicitly approved

### Safe Transmission
- `CwSendText` approves and sends a pending proposal **or** transmits custom freeform text
- Emergency abort (`CwAbort`) stops transmission immediately
- QSO tracker is notified of every sent message to keep state consistent

### Live MCP Resources
| Resource URI | Description |
|---|---|
| `flex://radio/state` | Current radio state as JSON |
| `flex://cw/live` | Real-time CW decode buffer with diagnostics |
| `flex://cw/recent` | Last 20 decoded messages |
| `flex://cw/proposals` | Pending AI reply proposals awaiting approval |

---

## Prerequisites

| Requirement | Details |
|---|---|
| **.NET 9 SDK** | [Download](https://dotnet.microsoft.com/download/dotnet/9.0) |
| **FlexRadio hardware** | Flex 6000/8000 series on the same network subnet |
| **SmartSDR** (optional) | Running for GUI access; required for DAX audio to be available |
| **MCP client** | Claude Desktop, or any MCP-compatible host |

---

## Installation

```bash
# 1. Clone the repository
git clone https://github.com/patrickrb/smartsdr-mcp.git
cd smartsdr-mcp

# 2. Restore NuGet packages
dotnet restore

# 3. Build the solution
dotnet build --configuration Release
```

---

## Configuration

Open `src/SmartSdrMcp/Program.cs` and update the two constants near the top:

```csharp
const string MyCallsign = "K1AF";    // ← your amateur radio callsign
const string MyName     = "PATRICK"; // ← your name (used in CW exchanges)
```

These values are used by the QSO tracker (to recognize messages addressed to you) and by the AI reply generator (to sign outgoing CW).

No other configuration files are required. All runtime parameters (DAX channel, WPM lock, frequency) are passed as tool arguments.

---

## Running the Server

The MCP server communicates over **stdio**, so it is launched by your MCP client automatically.

**For manual testing:**
```bash
dotnet run --project src/SmartSdrMcp/SmartSdrMcp.csproj
```

### Claude Desktop integration

Add the following entry to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "smartsdr": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/smartsdr-mcp/src/SmartSdrMcp/SmartSdrMcp.csproj"
      ]
    }
  }
}
```

Or, if you prefer to run the compiled binary:

```json
{
  "mcpServers": {
    "smartsdr": {
      "command": "/absolute/path/to/smartsdr-mcp/src/SmartSdrMcp/bin/Release/net9.0/SmartSdrMcp"
    }
  }
}
```

---

## MCP Tools Reference

### Radio Tools

| Tool | Parameters | Description |
|---|---|---|
| `ConnectRadio` | `serial?`, `ip?` | Discover and connect to a FlexRadio on the network. Optionally target a specific serial number or IP address. |
| `DisconnectRadio` | — | Disconnect from the currently connected radio. |
| `GetRadioState` | — | Return current frequency, mode, TX state, and CW pitch as JSON. |
| `SetFrequency` | `frequencyMHz` | Set the active slice frequency in MHz (e.g., `14.035`). |
| `SetMode` | `mode` | Set the demodulation mode: `CW`, `USB`, `LSB`, `AM`, or `FM`. |

### CW Listener Tools

| Tool | Parameters | Description |
|---|---|---|
| `CwListenerStart` | `daxChannel` (default `1`), `fixedWpm` (default `0` = auto) | Start Morse decoding on the specified DAX channel. Configures the band-pass filter to the radio's CW pitch automatically. |
| `CwListenerStop` | — | Stop CW decoding and release the DAX audio stream. |
| `CwGetLiveText` | — | Get the real-time decode buffer with diagnostic telemetry (WPM, tone magnitude, noise floor, key-event log). |
| `CwGetRecentMessages` | `count` (default `10`) | Return the last N decoded messages with timestamps, callsigns, and CQ detection flags. |
| `CwGetQsoState` | — | Return the current QSO state machine snapshot as JSON. |
| `CwResetQso` | — | Reset QSO tracking and the decode buffer back to Idle. |

### CW Transmit Tools

| Tool | Parameters | Description |
|---|---|---|
| `CwGenerateReply` | — | Generate an AI-suggested CW reply based on the current QSO state. Returns a proposal ID, suggested text, reason, WPM, and estimated duration. |
| `CwGetPendingReplies` | — | List all proposals currently awaiting approval. |
| `CwSendText` | `proposalId?`, `customText?`, `wpm` (default `20`) | Approve and transmit a pending proposal by ID, **or** send freeform custom text. **This transmits on the air.** |
| `CwAbort` | — | Emergency stop — immediately halt any CW transmission in progress. |

---

## Typical Workflow

```
1.  ConnectRadio()               – discover and connect to the radio
2.  SetFrequency(14.035)         – tune to 20 m CW
3.  SetMode("CW")                – set demodulation mode
4.  CwListenerStart()            – begin decoding Morse
         ┌─ CwGetLiveText()      – monitor real-time decode
         └─ CwGetRecentMessages()– review decoded history
5.  CwGetQsoState()              – check what the AI knows about the QSO
6.  CwGenerateReply()            – AI proposes a reply (no RF yet)
7.  CwGetPendingReplies()        – review the proposal
8.  CwSendText(proposalId=...)   – approve → transmit
         or
    CwAbort()                    – cancel at any time
9.  CwListenerStop()             – done for the session
10. DisconnectRadio()
```

---

## Architecture

```
┌───────────────────────────────────────────────────┐
│               MCP Client (e.g. Claude)            │
└──────────────────────┬────────────────────────────┘
                       │ stdio (MCP protocol)
┌──────────────────────▼────────────────────────────┐
│                  SmartSDR MCP Server               │
│                                                   │
│  Tools ──────────────────────── Resources         │
│  RadioTools          flex://radio/state           │
│  CwListenerTools     flex://cw/live               │
│  CwTransmitTools     flex://cw/recent             │
│                      flex://cw/proposals          │
│                                                   │
│  Core Services                                    │
│  RadioManager   ←→  FlexLib (FlexRadio API)       │
│  AudioPipeline  ←→  DAX (24 kHz PCM over network) │
│  CwPipeline         BPF → AGC → Envelope → Morse  │
│  MessageSegmenter   Groups chars into messages    │
│  QsoTracker         State machine (QSO lifecycle) │
│  ReplyGenerator     Contextual CW reply logic     │
│  TransmitController Proposal queue + TX safety    │
└──────────────────────┬────────────────────────────┘
                       │ FlexLib / VITA 49
┌──────────────────────▼────────────────────────────┐
│          FlexRadio Hardware (Flex 6000/8000)       │
└───────────────────────────────────────────────────┘
```

### Key Design Principles

- **Proposal-approval loop** — the AI suggests CW text; a human must explicitly approve before any RF is emitted.
- **Event-driven pipeline** — audio samples flow through DSP stages and fire events consumed by higher-level services.
- **Single-responsibility services** — each layer (audio, DSP, segmentation, QSO state, AI, TX) is independently testable and replaceable.
- **Stdio transport** — the server has no open ports; it is driven entirely by the MCP client over standard I/O.

---

## Tech Stack

| Component | Details |
|---|---|
| Language | C# 12 with nullable reference types |
| Framework | .NET 9.0 |
| MCP library | `ModelContextProtocol` v1.0.0 |
| Radio API | FlexLib (ported to .NET 9, included in `lib/`) |
| Data streaming | VITA 49 protocol (UDP) for real-time audio and waterfall |
| Audio | 24 kHz mono PCM de-interleaved from DAX stereo stream |
| DI / hosting | `Microsoft.Extensions.Hosting` v9.0.0 |

---

## License

See [LICENSE](LICENSE) for details.
