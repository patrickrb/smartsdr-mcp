# SmartSDR MCP

A **Model Context Protocol (MCP) server** that lets AI assistants such as Claude control FlexRadio software-defined radios and operate CW (Morse code) QSOs semi-autonomously.

---

## Overview

SmartSDR MCP bridges your FlexRadio hardware with any MCP-compatible AI assistant. The server exposes a set of tools and live resources that allow the AI to:

- **Discover and connect** to FlexRadios on the local network
- **Control the radio** — frequency, demodulation mode, and more
- **Decode Morse code** in real-time using a custom DSP pipeline over DAX audio
- **Transcribe SSB voice** using Whisper speech-to-text
- **Track QSO state** — detect CQ calls, callsigns, signal reports, and exchanges
- **Generate contextually appropriate CW replies** with AI assistance
- **Transmit safely** with a proposal-approval loop: the AI suggests, you approve, then it transmits
- **Contest automation** (experimental) — monitor SSB contest frequencies, identify stations, and auto-call

The design keeps humans in the loop: no RF is ever emitted without explicit approval.

---

## Features

### Radio Control
- Discover all FlexRadios on the local network (or target a specific serial number or IP)
- Connect and disconnect from the radio programmatically
- Read current frequency, mode, TX state, and CW pitch
- Set VFO frequency (e.g., `14.035` MHz for 20 m CW)
- Set demodulation mode: `CW`, `USB`, `LSB`, `AM`, `FM`
- List and configure all receiver slices (frequency, mode, filter, antenna, audio, etc.)
- Adjust receiver passband filters, RIT/XIT offsets, and AGC settings
- Control noise reduction (NR, NB, ANF, WNB) with individual levels
- Read real-time meters: S-meter, SWR, forward/reflected power, PA temperature, voltage, ALC
- Set RF transmit power and tune power levels
- Manage Tracking Notch Filters (TNF) to null out interference
- Control antenna tuner (ATU): status, enable/disable, auto-tune
- Save, load, and delete memory channels
- View and manage DX spots
- Read GPS/GNSS data: grid square, coordinates, altitude, satellites

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

### SSB Voice Transcription
- Real-time speech-to-text using Whisper (small.en or base.en models)
- Ham radio contest-optimized prompt for better callsign/phonetic recognition
- Hallucination filtering to suppress common Whisper artifacts
- Live transcription buffer and segment history

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

### Contest Agent (Experimental)
- Monitors SSB contest frequencies via Whisper transcription + Claude Haiku analysis
- Identifies running stations calling CQ and determines optimal call timing
- DX cluster integration (HamQTH) for station identification when transcription is ambiguous
- Auto-call mode with TTS voice TX through DAX TX
- State machine tracks QSO progression: Monitoring → CallingStation → ExchangingReports → Completing

> **Note:** Voice TX audio quality is experimental. DAX TX buffer pacing can cause distortion under some conditions.

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
| **Whisper model** (for SSB) | `ggml-small.en.bin` or `ggml-base.en.bin` in `models/` directory |
| **Anthropic API key** (for contest agent) | Required for Claude Haiku situation analysis |

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

Open `src/SmartSdrMcp/Program.cs` and update the constants near the top:

