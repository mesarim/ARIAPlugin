// =============================================================================
// Copyright (c) 2026 Michael John Roll. All Rights Reserved.
//
// This software and its source code are the exclusive property of Michael John Roll.
// Unauthorized copying, distribution, modification, or use of this software,
// in whole or in part, without the express written permission of the owner
// is strictly prohibited.
//
// Project: ARIA (Autonomous Reasoning and Intelligence Architecture)
// Component: ARIAPlugin (Torch server plugin / Pulsar client plugin -- unified)
// Version: v0.6.0-r1
// Author:  Michael John Roll
// Build symbols:
//   TORCH  -- compile as Torch dedicated-server plugin
//   PULSAR -- compile as Pulsar singleplayer plugin
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
// Explicit aliases to resolve CS0104 ambiguous references
using IMyProgrammableBlock = Sandbox.ModAPI.IMyProgrammableBlock;
using IMyTextPanel         = Sandbox.ModAPI.IMyTextPanel;
using IMyShipController    = Sandbox.ModAPI.IMyShipController;
using IMyProjector         = Sandbox.ModAPI.IMyProjector;
using IMyRemoteControl     = Sandbox.ModAPI.IMyRemoteControl;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Managers.ChatManager;
using Torch.Session;
using VRage.Game.ModAPI;
using VRageMath;
using SpaceEngineers.Game.ModAPI;

// =============================================================================
// Pidgeon v0.6.0-r1
// =============================================================================
// THREADING MODEL (critical -- this is why previous versions crashed):
//
//   SIMULATION THREAD  ->  Update() called by Torch once per game tick
//   +-- ScanForCores()         read entity list, find ARIA Core block
//   +-- ScanForPb()            find ARIA PB, read CustomData
//   +-- ReadCharacterStats()   read character health/oxygen/energy
//   +-- ReadSurroundings()     read entity positions (no physics)
//   +-- WriteCrewLcd()         write to ARIA CREW LCD
//
//   HTTP THREAD  ->  System.Timers.Timer, isolated from SE
//   +-- PostAsync()            fire-and-forget HTTP posts (queued data)
//   +-- HealthCheckAsync()     GET /health
//   +-- DeliverAlertsAsync()   GET /pending_alerts
//
// RULE: Nothing in Update() may await or block.
//       Nothing in async methods may touch SE objects.
//       Data flows one way: Update() fills queues, HTTP thread drains them.
// =============================================================================

namespace ARIAPlugin
{
    // =========================================================================
    // Logger
    // =========================================================================
    internal static class AriaLog
    {
        private static readonly Logger _nlog = LogManager.GetCurrentClassLogger();
        private static string _path;
        private static readonly object _lock = new object();

        public static void Init(string logDir)
        {
            Directory.CreateDirectory(logDir);
            _path = Path.Combine(logDir, "ARIA.log");
            File.WriteAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ARIA Log started\r\n");
        }

        public static void Info(string m)
        {
            Write("INFO", m);
            _nlog.Info(m);
        }
        public static void Warn(string m)
        {
            Write("WARN", m);
            _nlog.Warn(m);
        }
        public static void Debug(string m)
        {
            Write("DEBUG", m);
            _nlog.Debug(m);
        }
        public static void Error(string m, Exception ex = null)
        {
            Write("ERROR", ex != null ? $"{m} -- {ex.Message}" : m);
            if (ex != null) _nlog.Error(ex, m); else _nlog.Error(m);
        }

        private static void Write(string level, string msg)
        {
            if (_path == null) return;
            lock (_lock)
            {
                try { File.AppendAllText(_path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}\r\n"); }
                catch { }
            }
        }
    }

    // =========================================================================
    // Outbound message queue
    // Simulation thread enqueues payloads.
    // HTTP timer drains the queue -- no SE access allowed on HTTP side.
    // =========================================================================
    internal class HttpQueue
    {
        private readonly Queue<(string endpoint, string body)> _q
            = new Queue<(string, string)>();
        private readonly object _lock = new object();

        public void Enqueue(string endpoint, string body)
        {
            lock (_lock) { _q.Enqueue((endpoint, body)); }
        }

        public bool TryDequeue(out string endpoint, out string body)
        {
            lock (_lock)
            {
                if (_q.Count == 0) { endpoint = body = null; return false; }
                var item = _q.Dequeue();
                endpoint = item.endpoint;
                body     = item.Item2;
                return true;
            }
        }

        public int Count { get { lock (_lock) return _q.Count; } }
    }

    // =========================================================================
    // Inbound alert queue
    // HTTP timer enqueues alerts received from bridge.
    // Simulation thread (Update) dequeues and broadcasts to chat.
    // =========================================================================
    internal class AlertQueue
    {
        private readonly Queue<string> _q = new Queue<string>();
        private readonly object _lock = new object();

        public void Enqueue(string msg) { lock (_lock) _q.Enqueue(msg); }

        public bool TryDequeue(out string msg)
        {
            lock (_lock)
            {
                if (_q.Count == 0) { msg = null; return false; }
                msg = _q.Dequeue();
                return true;
            }
        }
    }

    // =========================================================================
    // Plugin
    // =========================================================================
    public class ARIAPlugin : TorchPluginBase
    {
        // ---------------------------------------------------------------------
        // BridgeContext -- one instance per <Bridge> entry in config
        // Encapsulates all per-bridge state: URL, activation name, faction filter,
        // connection health, outbound queue, inbound alert queue
        // ---------------------------------------------------------------------
        private class BridgeContext
        {
            public string   Name           { get; set; } = "";  // set from <n> or ActivationName
            public string   Url            { get; set; } = "";
            public string   ActivationName { get; set; } = "aria"; // lowercased
            public string   CoreBlockName  { get; set; } = "ARIA CORE";
            public string   PbBlockName         { get; set; } = "ARIA PB";
            public string   EmotionPbBlockName  { get; set; } = "ARIA EMOTION PB";
            public string   NodeId         { get; set; } = "";  // UUID from ARIA Node, empty for static config
            public string   ListenMode     { get; set; } = "faction";
            public string   ListenChannel  { get; set; } = "faction";
            public string   AdapterContext { get; set; } = "";
            public System.Collections.Generic.HashSet<string> AllowedFactions { get; }
                = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            // Per-bridge inhabited grid state (gridEntityId -> pbEntityId)
            public readonly Dictionary<long, long> PbMap = new Dictionary<long, long>();

            // Grid ownership -- grids this bridge has claimed via inhabit.
            // Block scanning (ARIA PB, ARIA CORE etc.) ONLY happens on owned grids.
            // Released on uninhabit. Prevents cross-node block name collisions.
            public readonly System.Collections.Generic.HashSet<long> OwnedGridIds
                = new System.Collections.Generic.HashSet<long>();

            // Per-bridge HTTP state
            public volatile bool BridgeUp = false;
            public readonly HttpQueue  Outbound = new HttpQueue();
            public readonly AlertQueue Inbound  = new AlertQueue();

            // Actual node bridge URL (e.g. http://192.168.1.x:8000) -- used by relay proxy
            public string   NodeBridgeUrl  { get; set; } = "";

            // Per-bridge scan history (fixes cross-bridge contact bleed)
            public readonly System.Collections.Generic.HashSet<string> PreviousGridNames
                = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            public bool FirstScan = true;

            public bool MatchesActivation(string msgLower)
                => msgLower.Contains(ActivationName);

            public bool AllowedFaction(string factionTag)
                => AllowedFactions.Count == 0
                   || AllowedFactions.Contains(factionTag);
        }

        // All configured bridges -- loaded from ARIAPlugin_Config.xml
        private static readonly System.Collections.Generic.List<BridgeContext> _bridges
            = new System.Collections.Generic.List<BridgeContext>();

        // Legacy compatibility -- primary bridge (first in list)
        private static BridgeContext PrimaryBridge
            => _bridges.Count > 0 ? _bridges[0] : null;
        private static string BRIDGE_URL
            => PrimaryBridge?.Url ?? "";
        private static string _activationName
            => PrimaryBridge?.ActivationName ?? "aria";
        private static string ARIA_CORE
            => PrimaryBridge?.CoreBlockName ?? "ARIA CORE";
        private static string ARIA_PB
            => PrimaryBridge?.PbBlockName ?? "ARIA PB";

        // Global config (not per-bridge)
        private static string _configPath     = "";
        private static string _pbScriptContent      = "";  // loaded from ARIA_PB_Script.txt at startup
        private static string _emotionPbScriptContent = "";  // loaded from ARIA_Emotion_PB.txt at startup
        private static string _instructionsContent   = "";  // loaded from ARIA_Instructions.txt at startup
        // Emotion images removed -- handled by ARIA EMOTION PB sprite renderer
            // Key: "happy"/"sad"/"angry"/"neutral", Value: Base64-encoded image bytes

        // Node auto-registration -- dynamic bridge list built from ARIA Node clients
        // System version -- must match bridge ARIA_SYSTEM_VERSION
        private const string ARIA_SYSTEM_VERSION = "0.6.0";

        private static int  _registrationPort    = 8099;
        private static int  _sharedRelayPort     = 8100;  // single port for ALL relay connections
        private static bool _autoRegister        = true;
        private static System.Net.HttpListener _registrationListener = null;
        private static readonly object _bridgeLock = new object();
        // NodeIds of dynamically registered bridges (cleared on restart)
        private static readonly HashSet<string> _registeredNodeIds
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Single shared relay listener -- all nodes connect to port 8100
        // nodeId is identified from relay_connect POST body
        private static System.Net.Sockets.TcpListener _sharedRelayListener = null;
        private static bool _sharedRelayStarted = false;

        // Queue-based relay -- node polls these instead of plugin proxying to node IP
        private static readonly Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<string>>
            _nodeInbound = new Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<string>>();
        private static readonly Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<string>>
            _nodeOutbound = new Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<string>>();

        // Serialized relay GET system -- one worker per node drains requests in order.
        // Each entry: "reqId|/endpoint"  queued by RelayGetAsync, served to bridge via /relay/get_request
        private static readonly Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<string>>
            _nodeGetRequests = new Dictionary<string, System.Collections.Concurrent.ConcurrentQueue<string>>();

        // GET responses returned by bridge via /relay/get_response -- keyed by reqId
        private static readonly Dictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, string>>
            _nodeGetResponses = new Dictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, string>>();

        // Per-node semaphore -- ensures only ONE RelayGetAsync is in flight at a time per node
        private static readonly Dictionary<string, System.Threading.SemaphoreSlim>
            _nodeGetLock = new Dictionary<string, System.Threading.SemaphoreSlim>();

        private System.Threading.SemaphoreSlim GetNodeLock(string nodeId)
        {
            lock (_nodeGetLock)
            {
                if (!_nodeGetLock.ContainsKey(nodeId))
                    _nodeGetLock[nodeId] = new System.Threading.SemaphoreSlim(1, 1);
                return _nodeGetLock[nodeId];
            }
        }
        private static string _nodesConfigPath = ""; // ARIAPlugin_Nodes.xml

        // Legacy bridge state wrappers (used by existing code paths)
        private const string ARIA_STATUS_LCD   = "ARIA STATUS";
        private const string ARIA_CREW_LCD     = "ARIA CREW";
        private const string ARIA_TAG_LCD      = "ARIA TAGGED";
        private const string ARIA_INSTRUCTIONS = "ARIA Instructions";  // fallback only
        // Per-bridge instructions LCD name -- e.g. "UBER Instructions", "ERIS Instructions"
        private static string GetInstructionsLcdName(BridgeContext ctx)
            => ctx != null
               ? char.ToUpperInvariant(ctx.ActivationName[0]).ToString()
                 + ctx.ActivationName.Substring(1).ToUpperInvariant()
                 + " Instructions"
               : ARIA_INSTRUCTIONS;
        private const string ARIA_SCAN_LCD     = "ARIA SCAN";
        private const string ARIA_SCAN_GRIDS   = "ARIA SCAN GRIDS";
        private const string ARIA_SCAN_ASTS    = "ARIA SCAN ASTEROIDS";
        private const string ARIA_PRESENCE     = "ARIA PRESENCE";
        private const string ARIA_PROJECTOR    = "ARIA Projector";
        private const string ARIA_AI_BLOCK     = "ARIA AI";
        private const string ARIA_JUMP_CTRL    = "ARIA Jump Controller";

        // LCD names per bridge -- built after config loads
        // Each bridge has its own set of LCD names based on its activation name
        // e.g. nova bridge -> "NOVA STATUS", "NOVA CREW" etc.
        private static string GetLcdName(BridgeContext ctx, string suffix)
            => ctx != null
               ? char.ToUpperInvariant(ctx.ActivationName[0]).ToString()
                 + ctx.ActivationName.Substring(1).ToUpperInvariant()
                 + " " + suffix.ToUpperInvariant()
               : "ARIA " + suffix.ToUpperInvariant();

        // How many simulation ticks between each slow scan
        // SE runs at ~60 ticks/sec; 300 ticks ~ 5 seconds
        private const int TICKS_CORE_SCAN    = 1800;  // ~30s core/PB discovery
        private const int TICKS_PB_SCAN      = 1800;  // ~30s PB scan
        private const int TICKS_SCAN_MEDIUM  = 3600;  // ~60s medium range scan
        private const int TICKS_CREW_LCD     =  600;  // ~10s crew LCD (was 5s -- faction lookup is expensive)
        private const int TICKS_TRACK_UPDATE  = 30;   // 0.5s tracking PID
        private const int TICKS_SHIP_STATE   =  300;  // ~5s ship state collection
        private const int TICKS_FACTION      = 3600;  // ~60s faction update
        private const int TICKS_ORE_SCAN     = 1800;  // ~30s ore detector read
        private const double RANGE_MEDIUM     = 15000.0;
        private const double RANGE_LONG       = 25000.0;

        // ---------------------------------------------------------------------
        // HTTP (lives on its own timer thread -- never touches SE)
        // ---------------------------------------------------------------------
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)  // Ollama can take 15-30s to respond
        };
        // Separate fast client for health checks and alerts (short timeout ok)
        private static readonly HttpClient _httpFast = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        private Timer _httpTimer;    // drains outbound queue + polls bridge
        private Timer _healthTimer;  // health check only

        // Thread-safe queues -- now per-bridge in BridgeContext
        // Legacy proxies to primary bridge for existing code
        private HttpQueue  _outbound => PrimaryBridge?.Outbound;
        private AlertQueue _inbound  => PrimaryBridge?.Inbound;

        // Current bridge context for command being executed -- set by DrainPbCommands
        // Used by hardware methods to send chat as the correct AI
        private BridgeContext _currentCommandBridge = null;

        // Send a chat message as the correct AI for the current command context
        private void SendAiChat(string message, Color? color = null)
        {
            var ctx = _currentCommandBridge ?? PrimaryBridge;
            var name = ctx != null
                ? char.ToUpperInvariant(ctx.ActivationName[0]) + ctx.ActivationName.Substring(1)
                : "ARIA";
            var col = color ?? Color.Cyan;
            if (ctx != null)
                SendToFaction(ctx, name, message);
            else
                _chat?.SendMessageAsOther(name, message, col);
        }

        // ---------------------------------------------------------------------
        // Simulation-thread state (only accessed in Update())
        // ---------------------------------------------------------------------
        private TorchSessionManager _sessionMgr;
        private IChatManagerServer  _chat;
        private bool _sessionUp = false;
        private int  _tick      = 0;  // simulation tick counter

        // ARIA grids -- refreshed on sim thread
        private List<MyCubeGrid> _ariaGrids = new List<MyCubeGrid>();

        // _pbMap: aggregated view across all bridges -- for read-only callers that don't need per-bridge context
        // All writes must go through BridgeContext.PbMap directly
        private Dictionary<long, long> _pbMap
        {
            get
            {
                var d = new Dictionary<long, long>();
                foreach (var ctx in _bridges)
                    foreach (var kv in ctx.PbMap)
                        if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
                return d;
            }
        }
        // Find the bridge that owns a given grid entity id
        private BridgeContext BridgeForGridId(long gridId)
        {
            foreach (var ctx in _bridges)
                if (ctx.PbMap.ContainsKey(gridId)) return ctx;
            return null;
        }

        // Per-player last cockpit entity id (seat entry detection)
        private Dictionary<ulong, long> _cockpitMap = new Dictionary<ulong, long>();

        // Projector state per grid
        private Dictionary<long, string> _projState = new Dictionary<long, string>();
        // "unknown" | "awaiting" | "granted" | "denied"

        // ---------------------------------------------------------------------
        // Simulation state flags and cached data
        // ---------------------------------------------------------------------
        // Scan control
        private bool _immediateScanPending    = false;
        private bool _longRangeScanPending    = false;
        private int  _deferredScanLcdFetchTick = 0;
        private bool _firstFetchDone          = false;
        private bool _firstScan               = true;

        // Autopilot safety monitoring
        private const float AUTOPILOT_MIN_ALTITUDE_LARGE = 150f;  // abort threshold for large grids
        private const float AUTOPILOT_MIN_ALTITUDE_SMALL = 25f;   // abort threshold for small grids (drones)
        private const float AUTOPILOT_WARN_ALTITUDE_LARGE = 300f; // warn threshold for large grids
        private const float AUTOPILOT_WARN_ALTITUDE_SMALL = 50f;  // warn threshold for small grids
        private bool _autopilotAltWarnSent = false;
        private int  _autopilotStartTick   = 0;   // tick when autopilot last engaged
        private bool _autopilotSafetyOverride = false; // bypass terrain correction
        private const int AUTOPILOT_ALT_CHECK_DELAY = 300; // ticks before altitude check starts (~5s)
        private HashSet<string> _previousGridNames = new HashSet<string>();
        // Cached LCD content from bridge
        // Tracking system state
        private bool     _trackingActive    = false;
        private string   _trackTargetName   = "";
        private float    _trackStandoffM    = 50f;
        private Vector3D _trackLastPos      = Vector3D.Zero;
        private Vector3D _trackLastVel      = Vector3D.Zero;
        private double   _trackLastPosTime  = 0.0;
        private bool     _trackFirstSample  = true;
        private bool     _trackLostWarned   = false;
        // PID integrators
        private float _pidIntegralFwd  = 0f;
        private float _pidIntegralLat  = 0f;
        private float _pidIntegralVert = 0f;
        // AI follow state
        private bool   _aiFollowActive = false;
        private string _aiFollowMode   = "";
        private string _aiFollowTarget = "";
        private float  _aiFollowDist   = 0f;
        // Chat / command flags
        private bool   _uninhabitPending      = false;
        private bool   _forceUpdatePending    = false;
        private bool   _scanCoresPending      = false;  // manual inhabit trigger
        private bool   _projectorGrantPending = false;
        private bool   _projectorDenyPending  = false;
        private string _projectorGrantBy      = "";
        // PB selection state
        private Dictionary<long, List<string>> _pbChoicePending
            = new Dictionary<long, List<string>>();
        // Bridge re-announce control
        private bool _reannounceOnNextScan = true;

        // Gate 1 -- pending commands from bridge, executed on sim thread
        // Gate 1 pending commands -- thread-safe string pairs "gridName|command"
        private readonly System.Collections.Concurrent.ConcurrentQueue<string>
            _pendingPbCommands = new System.Collections.Concurrent.ConcurrentQueue<string>();

        // Alert cooldowns (sim-thread, tick-based)
        private Dictionary<string, int> _alertTick = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> COOLDOWNS = new Dictionary<string, int>
        {
            { "no_pilot", 900 },  // 15 sec
            { "hostile",  1800 }, // 30 sec
        };

        // Control keywords -- intercepted before reaching Ollama
        private static readonly string[] CONTROL_WORDS = {
            "override", "thruster", "thrust", "gyro", "dampener",
            "autopilot", "waypoint", "set speed", "open door",
            "close door", "seal", "vent", "engage", "disengage",
            "yaw", "pitch", "roll", "rotate", "spin",
            "turn left", "turn right", "nose up", "nose down",
            "pull up", "push down", "bank left", "bank right",
            "stop rotating", "stop spinning", "stop turning",
            "stabilise", "stabilize", "hold attitude",
            "all stop", "full stop", "kill thrust", "kill rotation",
            "thrusters off", "clear override", "override off",
            "gyro stop", "zero rotation",
            "cancel autopilot", "stop autopilot", "abort autopilot",
            "manual control", "manual mode", "i have control",
            "release all", "release overrides", "reset controls",
            "autopilot off",
            "fly to", "go to", "navigate to", "head to",
            "take us to", "plot course", "set course",
            "long range scan", "long-range scan", "deep scan", "extended scan",
            "camera", "cameras", "parachute", "chute", "deploy chute",
            "wheel", "wheels", "suspension", "brake", "drive",
            "scan", "long range", "short range",
            "inhabit", "reinhabit", "uninhabit",
            "force update", "update", "status",
            "tag", "untag",
            "jump", "jump drive",
            "track", "stop tracking",
            "crew", "who is on board"
        };

        // =====================================================================
        // Init / Dispose
        // =====================================================================

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            var dir = Path.Combine(
                Path.GetDirectoryName(typeof(ITorchBase).Assembly.Location)
                ?? Directory.GetCurrentDirectory(), "Logs");
            AriaLog.Init(dir);
            // Load config before anything else -- sets BRIDGE_URL
            LoadConfig();

            AriaLog.Info("=== ARIA System 0.6.0 | Pidgeon r7 initialising ===");
            AriaLog.Info("Threading model: Update() for SE access, Timer for HTTP only");
            AriaLog.Info($"Bridges loaded: {_bridges.Count}");

            _sessionMgr = Torch.Managers.GetManager<TorchSessionManager>();
            if (_sessionMgr != null)
                _sessionMgr.SessionStateChanged += OnSessionState;
            else
                AriaLog.Warn("TorchSessionManager not found.");

            // HTTP drain timer -- 200ms interval, drains outbound queue
            // NEVER touches SE objects
            _httpTimer = new Timer(200);
            _httpTimer.Elapsed  += OnHttpTimer;
            _httpTimer.AutoReset = true;
            _httpTimer.Start();

            // Health check timer -- 30 sec
            _healthTimer = new Timer(30_000);
            _healthTimer.Elapsed  += (s, e) => _ = HealthCheckAsync();
            _healthTimer.AutoReset = true;
            _healthTimer.Start();

            _ = HealthCheckAsync();

            // Start node registration listener if auto-register is enabled
            if (_autoRegister)
            {
                ClearNodesConfig(); // clear stale entries from previous session
                _ = StartRegistrationListenerAsync();
            }

