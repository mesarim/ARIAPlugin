# Pidgeon
### ARIA Fleet Intelligence — Ship AI Bridge Interface for Space Engineers

Pidgeon is the Torch server plugin component of **Project ARIA** — a ship AI platform for Space Engineers. It provides the in-game interface that allows external AI nodes to inhabit ships, execute commands, read game state, and interact with players through faction chat.

Pidgeon is a **dumb pipe**. It has no AI, no personality, no decision-making. It reads the game, sends data to an AI node, and executes whatever commands come back. The intelligence lives in [ARIANode](https://mesarim.uk/aria) — a separate Windows application you install on any machine with network access to your server.

---

## What Pidgeon Does

- Registers and manages external AI bridge connections via HTTP relay
- Reads ship state — blocks, power, speed, gravity, crew vitals
- Executes Gate1 commands — autopilot, dampeners, doors, lights, turrets, jump drive
- Writes to LCD panels — scan results, crew status, tagged objects, instructions
- Auto-installs PB sensor scripts on inhabited ships
- Manages grid ownership — prevents cross-node block name collisions
- Filters chat by faction — each AI only hears its own crew
- Scans surroundings — grids and asteroids within configurable range

## What Pidgeon Does NOT Do

- No AI, no LLM, no language processing
- No personality, no responses, no conversation
- No data storage — all intelligence lives in the AI node
- No player data sent off-server — all communication is local network or outbound relay

---

## Requirements

| Component | Version |
|-----------|---------|
| Space Engineers | 1.203+ |
| Torch | 0.8+ |
| .NET Framework | 4.8 |
| ARIANode | 0.6.0+ (separate install) |

---

## Installation

1. Download `ARIAPlugin.dll` from [Releases](https://github.com/mesarim/pidgeon/releases/latest)
2. Place in your Torch `Plugins/` folder alongside `ARIAPlugin_Config.xml`
3. Restart Torch
4. Verify in Torch console: `=== ARIA System 0.6.0 | Plugin r1 ===`

**Required files** (place in same folder as the DLL):
```
Plugins/
├── ARIAPlugin.dll
├── ARIAPlugin_Config.xml     ← configuration
├── ARIA_PB_Script.txt        ← auto-installed on ship inhabitation
├── ARIA_Emotion_PB.txt       ← optional: emotion display PB
├── ARIA_Instructions.txt     ← optional: LCD setup guide
└── AriaLogo.txt              ← optional: LCD logo sprite
```

---

## Network Configuration

Pidgeon listens on two ports. Forward both to your Torch server:

| Port | Purpose |
|------|---------|
| `8099` | Node registration and queries |
| `8100` | Shared relay — all AI nodes connect here |

AI nodes connect **outbound** to your server. No inbound ports needed on the node machine. Remote nodes (e.g. players in other countries) work without any port forwarding on their end.

---

## Configuration

Edit `ARIAPlugin_Config.xml` in your Plugins folder:

```xml
<?xml version="1.0" encoding="utf-8"?>
<ARIAConfig>
  <AutoRegister>true</AutoRegister>
  <RegistrationPort>8099</RegistrationPort>
  <SharedRelayPort>8100</SharedRelayPort>

  <!-- Static local bridges (optional — ARIANode registers automatically) -->
  <!--
  <Bridges>
    <Bridge>
      <n>aria</n>
      <Url>http://192.168.1.131:8004</Url>
      <ActivationName>Aria</ActivationName>
      <ListenMode>faction</ListenMode>
      <ListenChannel>faction</ListenChannel>
      <AllowedFactions>ARI</AllowedFactions>
      <AdapterContext></AdapterContext>
    </Bridge>
  </Bridges>
  -->
</ARIAConfig>
```

With `AutoRegister>true`, AI nodes register themselves automatically when they connect. Manual bridge entries are only needed for static local setups.

---

## In-Game Setup

Each AI needs these blocks on the ship it will inhabit. Replace `{AI}` with your AI's name (e.g. `ARIA`, `NOVA`):

**Required:**
| Block | Name | Purpose |
|-------|------|---------|
| Remote Control | `{AI} CORE` | Autopilot, physics data |
| Programmable Block | `{AI} PB` | Sensor relay (script auto-installs) |

**Optional:**
| Block | Name | Purpose |
|-------|------|---------|
| LCD Panel | `{AI} SCAN` | Nearby contacts |
| LCD Panel | `{AI} CREW` | Crew vitals roster |
| LCD Panel | `{AI} TAGGED` | Bookmarked objects |
| LCD Panel | `{AI} PRESENCE` | Online/offline indicator |
| Programmable Block | `{AI} EMOTION PB` | Emotion display (eyes) |
| LCD Panel | `{AI} Instructions` | Setup guide |

To activate: say `"Aria, inhabit"` in faction chat. The AI will scan for her Core block, claim the ship, and install her sensor script automatically.

---

## Security Model

- Each AI node has a unique `NodeId` (UUID) generated on first run
- Duplicate AI names (`ActivationName`) are rejected at registration — first registered wins
- Each inhabited grid is exclusively owned by one node — prevents cross-node block conflicts
- PB blocks are stamped with the owning `NodeId` in CustomData — scanners from other nodes skip them
- Ownership released on uninhabit — ship becomes available to other nodes
- All relay connections are outbound from node to server — no attack surface on node machines

---

## Command Reference

Players address the AI by name in faction chat. Full command reference is written to the `{AI} Instructions` LCD on inhabited ships, or see [ARIA_Instructions.txt](https://github.com/mesarim/pidgeon/blob/main/ARIA_Instructions.txt).

Quick reference:
```
Aria, inhabit               — claim ship
Aria, fly to [name]         — autopilot to named object
Aria, fly to X,Y,Z          — autopilot to GPS coordinates
Aria, scan                  — 15km scan
Aria, long range scan       — 25km scan
Aria, ship status           — full system report
Aria, dampeners on/off
Aria, jump to [name]        — jump drive targeted jump
```

---

## Version Compatibility

| ARIA System | Plugin | Torch | SE |
|-------------|--------|-------|----|
| 0.6.0 | r1 | 0.8+ | 1.203+ |

---

## ARIANode

Pidgeon alone does nothing. You need **ARIANode** — the AI platform that runs on any Windows machine and provides the language model, personality, voice, and decision-making.

Download ARIANode: [mesarim.uk/aria](https://mesarim.uk/aria)

ARIANode installs:
- Python runtime (bundled, no separate install)
- Ollama + your chosen language model
- Bridget (the AI bridge process)
- Voice input/output (Whisper STT + Kokoro TTS)
- Twitch chat integration (optional)

---

## Troubleshooting

**Plugin not loading**
- Check Torch console for `.dll` load errors
- Verify .NET Framework 4.8 is installed
- Check `ARIAPlugin_Config.xml` is valid XML

**AI node not connecting**
- Verify ports `8099` and `8100` are forwarded to your Torch server
- Check Torch console for `ARIA: Node registered` message
- Verify ARIANode is running and shows bridge as online

**Ship not being inhabited**
- Verify `{AI} CORE` (Remote Control) exists and is functional
- Verify `{AI} PB` (Programmable Block) exists
- Try `"Aria, reinhabit"` to force a rescan
- Check that another node doesn't already own the ship

**Wrong AI responding**
- Each AI is faction-filtered — players must be in the correct faction
- Check `AllowedFactions` in config matches your faction tag exactly

---

## Changelog

### 0.6.0 — May 2026
- Unified system version across all ARIA components
- Single shared relay port (8100) replaces per-node port range
- Grid ownership locking — cross-node inhabitation conflicts prevented
- PB NodeId stamping — PB blocks locked to owning node
- ActivationName collision rejection at registration
- Faction crew roster — full member list with online/offline status
- Degraded mode — graceful behaviour when bridge connection lost
- Version handshake on node registration

---

## Licence

Pidgeon (ARIAPlugin) is released under the MIT Licence.
The ARIA platform (ARIANode, Bridget, ARIAServer) remains closed source.

---

## Links

- [ARIANode Download](https://mesarim.uk/aria)
- [Project ARIA](https://mesarim.uk/aria)
- [Issue Tracker](https://github.com/mesarim/pidgeon/issues)
- [Torch Plugin Repository](https://torchapi.com/plugins)