```csharp
const string MyCallsign = "K1AF";    // ← your amateur radio callsign
const string MyName     = "PATRICK"; // ← your name (used in CW exchanges)
const string MyQth      = "Kansas";  // ← your location (used in contest exchanges)
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
| `connect_radio` | `serial?`, `ip?` | Discover and connect to a FlexRadio on the network |
| `list_radios` | — | Discover radios currently visible on the network |
| `disconnect_radio` | — | Disconnect from the currently connected radio |
| `get_radio_state` | — | Get current frequency, mode, TX state, and CW pitch |
| `radio_health` | — | Get overall radio/listener health diagnostics |
| `set_frequency` | `frequencyMHz` | Set the active slice frequency in MHz (e.g., `14.035`) |
| `step_frequency` | `hz` | Step active slice frequency by N Hz (positive or negative) |
| `set_active_slice` | `index` | Set the active slice by index |
| `set_mode` | `mode` | Set the demodulation mode: `CW`, `USB`, `LSB`, `AM`, or `FM` |
| `set_cw_profile` | `wpm?`, `pitch?`, `breakIn?`, `iambic?` | Set CW profile values in one operation |
| `get_meters` | — | Get real-time meter readings (S-meter, SWR, power, ALC, voltage, PA temp, mic, compression) |
| `get_filter` | — | Get current receiver passband filter bounds (low/high Hz) |
| `set_filter` | `low`, `high` | Set receiver passband filter bounds in Hz |
| `set_rit` | `enabled?`, `offsetHz?` | Set RIT (Receiver Incremental Tuning) |
| `set_xit` | `enabled?`, `offsetHz?` | Set XIT (Transmitter Incremental Tuning) |
| `list_slices` | — | List all receiver slices with full configuration |
| `get_noise_reduction` | — | Get noise reduction settings (NR, NB, ANF, WNB) |
| `set_noise_reduction` | `nrOn?`, `nrLevel?`, `nbOn?`, `nbLevel?`, `anfOn?`, `anfLevel?`, `wnbOn?`, `wnbLevel?` | Set noise reduction on the active slice |
| `get_gps` | — | Get GPS data: lat, lon, grid square, altitude, satellites |
| `list_tnfs` | — | List all Tracking Notch Filters |
| `add_tnf` | `frequencyMHz` | Add a Tracking Notch Filter at a frequency |
| `remove_tnf` | `tnfId` | Remove a Tracking Notch Filter by ID |
| `get_rf_power` | — | Get current RF power, tune power, and max power level |
| `set_rf_power` | `rfPower?`, `tunePower?` | Set RF transmit power (0-100) and/or tune power |
| `get_atu_status` | — | Get antenna tuner status (present, enabled, tuning, bypass) |
| `atu_tune` | — | Initiate ATU auto-tune cycle |
| `list_memories` | — | List all saved memory channels |
| `load_memory` | `index` | Load (recall) a saved memory channel |
| `delete_memory` | `index` | Delete a saved memory channel |
| `list_spots` | — | List all DX spots with callsign, frequency, mode, spotter |
| `remove_spot` | `callsign` | Remove a DX spot by callsign |
| `get_agc` | — | Get AGC settings (mode, threshold, off-level) |
| `set_agc` | `mode?`, `threshold?`, `offLevel?` | Set AGC mode (off/slow/med/fast) and threshold |

### CW Listener Tools

| Tool | Parameters | Description |
|---|---|---|
| `cw_listener_start` | `daxChannel` (default `1`), `fixedWpm` (default `0` = auto) | Start Morse decoding on the specified DAX channel |
| `cw_listener_stop` | — | Stop CW decoding and release the DAX audio stream |
| `cw_get_live_text` | — | Get the real-time decode buffer with diagnostics |
| `cw_decode_diagnostics` | — | Get low-level decoder diagnostics for troubleshooting |
| `cw_get_recent_messages` | `count` (default `10`) | Get the last N decoded messages with timestamps |
| `cw_get_qso_state` | — | Get the current QSO state machine snapshot |
| `cw_reset_qso` | — | Reset QSO tracking back to Idle |
| `record_audio_start` | `format`, `seconds?` | Start recording DAX audio (wav or raw) |
| `record_audio_stop` | — | Stop recording and return file details |
| `qso_export` | `format` | Export QSO data (json or adif) |

### CW Transmit Tools

| Tool | Parameters | Description |
|---|---|---|
| `cw_generate_reply` | — | Generate an AI-suggested CW reply based on QSO state |
| `cw_get_pending_replies` | — | List all proposals currently awaiting approval |
| `clear_pending_replies` | — | Clear all queued reply proposals |
| `set_tx_guard` | `armed?`, `maxSeconds?`, `requireProposal?` | Configure TX guard safety settings |
| `get_tx_guard` | — | Get current TX guard settings |
| `cw_send_text` | `proposalId?`, `customText?`, `wpm` (default `20`) | Approve and transmit a pending proposal, or send custom text |
| `cw_abort` | — | Emergency stop — immediately halt CW transmission |
| `run_macro` | `name` | Run predefined macro workflows (find_cq, answer_cq, close_qso_safely) |

### SSB Listener Tools

| Tool | Parameters | Description |
|---|---|---|
| `ssb_listener_start` | `daxChannel` (default `1`) | Start SSB speech-to-text listener using Whisper |
| `ssb_listener_stop` | — | Stop SSB speech-to-text listener |
| `ssb_get_live_text` | — | Get live SSB transcription |
| `ssb_get_recent_messages` | `count` (default `10`) | Get the last N transcribed SSB speech segments |

### Contest Agent Tools (Experimental)

| Tool | Parameters | Description |
|---|---|---|
| `contest_set_api_key` | `apiKey` | Set the Anthropic API key for Claude Haiku analysis |
| `contest_agent_start` | `callsign`, `name?`, `qth?`, `autoMode?` | Start the contest agent |
| `contest_agent_stop` | — | Stop the contest agent |
| `contest_agent_status` | — | Get current agent status (stage, prompt, running station, QSO count) |
| `contest_agent_ack` | — | Acknowledge current prompt (operator has spoken) |
| `contest_agent_skip` | — | Skip current opportunity, return to monitoring |
| `contest_agent_log` | — | Get list of completed contest QSOs |
| `contest_voice_test` | `text` (default `"Kilo One Alpha Foxtrot"`) | Test voice TX with TTS or a 1kHz tone (`text="tone"`) — RF power set to 0W for safety |

---

## Typical Workflow

```
1.  connect_radio()              – discover and connect to the radio
2.  set_frequency(14.035)        – tune to 20 m CW
3.  set_mode("CW")               – set demodulation mode
4.  cw_listener_start()          – begin decoding Morse
         ┌─ cw_get_live_text()   – monitor real-time decode
         └─ cw_get_recent_messages() – review decoded history
