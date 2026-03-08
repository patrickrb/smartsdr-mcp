# SmartSDR MCP Server

A Model Context Protocol (MCP) server for FlexRadio SmartSDR. Connects to a FLEX-6000 series radio and exposes CW decoding, SSB transcription, contest automation, and radio control as MCP tools for use with AI assistants.

## Requirements

- .NET 9.0
- FlexRadio FLEX-6000 series (tested on FLEX-6400)
- SmartSDR running with a GUI client connected
- DAX audio enabled (channel 1)
- Whisper model for SSB transcription (`models/ggml-small.en.bin` or `ggml-base.en.bin`)

## Build

```bash
dotnet build src/SmartSdrMcp/SmartSdrMcp.csproj
```

## MCP Client Configuration

The server runs via stdio transport. Example config for Claude Desktop or similar MCP clients:

```json
{
  "mcpServers": {
    "smartsdr": {
      "command": "dotnet",
      "args": ["run", "--project", "src/SmartSdrMcp/SmartSdrMcp.csproj"]
    }
  }
}
```

## Tools

### Radio Control

| Tool | Description |
|------|-------------|
| `connect_radio` | Discover and connect to a FlexRadio on the network |
| `list_radios` | Discover radios currently visible on the network |
| `disconnect_radio` | Disconnect from the currently connected radio |
| `get_radio_state` | Get current state including frequency, mode, and TX state |
| `radio_health` | Get overall radio/listener health diagnostics |
| `set_frequency` | Set the active slice frequency in MHz |
| `step_frequency` | Step active slice frequency by N Hz |
| `set_active_slice` | Set the active slice by index |
| `set_mode` | Set the demodulation mode (CW, USB, LSB, AM, FM) |
| `set_cw_profile` | Set CW profile values (wpm, pitch, breakIn, iambic) |

### CW Listener & Decoder

| Tool | Description |
|------|-------------|
| `cw_listener_start` | Start continuous CW listening and Morse decoding on the current slice |
| `cw_listener_stop` | Stop CW listening and audio capture |
| `cw_get_live_text` | Get the live CW decode buffer |
| `cw_decode_diagnostics` | Get low-level decoder diagnostics for troubleshooting |
| `cw_get_recent_messages` | Get the last N decoded CW messages with timestamps |
| `cw_get_qso_state` | Get current QSO tracking state |
| `cw_reset_qso` | Reset QSO tracking state to Idle |
| `record_audio_start` | Start recording DAX audio (wav or raw) |
| `record_audio_stop` | Stop recording and return file details |
| `qso_export` | Export QSO data (json or adif) |

### CW Transmit

| Tool | Description |
|------|-------------|
| `cw_generate_reply` | Generate an AI-suggested CW reply based on QSO state |
| `cw_get_pending_replies` | Get all pending reply proposals awaiting approval |
| `clear_pending_replies` | Clear all queued reply proposals |
| `set_tx_guard` | Configure TX guard (arm/disarm, max seconds, require proposal) |
| `get_tx_guard` | Get current TX guard settings |
| `cw_send_text` | Send CW text via the radio (approve proposal or send custom text) |
| `cw_abort` | Emergency abort: immediately stop CW transmission |
| `run_macro` | Run predefined macro workflows (find_cq, answer_cq, close_qso_safely) |

### SSB Listener

| Tool | Description |
|------|-------------|
| `ssb_listener_start` | Start SSB speech-to-text listener using Whisper |
| `ssb_listener_stop` | Stop SSB speech-to-text listener |
| `ssb_get_live_text` | Get live SSB transcription |
| `ssb_get_recent_messages` | Get the last N transcribed SSB speech segments |

### Contest Agent (Experimental)

The contest agent monitors SSB contest frequencies, identifies running stations using Whisper transcription + Claude Haiku analysis, and can auto-call when openings are detected. Voice TX uses Windows SAPI text-to-speech streamed through DAX TX.

> **Note:** Voice TX audio quality is experimental. DAX TX buffer pacing can cause distortion under some conditions. The source audio is confirmed clean via debug WAV output; the issue is in radio buffer management timing.

| Tool | Description |
|------|-------------|
| `contest_set_api_key` | Set the Anthropic API key for Claude Haiku analysis |
| `contest_agent_start` | Start the contest agent (callsign, name, qth, autoMode) |
| `contest_agent_stop` | Stop the contest agent |
| `contest_agent_status` | Get current agent status (stage, prompt, running station, QSO count) |
| `contest_agent_ack` | Acknowledge current prompt (operator has spoken) |
| `contest_agent_skip` | Skip current opportunity, return to monitoring |
| `contest_agent_log` | Get list of completed contest QSOs |
| `contest_voice_test` | Test voice TX with TTS or a 1kHz tone (RF power set to 0W for safety) |

## Architecture

```
SmartSdrMcp (MCP Server / DI host)
├── SmartSdrMcp.Radio    - FlexLib connection, GUI client binding
├── SmartSdrMcp.Audio    - DAX RX audio stream, de-interleave
├── SmartSdrMcp.Cw       - CW decode pipeline (Goertzel → Envelope → Morse)
├── SmartSdrMcp.Ssb      - SSB transcription (Whisper)
├── SmartSdrMcp.Contest  - Contest agent, voice TX, DX cluster
├── SmartSdrMcp.Mcp      - MCP tools and resources
└── lib/FlexLib_Core     - FlexRadio library
```

## License

Private. Not for redistribution.
