# Pidgeon (ARIAPlugin) Changelog

## [0.6.0] — 2026-05-05T00:00:00Z
- Shared relay port (8100) replaces per-node port range
- Grid ownership locking — cross-node inhabitation conflicts prevented
- PB NodeId stamping — blocks locked to owning node
- ActivationName collision rejection at registration (HTTP 409)
- Faction crew roster — full member list with online/offline status
- Version handshake on node registration
- Jump drive: terminal action + dynamic reflection fallback
- REINHABIT command format fixed
- Speed/gravity fix — any functional cockpit used for physics data
- Scan interval halved — 60s (was 120s)