5.  cw_get_qso_state()           – check what the AI knows about the QSO
6.  cw_generate_reply()          – AI proposes a reply (no RF yet)
7.  cw_get_pending_replies()     – review the proposal
8.  cw_send_text(proposalId=...) – approve → transmit
         or
    cw_abort()                   – cancel at any time
9.  cw_listener_stop()           – done for the session
10. disconnect_radio()
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
│  SsbListenerTools    flex://cw/proposals          │
│  ContestTools                                     │
│                                                   │
│  Core Services                                    │
│  RadioManager   ←→  FlexLib (FlexRadio API)       │
│  AudioPipeline  ←→  DAX (24 kHz PCM over network) │
│  CwPipeline         BPF → Goertzel → Envelope     │
│  SsbPipeline        Whisper speech-to-text        │
│  ContestAgent       Haiku analysis + state machine│
│  QsoTracker         State machine (QSO lifecycle) │
│  ReplyGenerator     Contextual CW reply logic     │
│  TransmitController Proposal queue + TX safety    │
│  VoiceTransmitter   TTS → DAX TX streaming        │
│  ClusterClient      DX cluster spot lookups       │
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
| Speech-to-text | Whisper.net (whisper.cpp bindings) |
| AI analysis | Anthropic Claude Haiku (contest agent) |
| Data streaming | VITA 49 protocol (UDP) for real-time audio and waterfall |
| Audio | 24 kHz mono PCM de-interleaved from DAX stereo stream |
| DI / hosting | `Microsoft.Extensions.Hosting` v9.0.0 |

---

## License

See [LICENSE](LICENSE) for details.