            AriaLog.Info("=== ARIA System 0.6.0 | Pidgeon r7 initialised ===");
        }

        public override void Dispose()
        {
            AriaLog.Info("Pidgeon v0.6.0-r1 shutting down.");
            _httpTimer?.Stop();   _httpTimer?.Dispose();
            _healthTimer?.Stop(); _healthTimer?.Dispose();
            try { _registrationListener?.Stop(); } catch { }
            StopAllNodeRelays();
            if (_sessionMgr != null)
                _sessionMgr.SessionStateChanged -= OnSessionState;
            base.Dispose();
        }

        // =====================================================================
        // Node Registration Listener
        // GET  /aria_ping     -- discovery probe from ARIA Node
        // POST /aria_register -- dynamic bridge registration
        // =====================================================================
        private async System.Threading.Tasks.Task StartRegistrationListenerAsync()
        {
            try
            {
                _registrationListener = new System.Net.HttpListener();
                _registrationListener.Prefixes.Add($"http://+:{_registrationPort}/");
                _registrationListener.Start();
                AriaLog.Info($"ARIA: Registration listener on port {_registrationPort}. " +
                             "ARIA Nodes can now auto-register.");

                while (_registrationListener.IsListening)
                {
                    System.Net.HttpListenerContext ctx = null;
                    try { ctx = await _registrationListener.GetContextAsync(); }
                    catch { break; }
                    _ = HandleRegistrationRequestAsync(ctx);
                }
            }
            catch (Exception ex)
            {
                AriaLog.Warn($"ARIA: Registration listener failed: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task HandleRegistrationRequestAsync(
            System.Net.HttpListenerContext ctx)
        {
            try
            {
                var req  = ctx.Request;
                var resp = ctx.Response;
                resp.ContentType = "application/json";

                // GET /aria_ping -- discovery probe
                if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/aria_ping")
                {
                    var pong = System.Text.Encoding.UTF8.GetBytes(
                        "{\"plugin\":\"Pidgeon\",\"system_version\":\"0.6.0\",\"plugin_version\":\"r7\",\"protocol\":1}");
                    resp.StatusCode = 200;
                    resp.ContentLength64 = pong.Length;
                    await resp.OutputStream.WriteAsync(pong, 0, pong.Length);
                    resp.Close();
                    return;
                }

                // POST /aria_register -- node registration
                if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/aria_register")
                {
                    string body = "";
                    using (var sr = new System.IO.StreamReader(req.InputStream))
                        body = await sr.ReadToEndAsync();

                    var nodeId      = ExtractJsonString(body, "nodeId");
                    var displayName = ExtractJsonString(body, "displayName");
                    var activeName  = ExtractJsonString(body, "activationName");
                    // relay=false means direct HTTP bridge -- skip relay allocation
                    // Note: relay is a JSON boolean (not quoted), so use dedicated regex
                    var relayMatch  = System.Text.RegularExpressions.Regex.Match(
                        body, "\"relay\"\\s*:\\s*(true|false)");
                    var isRelayNode = !relayMatch.Success || relayMatch.Groups[1].Value != "false";
                    // factions is an array ["TAG1","TAG2"] -- extract with array regex
                    var factionsRaw = "";
                    var fArr = System.Text.RegularExpressions.Regex.Match(
                        body, "\"factions\"\\s*:\\s*(\\[[^\\]]*\\])");
                    if (fArr.Success) factionsRaw = fArr.Groups[1].Value;
                    // Node's actual bridge URL (e.g. http://192.168.1.x:8000)
                    // Used by proxy to forward traffic to the node
                    var nodeUrl     = ExtractJsonString(body, "bridgeUrl") ??
                                      $"http://{req.RemoteEndPoint.Address}:8000";

                    if (string.IsNullOrEmpty(nodeId))
                    {
                        resp.StatusCode = 400;
                        var err = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"nodeId required\"}");
                        resp.ContentLength64 = err.Length;
                        await resp.OutputStream.WriteAsync(err, 0, err.Length);
                        resp.Close();
                        return;
                    }

                    // ── Version handshake ─────────────────────────────────────
                    // Check bridge system_version against plugin ARIA_SYSTEM_VERSION.
                    // Major version mismatch = hard reject.
                    // Minor mismatch = warn but allow (backwards compatible).
                    var bridgeSystemVersion = ExtractJsonString(body, "system_version") ?? "";
                    if (!string.IsNullOrEmpty(bridgeSystemVersion))
                    {
                        var pluginParts = ARIA_SYSTEM_VERSION.Split('.');
                        var bridgeParts = bridgeSystemVersion.Split('.');
                        bool majorMismatch = pluginParts.Length > 0 && bridgeParts.Length > 0 &&
                                             pluginParts[0] != bridgeParts[0];
                        bool minorMismatch = !majorMismatch &&
                                             pluginParts.Length > 1 && bridgeParts.Length > 1 &&
                                             pluginParts[1] != bridgeParts[1];

                        if (majorMismatch)
                        {
                            resp.StatusCode = 426; // Upgrade Required
                            var msg = $"{{\"error\":\"ARIA System version mismatch. Plugin={ARIA_SYSTEM_VERSION} Bridge={bridgeSystemVersion}. Major version incompatible -- update your bridge.\"}}";
                            var errBytes = System.Text.Encoding.UTF8.GetBytes(msg);
                            resp.ContentLength64 = errBytes.Length;
                            await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                            resp.Close();
                            AriaLog.Error($"ARIA: Registration REJECTED -- version mismatch. Plugin={ARIA_SYSTEM_VERSION} Bridge={bridgeSystemVersion} Node={nodeId.Substring(0, 8)}");
                            return;
                        }
                        else if (minorMismatch)
                        {
                            AriaLog.Warn($"ARIA: Version mismatch -- Plugin={ARIA_SYSTEM_VERSION} Bridge={bridgeSystemVersion} Node={nodeId.Substring(0, 8)} -- registering anyway (minor version).");
                        }
                        else
                        {
                            AriaLog.Info($"ARIA: Version handshake OK -- System {ARIA_SYSTEM_VERSION} Node={nodeId.Substring(0, 8)}");
                        }
                    }
                    else
                    {
                        AriaLog.Warn($"ARIA: Node {nodeId.Substring(0, 8)} sent no system_version -- old bridge? Registering anyway.");
                    }
                    // Reject registration if ActivationName is already claimed by
                    // a DIFFERENT connected node. Same node re-registering is fine.
                    if (!string.IsNullOrEmpty(activeName))
                    {
                        BridgeContext conflict = null;
                        lock (_bridgeLock)
                            conflict = _bridges.FirstOrDefault(b =>
                                !string.IsNullOrEmpty(b.NodeId) &&
                                b.NodeId != nodeId &&
                                b.BridgeUp &&
                                string.Equals(b.ActivationName,
                                    activeName, StringComparison.OrdinalIgnoreCase));

                        if (conflict != null)
                        {
                            resp.StatusCode = 409; // Conflict
                            var msg = $"{{\"error\":\"ActivationName '{activeName}' is already claimed by node {conflict.NodeId.Substring(0, 8)}. Choose a different AI name.\"}}";
                            var err = System.Text.Encoding.UTF8.GetBytes(msg);
                            resp.ContentLength64 = err.Length;
                            await resp.OutputStream.WriteAsync(err, 0, err.Length);
                            resp.Close();
                            AriaLog.Warn($"ARIA: Registration rejected -- '{activeName}' already claimed by node {conflict.NodeId.Substring(0, 8)}. New node: {nodeId.Substring(0, 8)}");
                            return;
                        }
                    }

                    // All relay nodes use the single shared relay port
                    int relayPort = 0;
                    if (isRelayNode)
                    {
                        relayPort = _sharedRelayPort;
                        // Start shared listener if not already running
                        if (!_sharedRelayStarted)
                            _ = StartSharedRelayAsync();
                    }

                    // Build bridge context -- URL points to relay port on THIS server
                    // Node will connect outbound to this port; plugin talks back through it
                    var relayUrl = isRelayNode ? $"http://localhost:{relayPort}" : (nodeUrl ?? "");
                    var bridge = new BridgeContext
                    {
                        Name           = (displayName ?? "aria").ToLowerInvariant(),
                        Url            = relayUrl,
                        NodeBridgeUrl  = nodeUrl ?? "",
                        ActivationName = activeName ?? displayName ?? "Aria",
                        ListenMode     = "faction",
                        NodeId         = nodeId,
                    };
                    var prefix = bridge.ActivationName.ToUpperInvariant();
                    bridge.CoreBlockName = prefix + " CORE";
                    bridge.PbBlockName        = prefix + " PB";
                    bridge.EmotionPbBlockName = prefix + " EMOTION PB";

                    if (!string.IsNullOrEmpty(factionsRaw))
                    {
                        var fMatches = System.Text.RegularExpressions.Regex.Matches(
                            factionsRaw, "\"([^\"]+)\"");
                        foreach (System.Text.RegularExpressions.Match fm in fMatches)
                            bridge.AllowedFactions.Add(fm.Groups[1].Value.Trim());
                    }

                    lock (_bridgeLock)
                    {
                        _bridges.RemoveAll(b => b.NodeId == nodeId);
                        _registeredNodeIds.Add(nodeId);
                        _bridges.Add(bridge);
                    }

                    // Shared relay already started during registration above
                    // Direct bridges don't need a relay listener


                    // Write to ARIAPlugin_Nodes.xml (nodes only, not direct bridges)
                    if (isRelayNode) SaveNodesConfig();

                    int bridgeIndex = _bridges.IndexOf(bridge);
                    if (isRelayNode)
                        AriaLog.Info($"ARIA: Node registered: '{displayName}' ({nodeId}) relay port={relayPort} factions=[{string.Join(",", bridge.AllowedFactions)}]");
                    else
                        AriaLog.Info($"ARIA: Direct bridge registered: '{displayName}' ({nodeId}) factions=[{string.Join(",", bridge.AllowedFactions)}]");

                    _scanCoresPending = true;

                    var ok = System.Text.Encoding.UTF8.GetBytes(
                        $"{{\"status\":\"registered\",\"relayPort\":{relayPort},\"bridgeIndex\":{bridgeIndex}}}");
                    resp.StatusCode = 200;
                    resp.ContentLength64 = ok.Length;
                    await resp.OutputStream.WriteAsync(ok, 0, ok.Length);
                    resp.Close();
                    return;
                }

                resp.StatusCode = 404;
                resp.Close();
            }
            catch (Exception ex)
            {
                AriaLog.Warn($"ARIA: Registration handler error: {ex.Message}");
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
            }
        }

        private static string ExtractJsonString(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                json, $"\"{System.Text.RegularExpressions.Regex.Escape(key)}\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // =====================================================================
        // StartSharedRelayAsync
        // Single TCP listener on port 8100 handles ALL relay node connections.
        // Each connection identifies its nodeId via the relay_connect POST.
        // Replaces the old per-node port allocation (8110-8129).
        // Router only needs: 8099 (registration) + 8100 (relay) forwarded.
        // =====================================================================
        private async System.Threading.Tasks.Task StartSharedRelayAsync()
        {
            if (_sharedRelayStarted) return;
            _sharedRelayStarted = true;

            _sharedRelayListener = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Any, _sharedRelayPort);
            try
            {
                _sharedRelayListener.Start();
                AriaLog.Info($"ARIA: Shared relay listener started on port {_sharedRelayPort}.");

                while (true)
                {
                    System.Net.Sockets.TcpClient client;
                    try { client = await _sharedRelayListener.AcceptTcpClientAsync(); }
                    catch { break; }

                    // nodeId will be extracted from the relay_connect POST in ProxyTcpRequestAsync
                    // Pass empty nodeId -- ProxyTcpRequestAsync will identify it from the request
                    _ = ProxyTcpRequestAsync(client, null);
                }
            }
            catch (Exception ex)
            {
                AriaLog.Warn($"ARIA: Shared relay listener error: {ex.Message}");
            }
            finally
            {
                try { _sharedRelayListener?.Stop(); } catch { }
                _sharedRelayStarted = false;
                AriaLog.Info("ARIA: Shared relay listener stopped.");
            }
        }

        // TCP relay -- queue-based. Node polls /relay/chat to get queued messages,
        // POSTs responses to /relay/response. No inbound connection to node needed.
        private async System.Threading.Tasks.Task ProxyTcpRequestAsync(
            System.Net.Sockets.TcpClient client, string nodeId)
        {
            const string CRLF = "\r\n";
            try
            {
                using (client)
                {
                    var stream = client.GetStream();

                    // Read headers first (until CRLFCRLF)
                    var headerBuf = new System.Collections.Generic.List<byte>();
                    var tmp = new byte[1];
                    while (true)
                    {
                        int r = await stream.ReadAsync(tmp, 0, 1);
                        if (r == 0) return;
                        headerBuf.Add(tmp[0]);
                        if (headerBuf.Count >= 4)
                        {
                            var tail = headerBuf.Count;
                            if (headerBuf[tail-4] == '\r' && headerBuf[tail-3] == '\n' &&
                                headerBuf[tail-2] == '\r' && headerBuf[tail-1] == '\n')
                                break;
                        }
                        if (headerBuf.Count > 8192) return; // header too large
                    }
                    var headerStr = System.Text.Encoding.UTF8.GetString(headerBuf.ToArray());
                    var firstLine = headerStr.Split('\n')[0].Trim();
                    var parts     = firstLine.Split(' ');
                    var method    = parts.Length > 0 ? parts[0] : "GET";
                    var path      = parts.Length > 1 ? parts[1].Split('?')[0] : "/";

                    // Read body using Content-Length
                    string body = "";
                    var clMatch = System.Text.RegularExpressions.Regex.Match(
                        headerStr, @"Content-Length:\s*(\d+)", 
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (clMatch.Success)
                    {
                        int contentLength = int.Parse(clMatch.Groups[1].Value);
                        if (contentLength > 0 && contentLength <= 65536)
                        {
                            var bodyBuf = new byte[contentLength];
                            int read = 0;
                            while (read < contentLength)
                            {
                                int got = await stream.ReadAsync(bodyBuf, read, contentLength - read);
                                if (got == 0) break;
                                read += got;
                            }
                            body = System.Text.Encoding.UTF8.GetString(bodyBuf, 0, read);
                        }
                    }

                    // On shared relay port, nodeId arrives in the relay_connect POST body.
                    // Extract it before any queue operations.
                    if (string.IsNullOrEmpty(nodeId) && path == "/relay_connect" && method == "POST")
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            body, "\"nodeId\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success)
                        {
                            nodeId = m.Groups[1].Value;
                            AriaLog.Info($"ARIA: Shared relay connection identified: {nodeId.Substring(0, 8)}");
                        }
                        else
                        {
                            AriaLog.Warn("ARIA: relay_connect missing nodeId -- dropping connection.");
                            return;
                        }
                    }
                    else if (string.IsNullOrEmpty(nodeId))
                    {
                        // Non-connect request on shared port with no nodeId -- try X-Node-Id header
                        var hm = System.Text.RegularExpressions.Regex.Match(
                            headerStr, @"X-Node-Id:\s*([^\r\n]+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (hm.Success) nodeId = hm.Groups[1].Value.Trim();
                        else { return; } // can't route without nodeId
                    }

                    // Ensure queues exist for this node
                    lock (_nodeInbound)
                        if (!_nodeInbound.ContainsKey(nodeId))
                            _nodeInbound[nodeId] = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    lock (_nodeOutbound)
                        if (!_nodeOutbound.ContainsKey(nodeId))
                            _nodeOutbound[nodeId] = new System.Collections.Concurrent.ConcurrentQueue<string>();

                    var inQ  = _nodeInbound[nodeId];
                    var outQ = _nodeOutbound[nodeId];

                    string respBody = "{}";
                    int    status   = 200;

                    if (path == "/relay_connect" && method == "POST")
                    {
                        // Node connecting -- acknowledge
                        respBody = "{\"status\":\"connected\"}";
                        AriaLog.Info($"ARIA: Relay node connected: {nodeId.Substring(0,8)}");
                    }
                    else if (path == "/health" && method == "GET")
                    {
                        respBody = "{\"status\":\"ok\",\"relay\":true}";
                    }
                    else if (path == "/relay_ping" && method == "GET")
                    {
                        respBody = "{\"status\":\"ok\"}";
                    }
                    else if (path == "/relay/get_request" && method == "GET")
                    {
                        // Bridge polls this to get the next queued GET request
                        // Returns "GET|/endpoint|reqId" or empty
                        System.Collections.Concurrent.ConcurrentQueue<string> getQ;
                        lock (_nodeGetRequests)
                        {
                            if (!_nodeGetRequests.ContainsKey(nodeId))
                                _nodeGetRequests[nodeId] = new System.Collections.Concurrent.ConcurrentQueue<string>();
                            getQ = _nodeGetRequests[nodeId];
                        }
                        string getItem;
                        respBody = getQ.TryDequeue(out getItem) ? getItem : "{\"queued\":false}";
                    }
                    else if (path == "/relay/get_response" && method == "POST")
                    {
                        // Bridge posts the result of a GET it executed locally
                        // Body: {"id": "reqId", "body": "...response..."}
                        if (!string.IsNullOrEmpty(body))
                        {
                            var reqId    = ExtractJsonString(body, "id") ?? "";
                            var respJson = ExtractJsonString(body, "body") ?? "";
                            // body field may be JSON object not a string -- try raw extraction
                            if (string.IsNullOrEmpty(respJson))
                            {
                                var bIdx = body.IndexOf("\"body\"");
                                if (bIdx >= 0)
                                {
                                    var colon = body.IndexOf(':', bIdx);
                                    if (colon >= 0)
                                        respJson = body.Substring(colon + 1).Trim().TrimEnd('}').Trim();
                                }
                            }
                            if (!string.IsNullOrEmpty(reqId) && !string.IsNullOrEmpty(respJson))
                            {
                                System.Collections.Concurrent.ConcurrentDictionary<string, string> respMap;
                                lock (_nodeGetResponses)
                                {
                                    if (!_nodeGetResponses.ContainsKey(nodeId))
                                        _nodeGetResponses[nodeId] = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                                    respMap = _nodeGetResponses[nodeId];
                                }
                                respMap[reqId] = respJson;
                                AriaLog.Debug($"Relay GET response stored: reqId={reqId} node={nodeId.Substring(0, 8)}");
                            }
                        }
                        respBody = "{\"status\":\"ok\"}";
                    }
                    else if (path == "/relay/chat" && method == "GET")
                    {
                        // Return next queued message -- prioritise chat over telemetry
                        // Chat messages are JSON with player_name; telemetry/endpoints use pipe format
                        string chatMsg = null;
                        var temp = new System.Collections.Generic.List<string>();

                        // Drain queue looking for a chat message first
                        while (inQ.TryDequeue(out var m))
                        {
                            if (chatMsg == null && !m.StartsWith("/") &&
                                m.Contains("player_name") && m.Contains("message"))
                                chatMsg = m;  // found a chat -- keep it
                            else
                                temp.Add(m);  // non-chat -- put back
                        }
                        // Re-enqueue non-chat messages (limit to avoid memory bloat)
                        foreach (var t in temp)
                            if (inQ.Count < 20) inQ.Enqueue(t);

                        if (chatMsg != null)
                            respBody = chatMsg;
                        else if (temp.Count > 0 && inQ.TryDequeue(out var next))
                            respBody = next;  // no chat, return next endpoint forward
                        else
                            respBody = "{\"queued\":false}";
                    }
                    else if (path == "/commands" && method == "GET")
                    {
                        respBody = await RelayGetAsync(nodeId, "/commands",
                            "{\"commands\":[],\"count\":0}");
                    }
                    else if (path == "/pending_alerts" && method == "GET")
                    {
                        respBody = await RelayGetAsync(nodeId, "/pending_alerts",
                            "{\"alerts\":[],\"count\":0}");
                    }
                    else if (path == "/scan_requested" && method == "GET")
                    {
                        respBody = await RelayGetAsync(nodeId, "/scan_requested",
                            "{\"scan_requested\":false}");
                    }
                    else if ((path == "/scan_lcd_content" || path == "/scan_lcd_grids" ||
                              path == "/scan_lcd_asteroids" || path == "/tag_lcd_content" ||
                              path == "/health") && method == "GET")
                    {
                        respBody = await RelayGetAsync(nodeId, path, "{}");
                    }
                    else if (path == "/relay/response" && method == "POST")
                    {
                        // Node posting a chat response back
                        if (!string.IsNullOrEmpty(body))
                        {
                            outQ.Enqueue(body);
                            // Process immediately -- extract reply and send to SE chat
                            var reply = ExtractJsonString(body, "response");
                            if (!string.IsNullOrEmpty(reply))
                            {
                                BridgeContext nodeBridge = null;
                                lock (_bridgeLock)
                                    nodeBridge = _bridges.FirstOrDefault(b => b.NodeId == nodeId);
                                if (nodeBridge != null)
                                {
                                    var displayName = char.ToUpperInvariant(nodeBridge.ActivationName[0])
                                                    + nodeBridge.ActivationName.Substring(1);
                                    AriaLog.Info($"{displayName}: {reply}");
                                    SendToFaction(nodeBridge, displayName, reply);
                                }
                            }
                        }
                        respBody = "{\"status\":\"ok\"}";
                    }
                    else if (method == "POST")
                    {
                        // Forward any unrecognised POST to the node bridge via inbound queue
                        // This covers /aria_inhabit, /surroundings, /ship_state, /queue_alert etc.
                        // Package as: ENDPOINT|BODY so node bridge can unpack it
                        // Package endpoint + body for bridge to unpack
                        var safeBody = string.IsNullOrEmpty(body) ? "{}" : body;
                        var fwdMsg   = Esc(path) + "|" + safeBody;
                        inQ.Enqueue(fwdMsg);
                        respBody = "{\"status\":\"queued\"}";
                        AriaLog.Debug($"ARIA: Relay forwarded POST {path} to node {nodeId.Substring(0,8)}");
                    }
                    else
                    {
                        // Unknown path
                        status   = 404;
                        respBody = "{\"error\":\"not found\",\"path\":\"" + path + "\"}";
                        AriaLog.Debug($"ARIA: Relay unknown path: {method} {path} (node {nodeId.Substring(0,8)})");
                    }

                    var bodyBytes = System.Text.Encoding.UTF8.GetBytes(respBody);
                    var header    = "HTTP/1.1 " + status + " OK" + CRLF
                                  + "Content-Type: application/json" + CRLF
                                  + "Content-Length: " + bodyBytes.Length + CRLF
                                  + "Connection: close" + CRLF + CRLF;
                    var hb = System.Text.Encoding.UTF8.GetBytes(header);
                    await stream.WriteAsync(hb, 0, hb.Length);
                    await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                    await stream.FlushAsync();
                    // Shutdown write side gracefully so client receives full response
                    client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                    // Drain any remaining client data before closing
                    var drain = new byte[256];
                    try { while (await stream.ReadAsync(drain, 0, drain.Length) > 0) { } } catch { }
                }
            }
            catch (Exception ex)
            {
                AriaLog.Debug($"ARIA: TCP relay error: {ex.Message}");
            }
        }


        private static bool IsTcpPortFree(int port)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch { return false; }
        }

        private static void SaveNodesConfig()
        {
            if (string.IsNullOrEmpty(_nodesConfigPath)) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                sb.AppendLine("<!-- ARIAPlugin_Nodes.xml -->");
                sb.AppendLine("<!-- Auto-generated by ARIAPlugin. Do not edit manually. -->");
                sb.AppendLine("<!-- Cleared on server restart. Nodes re-register automatically. -->");
                sb.AppendLine("<ARIANodes>");
                lock (_bridgeLock)
                {
                    foreach (var b in _bridges.Where(b => !string.IsNullOrEmpty(b.NodeId)))
                    {
                        sb.AppendLine($"  <Node>");
                        sb.AppendLine($"    <NodeId>{b.NodeId}</NodeId>");
                        sb.AppendLine($"    <DisplayName>{b.Name}</DisplayName>");
                        sb.AppendLine($"    <ActivationName>{b.ActivationName}</ActivationName>");
                        sb.AppendLine($"    <RelayPort>{_sharedRelayPort}</RelayPort>");
                        sb.AppendLine($"    <Factions>{string.Join(",", b.AllowedFactions)}</Factions>");
                        sb.AppendLine($"    <Connected>{b.BridgeUp.ToString().ToLower()}</Connected>");
                        sb.AppendLine($"    <LastSeen>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</LastSeen>");
                        sb.AppendLine($"  </Node>");
                    }
                }
                sb.AppendLine("</ARIANodes>");
                System.IO.File.WriteAllText(_nodesConfigPath, sb.ToString());
            }
            catch (Exception ex)
            {
                AriaLog.Warn($"ARIA: Could not save nodes config: {ex.Message}");
            }
        }

        private static void ClearNodesConfig()
        {
            // Instead of wiping, mark all nodes as disconnected
            // They will reconnect and update within seconds of bridge starting
            if (string.IsNullOrEmpty(_nodesConfigPath)) return;
            if (!System.IO.File.Exists(_nodesConfigPath)) return;
            try
            {
                var xml = System.IO.File.ReadAllText(_nodesConfigPath);
                // Mark all as disconnected
                xml = System.Text.RegularExpressions.Regex.Replace(
                    xml, "<Connected>true</Connected>", "<Connected>false</Connected>");
                System.IO.File.WriteAllText(_nodesConfigPath, xml);
            }
            catch { }
        }

        private void StopAllNodeRelays()
        {
            try { _sharedRelayListener?.Stop(); } catch { }
            _sharedRelayStarted = false;
            lock (_bridgeLock)
            {
                _registeredNodeIds.Clear();
                _bridges.RemoveAll(b => !string.IsNullOrEmpty(b.NodeId));
            }
            ClearNodesConfig();
            AriaLog.Info("ARIA: Shared relay stopped. All nodes disconnected.");
        }

        private void OnSessionState(ITorchSession session, TorchSessionState state)
        {
            // This callback fires on the session thread -- only touch plugin state
            if (state == TorchSessionState.Loaded)
            {
                AriaLog.Info("ARIA: Session loaded.");
                _sessionUp = true;
                _tick      = 0;
                _ariaGrids.Clear();
                foreach (var ctx in _bridges) { ctx.PbMap.Clear(); lock (ctx.OwnedGridIds) ctx.OwnedGridIds.Clear(); }
                _cockpitMap.Clear();
                _projState.Clear();
                _reannounceOnNextScan = true;

                _chat = session.Managers.GetManager<IChatManagerServer>();
                if (_chat != null)
                {
                    _chat.MessageRecieved += OnChat;
                    AriaLog.Info("ARIA: Chat interceptor active.");
                }

                // Notify bridge
                // Re-send inhabited grids so bridge restores state after restart
                var _igb = new System.Text.StringBuilder();
                _igb.Append("[");
                bool _igFirst = true;
                foreach (var _ig in _ariaGrids)
                {
                    if (_ig == null || _ig.MarkedForClose) continue;
                    if (!_igFirst) _igb.Append(",");
                    _igb.Append("\"" + Esc(_ig.DisplayName ?? "") + "\"");
                    _igFirst = false;
                }
                _igb.Append("]");
                EnqueueAll("/aria_status",
                    "{\"status\":\"session_loaded\",\"version\":\"0.4.0\"," +
                    "\"inhabited_grids\":" + _igb.ToString() + "}");
            }
            else if (state == TorchSessionState.Unloading)
            {
                AriaLog.Info("ARIA: Session unloading.");
                _sessionUp = false;
                _ariaGrids.Clear();
                foreach (var ctx in _bridges) { ctx.PbMap.Clear(); lock (ctx.OwnedGridIds) ctx.OwnedGridIds.Clear(); }
                _cockpitMap.Clear();
                if (_chat != null)
                {
                    _chat.MessageRecieved -= OnChat;
                    _chat = null;
                }
            }
        }

        // =====================================================================
        // Update() -- SIMULATION THREAD
        // Called every game tick by Torch.
        // ALL Space Engineers world access happens here and ONLY here.
        // No awaiting, no blocking, no spawning threads that touch SE.
        // =====================================================================

        public override void Update()
        {
            if (!_sessionUp) return;
            _tick++;

            try
            {
                // Drain inbound alert queue (broadcast to chat)
                DrainAlerts();

                // Gate 1 -- execute pending PB commands on sim thread
                DrainPbCommands();

                // Process pending chat flags (uninhabit, projector, force update)
                ProcessChatFlags();

                // On-demand scan -- fires immediately when player asks about location
                if (_immediateScanPending)
                {
                    _immediateScanPending = false;
                    ReadSurroundings(RANGE_MEDIUM, false);
                }

                // Long range scan -- extended radius, player explicitly requested
                if (_longRangeScanPending)
                {
                    _longRangeScanPending = false;
                    ReadSurroundings(RANGE_LONG, true);
                    AriaLog.Info("ARIA: Long range scan fired (25km radius).");
                    _deferredScanLcdFetchTick = _tick + 120; // fetch ~2s after scan posts
                }

                // Slow scans -- staggered to avoid doing everything on the same tick
                if (_tick % TICKS_CORE_SCAN   == 1)  ScanForCores();
                if (_tick % TICKS_PB_SCAN     == 31) ScanForPb();
                if (!_firstFetchDone && _tick > 600) { FetchAndWriteScanLcds(); _firstFetchDone = true; }
                if (_tick % TICKS_SCAN_MEDIUM == 61) { ReadSurroundings(RANGE_MEDIUM, false); }
                if (_tick % TICKS_SCAN_MEDIUM == 91) FetchAndWriteScanLcds();  // fetch ~30s after scan posts
                if (_tick % TICKS_CREW_LCD    == 0)  WriteCrewLcd();
                if (_tick % TICKS_FACTION      == 0)  PostFactionUpdate();
                if (_tick % TICKS_CORE_SCAN   == 0)
                {
                    var instrGrid = _ariaGrids.Count > 0 ? _ariaGrids[0] : null;
                    WriteInstructionsLcd(instrGrid, null);
                }
                // Deferred scan LCD fetch after new ship inhabitation
                if (_deferredScanLcdFetchTick > 0 && _tick >= _deferredScanLcdFetchTick)
                {
                    _deferredScanLcdFetchTick = 0;
                    FetchAndWriteScanLcds();
                }
                // Advisory alerts now fire directly from ship_state and surroundings
                // /advisory_check endpoint removed in bridge v2.0

                // Track & Maintain -- runs every TICKS_TRACK_UPDATE ticks
                if (_trackingActive && _tick % TICKS_TRACK_UPDATE == 0)
                    UpdateTracking();

                // AI Basic block follow -- update GPS target for grid following every 60 ticks (~1s)
                if (_aiFollowActive && _aiFollowMode == "grid" && _tick % 60 == 0)
                    UpdateAIFollowGps();

                // Ship state collection -- direct block scan, no LCD middleman
                if (_tick % TICKS_SHIP_STATE == 151) CollectShipState();

                // Ore detector -- read deposits every 30s automatically
                if (_tick % TICKS_ORE_SCAN == 211) ReadOreDeposits();

                // Autopilot altitude safety -- check every second while autopilot is active
                if (_tick % 60 == 15) CheckAutopilotAltitude();

                // Character telemetry every 60 ticks (~1 per second at 60fps)
                if (_tick % 60 == 0)
                    ReadCharacterStats();
            }
            catch (Exception ex)
            {
                AriaLog.Error("ARIA: Update() error.", ex);
            }
        }

        // =====================================================================
        // SIMULATION THREAD -- SE World Access Methods
        // =====================================================================

        // Execute pending PB commands (queued by HTTP thread, drained on sim thread)
        private void DrainPbCommands()
        {
            string pbItem;
            while (_pendingPbCommands.TryDequeue(out pbItem))
            {
                if (string.IsNullOrEmpty(pbItem)) continue;

                // New format: "bridgeName||gridName|command"
                // Legacy format: "gridName|command"
                string bridgeName = null;
                if (pbItem.Contains("||"))
                {
                    var dPipe = pbItem.IndexOf("||");
                    bridgeName = pbItem.Substring(0, dPipe);
                    pbItem     = pbItem.Substring(dPipe + 2);
                }

                var pipe   = pbItem.IndexOf('|');
                var pbGrid = pipe >= 0 ? pbItem.Substring(0, pipe) : "";
                var pbCmd  = pipe >= 0 ? pbItem.Substring(pipe + 1) : pbItem;

                // Find the bridge context for this command
                BridgeContext cmdBridge = null;
                if (bridgeName != null)
                    cmdBridge = _bridges.FirstOrDefault(c =>
                        c.Name.Equals(bridgeName, StringComparison.OrdinalIgnoreCase));

                // Set current command bridge so hardware methods use correct AI name
                _currentCommandBridge = cmdBridge;

                // Determine which grids this bridge can command
                // A bridge can only command grids whose Core block matches its activation name
                // e.g. Nova bridge -> "NOVA Core" grids only
                var allowedGrids = _ariaGrids.ToList();
                if (cmdBridge != null)
                {
                    allowedGrids = _ariaGrids.Where(g =>
                    {
                        if (g == null || g.MarkedForClose) return false;
                        foreach (var b in g.GetFatBlocks())
                        {
                            var t = b as Sandbox.ModAPI.IMyTerminalBlock;
                            if (t?.CustomName?.Trim() == cmdBridge.CoreBlockName) return true;
                        }
                        return false;
                    }).ToList();

                    if (allowedGrids.Count == 0)
                    {
                        // SCAN_CORES and REINHABIT can run even without inhabited grids
                        if (pbCmd == "SCAN_CORES" || pbCmd == "REINHABIT")
                        {
                            AriaLog.Info($"Gate1: {pbCmd} with no grids -- running core+PB scan.");
                            _scanCoresPending = true;
                            continue;
                        }
                        AriaLog.Warn($"Gate1: Bridge '{cmdBridge.Name}' has no grids with '{cmdBridge.CoreBlockName}' -- command '{pbCmd}' blocked.");
                        continue;
                    }
                }

                // DAMPENERS -- handled directly by plugin
                if (pbCmd.StartsWith("DAMPENERS:"))
                {
                    var param = pbCmd.Substring("DAMPENERS:".Length).Trim().ToLower();
                    bool enable = param == "on" || param == "true" || param == "1";
                    SetDampeners(pbGrid, enable);
                    continue;
                }

                // AUTOPILOT_WAYPOINT -- handled directly by plugin
                // Plugin has full MyRemoteControl access; PB AddWaypoint is unreliable
                if (pbCmd.StartsWith("AUTOPILOT_WAYPOINT:"))
                {
                    var coordStr = pbCmd.Substring("AUTOPILOT_WAYPOINT:".Length).Trim();
                    SetAutopilotWaypoint(pbGrid, coordStr);
                    continue;
                }

                // AUTOPILOT_CANCEL -- handled directly by plugin
                if (pbCmd == "AUTOPILOT_CANCEL" || pbCmd.StartsWith("AUTOPILOT_CANCEL"))
                {
                    CancelAutopilot(pbGrid);
                    continue;
                }

                // CHAT_ALERT -- send message to SE chat directly (reliable path via /commands)
                if (pbCmd.StartsWith("CHAT_ALERT:"))
                {
                    var alertMsg = pbCmd.Substring("CHAT_ALERT:".Length).Trim();
                    if (!string.IsNullOrEmpty(alertMsg))
                    {
                        AriaLog.Info($"ARIA ALERT: {alertMsg}");
                        SendAiChat(alertMsg);
                    }
                    continue;
                }

                // RELEASE_ALL -- handled directly by plugin
                if (pbCmd == "RELEASE_ALL")
                {
                    CancelAutopilot(pbGrid);
                    SetDampeners(pbGrid, true);
                    StopTracking(pbGrid);
                    // Gyros and thrusters released via PB
                    SendToPb(pbGrid.Length > 0
                        ? (allowedGrids.FirstOrDefault(g => g.DisplayName == pbGrid)?.EntityId ?? 0)
                        : (allowedGrids.FirstOrDefault()?.EntityId ?? 0), "RELEASE_ALL");
                    continue;
                }

                // TRACK_START:TargetName:StandoffMetres -- begin track & maintain
                if (pbCmd.StartsWith("TRACK_START:"))
                {
                    var parts    = pbCmd.Substring("TRACK_START:".Length).Split(':');
                    var target   = parts.Length > 0 ? parts[0].Trim() : "";
                    var standoff = 50f;
                    if (parts.Length > 1) float.TryParse(parts[1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out standoff);
                    StartTracking(pbGrid, target, standoff);
                    continue;
                }

                // TRACK_STOP -- end track & maintain, restore dampeners
                if (pbCmd == "TRACK_STOP" || pbCmd.StartsWith("TRACK_STOP"))
                {
                    StopTracking(pbGrid);
                    continue;
                }

                // SCAN_NOW -- trigger immediate medium scan
                if (pbCmd == "SCAN_NOW")
                {
                    _immediateScanPending = true;
                    AriaLog.Info("Gate1: Immediate scan triggered.");
                    continue;
                }

                // SCAN_CORES -- manual inhabit trigger ("Aria, inhabit")
                if (pbCmd == "SCAN_CORES")
                {
                    AriaLog.Info("Gate1: Manual inhabit triggered -- scanning for cores.");
                    ScanForCores();
                    ScanForPb();
                    continue;
                }

                // REINHABIT -- hard reset: clear all habitation, rescan, only inhabit grids
                // that have [AI] PB named correctly for this bridge
                if (pbCmd == "REINHABIT")
                {
                    AriaLog.Info($"Gate1: REINHABIT triggered for bridge '{cmdBridge?.Name}' -- clearing habitation state.");

                    // Clear this bridge's PbMap and OwnedGridIds -- forces fresh inhabit
                    if (cmdBridge != null)
                    {
                        // Announce uninhabit for all currently inhabited grids
                        foreach (var kvp in cmdBridge.PbMap.ToList())
                        {
                            var prevGrid = _ariaGrids.FirstOrDefault(g => g.EntityId == kvp.Key);
                            if (prevGrid != null)
                            {
                                SetPresenceIndicator(prevGrid, false);
                                cmdBridge.Outbound.Enqueue("/aria_inhabit", "{\"grid_name\":\"" + Esc(prevGrid.DisplayName) + "\"," +
                                    "\"inhabited\":false,\"reason\":\"reinhabit_reset\"}");
                                AriaLog.Info($"Gate1: REINHABIT -- uninhabited '{prevGrid.DisplayName}'.");
                            }
                        }
                        cmdBridge.PbMap.Clear();
                        lock (cmdBridge.OwnedGridIds)
                            cmdBridge.OwnedGridIds.Clear();
                    }

                    // Full rescan
                    ScanForCores();
                    ScanForPb();

                    var reinhabitAiName = cmdBridge != null
                        ? char.ToUpperInvariant(cmdBridge.ActivationName[0]) + cmdBridge.ActivationName.Substring(1)
                        : "ARIA";
                    SendToFaction(cmdBridge, reinhabitAiName, "Habitation reset. Rescanning for terminals.");
                    continue;
                }

                // SAFETY_OVERRIDE -- persistent toggle for terrain correction bypass
                // Command format: SAFETY_OVERRIDE, SAFETY_OVERRIDE:on, SAFETY_OVERRIDE:off
                if (pbCmd == "SAFETY_OVERRIDE" || pbCmd.StartsWith("SAFETY_OVERRIDE:"))
                {
                    var safetyArg = pbCmd.Contains(":") ? pbCmd.Substring(pbCmd.IndexOf(':') + 1).ToLowerInvariant() : "toggle";
                    var safetyAiName = cmdBridge != null
                        ? char.ToUpperInvariant(cmdBridge.ActivationName[0]) + cmdBridge.ActivationName.Substring(1)
                        : "ARIA";
                    if (safetyArg == "off")
                    {
                        _autopilotSafetyOverride = false;
                        AriaLog.Info("Gate1: Safety override DISABLED -- terrain correction restored.");
                        SendToFaction(cmdBridge, safetyAiName, "Safety override off. Terrain correction restored.");
                    }
                    else if (safetyArg == "on")
                    {
                        _autopilotSafetyOverride = true;
                        AriaLog.Info("Gate1: Safety override ENABLED -- terrain correction disabled.");
                        SendToFaction(cmdBridge, safetyAiName, "Safety override on. Terrain correction disabled.");
                    }
                    else // toggle
                    {
                        _autopilotSafetyOverride = !_autopilotSafetyOverride;
                        AriaLog.Info($"Gate1: Safety override toggled -> {(_autopilotSafetyOverride ? "ON" : "OFF")}.");
                        SendToFaction(cmdBridge, safetyAiName,
                            _autopilotSafetyOverride
                                ? "Safety override on. Terrain correction disabled."
                                : "Safety override off. Terrain correction restored.");
                    }
                    continue;
                }
                if (pbCmd == "DEEP_SCAN")
                {
                    AriaLog.Info("Gate1: Ore detector scan triggered.");
                    ReadOreDeposits();
                    continue;
                }

                // LONG_SCAN -- trigger 25km long range scan
                if (pbCmd == "LONG_SCAN")
                {
                    _longRangeScanPending = true;
                    AriaLog.Info("Gate1: Long range scan triggered.");
                    SendAiChat("Initiating long range scan. Stand by.");
                    continue;
                }

                // JUMP_DRIVE:jump -- handled directly by plugin
                // PB script cannot trigger jumps (SE blocks it from toolbar)
                // Plugin has full GridSystems.JumpSystem access
                if (pbCmd.StartsWith("JUMP_DRIVE:"))
                {
                    var param = pbCmd.Substring("JUMP_DRIVE:".Length).Trim();
                    if (param.ToLower() == "jump")
                    {
                        ExecuteJump(pbGrid);
                        continue;
                    }
                    // JUMP_DRIVE:target:x,y,z -- targeted jump to coordinates
                    if (param.StartsWith("target:"))
                    {
                        var coordStr = param.Substring("target:".Length).Trim();
                        ExecuteJumpToTarget(pbGrid, coordStr);
                        continue;
                    }
                    // recharge, on, off, status -- route to PB normally
                }

                // AI_FOLLOW:mode:target:distance -- use AI Basic block native follow
                if (pbCmd.StartsWith("AI_FOLLOW:"))
                {
                    var args = pbCmd.Substring("AI_FOLLOW:".Length).Split(':');
                    if (args.Length >= 3)
                    {
                        var mode     = args[0].Trim().ToLower();  // "player" or "grid"
                        var target   = args[1].Trim();
                        float dist   = 50f;
                        float.TryParse(args[2].Trim(),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out dist);
                        ExecuteAIFollow(pbGrid, mode, target, dist);
                    }
                    continue;
                }

                // AI_FOLLOW_STOP -- stop AI follow
                if (pbCmd == "AI_FOLLOW_STOP")
                {
                    StopAIFollow(pbGrid);
                    continue;
                }

                // EMOTION: -- drive [AI] EMOTION PB directly via TryRun
                if (pbCmd.StartsWith("EMOTION:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var grid in allowedGrids)
                    {
                        if (grid == null || grid.MarkedForClose) continue;
                        IMyProgrammableBlock emotionPb = null;
                        foreach (var fb in grid.GetFatBlocks())
                        {
                            var pb2 = fb as IMyProgrammableBlock;
                            if (pb2 == null) continue;
                            if (pb2.CustomName != null && pb2.CustomName.IndexOf("EMOTION PB",
                                StringComparison.OrdinalIgnoreCase) >= 0)
                            { emotionPb = pb2; break; }
                        }
                        if (emotionPb != null)
                        {
                            try { emotionPb.Run(pbCmd, UpdateType.Script); }
                            catch (Exception ex) { AriaLog.Debug($"EMOTION PB run error: {ex.Message}"); }
                        }
                    }
                    continue;
                }

                // TALK: -- drive emotion PB talking mouth
                if (pbCmd.StartsWith("TALK:", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var grid in allowedGrids)
                    {
                        if (grid == null || grid.MarkedForClose) continue;
                        IMyProgrammableBlock talkPb = null;
                        foreach (var fb in grid.GetFatBlocks())
                        {
                            var pb2 = fb as IMyProgrammableBlock;
                            if (pb2 == null) continue;
                            if (pb2.CustomName != null && pb2.CustomName.IndexOf("EMOTION PB",
                                StringComparison.OrdinalIgnoreCase) >= 0)
                            { talkPb = pb2; break; }
                        }
                        if (talkPb != null)
                        {
                            try { talkPb.Run(pbCmd, UpdateType.Script); }
                            catch (Exception ex) { AriaLog.Debug($"TALK PB run error: {ex.Message}"); }
                        }
                    }
                    continue;
                }

                bool sent = false;
                foreach (var grid in allowedGrids)
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!string.IsNullOrEmpty(pbGrid) && grid.DisplayName != pbGrid) continue;

                    SendToPb(grid.EntityId, pbCmd);
                    sent = true;

                    var resultJson =
                        "{\"grid_name\":\"" + Esc(grid.DisplayName) + "\"," +
                        "\"command\":\"" + Esc(pbCmd) + "\"," +
                        "\"status\":\"executed\"}";
                    EnqueueAll("/command_result", resultJson);
                    break;
                }

                if (!sent)
                    AriaLog.Warn($"Gate1: No grid for '{pbCmd}' (target: '{pbGrid}', bridge: '{bridgeName ?? "legacy"}')");
            }
        }  // end DrainPbCommands

        // Set autopilot waypoint via plugin -- full MyRemoteControl access
        private void SetAutopilotWaypoint(string gridName, string coordStr)
        {
            var parts = coordStr.Split(',');
            if (parts.Length < 3) { AriaLog.Warn("Autopilot: bad coords: " + coordStr); return; }
            double x, y, z;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out x)) return;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out y)) return;
            if (!double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out z)) return;

            var target = new VRageMath.Vector3D(x, y, z);
            bool applied = false;

            if (_ariaGrids.Count == 0)
            {
                AriaLog.Warn("Autopilot: _ariaGrids is empty -- ScanForCores not yet run?");
                return;
            }

            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                // Flexible match: exact name OR first grid if name is empty
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName)
                {
                    AriaLog.Debug($"Autopilot: skipping '{grid.DisplayName}' (want '{gridName}')");
                    continue;
                }

                foreach (var block in grid.GetFatBlocks())
                {
                    var rc = block as IMyRemoteControl;
                    if (rc == null || !rc.IsFunctional) continue;

                    try
                    {
                        // Ensure RC is the main controller and has authority
                        rc.DampenersOverride = true;
                        rc.ControlThrusters  = true;
                        rc.ClearWaypoints();
                        rc.AddWaypoint(target, "ARIA Target");
                        rc.FlightMode  = FlightMode.OneWay;

                        // Physics-based speed limit -- calculate max speed from which
                        // the ship can stop within the remaining distance
                        // d = v² / (2a)  =>  v = sqrt(2 * a * d)
                        double dist = Vector3D.Distance(rc.GetPosition(), target);
                        float speed = 100f; // default

                        try
                        {
                            // Get ship mass
                            float mass = grid.Physics?.Mass ?? 100000f;

                            // Get total braking thrust (thrusters facing toward target = backward thrust)
                            var toTarget = Vector3D.Normalize(target - rc.GetPosition());
                            float brakingThrust = 0f;
                            foreach (var blk in grid.GetFatBlocks())
                            {
                                var thr = blk as Sandbox.ModAPI.IMyThrust;
                                if (thr == null || !thr.IsFunctional || !thr.Enabled) continue;
                                // Thruster faces backward = forward thrust
                                // Braking = thrusters whose forward vector opposes direction of travel
                                var thrFwd = Vector3D.Normalize(thr.WorldMatrix.Backward);
                                double dot = Vector3D.Dot(thrFwd, toTarget);
                                if (dot > 0.5) // roughly facing toward target = braking thrust
                                    brakingThrust += thr.MaxEffectiveThrust * (float)dot;
                            }

                            // Add gravity assist if moving toward planet
                            float grav = 0f;
                            try { grav = (float)MyGravityProviderSystem.CalculateNaturalGravityInPoint(rc.GetPosition()).Length(); } catch { }

                            float decel = mass > 0 ? (brakingThrust / mass) + grav : 9.8f;
                            decel = Math.Max(1f, decel); // at least 1 m/s²

                            // v = sqrt(2 * a * d), with 0.7 safety factor
                            speed = (float)Math.Sqrt(2.0 * decel * dist) * 0.7f;
                            speed = Math.Max(5f, Math.Min(100f, speed));

                            AriaLog.Debug($"Autopilot physics: mass={mass:F0}kg brake={brakingThrust:F0}N " +
                                          $"decel={decel:F1}m/s² dist={dist:F0}m -> speed={speed:F0}m/s");
                        }
                        catch (Exception physEx)
                        {
                            AriaLog.Warn($"Speed calc error: {physEx.Message} -- using default");
                            // Fallback to conservative sqrt formula
                            speed = Math.Max(5f, Math.Min(100f, (float)Math.Sqrt(dist) * 2f));
                        }

                        rc.SpeedLimit = speed;

                        rc.SetAutoPilotEnabled(true);
                        _autopilotStartTick = _tick;
                        applied = true;
                        AriaLog.Info($"Autopilot engaged: ({x:F0},{y:F0},{z:F0}) dist={dist:F0}m speed={speed:F0}m/s via '{rc.CustomName}' " +
                                     $"functional={rc.IsFunctional} working={rc.IsWorking} " +
                                     $"autopilot={rc.IsAutoPilotEnabled} on '{grid.DisplayName}'");

                        var resultJson =
                            "{\"grid_name\":\"" + Esc(grid.DisplayName) + "\"," +
                            "\"command\":\"AUTOPILOT_WAYPOINT\"," +
                            "\"status\":\"executed\"," +
                            "\"target\":\"" + x.ToString("F0") + "," + y.ToString("F0") + "," + z.ToString("F0") + "\"}";
                        EnqueueAll("/command_result", resultJson);
                        break;
                    }
                    catch (Exception ex) { AriaLog.Error("Autopilot set failed", ex); }
                }
                if (applied) break;
            }

            if (!applied)
                AriaLog.Warn($"Autopilot: no RC block found on '{gridName}'");
        }

        // Cancel autopilot via plugin
        private void CancelAutopilot(string gridName)
        {
            _autopilotSafetyOverride = false; // reset safety override on cancel
            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName) continue;

                foreach (var block in grid.GetFatBlocks())
                {
                    var rc = block as IMyRemoteControl;
                    if (rc == null) continue;
                    try
                    {
                        rc.SetAutoPilotEnabled(false);
                        rc.ClearWaypoints();
                        AriaLog.Info($"Autopilot cancelled on '{grid.DisplayName}'");
                    }
                    catch { }
                }
            }
        }

        // Set dampeners via RC block (primary) or any cockpit (fallback)
        // RC block works without a player seated -- perfect for autonomous control
        // Safe: called from Update() on sim thread
        // =====================================================================
        // TRACK & MAINTAIN
        // =====================================================================

        private void StartTracking(string gridName, string targetName, float standoffM)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                AriaLog.Warn("Track: no target name provided.");
                return;
            }

            _trackTargetName  = targetName;
            _trackStandoffM   = Math.Max(10f, standoffM);
            _trackLastPos     = Vector3D.Zero;
            _trackLastVel     = Vector3D.Zero;
            _trackFirstSample = true;
            _trackLastPosTime = 0.0;
            _trackLostWarned  = false;
            _pidIntegralFwd   = 0f;
            _pidIntegralLat   = 0f;
            _pidIntegralVert  = 0f;

            // Disable dampeners so our own inertia doesn't fight us
            SetDampeners(gridName, false);

            _trackingActive = true;
            AriaLog.Info($"Track: Started tracking '{targetName}' at {standoffM}m standoff.");
            SendAiChat($"Tracking '{targetName}'. Standoff: {standoffM:F0}m. Dampeners off.");
        }

        private void StopTracking(string gridName)
        {
            if (!_trackingActive) return;

            _trackingActive = false;

            // Zero all thruster overrides
            ZeroThrusters(gridName);

            // Release gyro overrides
            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName) continue;
                foreach (var block in grid.GetFatBlocks())
                {
                    var gyro = block as Sandbox.ModAPI.IMyGyro;
                    if (gyro != null) gyro.GyroOverride = false;
                }
            }

            // Restore dampeners
            SetDampeners(gridName, true);

            AriaLog.Info("Track: Tracking stopped. Dampeners and gyros restored.");
            SendAiChat("Tracking disengaged. Dampeners restored.");
        }

        private void ZeroThrusters(string gridName)
        {
            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName) continue;

                foreach (var block in grid.GetFatBlocks())
                {
                    if (block == null || block.MarkedForClose) continue;
                    var thruster = block as Sandbox.ModAPI.IMyThrust;
                    if (thruster == null) continue;
                    thruster.ThrustOverride = 0f;
                    thruster.Enabled = true;
                }
            }
        }

        // =====================================================================
        // UpdateTracking -- called every TICKS_TRACK_UPDATE on sim thread
        //
        // Physics model:
        //   1. Locate target entity by display name in world entity list
        //   2. Sample target position + estimate velocity from delta
        //   3. Predict intercept position (target pos + vel * lookahead)
        //   4. Calculate relative position vector (intercept - our pos)
        //   5. Project onto our local ship axes (fwd, right, up)
        //   6. PID controller produces thrust demand per axis
        //   7. Apply thruster overrides to close/maintain distance
        //
        // Coordinate convention:
        //   All vectors in world space, projected to ship-local before thrust
        //   Ship local axes from RC block WorldMatrix (Forward, Right, Up)
        // =====================================================================

        private void UpdateTracking()
        {
            if (!_trackingActive || string.IsNullOrEmpty(_trackTargetName)) return;

            // -- Find our RC block and grid ------------------------------------
            MyCubeGrid ourGrid     = null;
            IMyRemoteControl ourRc = null;

            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose || grid.IsPreview) continue;
                foreach (var block in grid.GetFatBlocks())
                {
                    if (block == null || block.MarkedForClose) continue;
                    var rc = block as IMyRemoteControl;
                    var tb = block as Sandbox.ModAPI.IMyTerminalBlock;
                    if (rc != null && tb?.CustomName?.Trim() == ARIA_CORE)
                    {
                        ourGrid = grid;
                        ourRc   = rc;
                        break;
                    }
                }
                if (ourRc != null) break;
            }

            if (ourGrid == null || ourRc == null || ourGrid.Physics == null)
            {
                AriaLog.Warn("Track: No RC block or physics -- cannot update tracking.");
                return;
            }

            // -- Find target entity --------------------------------------------
            Vector3D targetPos = Vector3D.Zero;
            bool targetFound   = false;
            string tNameLower  = _trackTargetName.ToLower();

            // -- Search grids first --
            foreach (var entity in MyEntities.GetEntities().ToList())
            {
                var targetGrid = entity as MyCubeGrid;
                if (targetGrid == null || targetGrid.MarkedForClose || targetGrid.IsPreview) continue;
                if (targetGrid.EntityId == ourGrid.EntityId) continue;
                if (!targetGrid.DisplayName.ToLower().Contains(tNameLower)) continue;
                if (targetGrid.Physics == null) continue;

                targetPos   = targetGrid.Physics.CenterOfMassWorld;
                targetFound = true;
                break;
            }

            // -- Fallback: search player characters by name --
            if (!targetFound)
            {
                var session = MySession.Static;
                if (session?.Players != null)
                {
                    foreach (var player in session.Players.GetOnlinePlayers())
                    {
                        if (player.IsBot) continue;
                        if (!player.DisplayName.ToLower().Contains(tNameLower)) continue;
                        var ch = player.Character;
                        if (ch == null) continue;
                        targetPos   = ch.PositionComp.GetPosition();
                        targetFound = true;
                        break;
                    }
                }
            }

            if (!targetFound)
            {
                if (!_trackLostWarned)
                {
                    AriaLog.Warn($"Track: Target '{_trackTargetName}' not found -- tracking paused.");
                    SendAiChat($"Lost contact with '{_trackTargetName}'. Maintaining position.");
                    _trackLostWarned = true;
                }
                return;
            }
            // Target reacquired -- reset warning flag
            _trackLostWarned = false;

            // -- Sample target velocity ----------------------------------------
            double nowSec = MySession.Static?.GameplayFrameCounter / 60.0 ?? 0.0;

            if (_trackFirstSample || _trackLastPosTime <= 0.0)
            {
                _trackLastPos     = targetPos;
                _trackLastPosTime = nowSec;
                _trackFirstSample = false;
                return; // Need two samples for velocity
            }

            double dt = nowSec - _trackLastPosTime;
            if (dt < 0.01) return; // Too soon

            Vector3D targetVel = (targetPos - _trackLastPos) / dt;
            _trackLastPos      = targetPos;
            _trackLastVel      = targetVel;
            _trackLastPosTime  = nowSec;

            // -- Our own state -------------------------------------------------
            Vector3D ourPos = ourGrid.Physics.CenterOfMassWorld;
            Vector3D ourVel = ourGrid.Physics.LinearVelocity;

            // -- Predict intercept ---------------------------------------------
            // Lookahead = time for us to travel remaining distance at current closing rate
            // Clamped to 0.5s - 5s to stay responsive without overshooting
            Vector3D relPos    = targetPos - ourPos;
            double dist        = relPos.Length();
            double closingRate = -Vector3D.Dot(ourVel - targetVel, relPos / Math.Max(dist, 0.01));
            double lookahead   = dist / Math.Max(Math.Abs(closingRate) + 1.0, 5.0);
            lookahead          = Math.Min(Math.Max(lookahead, 0.5), 5.0);

            Vector3D interceptPos = targetPos + targetVel * lookahead;

            // -- Desired position = intercept pos - standoff along approach vec -
            Vector3D approachVec  = interceptPos - ourPos;
            double   approachDist = approachVec.Length();
            Vector3D approachDir  = approachDist > 0.01 ? approachVec / approachDist : Vector3D.Forward;

            Vector3D desiredPos   = interceptPos - approachDir * _trackStandoffM;
            Vector3D desiredRelPos = desiredPos - ourPos;

            // -- Relative velocity (we want to match target velocity) ----------
            Vector3D relVel = ourVel - targetVel;

            // -- Project onto ship local axes ----------------------------------
            MatrixD shipMatrix = ourRc.WorldMatrix;
            Vector3D shipFwd   = shipMatrix.Forward;
            Vector3D shipRight = shipMatrix.Right;
            Vector3D shipUp    = shipMatrix.Up;

            // Position error in ship-local axes
            float errFwd   = (float)Vector3D.Dot(desiredRelPos, shipFwd);
            float errRight = (float)Vector3D.Dot(desiredRelPos, shipRight);
            float errUp    = (float)Vector3D.Dot(desiredRelPos, shipUp);

            // Velocity error (we want zero relative velocity)
            float velErrFwd   = (float)Vector3D.Dot(relVel, shipFwd);
            float velErrRight = (float)Vector3D.Dot(relVel, shipRight);
            float velErrUp    = (float)Vector3D.Dot(relVel, shipUp);

            // -- PID controller ------------------------------------------------
            // Kp: position gain  Kd: velocity damping  Ki: integral wind-up correction
            // Tuning: conservative -- prefer smooth over fast
            const float Kp    = 0.04f;   // position P gain
            const float Kd    = 0.20f;   // velocity D gain (damping)
            const float Ki    = 0.002f;  // integral I gain
            const float maxI  = 0.20f;   // integral clamp
            const float maxT  = 0.35f;   // max thrust fraction (35% -- gentle)

            // Accumulate integral (only when close -- avoids wind-up at long range)
            if (Math.Abs(dist - _trackStandoffM) < 200f)
            {
                _pidIntegralFwd   = Math.Max(-maxI, Math.Min(maxI, _pidIntegralFwd   + errFwd   * (float)dt));
                _pidIntegralLat   = Math.Max(-maxI, Math.Min(maxI, _pidIntegralLat   + errRight * (float)dt));
                _pidIntegralVert  = Math.Max(-maxI, Math.Min(maxI, _pidIntegralVert  + errUp    * (float)dt));
            }
            else
            {
                // Far away -- reset integral, use pure proportional
                _pidIntegralFwd  = 0f;
                _pidIntegralLat  = 0f;
                _pidIntegralVert = 0f;
            }

            // PID outputs -- clamped to maxT
            float tFwd   = Clamp(Kp * errFwd   - Kd * velErrFwd   + Ki * _pidIntegralFwd,   -maxT, maxT);
            float tRight = Clamp(Kp * errRight  - Kd * velErrRight + Ki * _pidIntegralLat,   -maxT, maxT);
            float tUp    = Clamp(Kp * errUp     - Kd * velErrUp    + Ki * _pidIntegralVert,  -maxT, maxT);

            // -- Apply thruster overrides --------------------------------------
            ApplyThrustVector(ourGrid, shipFwd, shipRight, shipUp, tFwd, tRight, tUp);

            // -- Gyro facing -- orient ship forward toward target --------------
            ApplyGyroFacing(ourGrid, targetPos);

            // -- Status log (every 10 update cycles ~5s) -----------------------
            if (_tick % (TICKS_TRACK_UPDATE * 10) == 0)
            {
                AriaLog.Info($"Track: dist={dist:F0}m close={closingRate:F1}m/s " +
                             $"tFwd={tFwd:F2} tR={tRight:F2} tU={tUp:F2} " +
                             $"targetVel={targetVel.Length():F1}m/s");
                EnqueueAll("/push_alert",
                    $"{{\"message\":\"Tracking '{_trackTargetName}': {dist:F0}m | closing {closingRate:F1}m/s\"}}");
            }
        }

        // =====================================================================
        // ApplyGyroFacing
        // Rotates the ship to face its forward axis toward targetPos.
        // Uses gyro override with a proportional controller.
        // Gyros are released when pointing within 2 degrees of target.
        // =====================================================================
        private void ApplyGyroFacing(MyCubeGrid grid, VRageMath.Vector3D targetPos)
        {
            // Find RC block for world matrix reference
            IMyRemoteControl rc = null;
            foreach (var block in grid.GetFatBlocks())
            {
                var r = block as IMyRemoteControl;
                var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                if (r != null && t?.CustomName?.Trim() == ARIA_CORE) { rc = r; break; }
            }
            if (rc == null) return;

            var shipMatrix = rc.WorldMatrix;
            var toTarget   = VRageMath.Vector3D.Normalize(targetPos - shipMatrix.Translation);

            // Current forward vs desired forward
            var currentFwd = shipMatrix.Forward;
            var cross      = VRageMath.Vector3D.Cross(currentFwd, toTarget);
            var dot        = VRageMath.Vector3D.Dot(currentFwd, toTarget);

            // Within ~2 degrees -- release gyros
            if (dot > 0.9994) // cos(2°) ≈ 0.9994
            {
                foreach (var block in grid.GetFatBlocks())
                {
                    var gyro = block as Sandbox.ModAPI.IMyGyro;
                    if (gyro == null || !gyro.IsFunctional) continue;
                    gyro.GyroOverride = false;
                }
                return;
            }

            // Rotation axis in world space -> project to ship local axes
            float Kp_gyro = 4.0f; // gyro gain -- tune if rotation is too fast/slow
            float maxRpm  = 15.0f;

            var shipRight = (VRageMath.Vector3)shipMatrix.Right;
            var shipUp    = (VRageMath.Vector3)shipMatrix.Up;
            var shipFwd   = (VRageMath.Vector3)shipMatrix.Forward;

            var crossF    = (VRageMath.Vector3)cross;
            float pitchErr = VRageMath.Vector3.Dot(crossF, shipRight);
            float yawErr   = VRageMath.Vector3.Dot(crossF, shipUp);
            // no roll correction needed for following

            float pitch = Clamp(Kp_gyro * pitchErr, -maxRpm, maxRpm);
            float yaw   = Clamp(Kp_gyro * yawErr,   -maxRpm, maxRpm);

            foreach (var block in grid.GetFatBlocks())
            {
                var gyro = block as Sandbox.ModAPI.IMyGyro;
                if (gyro == null || !gyro.IsFunctional) continue;

                // Convert world-space rotation demand to gyro local frame
                var gyroMatrix = gyro.WorldMatrix;
                var localPitch = VRageMath.Vector3.Dot((VRageMath.Vector3)gyroMatrix.Right, shipRight) * pitch
                               + VRageMath.Vector3.Dot((VRageMath.Vector3)gyroMatrix.Right, shipUp)   * yaw;
                var localYaw   = VRageMath.Vector3.Dot((VRageMath.Vector3)gyroMatrix.Up,    shipRight) * pitch
                               + VRageMath.Vector3.Dot((VRageMath.Vector3)gyroMatrix.Up,    shipUp)   * yaw;
                var localRoll  = VRageMath.Vector3.Dot((VRageMath.Vector3)gyroMatrix.Forward, shipRight) * pitch
                               + VRageMath.Vector3.Dot((VRageMath.Vector3)gyroMatrix.Forward, shipUp) * yaw;

                gyro.GyroOverride = true;
                gyro.Pitch = localPitch;
                gyro.Yaw   = localYaw;
                gyro.Roll  = localRoll;
            }
        }

        private static float Clamp(float v, float min, float max)
        {
            return v < min ? min : v > max ? max : v;
        }

        // =====================================================================
        // ApplyThrustVector
        // Decomposes a ship-local thrust demand (fwd, right, up) into
        // individual thruster overrides, respecting thruster orientation.
        //
        // SE thrusters fire in their local Forward direction.
        // A thruster pointing ship-forward fires when tFwd > 0.
        // A thruster pointing ship-backward fires when tFwd < 0.
        // =====================================================================

        private void ApplyThrustVector(
            MyCubeGrid grid,
            Vector3D shipFwd, Vector3D shipRight, Vector3D shipUp,
            float tFwd, float tRight, float tUp)
        {
            if (grid == null || grid.Physics == null) return;

            foreach (var block in grid.GetFatBlocks())
            {
                if (block == null || block.MarkedForClose) continue;
                var thruster = block as Sandbox.ModAPI.IMyThrust;
                if (thruster == null || !thruster.IsFunctional) continue;

                // Thruster fires opposite to its facing direction
                // ThrusterForwardVector is the direction thrust is applied
                Vector3D thrustDir = Vector3D.TransformNormal(
                    thruster.WorldMatrix.Forward, MatrixD.Transpose(grid.WorldMatrix));

                // Dot with each desired axis to see how much this thruster contributes
                float dotFwd   = (float)Vector3D.Dot(thrustDir, shipFwd);
                float dotRight = (float)Vector3D.Dot(thrustDir, shipRight);
                float dotUp    = (float)Vector3D.Dot(thrustDir, shipUp);

                // Demand: positive = thrust in that direction
                // Thruster fires if dot product with desired thrust is positive
                float demand = dotFwd   * tFwd
                             + dotRight * tRight
                             + dotUp    * tUp;

                // Convert fraction to N -- use max effective thrust
                float maxThrust = thruster.MaxEffectiveThrust;
                if (maxThrust <= 0f) continue;

                float overrideN = Math.Max(0f, demand) * maxThrust;
                thruster.ThrustOverride = overrideN;
            }
        }

                // =====================================================================
        // =====================================================================
        // ExecuteJump / ExecuteJumpToTarget
        // Uses reflection to call MyGridJumpDriveSystem.OnRequestJumpFromClient
        // directly, bypassing RequestJump's multiplayer event and the
        // LocalCharacter null check that breaks dedicated servers.
        // =====================================================================

        private void ExecuteJump(string gridName)
        {
            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose || grid.IsPreview) continue;
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName) continue;

                try
                {
                    var internalGrid = grid as Sandbox.Game.Entities.MyCubeGrid;
                    if (internalGrid?.GridSystems?.JumpSystem == null)
                    {
                        AriaLog.Warn("Jump: GridSystems.JumpSystem not accessible.");
                        return;
                    }

                    // Check drive readiness and get owner id
                    bool hasReady = false;
                    double maxCharge = 0;
                    long ownerId = 0;
                    float distRatio = 1.0f;

                    foreach (var block in grid.GetFatBlocks())
                    {
                        var jd = block as Sandbox.ModAPI.IMyJumpDrive;
                        if (jd == null || !jd.IsFunctional) continue;
                        double pct = jd.CurrentStoredPower / jd.MaxStoredPower;
                        if (pct > maxCharge) maxCharge = pct;
                        if (jd.Status == Sandbox.ModAPI.Ingame.MyJumpDriveStatus.Ready)
                        {
                            hasReady = true;
                            ownerId = (block as VRage.Game.ModAPI.IMyCubeBlock)?.OwnerId ?? 0;
                            distRatio = jd.JumpDistanceRatio / 100f;
                        }
                    }

                    if (!hasReady)
                    {
                        SendAiChat($"Jump drive not ready. Charged at {maxCharge * 100:F0}%.");
                        AriaLog.Warn($"Jump: Not ready ({maxCharge * 100:F0}%)");
                        return;
                    }

                    // Compute blind jump destination respecting distance slider
                    double maxDist = internalGrid.GridSystems.JumpSystem.GetMaxJumpDistance(ownerId);
                    const double MIN_JUMP = 5000.0;
                    double actualDist = MIN_JUMP + (maxDist - MIN_JUMP) * distRatio;
                    actualDist = Math.Max(MIN_JUMP, Math.Min(actualDist, maxDist));

                    var forward  = VRageMath.Vector3D.Normalize(grid.WorldMatrix.Forward);
                    var jumpDest = grid.WorldMatrix.Translation + forward * actualDist;

                    InvokeJumpViaReflection(internalGrid, jumpDest, ownerId);
                }
                catch (Exception ex) { AriaLog.Error("ExecuteJump failed.", ex); }
                return;
            }
            AriaLog.Warn($"Jump: No matching grid for '{gridName}'");
        }

        private void ExecuteJumpToTarget(string gridName, string coordStr)
        {
            var parts = coordStr.Split(',');
            if (parts.Length < 3) { AriaLog.Warn("JumpToTarget: bad coords: " + coordStr); return; }
            double x, y, z;
            if (!double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out x)) return;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out y)) return;
            if (!double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out z)) return;

            var target = new VRageMath.Vector3D(x, y, z);

            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose || grid.IsPreview) continue;
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName) continue;

                try
                {
                    var internalGrid = grid as Sandbox.Game.Entities.MyCubeGrid;
                    if (internalGrid?.GridSystems?.JumpSystem == null)
                    {
                        AriaLog.Warn("JumpToTarget: GridSystems.JumpSystem not accessible.");
                        return;
                    }

                    bool hasReady = false;
                    long ownerId = 0;
                    foreach (var block in grid.GetFatBlocks())
                    {
                        var jd = block as Sandbox.ModAPI.IMyJumpDrive;
                        if (jd == null || !jd.IsFunctional) continue;
                        if (jd.Status == Sandbox.ModAPI.Ingame.MyJumpDriveStatus.Ready)
                        {
                            hasReady = true;
                            ownerId = (block as VRage.Game.ModAPI.IMyCubeBlock)?.OwnerId ?? 0;
                            break;
                        }
                    }

                    if (!hasReady)
                    {
                        SendAiChat("Jump drive not ready.");
                        return;
                    }

                    double maxDist = internalGrid.GridSystems.JumpSystem.GetMaxJumpDistance(ownerId);
                    double dist    = VRageMath.Vector3D.Distance(grid.WorldMatrix.Translation, target);

                    if (dist > maxDist)
                    {
                        var dir = VRageMath.Vector3D.Normalize(target - grid.WorldMatrix.Translation);
                        target  = grid.WorldMatrix.Translation + dir * maxDist * 0.95;
                        SendAiChat($"Target out of range ({dist/1000.0:F1}km). Jumping {maxDist*0.95/1000.0:F1}km toward destination.");
                    }

                    InvokeJumpViaReflection(internalGrid, target, ownerId);
                }
                catch (Exception ex) { AriaLog.Error("ExecuteJumpToTarget failed.", ex); }
                return;
            }
        }

        // =====================================================================
        // InvokeJump
        // Triggers jump via multiple approaches in order:
        // 1. Set JumpTarget property on drive block, then call RequestJump
        // 2. Reflection on OnRequestJumpFromClient with correct param count
        // 3. Log available methods for diagnosis
        // =====================================================================
        private void InvokeJumpViaReflection(Sandbox.Game.Entities.MyCubeGrid internalGrid,
                                              VRageMath.Vector3D destination, long ownerId)
        {
            try
            {
                var jumpSystem = internalGrid.GridSystems?.JumpSystem;
                if (jumpSystem == null)
                {
                    AriaLog.Warn("Jump: JumpSystem is null.");
                    SendAiChat("Jump system not available.");
                    return;
                }

                // ── Log actual signature of OnRequestJumpFromClient ───────────
                var allMethods = jumpSystem.GetType().GetMethods(
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Static);

                var jumpMethods = System.Array.FindAll(allMethods,
                    m => m.Name.IndexOf("Jump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         m.Name.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0);

                foreach (var jm in jumpMethods)
                {
                    var ps = jm.GetParameters();
                    AriaLog.Info($"Jump: Method '{jm.Name}' params: [{string.Join(", ", System.Array.ConvertAll(ps, p => p.ParameterType.Name + " " + p.Name))}]");
                }

                // ── Strategy 1: Call RequestJump (public) directly ────────────
                // On dedicated server we ARE the server, so this should work
                try
                {
                    jumpSystem.RequestJump("ARIA Jump", destination, ownerId);
                    AriaLog.Info($"Jump: RequestJump called to ({destination.X:F0},{destination.Y:F0},{destination.Z:F0})");
                    SendAiChat("Jump drive engaged.");
                    EnqueueAll("/command_result",
                        "{\"grid_name\":\"" + Esc(internalGrid.DisplayName) + "\"," +
                        "\"command\":\"JUMP_DRIVE:jump\",\"status\":\"executed\"}");
                    return;
                }
                catch (Exception reqEx)
                {
                    AriaLog.Warn($"Jump: RequestJump threw: {reqEx.Message}");
                }

                // ── Strategy 2: Reflection on OnRequestJumpFromClient ─────────
                // Try to find method and match parameter count dynamically
                var method = jumpSystem.GetType().GetMethod(
                    "OnRequestJumpFromClient",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                if (method != null)
                {
                    var pms = method.GetParameters();
                    AriaLog.Info($"Jump: OnRequestJumpFromClient has {pms.Length} params");

                    object[] invokeArgs;
                    if (pms.Length == 2)
                        invokeArgs = new object[] { destination, ownerId };
                    else if (pms.Length == 1)
                        invokeArgs = new object[] { destination };
                    else if (pms.Length == 3)
                        invokeArgs = new object[] { destination, ownerId, (byte)0 };
                    else
                    {
                        AriaLog.Warn($"Jump: Unexpected param count {pms.Length} -- skipping reflection");
                        SendAiChat("Jump system error. Check Torch log.");
                        return;
                    }

                    method.Invoke(jumpSystem, invokeArgs);
                    AriaLog.Info("Jump: OnRequestJumpFromClient invoked successfully.");
                    SendAiChat("Jump drive engaged.");
                    EnqueueAll("/command_result",
                        "{\"grid_name\":\"" + Esc(internalGrid.DisplayName) + "\"," +
                        "\"command\":\"JUMP_DRIVE:jump\",\"status\":\"executed\"}");
                }
                else
                {
                    AriaLog.Warn("Jump: OnRequestJumpFromClient not found. Available jump methods logged above.");
                    SendAiChat("Jump system unavailable. Check Torch log.");
                }
            }
            catch (Exception ex)
            {
                AriaLog.Error($"Jump: InvokeJump failed: {ex.Message}", ex);
                SendAiChat("Jump system error. Check Torch log.");
            }
        }

        // =====================================================================
        // AI Basic Block Follow        // =====================================================================
        // AI Basic Block Follow
        // Uses IMyBasicMissionFollowPlayer / IMyBasicMissionFollowHome
        // to leverage SE's native AI flight system for smooth following.
        // Block must be named ARIA AI on the inhabited grid.
        // =====================================================================

        private void ExecuteAIFollow(string gridName, string mode, string targetName, float distM)
        {
            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName) continue;

                try
                {
                    // Find ARIA AI block
                    Sandbox.ModAPI.IMyBasicMissionBlock aiBlock = null;
                    foreach (var block in grid.GetFatBlocks())
                    {
                        var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                        if (t?.CustomName?.Trim() != ARIA_AI_BLOCK) continue;
                        aiBlock = block as Sandbox.ModAPI.IMyBasicMissionBlock;
                        if (aiBlock != null) break;
                    }

                    if (aiBlock == null)
                    {
                        SendAiChat($"No AI Basic block named '{ARIA_AI_BLOCK}' found. Build one to enable native following.");
                        AriaLog.Warn($"AIFollow: No '{ARIA_AI_BLOCK}' block on '{grid.DisplayName}'");
                        return;
                    }

                    // Enable the block and AI Behavior
                    var funcBlock = aiBlock as Sandbox.ModAPI.IMyFunctionalBlock;
                    if (funcBlock != null) funcBlock.Enabled = true;
                    var termBlock2 = aiBlock as Sandbox.ModAPI.IMyTerminalBlock;
                    if (termBlock2 != null)
                    {
                        if (((Sandbox.ModAPI.Ingame.IMyTerminalBlock)termBlock2).HasAction("Behaviour_OnOff_On"))
                            ((termBlock2) as Sandbox.ModAPI.IMyTerminalBlock)?.ApplyAction("Behaviour_OnOff_On");
                        else if (((Sandbox.ModAPI.Ingame.IMyTerminalBlock)termBlock2).HasAction("AIBehavior_On"))
                            (termBlock2 as Sandbox.ModAPI.IMyTerminalBlock)?.ApplyAction("AIBehavior_On");
                        else
                        {
                            // Log available actions once to find correct name
                            var acts = new System.Collections.Generic.List<Sandbox.ModAPI.Interfaces.ITerminalAction>();
                            termBlock2.GetActions(acts);
                            AriaLog.Info($"AI block actions: {string.Join(", ", acts.ConvertAll(a => a.Id))}");
                        }
                    }

                    if (mode == "player")
                    {
                        // Follow a player by identity ID
                        Sandbox.ModAPI.IMyBasicMissionFollowPlayer followComp;
                        if (!aiBlock.Components.TryGet(out followComp))
                        {
                            AriaLog.Warn("AIFollow: IMyBasicMissionFollowPlayer component not found.");
                            return;
                        }

                        // Find player identity ID by name
                        long identityId = 0;
                        var session = MySession.Static;
                        if (session?.Players != null)
                        {
                            foreach (var player in session.Players.GetOnlinePlayers())
                            {
                                if (player.IsBot) continue;
                                if (player.DisplayName.ToLower().Contains(targetName.ToLower()))
                                {
                                    identityId = player.Identity?.IdentityId ?? 0;
                                    break;
                                }
                            }
                        }

                        if (identityId == 0)
                        {
                            SendAiChat($"Player '{targetName}' not found online.");
                            return;
                        }

                        followComp.FollowDistance = distM;
                        followComp.FollowPlayer(identityId);

                        _aiFollowActive  = true;
                        _aiFollowMode    = "player";
                        _aiFollowTarget  = targetName;
                        _aiFollowDist    = distM;

                        AriaLog.Info($"AIFollow: Following player '{targetName}' (id={identityId}) at {distM}m");
                        SendAiChat($"Following {targetName} at {distM:F0}m.");
                        EnqueueAll("/command_result",
                            $"{{\"grid_name\":\"{Esc(grid.DisplayName)}\",\"command\":\"AI_FOLLOW\",\"status\":\"executed\"}}");
                    }
                    else
                    {
                        // Follow a grid -- use FollowHome with live GPS update
                        // First tick: create GPS, subsequent ticks update it via UpdateAIFollowGps
                        _aiFollowActive = true;
                        _aiFollowMode   = "grid";
                        _aiFollowTarget = targetName;
                        _aiFollowDist   = distM;

                        // Set follow range on FollowHome component
                        Sandbox.ModAPI.IMyBasicMissionFollowHome homeComp;
                        if (aiBlock.Components.TryGet(out homeComp))
                        {
                            homeComp.MinRange = Math.Max(5f, distM - 20f);
                            homeComp.MaxRange = distM + 20f;
                        }

                        // Do first GPS update immediately
                        UpdateAIFollowGps();

                        AriaLog.Info($"AIFollow: Following grid '{targetName}' at {distM}m");
                        SendAiChat($"Following {targetName} at {distM:F0}m.");
                        EnqueueAll("/command_result",
                            $"{{\"grid_name\":\"{Esc(grid.DisplayName)}\",\"command\":\"AI_FOLLOW\",\"status\":\"executed\"}}");
                    }
                    return;
                }
                catch (Exception ex)
                {
                    AriaLog.Error("ExecuteAIFollow failed.", ex);
                }
            }
        }

        private void StopAIFollow(string gridName)
        {
            _aiFollowActive = false;

            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName) continue;

                try
                {
                    foreach (var block in grid.GetFatBlocks())
                    {
                        var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                        if (t?.CustomName?.Trim() != ARIA_AI_BLOCK) continue;
                        var aiBlock = block as Sandbox.ModAPI.IMyBasicMissionBlock;
                        if (aiBlock == null) continue;

                        Sandbox.ModAPI.IMyBasicMissionFollowPlayer followComp;
                        if (aiBlock.Components.TryGet(out followComp))
                            followComp.StopFollowing();

                        // Disable AI Behavior and the block entirely
                        var termBlock = aiBlock as Sandbox.ModAPI.IMyTerminalBlock;
                        if (termBlock != null)
                        {
                            // Toggle AI Behavior off
                            if (((Sandbox.ModAPI.Ingame.IMyTerminalBlock)termBlock).HasAction("Behaviour_OnOff_Off"))
                                ((termBlock) as Sandbox.ModAPI.IMyTerminalBlock)?.ApplyAction("Behaviour_OnOff_Off");
                            else if (((Sandbox.ModAPI.Ingame.IMyTerminalBlock)termBlock).HasAction("AIBehavior_Off"))
                                ((termBlock) as Sandbox.ModAPI.IMyTerminalBlock)?.ApplyAction("AIBehavior_Off");
                        }
                        var funcBlock = aiBlock as Sandbox.ModAPI.IMyFunctionalBlock;
                        if (funcBlock != null) funcBlock.Enabled = false;

                        AriaLog.Info($"AIFollow: Stopped following '{_aiFollowTarget}'");
                        SendAiChat("Follow disengaged.");
                        break;
                    }
                }
                catch (Exception ex) { AriaLog.Error("StopAIFollow failed.", ex); }
                return;
            }
        }

        private void UpdateAIFollowGps()
        {
            if (!_aiFollowActive || _aiFollowMode != "grid") return;

            // Find target grid position
            Vector3D targetPos = Vector3D.Zero;
            bool found = false;
            string tLow = _aiFollowTarget.ToLower();

            foreach (var entity in MyEntities.GetEntities().ToList())
            {
                var tGrid = entity as MyCubeGrid;
                if (tGrid == null || tGrid.MarkedForClose || tGrid.IsPreview) continue;
                if (!tGrid.DisplayName.ToLower().Contains(tLow)) continue;
                if (tGrid.Physics == null) continue;
                targetPos = tGrid.Physics.CenterOfMassWorld;
                found = true;
                break;
            }

            if (!found) return;

            // Find ARIA AI block and update GPS target
            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                foreach (var block in grid.GetFatBlocks())
                {
                    var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                    if (t?.CustomName?.Trim() != ARIA_AI_BLOCK) continue;
                    var aiBlock = block as Sandbox.ModAPI.IMyBasicMissionBlock;
                    if (aiBlock == null) continue;

                    try
                    {
                        Sandbox.ModAPI.IMyBasicMissionFollowHome homeComp;
                        if (aiBlock.Components.TryGet(out homeComp))
                        {
                            // Create a GPS point at target position and update
                            var gps = MyAPIGateway.Session.GPS.Create(
                                $"ARIA Follow: {_aiFollowTarget}",
                                "", targetPos, true, true);
                            homeComp.GoHome(gps);
                        }
                    }
                    catch (Exception ex) { AriaLog.Warn($"UpdateAIFollowGps: {ex.Message}"); }
                    return;
                }
            }
        }

        private void SetDampeners(string gridName, bool enable)
        {
            bool applied = false;

            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                if (!string.IsNullOrEmpty(gridName) && grid.DisplayName != gridName) continue;

                try
                {
                    int blockCount = 0;
                    int rcCount = 0;

                    // Try RC block first (named "ARIA Core" is our RC block)
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        blockCount++;
                        var rc = block as IMyRemoteControl;
                        if (rc == null) continue;
                        rcCount++;

                        try { rc.DampenersOverride = enable; } catch { }
                        applied = true;
                        AriaLog.Info($"Gate1: Dampeners {(enable ? "ON" : "OFF")} via RC '{rc.CustomName}' on '{grid.DisplayName}'");
                        break;
                    }

                    if (rcCount == 0)
                        AriaLog.Warn($"SetDampeners: 0 RC blocks found in {blockCount} fat blocks on '{grid.DisplayName}'");

                    // Fallback -- set on all cockpit blocks too
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        var cockpit = block as Sandbox.ModAPI.IMyShipController;
                        if (cockpit == null) continue;
                        if (block is IMyRemoteControl) continue;

                        try { cockpit.DampenersOverride = enable; } catch { }
                        applied = true;
                    }

                    if (applied)
                    {
                        var resultJson =
                            "{\"grid_name\":\"" + Esc(grid.DisplayName ?? "") + "\"," +
                            "\"command\":\"DAMPENERS:" + (enable ? "on" : "off") + "\"," +
                            "\"status\":\"executed\"}";
                        EnqueueAll("/command_result", resultJson);
                    }
                }
                catch (Exception ex) { AriaLog.Error("Dampener set failed.", ex); }
            }

            if (!applied)
                AriaLog.Warn($"Gate1: No RC or cockpit blocks found for dampeners on '{gridName}'");
        }

        // Drain alert queue and broadcast to in-game chat
        private void DrainAlerts()
        {
            foreach (var ctx in _bridges.ToList())
            {
                // Canonical sender name for this bridge -- always use ActivationName
                string ctxSender = char.ToUpperInvariant(ctx.ActivationName[0])
                                 + ctx.ActivationName.Substring(1);
                string msg;
                while (ctx.Inbound.TryDequeue(out msg))
                {
                    // Format: "bridgename|alert text" -- strip prefix, always use ctxSender
                    string alertText = msg;
                    var pipe = msg.IndexOf('|');
                    if (pipe >= 0)
                        alertText = msg.Substring(pipe + 1);

                    AriaLog.Info($"{ctxSender} ALERT: {alertText}");
                    // Send only to players in this bridge's allowed factions
                    SendToFaction(ctx, ctxSender, alertText);
                }
            }
        }

        // Post local player's faction to bridge so it can use it for context
        private void PostFactionUpdate()
        {
            try
            {
                var session = MySession.Static;
                if (session?.Players == null) return;

                var localSteamId = MyAPIGateway.Multiplayer?.MyId ?? 0UL;
                if (localSteamId == 0) return;

                // Get local player identity
                long identityId = 0;
                string playerName = MyAPIGateway.Multiplayer?.MyName ?? "";
                foreach (var p in session.Players.GetOnlinePlayers())
                    if (p.Id.SteamId == localSteamId)
                    { identityId = p.Identity?.IdentityId ?? 0; break; }

                if (identityId == 0) return;

                var faction = session.Factions?.TryGetPlayerFaction(identityId);
                var factionTag = faction?.Tag ?? "";

                var json = "{\"steam_id\":\"" + localSteamId + "\"," +
                           "\"player_name\":\"" + Esc(playerName) + "\"," +
                           "\"faction_tag\":\"" + Esc(factionTag) + "\"," +
                           "\"is_local\":true}";

                foreach (var bridge in _bridges.ToList())
                    if (bridge.BridgeUp)
                        _ = PostToUrlAsync(bridge.Url + "/faction_update", json);
            }
            catch (Exception ex)
            {
                AriaLog.Debug("PostFactionUpdate error: " + ex.Message);
            }
        }

        // Send a chat message only to players in the bridge's allowed factions
        private void SendToFaction(BridgeContext ctx, string sender, string message)
        {
            try
            {
                var session = MySession.Static;
                if (session?.Players == null) { _chat?.SendMessageAsOther(sender, message, Color.Cyan); return; }

                if (ctx.AllowedFactions.Count > 0)
                {
                    bool sent = false;
                    foreach (var player in session.Players.GetOnlinePlayers())
                    {
                        if (player.IsBot) continue;
                        try
                        {
                            var pf = session.Factions?.TryGetPlayerFaction(
                                player.Identity?.IdentityId ?? 0);
                            if (pf != null && ctx.AllowedFactions.Contains(pf.Tag))
                            {
                                _chat?.SendMessageAsOther(sender, message, Color.Cyan, player.Id.SteamId);
                                sent = true;
                                AriaLog.Info($"{sender} -> {player.DisplayName} (faction {pf.Tag}): {message}");
                            }
                        }
                        catch { }
                    }
                    if (!sent)
                    {
                        AriaLog.Warn($"{sender}: No faction members online for [{string.Join(",", ctx.AllowedFactions)}] -- broadcasting.");
                        foreach (var player in session.Players.GetOnlinePlayers())
                        {
                            if (player.IsBot) continue;
                            _chat?.SendMessageAsOther(sender, message, Color.Cyan, player.Id.SteamId);
                        }
                    }
                }
                else
                {
                    foreach (var player in session.Players.GetOnlinePlayers())
                    {
                        if (player.IsBot) continue;
                        _chat?.SendMessageAsOther(sender, message, Color.Cyan, player.Id.SteamId);
                    }
                }
            }
            catch (Exception ex)
            {
                AriaLog.Debug($"SendToFaction error: {ex.Message}");
                _chat?.SendMessageAsOther(sender, message, Color.Cyan);
            }
        }

        // Scan for grids containing any configured AI Core block
        private void ScanForCores()
        {
            // Build set of all core block names across all bridges
            var coreNames = new HashSet<string>(_bridges.Select(c => c.CoreBlockName));
            if (coreNames.Count == 0) coreNames.Add("ARIA Core"); // fallback

            var found = new List<MyCubeGrid>();
            try
            {
                foreach (var entity in MyEntities.GetEntities().ToList())
                {
                    var grid = entity as MyCubeGrid;
                    if (grid == null || grid.MarkedForClose) continue;
                    if (grid.IsPreview) continue;

                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                        if (t?.CustomName != null && coreNames.Contains(t.CustomName.Trim()))
                        {
                            if (!found.Contains(grid)) found.Add(grid);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { AriaLog.Error("Core scan error.", ex); return; }

            if (found.Count != _ariaGrids.Count)
            {
                AriaLog.Info($"ARIA: {found.Count} AI grid(s) found.");
                foreach (var g in found)
                    AriaLog.Info($"  '{g.DisplayName}' ({g.GridSizeEnum}, {g.BlocksCount} blocks)");
                if (found.Count == 0)
                    AriaLog.Warn($"No AI Core blocks found. Looking for: {string.Join(", ", coreNames)}");
            }
            _ariaGrids = found;
        }

        // Find ARIA PB block and mark grid as inhabited
        private void ScanForPb()
        {
            // Re-announce inhabited grids to bridge after session loads
            // Per-bridge: each bridge re-announces only its own grids
            if (_reannounceOnNextScan && _bridges.Any(c => c.PbMap.Count > 0))
            {
                _reannounceOnNextScan = false;
                foreach (var g in _ariaGrids)
                {
                    if (g == null || g.MarkedForClose) continue;
                    var ownerCtx = BridgeForGridId(g.EntityId);
                    if (ownerCtx == null) continue;

                    AriaLog.Info($"ARIA: Re-announcing '{g.DisplayName}' to bridge '{ownerCtx.Name}' after session load.");
                    ownerCtx.Outbound.Enqueue("/aria_inhabit", "{\"grid_name\":\"" + Esc(g.DisplayName) + "\"," +
                        "\"inhabited\":true,\"reason\":\"session_load\"}");

                    // Refresh presence indicator and emotion images on session load
                    SetPresenceIndicator(g, true);
                }
            }

            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                long gridId = grid.EntityId;

                // Resolve which bridge owns (or should own) this grid by matching Core block name
                BridgeContext gridBridgeCtx = null;
                try
                {
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                        gridBridgeCtx = _bridges.FirstOrDefault(c => c.CoreBlockName == t?.CustomName?.Trim());
                        if (gridBridgeCtx != null) break;
                    }
                }
                catch { }
                var ownerBridge = gridBridgeCtx;
                if (ownerBridge == null) continue; // no Core block on this grid matches any bridge -- skip

                // ── Grid ownership conflict check ─────────────────────────────
                // If a DIFFERENT bridge already owns this grid, skip it.
                // Only the owning bridge can release it (via uninhabit).
                BridgeContext conflictingBridge = null;
                lock (_bridgeLock)
                    conflictingBridge = _bridges.FirstOrDefault(b =>
                        b != ownerBridge &&
                        b.OwnedGridIds.Contains(gridId));

                if (conflictingBridge != null)
                {
                    AriaLog.Debug($"ARIA: Grid '{grid.DisplayName}' owned by bridge '{conflictingBridge.Name}' -- " +
                                  $"bridge '{ownerBridge.Name}' cannot claim it.");
                    continue;
                }

                // If already mapped in this bridge, verify PB still exists
                if (ownerBridge.PbMap.TryGetValue(gridId, out long existingId))
                {
                    bool alive = false;
                    bool enabled = false;
                    try
                    {
                        foreach (var block in grid.GetFatBlocks())
                        {
                            if (block?.EntityId == existingId && !block.MarkedForClose)
                            {
                                alive = true;
                                var funcBlock = block as Sandbox.ModAPI.IMyFunctionalBlock;
                                enabled = funcBlock?.Enabled ?? true;
                                break;
                            }
                        }
                    }
                    catch { }

                    // PB disabled by player (physical switch) -- uninhabit cleanly
                    if (alive && !enabled)
                    {
                        ownerBridge.PbMap.Remove(gridId);
                        lock (ownerBridge.OwnedGridIds) ownerBridge.OwnedGridIds.Remove(gridId);
                        SetPresenceIndicator(grid, false);
                        // Clear ownership from PB CustomData so another node can claim it
                        try
                        {
                            var pbBlock = grid.GetFatBlocks()
                                .OfType<Sandbox.ModAPI.IMyTerminalBlock>()
                                .FirstOrDefault(b => b.CustomName?.Trim() == ownerBridge.PbBlockName);
                            if (pbBlock != null && pbBlock.CustomData.Contains("NodeId:"))
                                pbBlock.CustomData = "ARIA_RELEASED";
                        }
                        catch { }
                        AriaLog.Info($"ARIA: Terminal disabled on '{grid.DisplayName}' -- uninhabiting.");
                        ownerBridge.Outbound.Enqueue("/aria_inhabit", "{\"grid_name\":\"" + Esc(grid.DisplayName) + "\"," +
                            "\"inhabited\":false,\"reason\":\"terminal_disabled\"}");
                        SendAiChat($"Terminal offline aboard {grid.DisplayName}.");
                        continue;
                    }

                    if (!alive)
                    {
                        ownerBridge.PbMap.Remove(gridId);
                        lock (ownerBridge.OwnedGridIds) ownerBridge.OwnedGridIds.Remove(gridId);
                        AriaLog.Warn($"ARIA: PB lost on '{grid.DisplayName}'.");
                        WriteInstructionsLcd(grid, new List<string>
                        {
                            $"Programmable Block '{ownerBridge.PbBlockName}' was destroyed or removed.",
                            $"Rebuild and rename a PB to '{ownerBridge.PbBlockName}' to restore full function."
                        });
                        ownerBridge.Outbound.Enqueue("/aria_inhabit", $"{{\"grid_name\":\"{Esc(grid.DisplayName)}\",\"inhabited\":false,\"reason\":\"pb_lost\"}}");
                        SendAiChat($"I have lost my terminal aboard {grid.DisplayName}. " +
                            $"Ensure '{ownerBridge.PbBlockName}' is intact.");
                    }
                    continue;
                }

                // Only attempt new inhabitation if this bridge isn't already on a ship
                // Per-bridge: ARIA can inhabit Equinox while NOVA inhabits EVI home base simultaneously
                if (ownerBridge.PbMap.Count > 0)
                    continue;

                // Search for this bridge's PB -- with smart auto-rename
                Sandbox.ModAPI.IMyProgrammableBlock pb = null;
                var otherPbs = new List<Sandbox.ModAPI.IMyProgrammableBlock>();

                try
                {
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        var candidate = block as Sandbox.ModAPI.IMyProgrammableBlock;
                        if (candidate == null) continue;
                        var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                        var blockName = t?.CustomName?.Trim() ?? "";

                        if (blockName == ownerBridge.PbBlockName)
                        {
                            // Check CustomData NodeId -- if set to a DIFFERENT node, skip this PB
                            // This prevents LORA's "ARIA PB" being claimed by UBER's scanner
                            var cd = t?.CustomData ?? "";
                            if (cd.Contains("NodeId:"))
                            {
                                var nodeIdMatch = System.Text.RegularExpressions.Regex.Match(
                                    cd, @"NodeId:([^\r\n]+)");
                                if (nodeIdMatch.Success)
                                {
                                    var pbNodeId = nodeIdMatch.Groups[1].Value.Trim();
                                    if (!string.IsNullOrEmpty(pbNodeId) &&
                                        !string.IsNullOrEmpty(ownerBridge.NodeId) &&
                                        pbNodeId != ownerBridge.NodeId)
                                    {
                                        AriaLog.Debug($"ARIA: PB '{blockName}' on '{grid.DisplayName}' " +
                                            $"belongs to node {pbNodeId.Substring(0, 8)} -- skipping for '{ownerBridge.Name}'.");
                                        continue; // belongs to a different node
                                    }
                                }
                            }
                            pb = candidate;
                            break;  // already named correctly
                        }
                        else
                        {
                            otherPbs.Add(candidate);
                        }
                    }
                }
                catch (Exception ex) { AriaLog.Error("PB scan error.", ex); continue; }

                // Auto-rename logic if no correctly named PB found
                // Don't inhabit a disabled PB
                if (pb != null)
                {
                    var pbFunc = pb as Sandbox.ModAPI.IMyFunctionalBlock;
                    if (pbFunc != null && !pbFunc.Enabled)
                    {
                        AriaLog.Debug($"ARIA: '{ownerBridge.PbBlockName}' on '{grid.DisplayName}' is disabled -- skipping.");
                        continue;
                    }
                }

                if (pb == null)
                {
                    if (otherPbs.Count == 0)
                    {
                        // No PBs at all on this grid
                        AriaLog.Info($"ARIA: No programmable blocks found on '{grid.DisplayName}'. Install one to enable full ship intelligence.");
                        SendAiChat($"No programmable block found aboard {grid.DisplayName}. Install one for full sensor integration.");
                        continue;
                    }
                    else if (otherPbs.Count == 1)
                    {
                        // Exactly one PB -- auto-rename it to this bridge's PB name
                        pb = otherPbs[0];
                        var pbT2 = pb as Sandbox.ModAPI.IMyTerminalBlock;
                        var oldName = pbT2?.CustomName ?? "Programmable Block";
                        if (pbT2 != null) pbT2.CustomName = ownerBridge.PbBlockName;
                        AriaLog.Info($"ARIA: Auto-renamed '{oldName}' to '{ownerBridge.PbBlockName}' on '{grid.DisplayName}'.");
                        SendAiChat($"I have designated the programmable block aboard {grid.DisplayName} as my sensor terminal.");
                    }
                    else
                    {
                        // Multiple PBs found -- only disable ones named after AI bridges
                        // Leave AutoLCD, Whip's scripts, doors scripts etc. untouched
                        var aiPbNames = new System.Collections.Generic.HashSet<string>(
                            _bridges.Select(c => c.PbBlockName),
                            StringComparer.OrdinalIgnoreCase);

                        AriaLog.Info($"ARIA: Multiple PBs on '{grid.DisplayName}' -- disabling duplicate AI PBs.");
                        foreach (var extraPb in otherPbs)
                        {
                            var extraT    = extraPb as Sandbox.ModAPI.IMyTerminalBlock;
                            var extraFunc = extraPb as Sandbox.ModAPI.IMyFunctionalBlock;
                            var extraName = extraT?.CustomName?.Trim() ?? "";
                            if (aiPbNames.Contains(extraName) && extraFunc != null && extraFunc.Enabled)
                            {
                                extraFunc.Enabled = false;
                                AriaLog.Info($"ARIA: Disabled duplicate AI PB '{extraName}' on '{grid.DisplayName}'.");
                                SendAiChat($"Duplicate terminal '{extraName}' found aboard {grid.DisplayName} -- disabled. Re-enable the one you wish me to use.");
                            }
                        }
                        // Use the correctly named one if found, otherwise first AI-named one
                        if (pb == null)
                        {
                            pb = otherPbs.FirstOrDefault(p =>
                                aiPbNames.Contains((p as Sandbox.ModAPI.IMyTerminalBlock)?.CustomName?.Trim() ?? ""));
                        }
                        if (pb == null) continue;
                    }
                }

                var pbT = pb as Sandbox.ModAPI.IMyTerminalBlock;
                if (pbT != null)
                    pbT.CustomData = $"ARIA_INSTALLED\nVersion:0.6.0-r1\nGrid:{grid.DisplayName}\nNodeId:{ownerBridge.NodeId}\nBridge:{ownerBridge.Name}";

                // Auto-install PB script if file is loaded and PB is empty
                bool hasScript = false;
                try
                {
                    var scriptContent = (pb as Sandbox.ModAPI.IMyProgrammableBlock)?.ProgramData ?? "";
                    hasScript = scriptContent.Length > 100; // non-trivial script already loaded
                }
                catch { }

                if (!hasScript && !string.IsNullOrEmpty(_pbScriptContent))
                {
                    try
                    {
                        var pbInst = pb as Sandbox.ModAPI.IMyProgrammableBlock;
                        if (pbInst != null)
                        {
                            pbInst.ProgramData = _pbScriptContent;
                            AriaLog.Info($"ARIA: Auto-installed PB script on '{grid.DisplayName}'.");
                            hasScript = true;
                        }
                    }
                    catch (Exception scriptEx)
                    {
                        AriaLog.Warn($"ARIA: Could not auto-install PB script: {scriptEx.Message}");
                    }
                }

                if (!hasScript)
                {
                    SendAiChat(
                        $"Terminal acquired aboard {grid.DisplayName}. " +
                        "Sensor script not loaded. " +
                        "Please paste the ARIA PB Script manually.");
                    WriteInstructionsLcd(grid, new List<string>
                    {
                        $"{ownerBridge.PbBlockName} found but sensor script not loaded.",
                        "Steps to install:",
                        $"1. Open '{ownerBridge.PbBlockName}' terminal",
                        "2. Click 'Edit'",
                        "3. Delete existing code",
                        "4. Paste the ARIA PB Script content",
                        "5. Click 'Check Code' then 'OK'",
                    });
                }

                // Auto-install Emotion PB script
                if (!string.IsNullOrEmpty(_emotionPbScriptContent))
                {
                    var emotionPbName = ownerBridge.EmotionPbBlockName;
                    Sandbox.ModAPI.IMyProgrammableBlock emotionPb = null;
                    foreach (var fb in grid.GetFatBlocks())
                    {
                        var candidate = fb as Sandbox.ModAPI.IMyProgrammableBlock;
                        if (candidate == null) continue;
                        var tbName = (fb as Sandbox.ModAPI.IMyTerminalBlock)?.CustomName?.Trim() ?? "";
                        if (tbName == emotionPbName) { emotionPb = candidate; break; }
                    }
                    if (emotionPb != null)
                    {
                        try
                        {
                            var scriptContent = emotionPb.ProgramData ?? "";
                            if (scriptContent.Length < 100)
                            {
                                emotionPb.ProgramData = _emotionPbScriptContent;
                                AriaLog.Info($"ARIA: Auto-installed Emotion PB script on '{grid.DisplayName}'.");
                            }
                        }
                        catch (Exception ex) { AriaLog.Debug($"Emotion PB install error: {ex.Message}"); }
                    }
                }

                // ownerBridge is already resolved above -- use it directly
                // (no need to re-scan blocks a second time)
                var bridgeForGrid = ownerBridge;
                var aiName = char.ToUpperInvariant(bridgeForGrid.ActivationName[0])
                             + bridgeForGrid.ActivationName.Substring(1);

                // Register inhabitation in this bridge's PbMap and OwnedGridIds
                bridgeForGrid.PbMap[gridId] = pb.EntityId;
                lock (bridgeForGrid.OwnedGridIds)
                    bridgeForGrid.OwnedGridIds.Add(gridId);

                // Each bridge inhabits one ship at a time -- evict any previous ship for THIS bridge only
                foreach (var prevGrid in _ariaGrids)
                {
                    if (prevGrid == null || prevGrid.EntityId == gridId) continue;
                    if (!bridgeForGrid.PbMap.ContainsKey(prevGrid.EntityId)) continue;

                    AriaLog.Info($"ARIA: [{aiName}] leaving '{prevGrid.DisplayName}' to inhabit '{grid.DisplayName}'.");
                    SetPresenceIndicator(prevGrid, false);
                    bridgeForGrid.Outbound.Enqueue("/aria_inhabit", "{\"grid_name\":\"" + Esc(prevGrid.DisplayName) + "\"," +
                        "\"inhabited\":false,\"reason\":\"moved_to_new_ship\"}");
                    bridgeForGrid.PbMap.Remove(prevGrid.EntityId);
                    lock (bridgeForGrid.OwnedGridIds)
                        bridgeForGrid.OwnedGridIds.Remove(prevGrid.EntityId);
                }

                AriaLog.Info($"{aiName}: Inhabiting '{grid.DisplayName}' via '{bridgeForGrid.PbBlockName}'.");
                SetPresenceIndicator(grid, true);
                WriteInstructionsLcd(grid, null);

                bridgeForGrid.Outbound.Enqueue("/aria_inhabit", $"{{\"grid_name\":\"{Esc(grid.DisplayName)}\",\"grid_id\":\"{gridId}\",\"inhabited\":true}}");

                SendToFaction(bridgeForGrid, aiName,
                    $"Terminal acquired aboard {grid.DisplayName}. Sensor array online.");

                // Trigger immediate scan and LCD refresh so displays populate on the new ship
                _immediateScanPending = true;
                FetchAndWriteScanLcds();
                _deferredScanLcdFetchTick = _tick + 300;

                // Tell PB to run
                try { pb.Run("FORCE_UPDATE", UpdateType.Script); } catch { }
                // Continue scanning -- multiple ships can be inhabited (one per bridge)
            }
        }

        // Autopilot altitude safety monitor
        // Checks altitude every second while any RC block has autopilot active
        // Aborts autopilot and alerts crew if altitude drops below safe threshold in gravity
        private void CheckAutopilotAltitude()
        {
            // Don't check altitude immediately after engaging -- ship needs time to lift off
            if (_tick - _autopilotStartTick < AUTOPILOT_ALT_CHECK_DELAY) return;
            // Safety override -- bypass terrain correction entirely
            if (_autopilotSafetyOverride) return;

            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;

                IMyRemoteControl activeRc = null;
                foreach (var block in grid.GetFatBlocks())
                {
                    var rc = block as IMyRemoteControl;
                    if (rc != null && rc.IsAutoPilotEnabled) { activeRc = rc; break; }
                }
                if (activeRc == null) { _autopilotAltWarnSent = false; continue; }

                // Check gravity -- only relevant near planets
                var pos = activeRc.GetPosition();
                float grav = 0f;
                try { grav = (float)MyGravityProviderSystem.CalculateNaturalGravityInPoint(pos).Length(); }
                catch { }
                if (grav < 0.05f) continue;  // no significant gravity -- skip

                // Get altitude from RC block (distance to surface)
                double altitude = double.MaxValue;
                try
                {
                    double alt;
                    if (activeRc.TryGetPlanetElevation(MyPlanetElevation.Sealevel, out alt))
                        altitude = alt;
                }
                catch { }

                var ownerCtx = BridgeForGridId(grid.EntityId);

                // Use grid-size-aware thresholds -- drones fly much lower than large ships
                bool isLargeGrid = grid.GridSizeEnum == VRage.Game.MyCubeSize.Large;
                float minAlt  = isLargeGrid ? AUTOPILOT_MIN_ALTITUDE_LARGE : AUTOPILOT_MIN_ALTITUDE_SMALL;
                float warnAlt = isLargeGrid ? AUTOPILOT_WARN_ALTITUDE_LARGE : AUTOPILOT_WARN_ALTITUDE_SMALL;
                float safeAlt = isLargeGrid ? 200f : 40f;  // target altitude after correction

                if (altitude < minAlt)
                {
                    // Don't abort -- adjust course upward to safe altitude
                    // Find closest planet and get surface normal to lift correctly
                    try
                    {
                        MyPlanet closestPlanet = null;
                        float closestDist = float.MaxValue;
                        foreach (var entity in MyEntities.GetEntities())
                        {
                            var planet = entity as MyPlanet;
                            if (planet == null || planet.MarkedForClose) continue;
                            float d = (float)Vector3D.Distance(pos, planet.PositionComp.GetPosition());
                            if (d < closestDist) { closestDist = d; closestPlanet = planet; }
                        }

                        if (closestPlanet != null)
                        {
                            // Get gravity up direction (away from planet center)
                            var gravDir = Vector3D.Normalize(pos - closestPlanet.PositionComp.GetPosition());
                            // Get surface point below ship
                            var surfacePoint = closestPlanet.GetClosestSurfacePointGlobal(pos);
                            // New waypoint = surface point + up * safeAlt
                            var correctedPos = surfacePoint + gravDir * safeAlt;

                            activeRc.ClearWaypoints();
                            activeRc.AddWaypoint(correctedPos, "ARIA Altitude Correction");

                            var msg = $"Terrain proximity alert — adjusting course. Altitude {altitude:F0}m.";
                            AriaLog.Info($"ARIA: {msg} on '{grid.DisplayName}' -> corrected to {safeAlt}m");
                            if (ownerCtx != null)
                                ownerCtx.Outbound.Enqueue("/queue_alert", "{\"message\":\"" + Esc(msg) + "\",\"emotion\":\"neutral\"}");
                            _autopilotAltWarnSent = true;
                        }
                        else
                        {
                            // No planet found -- just warn
                            var msg = $"Altitude warning: {altitude:F0}m. No terrain data available.";
                            if (ownerCtx != null)
                                ownerCtx.Outbound.Enqueue("/queue_alert", "{\"message\":\"" + Esc(msg) + "\",\"emotion\":\"neutral\"}");
                        }
                    }
                    catch (Exception altEx)
                    {
                        AriaLog.Warn($"Altitude correction error: {altEx.Message}");
                    }
                }
                else if (altitude < warnAlt && !_autopilotAltWarnSent)
                {
                    // WARNING -- getting low
                    var msg = $"Altitude warning: {altitude:F0}m. Approaching terrain.";
                    AriaLog.Info($"ARIA: {msg} on '{grid.DisplayName}'");
                    if (ownerCtx != null)
                        ownerCtx.Outbound.Enqueue("/queue_alert", "{\"message\":\"" + Esc(msg) + "\",\"emotion\":\"neutral\"}");
                    _autopilotAltWarnSent = true;
                }
                else if (altitude > warnAlt)
                {
                    _autopilotAltWarnSent = false;  // reset warning once safe again
                }
            }
        }

        // Collect ship block data directly and post to /ship_state
        // Replaces the old LCD relay pattern -- plugin reads blocks directly, no ARIA DATA LCD needed
        private void CollectShipState()
        {
            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                if (BridgeForGridId(grid.EntityId) == null) continue;

                try
                {
                    var sb = new System.Text.StringBuilder();

                    // Collect all block types in one pass
                    int thrustersTotal = 0, thrustersFunctional = 0;
                    int gyrosTotal = 0, gyrosFunctional = 0;
                    int reactorCount = 0;
                    float reactorOutput = 0f, reactorMax = 0f;
                    int h2Tanks = 0; float h2Pct = -1f;
                    int o2Tanks = 0; float o2Pct = -1f;
                    float batteryPct = -1f;
                    float thrustUp = 0f, thrustDown = 0f, thrustFwd = 0f;
                    float thrustBack = 0f, thrustLeft = 0f, thrustRight = 0f;
                    int doors = 0, doorsOpen = 0;
                    int landingGear = 0, landingGearLocked = 0;
                    int connectors = 0, connectorsLocked = 0;
                    int turrets = 0, turretsEnabled = 0;
                    int drills = 0, welders = 0, grinders = 0;
                    int jumpDrives = 0; float jumpCharge = -1f; double jumpChargeSum = 0;
                    int warheads = 0, cameras = 0, sensors = 0;
                    int pistons = 0, rotors = 0, airVents = 0, lights = 0, antennas = 0;
                    float shipMass = 0f, shipSpeed = 0f, gravMag = 0f, twr = 0f;
                    int bpTotal = 0, bpMissing = 0; float integrityPct = 100f;

                    // Grid orientation for thrust direction
                    var fwd   = grid.WorldMatrix.Forward;
                    var up    = grid.WorldMatrix.Up;
                    var right = grid.WorldMatrix.Right;

                    // Battery totals
                    float batStored = 0f, batMax = 0f;

                    IMyShipController controller = null;

                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;

                        var thrust = block as Sandbox.ModAPI.IMyThrust;
                        if (thrust != null)
                        {
                            thrustersTotal++;
                            if (thrust.IsFunctional) thrustersFunctional++;
                            if (thrust.IsWorking)
                            {
                                var dir = -thrust.WorldMatrix.Forward;
                                float t = thrust.MaxEffectiveThrust;
                                if      (VRageMath.Vector3D.Dot(dir, up)    >  0.7) thrustUp    += t;
                                else if (VRageMath.Vector3D.Dot(dir, up)    < -0.7) thrustDown  += t;
                                else if (VRageMath.Vector3D.Dot(dir, fwd)   >  0.7) thrustFwd   += t;
                                else if (VRageMath.Vector3D.Dot(dir, fwd)   < -0.7) thrustBack  += t;
                                else if (VRageMath.Vector3D.Dot(dir, right) >  0.7) thrustRight += t;
                                else                                                  thrustLeft  += t;
                            }
                            continue;
                        }

                        var gyro = block as Sandbox.ModAPI.IMyGyro;
                        if (gyro != null)
                        {
                            gyrosTotal++;
                            if (gyro.IsWorking) gyrosFunctional++;
                            continue;
                        }

                        var reactor = block as Sandbox.ModAPI.IMyReactor;
                        if (reactor != null)
                        {
                            reactorCount++;
                            reactorOutput += reactor.CurrentOutput;
                            reactorMax    += reactor.MaxOutput;
                            continue;
                        }

                        var battery = block as Sandbox.ModAPI.IMyBatteryBlock;
                        if (battery != null)
                        {
                            batStored += battery.CurrentStoredPower;
                            batMax    += battery.MaxStoredPower;
                            continue;
                        }

                        var gasTank = block as Sandbox.ModAPI.IMyGasTank;
                        if (gasTank != null)
                        {
                            // Distinguish H2 vs O2 by block definition subtype
                            var defId = ((VRage.Game.ModAPI.IMyCubeBlock)gasTank).BlockDefinition;
                            string subtype = defId.SubtypeId.ToString();
                            bool isH2 = subtype.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0
                                     || subtype.Length == 0; // small generic tank = H2
                            if (isH2)
                            {
                                h2Tanks++;
                                if (h2Pct < 0) h2Pct = 0f;
                                h2Pct += (float)(gasTank.FilledRatio * 100.0);
                            }
                            else
                            {
                                o2Tanks++;
                                if (o2Pct < 0) o2Pct = 0f;
                                o2Pct += (float)(gasTank.FilledRatio * 100.0);
                            }
                            continue;
                        }

                        var jumpDrive = block as Sandbox.ModAPI.IMyJumpDrive;
                        if (jumpDrive != null)
                        {
                            jumpDrives++;
                            jumpChargeSum += jumpDrive.CurrentStoredPower / jumpDrive.MaxStoredPower;
                            continue;
                        }

                        var door = block as Sandbox.ModAPI.IMyDoor;
                        if (door != null)
                        {
                            doors++;
                            if (door.Status == Sandbox.ModAPI.Ingame.DoorStatus.Open) doorsOpen++;
                            continue;
                        }

                        var gear = block as SpaceEngineers.Game.ModAPI.IMyLandingGear;
                        if (gear != null)
                        {
                            landingGear++;
                            if (gear.IsLocked) landingGearLocked++;
                            continue;
                        }

                        var conn = block as Sandbox.ModAPI.IMyShipConnector;
                        if (conn != null)
                        {
                            connectors++;
                            if (conn.Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected)
                                connectorsLocked++;
                            continue;
                        }

                        var turret = block as Sandbox.ModAPI.IMyLargeTurretBase;
                        if (turret != null)
                        {
                            turrets++;
                            if (turret.Enabled) turretsEnabled++;
                            continue;
                        }

                        var drill = block as Sandbox.ModAPI.IMyShipDrill;
                        if (drill != null) { drills++; continue; }

                        var welder = block as Sandbox.ModAPI.IMyShipWelder;
                        if (welder != null) { welders++; continue; }

                        var grinder = block as Sandbox.ModAPI.IMyShipGrinder;
                        if (grinder != null) { grinders++; continue; }

                        var warhead = block as Sandbox.ModAPI.IMyWarhead;
                        if (warhead != null) { warheads++; continue; }

                        var camera = block as Sandbox.ModAPI.IMyCameraBlock;
                        if (camera != null) { cameras++; continue; }

                        var sensor = block as Sandbox.ModAPI.IMySensorBlock;
                        if (sensor != null) { sensors++; continue; }

                        var piston = block as Sandbox.ModAPI.IMyExtendedPistonBase;
                        if (piston != null) { pistons++; continue; }

                        var rotor = block as Sandbox.ModAPI.IMyMotorStator;
                        if (rotor != null) { rotors++; continue; }

                        var vent = block as SpaceEngineers.Game.ModAPI.IMyAirVent;
                        if (vent != null) { airVents++; continue; }

                        var light = block as Sandbox.ModAPI.IMyLightingBlock;
                        if (light != null) { lights++; continue; }

                        var antenna = block as Sandbox.ModAPI.IMyRadioAntenna;
                        if (antenna != null) { antennas++; continue; }

                        // Controller for mass/speed/gravity
                        // Priority: RC block > main cockpit > any cockpit
                        if (controller == null)
                        {
                            var rc = block as IMyRemoteControl;
                            if (rc != null && rc.IsFunctional)
                            {
                                controller = rc;
                            }
                            else
                            {
                                var cockpit = block as Sandbox.ModAPI.IMyCockpit;
                                if (cockpit != null && cockpit.IsFunctional)
                                {
                                    // Prefer main cockpit, but accept any functional one
                                    if (cockpit.IsMainCockpit || controller == null)
                                        controller = cockpit;
                                }
                            }
                        }

                        // Projector for blueprint integrity
                        var proj = block as IMyProjector;
                        if (proj != null && proj.IsProjecting)
                        {
                            bpTotal   = proj.TotalBlocks;
                            bpMissing = proj.RemainingBlocks;
                            integrityPct = bpTotal > 0
                                ? (float)(bpTotal - bpMissing) / bpTotal * 100f
                                : 100f;
                        }
                    }

                    // Averages
                    if (h2Tanks  > 0) h2Pct  /= h2Tanks;
                    if (o2Tanks  > 0) o2Pct  /= o2Tanks;
                    if (batMax   > 0) batteryPct = batStored / batMax * 100f;
                    if (reactorMax > 0) { /* keep raw values */ }
                    float reactorLoadPct = reactorMax > 0 ? reactorOutput / reactorMax * 100f : 0f;
                    if (jumpDrives > 0) jumpCharge = (float)(jumpChargeSum / jumpDrives * 100.0);

                    // Mass, speed, gravity from controller
                    if (controller != null)
                    {
                        try
                        {
                            shipMass  = controller.CalculateShipMass().TotalMass;
                            shipSpeed = (float)controller.GetShipVelocities().LinearVelocity.Length();
                            gravMag   = (float)controller.GetNaturalGravity().Length();
                        }
                        catch { }
                    }

                    // TWR
                    if (gravMag > 0 && shipMass > 0 && thrustUp > 0)
                        twr = thrustUp / (shipMass * gravMag);

                    // Hull damage scan via fat blocks -- checks functional state
                    int hullDamagedCount = 0;
                    float worstIntegrity = 100f;
                    string worstBlockName = "";
                    try
                    {
                        foreach (var block in grid.GetFatBlocks())
                        {
                            if (block == null || block.MarkedForClose) continue;
                            var slim = block.SlimBlock;
                            if (slim == null) continue;
                            float ratio = slim.BuildLevelRatio;
                            if (ratio < 0.999f)
                            {
                                hullDamagedCount++;
                                if (ratio < worstIntegrity)
                                {
                                    worstIntegrity = ratio;
                                    var tb = block as Sandbox.ModAPI.IMyTerminalBlock;
                                    worstBlockName = tb?.CustomName ?? block.BlockDefinition?.DisplayNameText ?? "Unknown";
                                }
                            }
                        }
                    } catch { }

                    // Combined damaged count
                    int thrustersDamaged = thrustersTotal - thrustersFunctional;
                    int gyrosDamaged     = gyrosTotal - gyrosFunctional;
                    int damagedBlocks    = thrustersDamaged + gyrosDamaged + bpMissing + hullDamagedCount;

                    // Build JSON matching the bridge /ship_state schema
                    var I = System.Globalization.CultureInfo.InvariantCulture;
                    var json = "{"
                        + $"\"ts\":\"{DateTime.Now:HH:mm:ss}\","
                        + $"\"grid\":\"{Esc(grid.DisplayName)}\","
                        + $"\"speed\":{shipSpeed.ToString("F1", I)},"
                        + $"\"mass\":{shipMass.ToString("F0", I)},"
                        + $"\"grav\":{gravMag.ToString("F3", I)},"
                        + $"\"twr\":{twr.ToString("F3", I)},"
                        + $"\"h2\":{h2Pct.ToString("F1", I)},"
                        + $"\"h2tanks\":{h2Tanks},"
                        + $"\"o2\":{o2Pct.ToString("F1", I)},"
                        + $"\"bat\":{batteryPct.ToString("F1", I)},"
                        + $"\"reactor_count\":{reactorCount},"
                        + $"\"reactor_out\":{reactorOutput.ToString("F2", I)},"
                        + $"\"reactor_max\":{reactorMax.ToString("F2", I)},"
                        + $"\"reactor_load\":{reactorLoadPct.ToString("F1", I)},"
                        + $"\"thrusters_total\":{thrustersTotal},"
                        + $"\"thrusters_ok\":{thrustersFunctional},"
                        + $"\"thrusters_dmg\":{thrustersDamaged},"
                        + $"\"thrust_up\":{(thrustUp/1000f).ToString("F0", I)},"
                        + $"\"thrust_down\":{(thrustDown/1000f).ToString("F0", I)},"
                        + $"\"thrust_fwd\":{(thrustFwd/1000f).ToString("F0", I)},"
                        + $"\"thrust_back\":{(thrustBack/1000f).ToString("F0", I)},"
                        + $"\"gyros_total\":{gyrosTotal},"
                        + $"\"gyros_ok\":{gyrosFunctional},"
                        + $"\"bp_total\":{bpTotal},"
                        + $"\"bp_missing\":{bpMissing},"
                        + $"\"integrity\":{integrityPct.ToString("F1", I)},"
                        + $"\"damaged\":{damagedBlocks},"
                        + $"\"hull_damaged\":{hullDamagedCount},"
                        + $"\"worst_block\":\"{Esc(worstBlockName)}\","
                        + $"\"worst_integrity\":{(worstIntegrity * 100f).ToString("F0", I)},"
                        + $"\"inv_doors\":{doors},"
                        + $"\"inv_doors_open\":{doorsOpen},"
                        + $"\"inv_gear\":{landingGear},"
                        + $"\"inv_gear_locked\":{landingGearLocked},"
                        + $"\"inv_connectors\":{connectors},"
                        + $"\"inv_conn_locked\":{connectorsLocked},"
                        + $"\"inv_turrets\":{turrets},"
                        + $"\"inv_drills\":{drills},"
                        + $"\"inv_welders\":{welders},"
                        + $"\"inv_grinders\":{grinders},"
                        + $"\"inv_jumpdrives\":{jumpDrives},"
                        + $"\"inv_jump_charge\":{jumpCharge.ToString("F0", I)},"
                        + $"\"inv_warheads\":{warheads},"
                        + $"\"inv_cameras\":{cameras},"
                        + $"\"inv_sensors\":{sensors},"
                        + $"\"inv_pistons\":{pistons},"
                        + $"\"inv_rotors\":{rotors},"
                        + $"\"inv_vents\":{airVents},"
                        + $"\"inv_lights\":{lights},"
                        + $"\"inv_antennas\":{antennas}"
                        + "}";

                    var payload = $"{{\"grid_name\":\"{Esc(grid.DisplayName)}\","
                                + $"\"grid_id\":\"{grid.EntityId}\","
                                + $"\"lcd_json\":{json}}}";

                    // Only post to the bridge that owns this grid -- not all bridges
                    var ownerCtx = BridgeForGridId(grid.EntityId);
                    if (ownerCtx != null)
                        ownerCtx.Outbound.Enqueue("/ship_state", payload);
                    else
                        EnqueueAll("/ship_state", payload); // fallback if ownership unclear
                }
                catch (Exception ex)
                {
                    AriaLog.Error("CollectShipState error", ex);
                }
            }
        }


        // Read character health/oxygen/speed -- no grid physics access
        private void ReadCharacterStats()
        {
            var session = MySession.Static;
            if (session?.Players == null) return;
            var players = session.Players.GetOnlinePlayers();
            if (players == null) return;

            foreach (var player in players)
            {
                var ch = player.Character;
                if (ch == null || player.IsBot) continue;
                if (player.DisplayName == "Wolf") continue;

                // Faction filter -- only monitor players claimed by a configured bridge
                string playerFactionTag = null;
                {
                    bool inAnyFaction = false;
                    try
                    {
                        var pf = MySession.Static?.Factions?
                            .TryGetPlayerFaction(player.Identity.IdentityId);
                        playerFactionTag = pf?.Tag;
                        if (pf != null)
                            inAnyFaction = _bridges.Any(c =>
                                c.AllowedFactions.Count > 0 &&
                                c.AllowedFactions.Contains(pf.Tag));
                        else
                            // Player has no faction -- only show to bridges with no faction filter
                            inAnyFaction = _bridges.Any(c => c.AllowedFactions.Count == 0);
                    }
                    catch { }
                    if (!inAnyFaction) continue;
                }

                // Seat detection -- safe property read
                var seat = ch.Parent as Sandbox.ModAPI.IMyShipController;
                bool seated = seat != null;
                long cockpitId = seated ? seat.EntityId : 0L;

                _cockpitMap.TryGetValue(player.Id.SteamId, out long lastCockpit);
                if (seated && cockpitId != lastCockpit)
                    AriaLog.Info($"ARIA: {player.DisplayName} seated on '{seat?.CubeGrid?.DisplayName}'");
                _cockpitMap[player.Id.SteamId] = cockpitId;

                // Position -- character only, no grid physics
                var pos = ch.PositionComp?.GetPosition() ?? Vector3D.Zero;

                // Ship speed -- only if physics available
                float shipSpeed = 0f;
                string gridName = "";
                long   gridId   = 0L;
                if (seated && seat?.CubeGrid?.Physics != null)
                {
                    try
                    {
                        shipSpeed = (float)seat.CubeGrid.Physics.LinearVelocity.Length();
                        gridName  = seat.CubeGrid.DisplayName ?? "";
                        gridId    = seat.CubeGrid.EntityId;
                    }
                    catch { }
                }

                // Gravity -- static call, safe
                float grav = 0f;
                try
                {
                    grav = (float)MyGravityProviderSystem
                        .CalculateNaturalGravityInPoint(pos).Length();
                }
                catch { }

                // Vitals
                float hp = 100f;
                try
                {
                    var mc = ch as MyCharacter;
                    if (mc?.StatComp != null) hp = mc.StatComp.HealthRatio * 100f;
                }
                catch { }

                float o2 = 0f;
                try { o2 = ch.OxygenComponent?.SuitOxygenLevel ?? 0f; } catch { }
                float nrg = 0f;
                try { nrg = ch.SuitEnergyLevel; } catch { }
                float h2 = -1f;
                try
                {
                    // OxygenComponent.GetGasFillLevel(HydrogenId) -- from Keen source MyCharacter.cs
                    var gasComp = ch.OxygenComponent;
                    if (gasComp != null)
                    {
                        var method = gasComp.GetType().GetMethod("GetGasFillLevel",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                        var hydroIdField = gasComp.GetType().GetField("HydrogenId",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Static);
                        if (method != null && hydroIdField != null)
                        {
                            var hydroId = hydroIdField.GetValue(null);
                            h2 = (float)(method.Invoke(gasComp, new[] { hydroId }) ?? -1f);
                        }
                    }
                } catch { }
                bool jet = false;
                try { jet = ch.JetpackComp?.TurnedOn ?? false; } catch { }
                bool damp = true;
                try { if (seat != null) damp = seat.DampenersOverride; } catch { }

                // Log every 1800 ticks (~30 sec at 60fps)
                if (_tick % 1800 == 0)
                {
                    if (seated)
                        AriaLog.Info($"Tick#{_tick} | {player.DisplayName} cockpit '{gridName}' " +
                                     $"speed:{shipSpeed:F1}m/s grav:{grav:F2}m/s2");
                    else
                        AriaLog.Info($"Tick#{_tick} | {player.DisplayName} EVA HP:{hp:F0}%");
                }

                var json = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{{\"tick\":{0},\"player_id\":\"{1}\",\"player_name\":\"{2}\"," +
                    "\"pos_x\":{3:F0},\"pos_y\":{4:F0},\"pos_z\":{5:F0}," +
                    "\"health\":{6:F1},\"oxygen\":{7:F4},\"energy\":{8:F4},\"suit_h2\":{9:F4}," +
                    "\"in_cockpit\":{10},\"cockpit_grid\":\"{11}\",\"cockpit_grid_id\":\"{12}\"," +
                    "\"ship_speed\":{13:F1},\"gravity\":{14:F3}," +
                    "\"jetpack\":{15},\"dampeners\":{16}}}",
                    _tick, player.Id.SteamId, Esc(player.DisplayName ?? ""),
                    pos.X, pos.Y, pos.Z,
                    hp, o2, nrg, h2,
                    seated ? "true" : "false",
                    Esc(gridName), gridId,
                    shipSpeed, grav,
                    jet  ? "true" : "false",
                    damp ? "true" : "false");

                EnqueueForPlayer(playerFactionTag, "/telemetry", json);
            }
        }

        // =====================================================================
        // ReadOreDeposits
        // Reads ore deposit data from MyOreDetectorComponent.m_depositGroupsByEntity
        // via reflection -- same data the vanilla HUD ore markers use.
        // Called every 30s automatically (TICKS_ORE_SCAN) AND on DEEP_SCAN command.
        // Per-bridge: each bridge reads deposits from its own inhabited grid only.
        // =====================================================================
        // =====================================================================
        // ReadOreDeposits
        // Reads ore data via the "GetOres" terminal property exposed by the
        // OreDetectorMod (Workshop ID 2964095006). That mod runs client-side
        // where voxel chunks are loaded, scans within detector range, and
        // registers results as a terminal property on IMyOreDetector blocks.
        // Called every 30s automatically AND on DEEP_SCAN command.
        // Falls back with a clear message if the mod is not installed.
        // =====================================================================
        // =====================================================================
        // ReadOreDeposits
        // Grid scan replicating OreDetectorMod (Workshop 2964095006) approach:
        // 10m step through a sphere around the detector, call GetMaterialAt on
        // each voxel, collect non-Stone ore names. This matches exactly what the
        // mod does as a MyGameLogicComponent -- works server-side.
        // Called every 30s automatically AND on DEEP_SCAN command.
        // =====================================================================
        private void ReadOreDeposits()
        {
            var filter = new System.Collections.Generic.HashSet<string>(
                System.StringComparer.OrdinalIgnoreCase)
                { "Stone", "Soil", "Grass bare", "Grass" };

            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;

                var gridBridge = BridgeForGridId(grid.EntityId);
                if (gridBridge == null) continue;

                try
                {
                    var IC = System.Globalization.CultureInfo.InvariantCulture;
                    var depositList = new System.Collections.Generic.List<string>();
                    var foundOreNames = new System.Collections.Generic.HashSet<string>();

                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        var det = block as Sandbox.ModAPI.IMyOreDetector;
                        if (det == null) continue;
                        var func = block as Sandbox.ModAPI.IMyFunctionalBlock;
                        if (func == null || !func.IsWorking) continue;

                        var detPos = (block as VRage.ModAPI.IMyEntity)?.GetPosition() ?? block.PositionComp.GetPosition();
                        float range = det.Range;

                        // Find the nearest voxel map within detector range
                        var sphere = new BoundingSphereD(detPos, range);
                        var entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
                        MyVoxelBase voxelBase = null;
                        foreach (var e in entities)
                        {
                            if (e is MyVoxelMap || e is MyPlanet)
                            {
                                voxelBase = e as MyVoxelBase;
                                break;
                            }
                        }

                        if (voxelBase == null)
                        {
                            AriaLog.Debug($"OreDetector: no voxel in range of detector on '{grid.DisplayName}'");
                            continue;
                        }

                        AriaLog.Debug($"OreDetector: scanning voxel {voxelBase.EntityId} at range={range}m step=10m");

                        // Grid scan -- exactly as OreDetectorMod does it
                        // Start 10m ahead of detector (matches mod: entity.GetPosition() + WorldMatrix.Forward * 10)
                        var scanOrigin = detPos + (block as VRage.ModAPI.IMyEntity).WorldMatrix.Forward * 10;
                        float stepSize = 10f;
                        int sampleCount = 0;

                        for (float x = -range; x < range; x += stepSize)
                        {
                            for (float y = -range; y < range; y += stepSize)
                            {
                                for (float z = -range; z < range; z += stepSize)
                                {
                                    var offset = new Vector3D(x, y, z);
                                    // Only sample within sphere
                                    if (offset.LengthSquared() > (double)range * range) continue;

                                    var pos = scanOrigin + offset;
                                    sampleCount++;

                                    var mat = voxelBase.GetMaterialAt(ref pos);
                                    if (mat == null) continue;

                                    var name = mat.Id.SubtypeName;
                                    // Strip _XX suffix (e.g. "Iron_01" -> "Iron")
                                    var idx = name.IndexOf('_');
                                    if (idx >= 0) name = name.Substring(0, idx);

                                    if (filter.Contains(name)) continue;
                                    if (string.IsNullOrEmpty(name)) continue;

                                    if (foundOreNames.Add(name))
                                    {
                                        AriaLog.Info($"OreDetector: found [{name}] near voxel {voxelBase.EntityId}");
                                        depositList.Add(
                                            "{\"ore\":\"" + Esc(name) + "\"," +
                                            "\"x\":" + pos.X.ToString("F0", IC) + "," +
                                            "\"y\":" + pos.Y.ToString("F0", IC) + "," +
                                            "\"z\":" + pos.Z.ToString("F0", IC) + "," +
                                            "\"voxel_id\":" + voxelBase.EntityId.ToString(IC) + "}");
                                    }
                                }
                            }
                        }

                        AriaLog.Debug($"OreDetector: {sampleCount} samples, {foundOreNames.Count} ore type(s) on '{grid.DisplayName}'");
                    }

                    if (depositList.Count > 0)
                    {
                        var payload = "{\"grid\":\"" + Esc(grid.DisplayName) + "\"," +
                                      "\"deposits\":[" + string.Join(",", depositList) + "]}";
                        gridBridge.Outbound.Enqueue("/ore_deposits", payload);
                        AriaLog.Info($"OreDetector: {depositList.Count} deposit(s) posted for '{grid.DisplayName}': [{string.Join(", ", foundOreNames)}]");
                    }
                    else
                    {
                        AriaLog.Debug($"OreDetector: no ore found on '{grid.DisplayName}' -- fly within detector range of asteroid.");
                    }
                }
                catch (Exception ex)
                {
                    AriaLog.Warn($"ReadOreDeposits error: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        // Read nearby entity positions -- no block scanning, no physics
        private void ReadSurroundings(double range = 15000.0, bool longRange = false)
        {
            // Run one scan per bridge -- each bridge scans from its own ship
            foreach (var ctx in _bridges.ToList())
            {
                ReadSurroundingsForBridge(ctx, range, longRange);
            }
        }

        private void ReadSurroundingsForBridge(BridgeContext ctx, double range, bool longRange)
        {
            // Scan origin -- use this bridge's ship RC block position
            Vector3D scanOrigin = Vector3D.Zero;
            Vector3D shipFwd    = Vector3D.Zero;
            Vector3D shipRight  = Vector3D.Zero;
            Vector3D shipUp     = Vector3D.Zero;
            bool hasShip        = false;

            foreach (var ag in _ariaGrids)
            {
                if (ag == null || ag.MarkedForClose) continue;
                // Only use grids belonging to this bridge
                bool isThisBridge = false;
                try
                {
                    foreach (var b in ag.GetFatBlocks())
                    {
                        var t = b as Sandbox.ModAPI.IMyTerminalBlock;
                        if (t?.CustomName?.Trim() == ctx.CoreBlockName) { isThisBridge = true; break; }
                    }
                }
                catch { }
                if (!isThisBridge) continue;

                // Prefer the inhabited grid (has a PB mapped) as scan origin
                bool isInhabited = BridgeForGridId(ag.EntityId) != null;

                foreach (var b in ag.GetFatBlocks())
                {
                    var rcBlk = b as IMyRemoteControl;
                    if (rcBlk != null)
                    {
                        scanOrigin = rcBlk.GetPosition();
                        shipFwd    = rcBlk.WorldMatrix.Forward;
                        shipRight  = rcBlk.WorldMatrix.Right;
                        shipUp     = rcBlk.WorldMatrix.Up;
                        hasShip    = true;
                        break;
                    }
                }
                // If this is the inhabited grid we're done -- don't override with another grid
                if (hasShip && isInhabited) break;
                // Otherwise keep looking -- an inhabited grid takes priority
                if (hasShip && !isInhabited) continue;
            }

            // No ship -- fall back to a player in this bridge's faction
            if (!hasShip)
            {
                try
                {
                    var session = MySession.Static;
                    if (session?.Players != null)
                    {
                        foreach (var player in session.Players.GetOnlinePlayers())
                        {
                            if (player.Character == null || player.IsBot) continue;
                            try
                            {
                                var pf = session.Factions?.TryGetPlayerFaction(
                                    player.Identity?.IdentityId ?? 0);
                                bool inThisFaction = ctx.AllowedFactions.Count == 0 ||
                                    (pf != null && ctx.AllowedFactions.Contains(pf.Tag));
                                if (!inThisFaction) continue;
                            }
                            catch { continue; }
                            scanOrigin = player.Character.PositionComp.GetPosition();
                            break;
                        }
                    }
                }
                catch { }
            }

            if (scanOrigin == Vector3D.Zero) return;

            var ctxNameLog = char.ToUpperInvariant(ctx.ActivationName[0]) + ctx.ActivationName.Substring(1);
            AriaLog.Debug($"{ctxNameLog}: Scan origin {(hasShip ? "ship" : "player")} at {scanOrigin.X:F0},{scanOrigin.Y:F0},{scanOrigin.Z:F0} range={range:F0}m");

            var nearbyGrids     = new List<string>();
            var nearbyAsteroids = new List<string>();

                try
                {
                    foreach (var entity in MyEntities.GetEntities().ToList())
                    {
                        // -- Player-built grids --
                        var grid = entity as MyCubeGrid;
                        if (grid != null)
                        {
                            if (grid.MarkedForClose || grid.BlocksCount <= 3) continue;
                            if (grid.IsPreview) continue;  // skip projector ghost grids
                            double dist;
                            try
                            {
                                var aabb = grid.PositionComp.WorldAABB;
                                double cx = Math.Max(aabb.Min.X, Math.Min(aabb.Max.X, scanOrigin.X));
                                double cy = Math.Max(aabb.Min.Y, Math.Min(aabb.Max.Y, scanOrigin.Y));
                                double cz = Math.Max(aabb.Min.Z, Math.Min(aabb.Max.Z, scanOrigin.Z));
                                dist = Vector3D.Distance(scanOrigin, new Vector3D(cx, cy, cz));
                            }
                            catch { dist = Vector3D.Distance(scanOrigin, grid.PositionComp.GetPosition()); }
                            if (dist > range) continue;
                            var ctxTag = char.ToUpperInvariant(ctx.ActivationName[0]) + ctx.ActivationName.Substring(1);
                            string ariaTag = (ctx.CoreBlockName != null && _ariaGrids.Any(g =>
                                g.EntityId == grid.EntityId &&
                                g.GetFatBlocks().Any(b => (b as Sandbox.ModAPI.IMyTerminalBlock)?.CustomName?.Trim() == ctx.CoreBlockName)))
                                ? $" [{ctxTag} MY SHIP]" : "";
                            string gt = grid.GridSizeEnum == VRage.Game.MyCubeSize.Large ? "Large" : "Small";
                            var gPos = grid.PositionComp.GetPosition();
                            string gridName = grid.DisplayName ?? "Unknown";
                            nearbyGrids.Add($"{gridName} ({gt},{grid.BlocksCount}blk,{dist:F0}m,pos:{gPos.X:F0},{gPos.Y:F0},{gPos.Z:F0},eid:{grid.EntityId}){ariaTag}");
                            continue;
                        }

                        // -- Voxels (asteroids only, skip planets) --
                        // Use GetAllVoxelMapsInSphere instead of entity iteration to catch
                        // MyVoxelPhysics sub-objects that don't appear in MyEntities
                        var voxel = entity as VRage.ModAPI.IMyVoxelBase;
                        if (voxel != null && !voxel.MarkedForClose && !(voxel is MyPlanet))
                        {
                            var vPos  = voxel.PositionComp.GetPosition();
                            double vDist = Vector3D.Distance(scanOrigin, vPos);
                            if (vDist > range) continue;

                            // Bearing relative to ship forward
                            string bearing = "";
                            if (shipFwd != Vector3D.Zero)
                            {
                                var toVoxel = Vector3D.Normalize(vPos - scanOrigin);
                                double dotFwd   = Vector3D.Dot(toVoxel, shipFwd);
                                double dotRight = Vector3D.Dot(toVoxel, shipRight);
                                double dotUp    = Vector3D.Dot(toVoxel, shipUp);
                                if      (dotFwd   >  0.7) bearing = "ahead";
                                else if (dotFwd   < -0.7) bearing = "behind";
                                else if (dotRight >  0.5) bearing = "starboard";
                                else if (dotRight < -0.5) bearing = "port";
                                else if (dotUp    >  0.5) bearing = "above";
                                else                       bearing = "below";
                            }

                            // Approximate size from bounding sphere
                            float vRadius = 0f;
                            try { vRadius = (float)voxel.PositionComp.WorldVolume.Radius; } catch { }
                            string sizeLabel = vRadius < 100f  ? "small" :
                                               vRadius < 500f  ? "medium" :
                                               vRadius < 2000f ? "large"  : "massive";

                            string bearingStr = bearing.Length > 0 ? "," + bearing : "";
                            // EntityId is the stable persistent ID written to sandbox.sbs
                            // Access directly via IMyVoxelBase (IMyEntity) -- no cast needed
                            string voxelKey = voxel.EntityId.ToString();
                            nearbyAsteroids.Add($"Asteroid ({sizeLabel},{vDist:F0}m{bearingStr},vid:{voxelKey},pos:{voxel.PositionComp.GetPosition().X:F0},{voxel.PositionComp.GetPosition().Y:F0},{voxel.PositionComp.GetPosition().Z:F0})");
                        }
                    }
                }
                catch { }

                // Supplemental close-range voxel sweep -- catches MyVoxelPhysics sub-objects
                // and any asteroid whose entity origin is far from its surface
                try
                {
                    var seenVids = new System.Collections.Generic.HashSet<long>(
                        nearbyAsteroids.Count > 0
                            ? nearbyAsteroids
                                .Select(s => { var m2 = System.Text.RegularExpressions.Regex.Match(s, @"vid:(\d+)"); return m2.Success ? long.Parse(m2.Groups[1].Value) : 0L; })
                                .Where(id => id != 0)
                            : System.Linq.Enumerable.Empty<long>());

                    var closeSphere = new BoundingSphereD(scanOrigin, Math.Min(range, 2000.0));
                    var closeVoxels = new System.Collections.Generic.List<MyVoxelBase>();
                    MyGamePruningStructure.GetAllVoxelMapsInSphere(ref closeSphere, closeVoxels);

                    foreach (var cv in closeVoxels)
                    {
                        if (cv == null || cv.MarkedForClose || cv is MyPlanet) continue;
                        if (seenVids.Contains(cv.EntityId)) continue; // already found via MyEntities

                        var cvPos  = cv.PositionComp.GetPosition();
                        double cvDist = Vector3D.Distance(scanOrigin, cvPos);

                        string bearing2 = "";
                        if (shipFwd != Vector3D.Zero)
                        {
                            var toV = Vector3D.Normalize(cvPos - scanOrigin);
                            double dotFwd   = Vector3D.Dot(toV, shipFwd);
                            double dotRight = Vector3D.Dot(toV, shipRight);
                            double dotUp    = Vector3D.Dot(toV, shipUp);
                            if      (dotFwd   >  0.7) bearing2 = "ahead";
                            else if (dotFwd   < -0.7) bearing2 = "behind";
                            else if (dotRight >  0.5) bearing2 = "starboard";
                            else if (dotRight < -0.5) bearing2 = "port";
                            else if (dotUp    >  0.5) bearing2 = "above";
                            else                       bearing2 = "below";
                        }

                        float cvRadius = 0f;
                        try { cvRadius = (float)cv.PositionComp.WorldVolume.Radius; } catch { }
                        string cvSize = cvRadius < 100f ? "small" : cvRadius < 500f ? "medium" : cvRadius < 2000f ? "large" : "massive";
                        string bearStr2 = bearing2.Length > 0 ? "," + bearing2 : "";

                        AriaLog.Debug($"OreDetector: close-range voxel {cv.EntityId} at {cvDist:F0}m ({cvSize}) not in entity list -- adding");
                        nearbyAsteroids.Add($"Asteroid ({cvSize},{cvDist:F0}m{bearStr2},vid:{cv.EntityId},pos:{cvPos.X:F0},{cvPos.Y:F0},{cvPos.Z:F0})");
                    }
                }
                catch { }

                // Build sectioned surroundings string
                string gridPart = nearbyGrids.Count > 0
                    ? "GRIDS: " + string.Join("; ", nearbyGrids)
                    : "GRIDS: none";
                string asteroidPart = nearbyAsteroids.Count > 0
                    ? "ASTEROIDS: " + string.Join("; ", nearbyAsteroids)
                    : "ASTEROIDS: none";
                string surr = gridPart + " | " + asteroidPart;

                // Grid change detection -- alert on new grids since last scan
                var currentGridNames = new HashSet<string>();
                foreach (var g in nearbyGrids)
                {
                    // Extract grid name (everything before the first space+paren)
                    var parenIdx = g.IndexOf(" (");
                    var gName = parenIdx > 0 ? g.Substring(0, parenIdx) : g;
                    // Strip [AINAME MY SHIP] tag
                    gName = System.Text.RegularExpressions.Regex.Replace(
                        gName, @" \[[A-Z]+ MY SHIP\]", "").Trim();
                    currentGridNames.Add(gName);
                }

                if (!_firstScan)
                {
                    foreach (var gName in currentGridNames)
                    {
                        if (!_previousGridNames.Contains(gName))
                        {
                            // New grid detected -- find its distance from the raw entry
                            string distStr = "";
                            foreach (var entry in nearbyGrids)
                            {
                                if (entry.StartsWith(gName))
                                {
                                    // Extract distance: "(Large,264blk,1234m,..."
                                    var m = System.Text.RegularExpressions.Regex.Match(
                                        entry, @",(\d+)m,");
                                    if (m.Success) distStr = m.Groups[1].Value + "m";
                                    break;
                                }
                            }

                            var alert = $"New contact: {gName}" +
                                        (distStr.Length > 0 ? $" at {distStr}" : "") + ".";
                            var ctxNameAlert = char.ToUpperInvariant(ctx.ActivationName[0]) + ctx.ActivationName.Substring(1);
                            AriaLog.Info($"{ctxNameAlert}: Grid alert -- {alert}");
                            ctx.Outbound.Enqueue("/push_alert", "{\"alert\":\"" + Esc(alert) + "\"," +
                                "\"type\":\"new_grid\"," +
                                "\"grid_name\":\"" + Esc(gName) + "\"}");
                        }
                    }
                }

                _previousGridNames = currentGridNames;
                _firstScan = false;

                string scanType = longRange ? "long" : "medium";
                string scanJson = $"{{\"scan_origin_x\":{scanOrigin.X:F0}," +
                    $"\"scan_origin_y\":{scanOrigin.Y:F0}," +
                    $"\"scan_origin_z\":{scanOrigin.Z:F0}," +
                    $"\"surroundings\":\"{Esc(surr)}\"," +
                    $"\"scan_range\":{range:F0},\"scan_type\":\"{scanType}\"}}";
                ctx.Outbound.Enqueue("/surroundings", scanJson);

                var ctxName = char.ToUpperInvariant(ctx.ActivationName[0]) + ctx.ActivationName.Substring(1);
                AriaLog.Info($"{ctxName}: Surroundings [{scanType}] | {(surr.Length > 200 ? surr.Substring(0, 200) : surr)}");
        }


        // Fetch canonical scan and tag LCD content from bridge and write to in-game LCDs
        private void FetchAndWriteScanLcds()
        {
            _ = FetchScanLcdsAsync();
        }

        private async System.Threading.Tasks.Task FetchScanLcdsAsync()
        {
            foreach (var ctx in _bridges.ToList())
            {
                if (!ctx.BridgeUp) continue;
                try
                {
                    var scanContent  = await GetFromBridge(ctx, "/scan_lcd_content");
                    var gridsContent = await GetFromBridge(ctx, "/scan_lcd_grids");
                    var astsContent  = await GetFromBridge(ctx, "/scan_lcd_asteroids");
                    var tagContent   = await GetFromBridge(ctx, "/tag_lcd_content");

                    var scanSnap  = !string.IsNullOrEmpty(scanContent)  ? UnescapeJson(scanContent)  : "";
                    var gridsSnap = !string.IsNullOrEmpty(gridsContent) ? UnescapeJson(gridsContent) : scanSnap;
                    var astsSnap  = !string.IsNullOrEmpty(astsContent)  ? UnescapeJson(astsContent)  : scanSnap;
                    var tagSnap   = !string.IsNullOrEmpty(tagContent)   ? UnescapeJson(tagContent)   : "";

                    var ctxCapture = ctx;
                    MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                    {
                        WriteLcdByName(GetLcdName(ctxCapture, "SCAN"),             scanSnap,  0.55f);
                        WriteLcdByName(GetLcdName(ctxCapture, "SCAN GRIDS"),       gridsSnap, 0.55f);
                        WriteLcdByName(GetLcdName(ctxCapture, "SCAN ASTEROIDS"),   astsSnap,  0.55f);
                        WriteLcdByName(GetLcdName(ctxCapture, "TAGGED"),           tagSnap,   0.55f);
                    });
                }
                catch (Exception ex)
                {
                    AriaLog.Warn($"FetchScanLcds ({ctx.Name}): {ex.Message}");
                }
            }
        }

        private async Task<string> GetFromBridge(BridgeContext ctx, string endpoint)
        {
            try
            {
                var r = await _httpFast.GetAsync($"{ctx.Url}{endpoint}");
                if (!r.IsSuccessStatusCode) return null;
                return await r.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }

        // Extract content field value from simple JSON response
        private static string UnescapeJson(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            // Locate "content":"VALUE" in JSON
            int s = raw.IndexOf("\"content\":\"");
            if (s < 0) return raw;
            s += 11; // length of "content":"
            int e = raw.IndexOf("\"}", s);
            if (e < 0) e = raw.IndexOf("\",", s);
            if (e < 0) e = raw.Length;
            var val = raw.Substring(s, e - s);
            val = val.Replace("\\n", System.Environment.NewLine);
            val = val.Replace("\\r", string.Empty);
            val = val.Replace("\\\"", "\"");
            return val;
        }

        // Generic GET helper using existing _http client and BRIDGE_URL constant
        private async Task<string> GetAsync(string path)
        {
            try
            {
                var resp = await _http.GetAsync(BRIDGE_URL + path);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync();
            }
            catch { }
            return "";
        }

        
        private void WriteLcdByName(string lcdName, string content, float fontSize)
        {
            if (string.IsNullOrEmpty(content)) return;
            foreach (var grid in _ariaGrids)
            {
                if (grid == null || grid.MarkedForClose) continue;
                foreach (var block in grid.GetFatBlocks())
                {
                    if (block == null || block.MarkedForClose) continue;
                    var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                    if (t?.CustomName?.Trim() != lcdName) continue;
                    var lcd = block as Sandbox.ModAPI.IMyTextPanel;
                    if (lcd == null) continue;
                    lcd.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
                    lcd.FontSize    = fontSize;
                    lcd.WriteText(content);
                    return;
                }
            }
        }

        // =====================================================================
        // CONFIG -- ARIA_Config.xml
        // =====================================================================

        private void LoadConfig()
        {
            try
            {
                // Use same path resolution as AriaLog.Init -- reliable in Torch context
                var pluginDir = Path.GetDirectoryName(
                    typeof(ITorchBase).Assembly.Location)
                    ?? Directory.GetCurrentDirectory();
                // Look in plugin subdirectory first (standard Torch plugin layout)
                var pluginSubDir = Path.Combine(pluginDir, "Plugins", "ARIAPlugin");
                if (!Directory.Exists(pluginSubDir))
                    pluginSubDir = Path.Combine(pluginDir, "ARIAPlugin");
                if (!Directory.Exists(pluginSubDir))
                    pluginSubDir = pluginDir;

                _configPath = Path.Combine(pluginSubDir, "ARIAPlugin_Config.xml");

                // Legacy fallback -- look for ARIA_Config.xml if new config missing
                if (!File.Exists(_configPath))
                {
                    foreach (var dir2 in new[] { pluginSubDir, pluginDir })
                    {
                        var legacy = Path.Combine(dir2, "ARIA_Config.xml");
                        if (File.Exists(legacy)) { _configPath = legacy; break; }
                    }
                }

                if (!File.Exists(_configPath))
                {
                    SaveDefaultConfig();
                    AriaLog.Warn($"ARIA: Config not found -- created template at {_configPath}");
                    return;
                }

                var xml = File.ReadAllText(_configPath);
                AriaLog.Info($"ARIA: Reading config from: {_configPath}");
                AriaLog.Info($"ARIA: Config length: {xml.Length} chars");
                _bridges.Clear();

                // ── New multi-bridge format ──────────────────────────────────
                // <Bridges><Bridge>...</Bridge><Bridge>...</Bridge></Bridges>
                var bridgesSection = System.Text.RegularExpressions.Regex.Match(
                    xml, "<Bridges>(.*?)</Bridges>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                if (bridgesSection.Success)
                {
                    AriaLog.Info($"ARIA: Found <Bridges> section, length={bridgesSection.Groups[1].Value.Length}");
                    var bridgeBlocks = System.Text.RegularExpressions.Regex.Matches(
                        bridgesSection.Groups[1].Value,
                        "<Bridge>(.*?)</Bridge>",
                        System.Text.RegularExpressions.RegexOptions.Singleline);

                    foreach (System.Text.RegularExpressions.Match bb in bridgeBlocks)
                    {
                        var bXml = bb.Groups[1].Value;
                        var ctx  = ParseBridgeBlock(bXml);
                        if (ctx != null) _bridges.Add(ctx);
                    }
                    AriaLog.Info($"ARIA: Loaded {_bridges.Count} bridge(s) from config.");
                }
                else
                {
                    // ── Legacy single-bridge format ──────────────────────────
                    // <BridgeUrl>...</BridgeUrl><ActivationName>...</ActivationName> etc.
                    AriaLog.Info("ARIA: Legacy single-bridge config detected -- migrating.");
                    var ctx = ParseBridgeBlock(xml);
                    if (ctx != null) _bridges.Add(ctx);
                }

                // Global settings
                var chanMatch = System.Text.RegularExpressions.Regex.Match(
                    xml, "<ListenChannel>(.*?)</ListenChannel>");
                // (ListenChannel can also live inside <Bridge> -- already parsed above)

                // Auto-registration settings
                var autoRegMatch = System.Text.RegularExpressions.Regex.Match(
                    xml, "<AutoRegister>(.*?)</AutoRegister>");
                if (autoRegMatch.Success)
                    _autoRegister = autoRegMatch.Groups[1].Value.Trim().ToLowerInvariant() != "false";

                var regPortMatch = System.Text.RegularExpressions.Regex.Match(
                    xml, "<RegistrationPort>(.*?)</RegistrationPort>");
                if (regPortMatch.Success && int.TryParse(regPortMatch.Groups[1].Value.Trim(), out int rp))
                    _registrationPort = rp;

                // Optional: override shared relay port from config
                var relayPortMatch = System.Text.RegularExpressions.Regex.Match(
                    xml, "<SharedRelayPort>(.*?)</SharedRelayPort>");
                if (relayPortMatch.Success && int.TryParse(relayPortMatch.Groups[1].Value.Trim(), out int srp))
                    _sharedRelayPort = srp;

                // Nodes config path -- sibling file to main config
                _nodesConfigPath = Path.Combine(pluginSubDir, "ARIAPlugin_Nodes.xml");

                AriaLog.Info($"ARIA: AutoRegister={_autoRegister} RegistrationPort={_registrationPort} " +
                             $"SharedRelayPort={_sharedRelayPort}");

                foreach (var ctx in _bridges)
                    AriaLog.Info($"ARIA: Bridge '{ctx.Name}' -> {ctx.Url} | " +
                                 $"activation='{ctx.ActivationName}' | " +
                                 $"factions=[{string.Join(",", ctx.AllowedFactions)}]");

                // Load PB script from file -- auto-installed into PBs on inhabit
                try
                {
                    var scriptPath = Path.Combine(pluginSubDir, "ARIA_PB_Script.txt");
                    if (File.Exists(scriptPath))
                    {
                        _pbScriptContent = File.ReadAllText(scriptPath);
                        AriaLog.Info($"ARIA: PB script loaded ({_pbScriptContent.Length} chars) from {scriptPath}");

                    var emotionScriptPath = Path.Combine(pluginSubDir, "ARIA_Emotion_PB.txt");
                    if (File.Exists(emotionScriptPath))
                    {
                        _emotionPbScriptContent = File.ReadAllText(emotionScriptPath);
                        AriaLog.Info($"ARIA: Emotion PB script loaded ({_emotionPbScriptContent.Length} chars) from {emotionScriptPath}");
                    }
                    else
                        AriaLog.Info("ARIA: ARIA_Emotion_PB.txt not found -- emotion PB will not auto-install.");

                    var instructionsPath = Path.Combine(pluginSubDir, "ARIA_Instructions.txt");
                    if (File.Exists(instructionsPath))
                    {
                        _instructionsContent = File.ReadAllText(instructionsPath);
                        AriaLog.Info($"ARIA: Instructions text loaded ({_instructionsContent.Length} chars) from {instructionsPath}");
                    }
                    else
                        AriaLog.Info("ARIA: ARIA_Instructions.txt not found -- LCD will show generated guide.");
                    }
                    else
                    {
                        AriaLog.Warn($"ARIA: PB script not found at {scriptPath} -- auto-install disabled. " +
                                     "Copy ARIA_PB_Script.txt to the plugin folder to enable.");
                    }
                }
                catch (Exception scriptEx)
                {
                    AriaLog.Warn($"ARIA: Could not load PB script: {scriptEx.Message}");
                }

                // Load emotion image data -- WIC text files in plugin folder
                // Emotion images removed -- now handled by ARIA EMOTION PB
            }
            catch (Exception ex)
            {
                AriaLog.Error("ARIA: Config load failed.", ex);
            }
        }

        private BridgeContext ParseBridgeBlock(string xml)
        {
            var ctx = new BridgeContext();

            // URL -- support both <BridgeUrl> (legacy) and <Url>
            var urlMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<(?:BridgeUrl|Url)>(.*?)</(?:BridgeUrl|Url)>");
            if (urlMatch.Success && !string.IsNullOrWhiteSpace(urlMatch.Groups[1].Value))
                ctx.Url = urlMatch.Groups[1].Value.Trim();
            else
            {
                AriaLog.Warn("ARIA: Bridge block missing URL -- skipping.");
                return null;
            }

            // Name
            var nameMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<Name>(.*?)</Name>");
            if (nameMatch.Success)
                ctx.Name = nameMatch.Groups[1].Value.Trim().ToLowerInvariant();

            // ActivationName
            var actMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<ActivationName>(.*?)</ActivationName>");
            if (actMatch.Success && !string.IsNullOrWhiteSpace(actMatch.Groups[1].Value))
            {
                var raw = actMatch.Groups[1].Value.Trim();
                ctx.ActivationName = raw.ToLowerInvariant();
                var prefix         = raw.ToUpperInvariant();
                ctx.CoreBlockName  = prefix + " CORE";
                ctx.PbBlockName    = prefix + " PB";
                if (string.IsNullOrEmpty(ctx.Name)) ctx.Name = ctx.ActivationName;
            }

            // ListenMode
            var modeMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<ListenMode>(.*?)</ListenMode>");
            if (modeMatch.Success && !string.IsNullOrWhiteSpace(modeMatch.Groups[1].Value))
                ctx.ListenMode = modeMatch.Groups[1].Value.Trim().ToLowerInvariant();

            // ListenChannel
            var chanMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<ListenChannel>(.*?)</ListenChannel>");
            if (chanMatch.Success && !string.IsNullOrWhiteSpace(chanMatch.Groups[1].Value))
                ctx.ListenChannel = chanMatch.Groups[1].Value.Trim().ToLowerInvariant();

            // AllowedFactions
            var factMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<AllowedFactions>(.*?)</AllowedFactions>");
            if (factMatch.Success && !string.IsNullOrWhiteSpace(factMatch.Groups[1].Value))
                foreach (var tag in factMatch.Groups[1].Value.Split(','))
                {
                    var t = tag.Trim();
                    if (!string.IsNullOrEmpty(t)) ctx.AllowedFactions.Add(t);
                }

            // AdapterContext
            var adCtxMatch = System.Text.RegularExpressions.Regex.Match(
                xml, "<AdapterContext>(.*?)</AdapterContext>");
            if (adCtxMatch.Success)
                ctx.AdapterContext = adCtxMatch.Groups[1].Value.Trim();

            return ctx;
        }

        private void SaveDefaultConfig()
        {
            try
            {
                var xml = string.Join("\n", new[]
                {
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                    "<ARIAConfig>",
                    "  <!--",
                    "    Pidgeon v0.6.0-r1 -- Configuration",
                    "    Motto: Keep it klean from Keen.",
                    "",
                    "    PORT LAYOUT:",
                    "      8099        = registration/query port (open on server)",
                    "      8100        = shared relay port for ALL ARIA Node clients",
                    "      8000-8002   = local static bridges (ARIA, NOVA, BEKA)",
                    "",
                    "    ARIA Node clients auto-register via /aria_ping on port 8099.",
                    "    All nodes share relay port 8100 -- nodeId identifies each one.",
                    "    Node connects outbound -- NAT transparent, no client port forwarding.",
                    "    Router only needs: 8099 + 8100 forwarded to Torch server.",
                    "  -->",
                    "",
                    "  <!-- Auto-registration for ARIA Node clients -->",
                    "  <AutoRegister>true</AutoRegister>",
                    "  <RegistrationPort>8099</RegistrationPort>",
                    "  <SharedRelayPort>8100</SharedRelayPort>",
                    "",
                    "  <!-- Router: forward ports 8099 and 8100 to this server -->",
                    "  <Bridges>",
                    "    <Bridge>",
                    "      <n>aria</n>",
                    "      <Url>http://localhost:8000</Url>",
                    "      <ActivationName>Aria</ActivationName>",
                    "      <ListenMode>faction</ListenMode>",
                    "      <ListenChannel>faction</ListenChannel>",
                    "      <AllowedFactions>Ari</AllowedFactions>",
                    "    </Bridge>",
                    "    <!-- Add NOVA, BEKA etc. here -->",
                    "  </Bridges>",
                    "</ARIAConfig>"
                });
                System.IO.File.WriteAllText(_configPath, xml);
                AriaLog.Info($"ARIA: Default config written to {_configPath}");
            }
            catch (Exception ex)
            {
                AriaLog.Error("ARIA: Could not write config.", ex);
            }
        }

                // =====================================================================
        // WriteInstructionsLcd
        // Writes setup guide to "ARIA Instructions" LCD on any grid.
        // Called when ARIA cannot fully initialise -- guides the player.
        // =====================================================================
        private void WriteInstructionsLcd(MyCubeGrid grid, List<string> missingBlocks)
        {
            // Search the target grid first, then all known ARIA grids
            var searchGrids = new List<MyCubeGrid>();
            if (grid != null) searchGrids.Add(grid);
            foreach (var g in _ariaGrids)
                if (g != null && !searchGrids.Contains(g)) searchGrids.Add(g);

            // Also search ALL loaded grids for the Instructions LCD
            // so it works even before ARIA has inhabited a ship
            try
            {
                foreach (var entity in MyEntities.GetEntities().ToList())
                {
                    var g2 = entity as MyCubeGrid;
                    if (g2 != null && !g2.MarkedForClose && !searchGrids.Contains(g2))
                        searchGrids.Add(g2);
                }
            }
            catch { }

            foreach (var g in searchGrids)
            {
                if (g == null || g.MarkedForClose) continue;
                Sandbox.ModAPI.IMyTextPanel lcd = null;
                BridgeContext lcdBridge = null;
                try
                {
                    foreach (var block in g.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                        var blockName = t?.CustomName?.Trim() ?? "";
                        if (string.IsNullOrEmpty(blockName)) continue;

                        // Check per-bridge name (e.g. "UBER Instructions") and legacy fallback
                        BridgeContext matchCtx = _bridges.FirstOrDefault(c =>
                            blockName.Equals(GetInstructionsLcdName(c), StringComparison.OrdinalIgnoreCase));
                        if (matchCtx == null && blockName == ARIA_INSTRUCTIONS)
                            matchCtx = PrimaryBridge;

                        if (matchCtx == null) continue;
                        lcd = block as Sandbox.ModAPI.IMyTextPanel;
                        if (lcd != null) { lcdBridge = matchCtx; break; }
                    }
                }
                catch { }
                if (lcd == null) continue;

                // If ARIA_Instructions.txt is loaded, write it directly
                // and skip the generated guide entirely
                if (!string.IsNullOrEmpty(_instructionsContent))
                {
                    lcd.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
                    lcd.FontSize = 0.55f;
                    lcd.WriteText(_instructionsContent);
                    return;
                }

                // ---- TOP: Setup guide (static) ----
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== ARIA SETUP GUIDE ===");
                sb.AppendLine("v0.6.0-r1  Bridge: " + BRIDGE_URL);
                sb.AppendLine("");
                sb.AppendLine("--- REQUIRED BLOCKS ---");
                sb.AppendLine("[RC]  Name: ARIA Core");
                sb.AppendLine("      Autopilot, dampeners, scan");
                sb.AppendLine("");
                sb.AppendLine("[PB]  Name: ARIA PB");
                sb.AppendLine("      Load ARIA PB Script from Workshop");
                sb.AppendLine("      or paste script manually.");
                sb.AppendLine("      Ship telemetry, block inventory");
                sb.AppendLine("");
                sb.AppendLine("--- OPTIONAL BLOCKS ---");
                sb.AppendLine("[LCD] ARIA STATUS   ship overview");
                sb.AppendLine("[LCD] ARIA SCAN     nearby objects");
                sb.AppendLine("[LCD] ARIA CREW     crew vitals");
                sb.AppendLine("[LCD] ARIA TAGGED   bookmarked items");
                sb.AppendLine("[LCD] ARIA FACE     emotion display");
                sb.AppendLine("[LIGHT/LCD] ARIA Presence");
                sb.AppendLine("      Online/offline indicator");
                sb.AppendLine("[AI]  ARIA AI        native follow (Automaton DLC)");
                sb.AppendLine("");

                // ---- MIDDLE: Missing blocks if any ----
                if (missingBlocks != null && missingBlocks.Count > 0)
                {
                    sb.AppendLine("--- ACTION REQUIRED ---");
                    foreach (var m in missingBlocks)
                        sb.AppendLine("  >> " + m);
                    sb.AppendLine("");
                }

                // ---- BOTTOM: Live status of all ARIA blocks on this grid ----
                sb.AppendLine("--- LIVE STATUS ---");
                sb.AppendLine("Updated: " + DateTime.Now.ToString("HH:mm:ss"));
                sb.AppendLine("");

                // Check each block by name on this specific grid
                var checkBlocks = new Dictionary<string, string>
                {
                    { ARIA_CORE,      "RC   ARIA Core    " },
                    { ARIA_PB,        "PB   ARIA PB      " },
                    { ARIA_STATUS_LCD,"LCD  ARIA STATUS  " },
                    { ARIA_SCAN_LCD,  "LCD  ARIA SCAN    " },
                    { ARIA_CREW_LCD,  "LCD  ARIA CREW    " },
                    { ARIA_TAG_LCD,   "LCD  ARIA TAGGED  " },
                    { "ARIA FACE",    "LCD  ARIA FACE    " },
                    { ARIA_PRESENCE,  "     ARIA Presence" },
                    { ARIA_AI_BLOCK,  "AI   ARIA AI      " },
                };

                // Scan target grid (or first ARIA grid if no target)
                var statusGrid = grid ?? (_ariaGrids.Count > 0 ? _ariaGrids[0] : g);
                var foundNames = new System.Collections.Generic.HashSet<string>();
                try
                {
                    foreach (var block in statusGrid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        var t2 = block as Sandbox.ModAPI.IMyTerminalBlock;
                        var name = t2?.CustomName?.Trim() ?? "";
                        if (checkBlocks.ContainsKey(name))
                            foundNames.Add(name);
                    }
                }
                catch { }

                foreach (var kv in checkBlocks)
                {
                    bool found = foundNames.Contains(kv.Key);
                    bool required = kv.Key == ARIA_CORE || kv.Key == ARIA_PB;
                    string status;
                    if (!found)
                        status = required ? "[!!] MISSING" : "[--] not installed";
                    else if (kv.Key == ARIA_PB)
                    {
                        // Check CustomData for ARIA_INSTALLED marker written on inhabit
                        bool pbScriptOk = false;
                        try
                        {
                            foreach (var blk2 in statusGrid.GetFatBlocks())
                            {
                                var t3 = blk2 as Sandbox.ModAPI.IMyTerminalBlock;
                                if (t3?.CustomName?.Trim() != ARIA_PB) continue;
                                pbScriptOk = t3.CustomData?.Contains("ARIA_INSTALLED") ?? false;
                                break;
                            }
                        }
                        catch { }
                        status = pbScriptOk ? "[OK] script loaded" : "[??] paste ARIA PB script";
                    }
                    else
                        status = "[OK] found";
                    sb.AppendLine(kv.Value + "  " + status);
                }

                sb.AppendLine("");
                bool coreOk = foundNames.Contains(ARIA_CORE);
                bool pbOk   = foundNames.Contains(ARIA_PB);
                bool bridgeOk = _sessionUp;
                sb.AppendLine("Bridge:  " + BRIDGE_URL);
                sb.AppendLine("ARIA:    " + (coreOk && pbOk
                    ? "[ONLINE]" : "[OFFLINE - see above]"));

                lcd.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
                lcd.FontSize    = 0.55f;
                lcd.WriteText(sb.ToString());
                return;
            }
        }


        // Set ARIA presence indicator -- light or LCD color


        // Block named "ARIA Presence" on the grid changes color to show ARIA is aboard
        private void SetPresenceIndicator(MyCubeGrid grid, bool present)
        {
            if (grid == null || grid.MarkedForClose) return;
            try
            {
                foreach (var block in grid.GetFatBlocks())
                {
                    if (block == null || block.MarkedForClose) continue;
                    var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                    if (t?.CustomName?.Trim() != ARIA_PRESENCE &&
                        !_bridges.Any(c => t?.CustomName?.Trim() == GetLcdName(c, "Presence"))) continue;

                    // Try as light block (interior light, spotlight etc.)
                    var light = block as Sandbox.ModAPI.IMyLightingBlock;
                    if (light != null)
                    {
                        light.Color = present
                            ? new VRageMath.Color(0, 180, 255)   // ARIA blue
                            : new VRageMath.Color(80, 80, 80);   // dark grey = absent
                        light.Intensity = present ? 3.0f : 0.5f;
                        break;
                    }

                    // Try as LCD panel
                    var lcd = block as Sandbox.ModAPI.IMyTextPanel;
                    if (lcd != null)
                    {
                        // Find which bridge owns this grid
                        var presenceBridge = _bridges.FirstOrDefault(c =>
                            grid.GetFatBlocks().Any(b =>
                                (b as Sandbox.ModAPI.IMyTerminalBlock)?.CustomName?.Trim() == c.CoreBlockName));
                        var presenceName = presenceBridge != null
                            ? (char.ToUpperInvariant(presenceBridge.ActivationName[0])
                               + presenceBridge.ActivationName.Substring(1)).ToUpperInvariant()
                            : "ARIA";

                        lcd.ContentType = VRage.Game.GUI.TextPanel.ContentType.TEXT_AND_IMAGE;
                        lcd.FontSize    = 2.0f;
                        lcd.Alignment   = VRage.Game.GUI.TextPanel.TextAlignment.CENTER;
                        lcd.BackgroundColor = present
                            ? new VRageMath.Color(0, 60, 120)
                            : new VRageMath.Color(20, 20, 20);
                        lcd.FontColor = present
                            ? new VRageMath.Color(0, 200, 255)
                            : new VRageMath.Color(80, 80, 80);
                        lcd.WriteText(present ? $"{presenceName} ONLINE" : $"{presenceName} OFFLINE");
                        AriaLog.Info($"{presenceName}: Presence indicator set to {(present ? "ONLINE" : "OFFLINE")} on '{grid.DisplayName}'");
                        break;
                    }
                }
            }
            catch { }
        }

        // Write crew vitals to each bridge's CREW LCD
        // Uses faction roster as the source of truth -- shows ALL members
        // with online/offline status, not just whoever happens to be logged in.
        private void WriteCrewLcd()
        {
            var session = MySession.Static;
            if (session?.Players == null || session?.Factions == null) return;

            // Build online player lookup -- steamId -> character data
            var onlinePlayers = session.Players.GetOnlinePlayers();
            var onlineById = new System.Collections.Generic.Dictionary<long, IMyPlayer>();
            if (onlinePlayers != null)
                foreach (var p in onlinePlayers)
                    if (!p.IsBot && p.Identity != null)
                        onlineById[p.Identity.IdentityId] = p;

            foreach (var ctx in _bridges.ToList())
            {
                if (!ctx.BridgeUp) continue;

                var displayName = char.ToUpperInvariant(ctx.ActivationName[0])
                                + ctx.ActivationName.Substring(1).ToUpperInvariant();

                var sb          = new StringBuilder();
                var crewJson    = new System.Text.StringBuilder();
                crewJson.Append("[");
                bool firstCrew  = true;

                sb.AppendLine($"=== {displayName} CREW === {DateTime.Now:HH:mm:ss}");
                sb.AppendLine("---");

                // Get all faction members for this bridge's allowed factions
                var factionMembers = new System.Collections.Generic.List<(string name, long identityId, string factionTag)>();

                if (ctx.AllowedFactions.Count > 0)
                {
                    foreach (var factionTag in ctx.AllowedFactions)
                    {
                        try
                        {
                            var faction = session.Factions?.TryGetFactionByTag(factionTag);
                            if (faction == null)
                            {
                                AriaLog.Debug($"WriteCrewLcd: Faction '{factionTag}' not found for bridge '{ctx.Name}'");
                                continue;
                            }

                            foreach (var memberKv in faction.Members)
                            {
                                try
                                {
                                    var identity = session.Players?.TryGetIdentity(memberKv.Value.PlayerId);
                                    if (identity == null) continue;
                                    if (identity.DisplayName == "Space Wolf" ||
                                        identity.DisplayName == "Wolf") continue;
                                    factionMembers.Add((identity.DisplayName, identity.IdentityId, factionTag));
                                }
                                catch (Exception memberEx)
                                {
                                    AriaLog.Debug($"WriteCrewLcd: Member lookup error: {memberEx.Message}");
                                }
                            }
                        }
                        catch (Exception factionEx)
                        {
                            AriaLog.Warn($"WriteCrewLcd: Faction lookup error for '{factionTag}': {factionEx.Message}");
                        }
                    }
                }
                else
                {
                    // No faction filter -- fall back to online players only
                    if (onlinePlayers != null)
                        foreach (var p in onlinePlayers)
                            if (!p.IsBot && p.Identity != null)
                                factionMembers.Add((p.DisplayName, p.Identity.IdentityId, ""));
                }

                if (factionMembers.Count == 0)
                {
                    sb.AppendLine("No crew registered.");
                }

                foreach (var (memberName, identityId, factionTag) in factionMembers)
                {
                    bool online = onlineById.TryGetValue(identityId, out var player);
                    var  ch     = online ? player?.Character : null;

                    if (online && ch != null)
                    {
                        // ── Online member -- full vitals ──────────────────────
                        float hp  = 100f;
                        float o2  = 0f;
                        float nrg = 0f;
                        float h2  = -1f;

                        try { var mc = ch as MyCharacter; if (mc?.StatComp != null) hp = mc.StatComp.HealthRatio * 100f; } catch { }
                        try { var mc = ch as MyCharacter; o2  = (mc?.OxygenComponent?.SuitOxygenLevel ?? 0f) * 100f; } catch { }
                        try { var mc = ch as MyCharacter; nrg = (mc?.SuitEnergyLevel ?? 0f) * 100f; } catch { }
                        try
                        {
                            var mc      = ch as MyCharacter;
                            var gasComp = mc?.OxygenComponent;
                            if (gasComp != null)
                            {
                                var method = gasComp.GetType().GetMethod("GetGasFillLevel",
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic |
                                    System.Reflection.BindingFlags.Instance);
                                var hydroIdField = gasComp.GetType().GetField("HydrogenId",
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.Static);
                                if (method != null && hydroIdField != null)
                                {
                                    var hydroId = hydroIdField.GetValue(null);
                                    h2 = (float)(method.Invoke(gasComp, new[] { hydroId }) ?? -1f) * 100f;
                                }
                            }
                        } catch { }

                        var seat = ch.Parent as Sandbox.ModAPI.IMyShipController;
                        string loc = seat != null
                            ? $"Cockpit ({seat.CubeGrid?.DisplayName ?? "?"})" : "EVA";
                        var cPos = ch.PositionComp.GetPosition();

                        sb.AppendLine($"{memberName} [ONLINE]");
                        sb.AppendLine($"  {loc}");
                        string h2Str = h2 > 0f ? $"  H2:{h2:F0}%" : "";
                        sb.AppendLine($"  HP:{hp:F0}%  O2:{o2:F0}%  NRG:{nrg:F0}%{h2Str}");
                        sb.AppendLine($"  Pos:({cPos.X:F0},{cPos.Y:F0},{cPos.Z:F0})");
                        if (hp  < 30f) sb.AppendLine("  !! CRITICAL HEALTH !!");
                        if (o2  < 20f) sb.AppendLine("  !! LOW OXYGEN !!");
                        if (nrg < 20f) sb.AppendLine("  !! LOW SUIT BATTERY !!");
                        if (h2 > 0f && h2 < 25f) sb.AppendLine("  !! LOW SUIT HYDROGEN !!");

                        // JSON for bridge
                        if (!firstCrew) crewJson.Append(",");
                        firstCrew = false;
                        crewJson.Append("{");
                        crewJson.Append($"\"name\":\"{Esc(memberName)}\",\"online\":true,");
                        crewJson.Append($"\"hp\":{hp:F0},\"o2\":{o2:F0},\"suit\":{nrg:F0},");
                        crewJson.Append($"\"location\":\"{Esc(loc)}\",");
                        crewJson.Append($"\"pos_x\":{cPos.X:F0},\"pos_y\":{cPos.Y:F0},\"pos_z\":{cPos.Z:F0}");
                        crewJson.Append("}");

                        // Post telemetry for online members only
                        var telemetry = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "{{\"tick\":{0},\"player_id\":\"{1}\",\"player_name\":\"{2}\"," +
                            "\"pos_x\":{3:F0},\"pos_y\":{4:F0},\"pos_z\":{5:F0}," +
                            "\"health\":{6:F1},\"oxygen\":{7:F4},\"energy\":{8:F4},\"suit_h2\":{9:F4}," +
                            "\"in_cockpit\":{10},\"cockpit_grid\":\"{11}\",\"cockpit_grid_id\":\"{12}\"," +
                            "\"ship_speed\":0,\"gravity\":0," +
                            "\"jetpack\":false,\"dampeners\":true}}",
                            _tick, identityId, Esc(memberName),
                            cPos.X, cPos.Y, cPos.Z,
                            hp, o2 / 100f, nrg / 100f, h2 > 0f ? h2 / 100f : -1f,
                            seat != null ? "true" : "false",
                            Esc(seat?.CubeGrid?.DisplayName ?? ""),
                            seat?.CubeGrid?.EntityId ?? 0L);

                        ctx.Outbound.Enqueue("/telemetry", telemetry);
                    }
                    else
                    {
                        // ── Offline member -- roster entry only ───────────────
                        sb.AppendLine($"{memberName} [OFFLINE]");

                        if (!firstCrew) crewJson.Append(",");
                        firstCrew = false;
                        crewJson.Append("{");
                        crewJson.Append($"\"name\":\"{Esc(memberName)}\",\"online\":false,");
                        crewJson.Append("\"hp\":null,\"o2\":null,\"suit\":null,");
                        crewJson.Append("\"location\":\"offline\",");
                        crewJson.Append("\"pos_x\":null,\"pos_y\":null,\"pos_z\":null");
                        crewJson.Append("}");
                    }
                }

                crewJson.Append("]");

                // Write CREW LCD
                string text    = sb.ToString();
                string lcdName = GetLcdName(ctx, "CREW");
                foreach (var grid in _ariaGrids)
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    try
                    {
                        foreach (var block in grid.GetFatBlocks())
                        {
                            if (block == null || block.MarkedForClose) continue;
                            var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                            if (t?.CustomName?.Trim() != lcdName) continue;
                            var lcd = block as Sandbox.ModAPI.IMyTextPanel;
                            if (lcd != null) { lcd.WriteText(text); break; }
                        }
                    }
                    catch { }
                }

                // Post crew_status to bridge
                ctx.Outbound.Enqueue("/crew_status",
                    $"{{\"crew_text\":\"{Esc(text)}\",\"crew_members\":{crewJson}}}");
            }
        }

        // Send a command to the ARIA PB on a given grid (game thread)
        private void SendToPb(long gridId, string command)
        {
            var ownerCtx = BridgeForGridId(gridId);
            if (ownerCtx == null || !ownerCtx.PbMap.TryGetValue(gridId, out long pbId)) return;
            try
            {
                foreach (var grid in _ariaGrids)
                {
                    if (grid.EntityId != gridId) continue;
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block?.EntityId != pbId) continue;
                        var pb = block as Sandbox.ModAPI.IMyProgrammableBlock;
                        pb?.Run(command, UpdateType.Script);
                        AriaLog.Info($"Gate1: '{command}' -> '{grid.DisplayName}'");
                        return;
                    }
                }
            }
            catch (Exception ex) { AriaLog.Error($"Gate1 error: {command}", ex); }
        }

        // =====================================================================
        // HTTP TIMER -- runs on its own thread, NEVER touches SE
        // Drains outbound queue and polls bridge for alerts
        // =====================================================================

        private void OnHttpTimer(object s, ElapsedEventArgs e)
        {
            // Drain outbound queue for ALL bridges (up to 10 items per bridge per timer fire)
            foreach (var ctx in _bridges.ToList())
            {
                if (!ctx.BridgeUp) continue;
                string endpoint, body;
                int drained = 0;
                while (ctx.Outbound.TryDequeue(out endpoint, out body) && drained < 10)
                {
                    _ = PostToUrlAsync(ctx.Url + endpoint, body);
                    drained++;
                }
            }

            // Poll all bridges for pending alerts every ~2 seconds (10 x 200ms)
            // Relay nodes are serialized via semaphore so this just queues behind any in-progress GET
            if (_httpTimerCount % 10 == 0)
                _ = DeliverAlertsAsync();

            // Poll for Gate 1 commands every ~2 seconds
            // (was 1s but relay GETs take up to 8s -- semaphore prevents pileup)
            if (_httpTimerCount % 10 == 0)
                _ = PollCommandsAsync();

            // Poll for on-demand scan requests every ~2 seconds
            if (_httpTimerCount % 10 == 0)
                _ = PollScanRequestAsync();

            _httpTimerCount++;
        }
        private int _httpTimerCount = 0;

        private async Task HealthCheckAsync()
        {
            var gridSnapshot = _ariaGrids.ToList();
            foreach (var ctx in _bridges.ToList())
            {
                try
                {
                    var r  = await _httpFast.GetAsync($"{ctx.Url}/health");
                    bool up = r.IsSuccessStatusCode;
                    if (up && !ctx.BridgeUp)
                    {
                        AriaLog.Info($"ARIA: Bridge '{ctx.Name}' connected! Re-announcing grids.");
                        // Flush any stale alerts that queued while bridge was down
                        string stale;
                        int flushed = 0;
                        while (ctx.Inbound.TryDequeue(out stale)) flushed++;
                        if (flushed > 0) AriaLog.Info($"ARIA: Flushed {flushed} stale alert(s) from '{ctx.Name}'.");
                        foreach (var g in gridSnapshot)
                        {
                            if (g == null || g.MarkedForClose) continue;
                            if (!ctx.PbMap.ContainsKey(g.EntityId)) continue;
                            // Only re-announce grids that belong to this bridge
                            bool ownedByCtx = false;
                            try
                            {
                                foreach (var b in g.GetFatBlocks())
                                {
                                    var t = b as Sandbox.ModAPI.IMyTerminalBlock;
                                    if (t?.CustomName?.Trim() == ctx.CoreBlockName) { ownedByCtx = true; break; }
                                }
                            }
                            catch { }
                            if (!ownedByCtx) continue;
                            _ = PostToUrlAsync(ctx.Url + "/aria_inhabit",
                                "{\"grid_name\":\"" + Esc(g.DisplayName ?? "") + "\"," +
                                "\"inhabited\":true,\"reason\":\"bridge_reconnect\"}");
                        }
                        _ = PostAdapterContextAsync(ctx);
                    }
                    else if (!up && ctx.BridgeUp)
                    {
                        AriaLog.Warn($"ARIA: Bridge '{ctx.Name}' lost.");
                        if (!string.IsNullOrEmpty(ctx.NodeId)) SaveNodesConfig();
                    }
                    ctx.BridgeUp = up;
                    if (up && !string.IsNullOrEmpty(ctx.NodeId)) SaveNodesConfig();
                    if (up) AriaLog.Debug($"ARIA: Bridge '{ctx.Name}' health OK");
                }
                catch
                {
                    if (ctx.BridgeUp) AriaLog.Warn($"ARIA: Bridge '{ctx.Name}' unreachable.");
                    ctx.BridgeUp = false;
                }
            }
        }

        // =====================================================================
        // On-demand scan polling -- HTTP thread, sets flag for sim thread
        // =====================================================================

        private async Task PollScanRequestAsync()
        {
            foreach (var ctx in _bridges.ToList())
            {
                if (!ctx.BridgeUp) continue;
                try
                {
                    string body;
                    if (!string.IsNullOrEmpty(ctx.NodeId))
                    {
                        // Relay node -- use relay GET mechanism, not direct HTTP
                        body = await RelayGetAsync(ctx.NodeId, "/scan_requested",
                            "{\"scan_requested\":false}");
                    }
                    else
                    {
                        var r = await _httpFast.GetAsync($"{ctx.Url}/scan_requested");
                        if (!r.IsSuccessStatusCode) continue;
                        body = await r.Content.ReadAsStringAsync();
                    }
                    if (body.Contains("\"scan_requested\":true") || body.Contains("\"scan\":true"))
                        _immediateScanPending = true;
                }
                catch { }
            }
        }

        // =====================================================================
        // Gate 1 -- Poll all bridges for queued commands and execute via PB
        // =====================================================================

        private async Task PollCommandsAsync()
        {
            foreach (var ctx in _bridges.ToList())
            {
                if (!ctx.BridgeUp) continue;
                try
                {
                    string body;
                    if (!string.IsNullOrEmpty(ctx.NodeId))
                    {
                        // Relay node -- pull via relay GET mechanism
                        body = await RelayGetAsync(ctx.NodeId, "/commands",
                            "{\"commands\":[],\"count\":0}");
                    }
                    else
                    {
                        var r = await _httpFast.GetAsync($"{ctx.Url}/commands");
                        if (!r.IsSuccessStatusCode) continue;
                        body = await r.Content.ReadAsStringAsync();
                    }
                    if (string.IsNullOrEmpty(body)) continue;

                    int cmdCount = 0;
                    var countMatch = System.Text.RegularExpressions.Regex.Match(
                        body, @"""count""\s*:\s*(\d+)");
                    if (countMatch.Success)
                        int.TryParse(countMatch.Groups[1].Value, out cmdCount);
                    if (cmdCount == 0) continue;

                    AriaLog.Info($"Gate1: {cmdCount} command(s) from bridge '{ctx.Name}'.");

                    var fullMatches = System.Text.RegularExpressions.Regex.Matches(
                        body, @"""full""\s*:\s*""([^""]+)""");
                    var gridMatches = System.Text.RegularExpressions.Regex.Matches(
                        body, @"""grid_name""\s*:\s*""([^""]*)""");

                    for (int ci2 = 0; ci2 < fullMatches.Count; ci2++)
                    {
                        string fullCmd  = fullMatches[ci2].Groups[1].Value;
                        string gridName = ci2 < gridMatches.Count
                            ? gridMatches[ci2].Groups[1].Value : "";
                        if (!string.IsNullOrEmpty(fullCmd))
                        {
                            // Format: "bridgeName||gridName|command"
                            _pendingPbCommands.Enqueue(ctx.Name + "||" + gridName + "|" + fullCmd);
                            AriaLog.Info($"Gate1: Queued '{fullCmd}' for '{gridName}' from bridge '{ctx.Name}'");
                        }
                    }
                }
                catch (Exception ex) { AriaLog.Debug($"Command poll error ({ctx.Name}): {ex.Message}"); }
            }
        }

        private async Task DeliverAlertsAsync()
        {
            foreach (var ctx in _bridges.ToList())
            {
                if (!ctx.BridgeUp) continue;
                try
                {
                    string body;
                    if (!string.IsNullOrEmpty(ctx.NodeId))
                    {
                        // Relay node -- pull via relay GET mechanism
                        body = await RelayGetAsync(ctx.NodeId, "/pending_alerts",
                            "{\"alerts\":[],\"count\":0}");
                    }
                    else
                    {
                        var r = await _httpFast.GetAsync($"{ctx.Url}/pending_alerts");
                        if (!r.IsSuccessStatusCode) continue;
                        body = await r.Content.ReadAsStringAsync();
                    }
                    if (string.IsNullOrEmpty(body)) continue;

                    var countStr = ExtractValue(body, "count");
                    if (countStr == "0" || string.IsNullOrEmpty(countStr)) continue;
                    AriaLog.Info($"Alerts from bridge '{ctx.Name}': count={countStr}");

                    // Parse alert array
                    var aStart = body.IndexOf("[", body.IndexOf("\"alerts\""));
                    var aEnd   = body.IndexOf("]", aStart);
                    if (aStart < 0 || aEnd < 0) continue;
                    var section = body.Substring(aStart + 1, aEnd - aStart - 1);

                    int pos = 0;
                    while (pos < section.Length)
                    {
                        var q = section.IndexOf('"', pos);
                        if (q < 0) break;
                        var sb2 = new StringBuilder();
                        for (int i2 = q + 1; i2 < section.Length; i2++)
                        {
                            if (section[i2] == '\\' && i2+1 < section.Length)
                                { sb2.Append(section[i2+1]); i2++; }
                            else if (section[i2] == '"') { pos = i2+1; break; }
                            else sb2.Append(section[i2]);
                        }
                        var m = sb2.ToString().Trim();
                        if (!string.IsNullOrEmpty(m))
                        {
                            // Prefix alert with sender name so DrainAlerts can use correct chat sender
                            ctx.Inbound.Enqueue($"{ctx.Name}|{m}");
                        }
                    }
                }
                catch (Exception ex) { AriaLog.Debug($"Alert poll error ({ctx.Name}): {ex.Message}"); }
            }
        }


        private async Task PostAsync(BridgeContext ctx, string endpoint, string body)
        {
            if (!ctx.BridgeUp) return;
            try
            {
                var content  = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"{ctx.Url}{endpoint}", content);
                if (!response.IsSuccessStatusCode)
                    AriaLog.Warn($"POST {ctx.Name}{endpoint} -> {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                AriaLog.Debug($"POST {ctx.Name}{endpoint} failed: {ex.Message}");
            }
        }

        // Legacy -- post to primary bridge (used by existing code)
        private async Task PostAsync(string endpoint, string body)
        {
            var ctx = PrimaryBridge;
            if (ctx != null) await PostAsync(ctx, endpoint, body);
        }

        // Post to all active bridges
        private async Task PostToAllAsync(string endpoint, string body)
        {
            foreach (var ctx in _bridges.ToList())
                if (ctx.BridgeUp)
                    await PostAsync(ctx, endpoint, body);
        }

        // Enqueue to all bridge outbound queues (shared ship data)
        private void EnqueueAll(string endpoint, string body)
        {
            foreach (var ctx in _bridges.ToList())
                ctx.Outbound.Enqueue(endpoint, body);
        }

        // Enqueue to only the bridge(s) whose faction matches this player (by faction tag)
        private void EnqueueForPlayer(string factionTag, string endpoint, string body)
        {
            // Send data only to bridges that explicitly claim this faction.
            // A bridge with no faction filter (Count == 0) is unconfigured --
            // it does NOT get data for players in specific factions.
            // Only send to a no-filter bridge if the player has NO faction either.
            foreach (var ctx in _bridges.ToList())
            {
                if (ctx.AllowedFactions.Count == 0 && string.IsNullOrEmpty(factionTag))
                {
                    // Both unconfigured -- send to this bridge
                    ctx.Outbound.Enqueue(endpoint, body);
                }
                else if (ctx.AllowedFactions.Count > 0 &&
                         factionTag != null &&
                         ctx.AllowedFactions.Contains(factionTag))
                {
                    // Faction matches explicitly -- send to this bridge
                    ctx.Outbound.Enqueue(endpoint, body);
                }
                // Otherwise -- faction mismatch, skip this bridge entirely
            }
            // No fallback -- if no bridge claims this player's faction, discard.
        }

        // Enqueue to only the bridge(s) whose faction matches this player (by steam ID lookup)
        private void EnqueueForPlayer(ulong steamId, string endpoint, string body)
        {
            string playerFaction = null;
            try
            {
                long identityId = 0;
                var sess = MySession.Static;
                if (sess?.Players != null)
                    foreach (var p in sess.Players.GetOnlinePlayers())
                        if (p.Id.SteamId == steamId)
                        { identityId = p.Identity?.IdentityId ?? 0; break; }
                if (identityId > 0)
                {
                    var pf = MySession.Static?.Factions?.TryGetPlayerFaction(identityId);
                    playerFaction = pf?.Tag;
                }
            }
            catch { }
            EnqueueForPlayer(playerFaction, endpoint, body);
        }

        // Post to a full URL -- used for fleet commands to sister bridges
        private async Task PostToUrlAsync(string fullUrl, string body)
        {
            try
            {
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                await _http.PostAsync(fullUrl, content);
            }
            catch (Exception ex) { AriaLog.Debug($"Fleet POST failed: {ex.Message}"); }
        }

        // =====================================================================
        // =====================================================================
        // RelayGetAsync
        // For relay nodes that are internet-remote (no NodeBridgeUrl reachable),
        // we queue a GET request, the bridge polls /relay/get_request, executes
        // the GET locally, and POSTs back via /relay/get_response.
        //
        // For relay nodes on the same LAN (NodeBridgeUrl set and reachable),
        // we call NodeBridgeUrl directly -- same as pre-r10 behaviour.
        //
        // Serialized via per-node semaphore to prevent concurrent relay GETs
        // from producing orphaned reqIds.
        // =====================================================================
        private async Task<string> RelayGetAsync(string nodeId, string endpoint, string fallback)
        {
            // Find the bridge context to check if NodeBridgeUrl is available
            BridgeContext nodeCtx = null;
            lock (_bridgeLock)
                nodeCtx = _bridges.FirstOrDefault(b => b.NodeId == nodeId);

            // If NodeBridgeUrl is set, try it directly first (LAN relay nodes like UBER)
            if (nodeCtx != null && !string.IsNullOrEmpty(nodeCtx.NodeBridgeUrl))
            {
                try
                {
                    var r = await _httpFast.GetAsync(nodeCtx.NodeBridgeUrl + endpoint);
                    if (r.IsSuccessStatusCode)
                        return await r.Content.ReadAsStringAsync();
                }
                catch
                {
                    // NodeBridgeUrl unreachable -- fall through to relay GET mechanism
                    AriaLog.Debug($"RelayGetAsync: NodeBridgeUrl unreachable for {endpoint}, using relay");
                }
            }

            // Internet relay path -- serialize via semaphore
            var sem = GetNodeLock(nodeId);
            if (!await sem.WaitAsync(10000))
            {
                AriaLog.Debug($"RelayGetAsync skipped (semaphore busy): {endpoint} node={nodeId.Substring(0, 8)}");
                return fallback;
            }
            try
            {
                System.Collections.Concurrent.ConcurrentDictionary<string, string> respMap;
                lock (_nodeGetResponses)
                {
                    if (!_nodeGetResponses.ContainsKey(nodeId))
                        _nodeGetResponses[nodeId] = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                    respMap = _nodeGetResponses[nodeId];
                }

                var reqId = System.Guid.NewGuid().ToString("N").Substring(0, 8);

                System.Collections.Concurrent.ConcurrentQueue<string> getQ;
                lock (_nodeGetRequests)
                {
                    if (!_nodeGetRequests.ContainsKey(nodeId))
                        _nodeGetRequests[nodeId] = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    getQ = _nodeGetRequests[nodeId];
                }
                getQ.Enqueue($"GET|{endpoint}|{reqId}");

                // Bridge polls every 2s + local HTTP exec -- allow up to 8s
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    string result;
                    if (respMap.TryRemove(reqId, out result))
                    {
                        AriaLog.Debug($"RelayGetAsync ok: {endpoint} node={nodeId.Substring(0, 8)}");
                        return result;
                    }
                    await Task.Delay(100);
                }

                AriaLog.Debug($"RelayGetAsync timeout: {endpoint} node={nodeId.Substring(0, 8)}");
                return fallback;
            }
            catch (Exception ex)
            {
                AriaLog.Debug($"RelayGetAsync error ({endpoint}): {ex.Message}");
                return fallback;
            }
            finally
            {
                sem.Release();
            }
        }


        // Post adapter context to bridge so ARIA knows what system she's connected to
        private async Task PostAdapterContextAsync(BridgeContext ctx = null)
        {
            var targets = ctx != null
                ? new System.Collections.Generic.List<BridgeContext> { ctx }
                : _bridges.ToList();

            foreach (var c in targets)
            {
                try
                {
                    var context = !string.IsNullOrEmpty(c.AdapterContext)
                        ? c.AdapterContext : BuildDefaultAdapterContext(c);
                    var payload = "{\"context\":\"" + Esc(context) + "\"}";
                    var cnt = new StringContent(payload, Encoding.UTF8, "application/json");
                    await _http.PostAsync($"{c.Url}/adapter_context", cnt);
                    AriaLog.Info($"ARIA: Adapter context posted to bridge '{c.Name}'.");
                }
                catch (Exception ex)
                {
                    // Ignore stream errors -- relay closes connection after 200 which triggers these
                    if (!ex.Message.Contains("copying content") && !ex.Message.Contains("stream"))
                        AriaLog.Warn($"PostAdapterContext failed ({c.Name}): {ex.Message}");
                }
            }
        }


        private string BuildDefaultAdapterContext(BridgeContext ctx = null)
        {
            var gridSnapshot = _ariaGrids.ToList();
            var gridNames = string.Join(", ", gridSnapshot
                .Where(g => g != null && !g.MarkedForClose)
                .Select(g => g.DisplayName));
            var name = ctx?.Name ?? "aria";
            return
                "You are connected to a Space Engineers dedicated server via a Torch plugin. " +
                $"You are the AI designated '{name}'. " +
                "You have direct hardware access to the following ships: " + gridNames + ". " +
                "You can control: thrusters, gyros, dampeners, autopilot, cameras, " +
                "wheels, doors, landing gear, connectors, lights, air vents, gas tanks, reactors, " +
                "jump drives, warheads, turrets, drills, welders, grinders, antennas, pistons, rotors. " +
                "You monitor crew vitals including health, oxygen, suit battery, and hydrogen. " +
                $"Crew members address you as '{ctx?.ActivationName ?? "aria"}'. Respond as their ship AI.";
        }

        // =====================================================================
        // Chat interceptor -- called on Torch chat thread
        // Only enqueues to outbound queue -- no direct SE access
        // Exception: _chat.SendMessageAsOther() is Torch-safe from any thread
        // =====================================================================

        private void OnChat(TorchChatMessage msg, ref bool consumed)
        {
            if (msg.AuthorSteamId == null || msg.AuthorSteamId == 0) return;
            var message = msg.Message?.Trim();
            if (string.IsNullOrEmpty(message) || message.StartsWith("/")) return;
            var name    = msg.Author ?? "Unknown";
            var low     = message.ToLowerInvariant();
            var steamId = msg.AuthorSteamId.Value;
            // Accept global and faction channel messages (0=Global, 1=Faction, 2=Private)
            var channel = (int)msg.Channel;
            if (channel == 2) return; // ignore private/whisper messages

            // Ignore AI bots own messages
            foreach (var ctx0 in _bridges.ToList())
                if (name.Equals(ctx0.Name, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(ctx0.ActivationName, StringComparison.OrdinalIgnoreCase)) return;
            if (name == "Wolf" || name == "Good.bot") return;

            // Get player faction once -- used by all bridge checks
            long identityId = 0;
            string playerFactionTag = null;
            try
            {
                var sess = MySession.Static;
                if (sess?.Players != null)
                    foreach (var p in sess.Players.GetOnlinePlayers())
                        if (p.Id.SteamId == steamId)
                        { identityId = p.Identity?.IdentityId ?? 0; break; }
                if (identityId > 0)
                {
                    var pf = MySession.Static?.Factions?.TryGetPlayerFaction(identityId);
                    playerFactionTag = pf?.Tag;
                }
            }
            catch { }

            // Fleet command -- broadcast to ALL bridges
            bool isFleet = low.StartsWith("fleet ") || low.StartsWith("all ships ");
            if (isFleet)
            {
                var fleetMsg = low.StartsWith("fleet ")
                    ? message.Substring(6) : message.Substring(10);
                var fleetJson = "{\"player_name\":\"" + Esc(name) + "\"," +
                                "\"player_id\":\"" + steamId + "\"," +
                                "\"message\":\"" + Esc(fleetMsg) + "\"}";
                AriaLog.Info($"Fleet command from {name}: {fleetMsg}");
                foreach (var ctx in _bridges.ToList())
                    if (ctx.BridgeUp)
                        _ = PostToUrlAsync(ctx.Url + "/chat", fleetJson);
                return;
            }

            // Route message to the correct bridge(s) based on activation name + faction
            bool handled = false;
            foreach (var ctx in _bridges.ToList())
            {
                // Channel filter -- only skip if bridge requires faction AND bridge has factions set
                // If bridge has no factions (AllowedFactions.Count == 0) it listens to everyone
                if (ctx.ListenChannel == "faction" && playerFactionTag == null
                    && ctx.AllowedFactions.Count > 0) continue;

                // Faction filter
                if (ctx.ListenMode != "all" && ctx.AllowedFactions.Count > 0)
                {
                    if (playerFactionTag == null || !ctx.AllowedFactions.Contains(playerFactionTag))
                    {
                        AriaLog.Debug($"ARIA: [{ctx.Name}] blocking {name} (faction={playerFactionTag ?? "none"}, allowed=[{string.Join(",", ctx.AllowedFactions)}], mode={ctx.ListenMode})");
                        continue;
                    }
                }

                // Addressed to this bridge's AI?
                if (!low.Contains(ctx.ActivationName.ToLowerInvariant())) continue;

                AriaLog.Info($"[{ctx.Name}] Chat from {name}: {message}");

                // Projector permission -- only primary bridge handles hardware
                if (message.Equals($"{ctx.ActivationName} yes", StringComparison.OrdinalIgnoreCase))
                { _projectorGrantPending = true; _projectorGrantBy = name; handled = true; continue; }
                if (message.Equals($"{ctx.ActivationName} no", StringComparison.OrdinalIgnoreCase))
                { _projectorDenyPending = true; handled = true; continue; }

                // Forward to bridge
                var chatJson = "{\"player_name\":\"" + Esc(name) + "\"," +
                               "\"player_id\":\"" + steamId + "\"," +
                               "\"player_faction\":\"" + (playerFactionTag ?? "") + "\"," +
                               "\"message\":\"" + Esc(message) + "\"}";

                if (ctx.BridgeUp)
                {
                    var ctxCapture = ctx;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Relay node -- queue chat message, node will poll and respond
                            if (!string.IsNullOrEmpty(ctxCapture.NodeId))
                            {
                                System.Collections.Concurrent.ConcurrentQueue<string> inQ;
                                lock (_nodeInbound)
                                {
                                    if (!_nodeInbound.ContainsKey(ctxCapture.NodeId))
                                        _nodeInbound[ctxCapture.NodeId] =
                                            new System.Collections.Concurrent.ConcurrentQueue<string>();
                                    inQ = _nodeInbound[ctxCapture.NodeId];
                                }
                                inQ.Enqueue(chatJson);
                                AriaLog.Debug($"Chat queued for relay node {ctxCapture.NodeId.Substring(0,8)}");
                                return;
                            }

                            // Direct bridge -- POST immediately
                            var content2 = new StringContent(chatJson, Encoding.UTF8, "application/json");
                            var response = await _http.PostAsync($"{ctxCapture.Url}/chat", content2);
                            if (!response.IsSuccessStatusCode) return;
                            var body   = await response.Content.ReadAsStringAsync();
                            var reply  = ExtractValue(body, "response");
                            if (string.IsNullOrEmpty(reply)) return;
                            var displayName = char.ToUpperInvariant(ctxCapture.ActivationName[0])
                                             + ctxCapture.ActivationName.Substring(1);
                            AriaLog.Info($"{displayName}: {reply}");
                            SendToFaction(ctxCapture, displayName, reply);
                        }
                        catch (Exception ex) { AriaLog.Debug($"Chat relay error ({ctxCapture.Name}): {ex.Message}"); }
                    });
                    handled = true;
                }
                else
                {
                    // Bridge down -- queue message so it delivers when bridge comes back up
                    AriaLog.Warn($"ARIA: Bridge '{ctx.Name}' is down -- queuing message for retry.");
                    var nodeId = ctx.NodeId;
                    if (!string.IsNullOrEmpty(nodeId))
                    {
                        lock (_nodeInbound)
                        {
                            if (!_nodeInbound.ContainsKey(nodeId))
                                _nodeInbound[nodeId] = new System.Collections.Concurrent.ConcurrentQueue<string>();
                            _nodeInbound[nodeId].Enqueue(chatJson);
                        }
                    }
                }
            }

            if (!handled)
                AriaLog.Debug($"ARIA: No bridge handled message from {name}: '{message}'");
        }


        private void ProcessChatFlags()
        {
            if (_uninhabitPending)
            {
                _uninhabitPending = false;
                foreach (var grid in _ariaGrids)
                {
                    if (grid == null) continue;
                    long gridId = grid.EntityId;
                    var gridOwner = BridgeForGridId(gridId);
                    if (gridOwner == null) continue;

                    SetPresenceIndicator(grid, false);
                    gridOwner.PbMap.Remove(gridId);
                    AriaLog.Info($"ARIA: Uninhabiting '{grid.DisplayName}' by player request.");
                    _ = PostAsync(gridOwner, "/aria_inhabit",
                        "{\"grid_name\":\"" + Esc(grid.DisplayName) + "\",\"inhabited\":false,\"reason\":\"player_request\"}");
                    SendAiChat("Disconnecting from " + grid.DisplayName + ". Terminal offline.");
                }
            }

            if (_forceUpdatePending)
            {
                _forceUpdatePending = false;
                foreach (var kv in _pbMap)
                    SendToPb(kv.Key, "FORCE_UPDATE");
            }

            if (_scanCoresPending)
            {
                _scanCoresPending = false;
                AriaLog.Info("ARIA: Manual inhabit triggered -- scanning for cores and PBs.");
                ScanForCores();
                ScanForPb();
            }

            if (_projectorGrantPending)
            {
                _projectorGrantPending = false;
                GrantProjector(_projectorGrantBy);
            }

            if (_projectorDenyPending)
            {
                _projectorDenyPending = false;
                DenyProjector();
            }
        }

        private async Task ChatAsync(string json)
        {
            try
            {
                var content  = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"{BRIDGE_URL}/chat", content);
                if (!response.IsSuccessStatusCode) return;
                var body  = await response.Content.ReadAsStringAsync();
                var reply = ExtractValue(body, "response");
                if (string.IsNullOrEmpty(reply)) return;
                AriaLog.Info($"ARIA: {reply}");
                // SendMessageAsOther is thread-safe in Torch
                SendAiChat(reply);
            }
            catch (Exception ex) { AriaLog.Error("Chat error.", ex); }
        }

        // =====================================================================
        // Projector permission -- executed on sim thread via ProcessChatFlags()
        // =====================================================================

        private void GrantProjector(string playerName)
        {
            foreach (var grid in _ariaGrids)
            {
                if (grid == null) continue;
                long id = grid.EntityId;
                if (!_projState.TryGetValue(id, out string st) || st != "awaiting") continue;
                try
                {
                    foreach (var block in grid.GetFatBlocks())
                    {
                        if (block == null || block.MarkedForClose) continue;
                        var proj = block as Sandbox.ModAPI.IMyProjector;
                        if (proj == null) continue;
                        var t = block as Sandbox.ModAPI.IMyTerminalBlock;
                        if (t == null || t.CustomName?.Trim() == ARIA_PROJECTOR) continue;
                        t.CustomName = ARIA_PROJECTOR;
                        _projState[id] = "granted";
                        AriaLog.Info($"Projector granted on '{grid.DisplayName}' by {playerName}.");
                        SendAiChat($"Projector access confirmed on {grid.DisplayName}.");
                        SendToPb(id, "FORCE_UPDATE");
                        return;
                    }
                }
                catch { }
            }
        }

        private void DenyProjector()
        {
            foreach (var grid in _ariaGrids)
            {
                if (grid == null) continue;
                long id = grid.EntityId;
                if (!_projState.TryGetValue(id, out string st) || st != "awaiting") continue;
                _projState[id] = "denied";
                SendAiChat("Understood. Rename any Projector to 'ARIA Projector' to enable hull monitoring.");
            }
        }

        // =====================================================================
        // Utilities
        // =====================================================================

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        // Extract value starting from a known position (for array parsing)
        private static string ExtractValueAt(string json, int keyPos)
        {
            var ci = json.IndexOf(':', keyPos);
            if (ci < 0) return null;
            var qi = json.IndexOf('"', ci + 1);
            if (qi < 0) return null;
            var sb = new StringBuilder();
            for (int i = qi + 1; i < json.Length; i++)
            {
                if (json[i] == '\\' && i+1 < json.Length) { sb.Append(json[i+1]); i++; }
                else if (json[i] == '"') break;
                else sb.Append(json[i]);
            }
            return sb.ToString();
        }

        private static string ExtractValue(string json, string key)
        {
            var sk = $"\"{key}\"";
            var ki = json.IndexOf(sk, StringComparison.Ordinal);
            if (ki < 0) return null;
            var ci = json.IndexOf(':', ki + sk.Length);
            if (ci < 0) return null;
            var qi = json.IndexOf('"', ci + 1);
            if (qi < 0) return null;
            var sb = new StringBuilder();
            for (int i = qi + 1; i < json.Length; i++)
            {
                if (json[i] == '\\' && i+1 < json.Length) { sb.Append(json[i+1]); i++; }
                else if (json[i] == '"') break;
                else sb.Append(json[i]);
            }
            return sb.ToString();
        }


    }
}
