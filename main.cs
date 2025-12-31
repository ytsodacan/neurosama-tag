using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using GorillaLocomotion;
using BepInEx;
using GorillaTag.CosmeticSystem;
using Photon.Pun;
using Photon.Voice.Unity;
using System.IO;
using System.Reflection;
using GorillaNetworking;
using System.Runtime.InteropServices.ComTypes;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using Valve.VR;
using Fusion;
using Photon.Realtime;
using ExitGames.Client.Photon;
using System.Linq;

namespace neurogtag
{
    [BepInPlugin("com.sodacan.gorillatag.neurogtag", "NeuroGTAG", "1.0.0")]
    public class NeuroButGtag : BaseUnityPlugin
    {
        private const string WebSocketUrl = "ws://localhost:8080";
        private ClientWebSocket _webSocket;
        private Thread _socketThread;
        private bool _isSocketConnected = false;
        private static ManualLogSource _logger;

        private Harmony _harmony;
        private Rigidbody _rigidbody;
        private Transform _playerTransform;
        private Transform _headTransform;
        private Transform _cameraTransform;
        private bool wasTaggedLastFrame = false;

        private Dictionary<string, bool> _wasdState = new Dictionary<string, bool>
        {
            { "W", false },
            { "A", false },
            { "S", false },
            { "D", false }
        };
        private bool _jumpState = false;
        private bool _emoteState = false;
        private float _walkSpeed = 4f;
        private float _jumpForce = 150f; // Increased jump force
        private bool _isGrounded = true; // Track grounded state for jumping
        private float _yawInput = 0f;    // Left/right rotation
        private float _rotationSpeed = 100f; // Increased rotation speed for degrees

        private float _jumpCooldown = 0.7f;  // Cooldown timer for jumping
        private const float JUMP_COOLDOWN_DURATION = 0.7f; // 0.2 seconds cooldown

        // Telemetry fields
        private float telemetryTimer = 0f;
        private const float TELEMETRY_INTERVAL = 0.1f; // 10 Hz
        private const float FORWARD_BLOCK_DISTANCE = 0.6f; // meters to check forward
        private const float LOS_HEIGHT_OFFSET = 0.15f; // small height offset for LOS ray

        public static NeuroButGtag Instance;
        private System.Random _random = new System.Random();
        private string currentTargetMode = "casual"; // default toggle state
        private string playerName = "";
        private string roomCode = "";
        private bool leavingRoom = false;
        private string colorInput = "1.0, 0.0, 0.0"; // Default red

        // Required empty callbacks to satisfy interfaces
        public void OnConnected() { }
        public void OnConnectedToMaster() { }
        public void OnDisconnected(DisconnectCause cause) { }
        public void OnRegionListReceived(RegionHandler regionHandler) { }
        public void OnCustomAuthenticationResponse(System.Collections.Generic.Dictionary<string, object> data) { }
        public void OnCustomAuthenticationFailed(string debugMessage) { }

        public void OnPlayerEnteredRoom(Player newPlayer) { }
        public void OnPlayerLeftRoom(Player otherPlayer) { }
        public void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable propertiesThatChanged) { }
        public void OnPlayerPropertiesUpdate(Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps) { }
        public void OnMasterClientSwitched(Player newMasterClient) { }

        // Class to represent the WASD state from the state/update command
        public class WasdState
        {
            public string W { get; set; }
            public string A { get; set; }
            public string S { get; set; }
            public string D { get; set; }
        }

        // Class to represent the data part of the state/update command
        public class StateUpdateData
        {
            public WasdState WASD { get; set; }
            public string JUMP { get; set; }
            public string EMOTE { get; set; }
            public float YAW { get; set; } // Changed YAW to float
        }

        void JoinRoom()
        {
            if (!PhotonNetwork.IsConnected) return;

            if (!string.IsNullOrEmpty(playerName))
            {
                PhotonNetwork.NickName = playerName;
            }

            if (!string.IsNullOrEmpty(roomCode))
            {
                PhotonNetwork.JoinOrCreateRoom(roomCode, new RoomOptions(), TypedLobby.Default);
                Debug.Log($"Attempting to join or create room: {roomCode}");
            }
        }

        public void OnLeftRoom()
        {
            if (leavingRoom)
            {
                Debug.Log("Successfully left the room.");
                leavingRoom = false;
            }
        }

        void SetColorFromInput(string input)
        {
            string[] parts = input.Split(',');
            if (parts.Length != 3)
            {
                Debug.LogError("Invalid color format. Use: R,G,B");
                return;
            }

            if (float.TryParse(parts[0], out float r) &&
                float.TryParse(parts[1], out float g) &&
                float.TryParse(parts[2], out float b))
            {
                Vector3 colorVec = new Vector3(r, g, b);

                Hashtable props = new Hashtable
                {
                    { "color", colorVec }
                };

                PhotonNetwork.LocalPlayer.SetCustomProperties(props);
                Debug.Log($"Set Photon player color to: {colorVec}");
            }
            else
            {
                Debug.LogError("Failed to parse RGB values.");
            }
        }

        void OnGUI()
        {
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                normal = { textColor = Color.white },
                alignment = TextAnchor.UpperRight
            };

            Rect rect = new Rect(Screen.width - 220, 150, 210, 100);
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), "NEUROSAMA LOADED", new GUIStyle(style) { normal = { textColor = Color.black } });
            GUI.Label(rect, "NEUROSAMA LOADED", style);
            if (GUI.Button(new Rect(Screen.width - 160, 50, 140, 40), $"Switch to {(currentTargetMode == "infection" ? "casual" : "infection")}"))
            {
                ToggleGamemode();
            }
            GUI.Label(new Rect(20, 20, 100, 25), "Name:");
            playerName = GUI.TextField(new Rect(70, 20, 150, 25), playerName, 25);

            GUI.Label(new Rect(20, 60, 100, 25), "Room Code:");
            roomCode = GUI.TextField(new Rect(110, 60, 150, 25), roomCode, 10);

            if (GUI.Button(new Rect(270, 60, 60, 25), "Join"))
            {
                JoinRoom();
            }

            if (GUI.Button(new Rect(20, 100, 100, 25), "Leave Room"))
            {
                if (PhotonNetwork.InRoom)
                {
                    PhotonNetwork.LeaveRoom();
                    leavingRoom = true;
                    Debug.Log("Leaving room...");
                }
            }
            GUI.Label(new Rect(20, 140, 100, 25), "Color (R,G,B):");
            colorInput = GUI.TextField(new Rect(130, 140, 150, 25), colorInput, 30);

            if (GUI.Button(new Rect(290, 140, 100, 25), "Set Color"))
            {
                SetColorFromInput(colorInput);
            }
        }

        void Awake()
        {
            Instance = this;
            _logger = Logger;
            _logger.LogInfo("Plugin NEUROGTAG is loaded!");

            _harmony = new Harmony("com.sodacan.gorillatag.neurogtag");
            _harmony.PatchAll(typeof(NeuroButGtag));

            ConnectWebSocket();
        }

        void OnDestroy()
        {
            DisconnectWebSocket();
            _harmony?.UnpatchSelf();
            PhotonNetwork.RemoveCallbackTarget(this);
        }

        void ToggleGamemode()
        {
            GorillaGameManager manager = GorillaGameManager.instance;
            if (manager == null)
            {
                Debug.LogError("GorillaGameManager not found.");
                return;
            }

            if (Photon.Pun.PhotonNetwork.InRoom)
            {
                string targetMode = currentTargetMode;
                ExitGames.Client.Photon.Hashtable roomProperties = new ExitGames.Client.Photon.Hashtable();
                roomProperties["gameMode"] = targetMode;
                Photon.Pun.PhotonNetwork.CurrentRoom.SetCustomProperties(roomProperties);

                Debug.Log($"Switched gamemode to: {targetMode}");

                currentTargetMode = (targetMode == "infection") ? "casual" : "infection";
            }
            else
            {
                Debug.LogWarning("Not currently in a room.");
            }
        }

        void Update()
        {
            if (_isSocketConnected && _rigidbody != null && _playerTransform != null)
            {
                HandleMovement();
                HandleRotation();
                CheckGrounded(); // Update grounded state
                _jumpCooldown -= Time.deltaTime; // Reduce cooldown timer
            }

            // Get the actual VRRig component from the NetworkView
            VRRig myRig = GorillaTagger.Instance?.myVRRig?.GetComponent<VRRig>();
            if (myRig == null) return;

            // Use setMatIndex to determine if the local player is tagged
            bool isTaggedNow = myRig.setMatIndex == 1; // 1 usually means infected

            if (isTaggedNow && !wasTaggedLastFrame)
            {
                Debug.Log("You were just tagged!");
            }

            wasTaggedLastFrame = isTaggedNow;

            // Telemetry timer (run regardless of movement state; SendTelemetry checks connection)
            telemetryTimer -= Time.deltaTime;
            if (telemetryTimer <= 0f)
            {
                telemetryTimer = TELEMETRY_INTERVAL;
                _ = SendTelemetry(); // fire-and-forget; SendTelemetry checks socket state internally
            }
        }

        void Start()
        {
            PhotonNetwork.AddCallbackTarget(this);
        }

        private void ConnectWebSocket()
        {
            _socketThread = new Thread(async () =>
            {
                try
                {
                    _webSocket = new ClientWebSocket();
                    await _webSocket.ConnectAsync(new Uri(WebSocketUrl), CancellationToken.None);
                    _isSocketConnected = true;
                    _logger.LogInfo("Connected to WebSocket server.");
                    await SendInitMessage();
                    await ReceiveData();
                }
                catch (Exception e)
                {
                    _logger.LogError($"WebSocket connection error: {e}");
                    _isSocketConnected = false;
                }
            });
            _socketThread.Start();
        }

        private void DisconnectWebSocket()
        {
            if (_webSocket != null)
            {
                try
                {
                    _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Plugin unloaded", CancellationToken.None).Wait();
                }
                catch (Exception e)
                {
                    _logger.LogWarning($"Error closing websocket: {e}");
                }
                _webSocket.Dispose();
            }
            _isSocketConnected = false;
            if (_socketThread != null)
            {
                _socketThread.Join();
            }
        }

        private async Task SendInitMessage()
        {
            if (_isSocketConnected && _webSocket.State == WebSocketState.Open)
            {
                string initMessage = "INIT";
                byte[] buffer = Encoding.UTF8.GetBytes(initMessage);
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                _logger.LogInfo("Sent INIT message.");
            }
        }

        private async Task ReceiveData()
        {
            try
            {
                byte[] buffer = new byte[4096];
                while (_isSocketConnected && _webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        ProcessServerMessage(message);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInfo("WebSocket connection closed by server.");
                        _isSocketConnected = false;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Error receiving data: {e}");
                _isSocketConnected = false;
            }
        }

        private void ProcessServerMessage(string message)
        {
            try
            {
                // Try parsing JSON first
                try
                {
                    var jsonDocument = JsonDocument.Parse(message);
                    if (jsonDocument.RootElement.TryGetProperty("command", out var commandElement) && commandElement.GetString() == "state/update")
                    {
                        if (jsonDocument.RootElement.TryGetProperty("data", out var dataElement))
                        {
                            try
                            {
                                var stateData = JsonSerializer.Deserialize<StateUpdateData>(dataElement.GetRawText());
                                if (stateData?.WASD != null)
                                {
                                    _wasdState["W"] = stateData.WASD.W == "1";
                                    _wasdState["A"] = stateData.WASD.A == "1";
                                    _wasdState["S"] = stateData.WASD.S == "1";
                                    _wasdState["D"] = stateData.WASD.D == "1";
                                }
                                if (stateData?.JUMP != null)
                                {
                                    _jumpState = stateData.JUMP == "1";
                                    if (_jumpState) Jump();
                                }
                                _yawInput = stateData.YAW;

                                _logger.LogInfo($"Received state update - W: {stateData?.WASD?.W}, A: {stateData?.WASD?.A}, S: {stateData?.WASD?.S}, D: {stateData?.WASD?.D}, Jump: {stateData?.JUMP}, Yaw: {stateData?.YAW}");
                            }
                            catch (JsonException e)
                            {
                                _logger.LogError($"Error deserializing state/update data: {e}");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Received 'state/update' command without data.");
                        }
                    }
                    else
                    {
                        // Could be other JSON messages in future - ignore or log
                        _logger.LogDebug($"Received JSON (unhandled): {message}");
                    }
                    return;
                }
                catch (JsonException)
                {
                    // Not JSON - fall through to legacy parsing below
                }

                // Legacy, colon-separated parsing
                string[] parts = message.Split(':');
                if (parts.Length < 2) return;
                string command = parts[0].ToUpper();
                switch (command)
                {
                    case "WASD":
                        if (parts.Length == 5)
                        {
                            _wasdState["W"] = parts[1] == "1";
                            _wasdState["A"] = parts[2] == "1";
                            _wasdState["S"] = parts[3] == "1";
                            _wasdState["D"] = parts[4] == "1";
                        }
                        break;
                    case "JUMP":
                        if (parts.Length == 2)
                        {
                            _jumpState = parts[1] == "1";
                            if (_jumpState) Jump();
                        }
                        break;
                    case "TURN":
                        if (parts.Length == 2)
                        {
                            if (float.TryParse(parts[1], out float turnValue))
                            {
                                _yawInput = turnValue;
                            }
                            else
                            {
                                _logger.LogWarning($"Invalid TURN value: {parts[1]}");
                                _yawInput = 0f;
                            }
                        }
                        break;
                    default:
                        _logger.LogWarning($"Received unknown command from server: {command}");
                        break;
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Error processing message: {e}");
            }
        }

        private void HandleMovement()
        {
            if (_rigidbody == null || _playerTransform == null || _cameraTransform == null) return;

            Vector3 movement = Vector3.zero;
            if (_wasdState["W"]) movement += _cameraTransform.forward;
            if (_wasdState["S"]) movement += -_cameraTransform.forward;
            if (_wasdState["A"]) movement += -_cameraTransform.right;
            if (_wasdState["D"]) movement += _cameraTransform.right;

            if (movement.magnitude > 1) movement.Normalize();

            Vector3 targetVelocity = movement * _walkSpeed;
            Quaternion playerRotation = _playerTransform.rotation;

            Vector3 worldVelocity = playerRotation * targetVelocity;

            Vector3 velocityChange = worldVelocity - new Vector3(_rigidbody.linearVelocity.x, 0, _rigidbody.linearVelocity.z);
            _rigidbody.AddForce(velocityChange, ForceMode.VelocityChange);
        }

        private void HandleRotation()
        {
            if (_cameraTransform == null) return;

            _cameraTransform.Rotate(Vector3.up, _yawInput * _rotationSpeed * Time.deltaTime);
        }

        private void Jump()
        {
            _logger.LogInfo($"Jump: _isGrounded = {_isGrounded}, _jumpCooldown = {_jumpCooldown}, _jumpState = {_jumpState}");
            if (_rigidbody != null && _isGrounded && _jumpCooldown <= 0f && _jumpState)
            {
                _logger.LogInfo("Jump: Conditions met, applying jump force");
                _rigidbody.AddForce(Vector3.up * _jumpForce, ForceMode.Impulse);
                _isGrounded = false;
                _jumpCooldown = JUMP_COOLDOWN_DURATION;
            }
            else
            {
                _logger.LogInfo($"Jump: Conditions not met, not jumping.");
            }
        }

        private void CheckGrounded()
        {
            if (_rigidbody == null || _playerTransform == null)
            {
                return;
            }
            RaycastHit hit;
            if (Physics.Raycast(_playerTransform.position, Vector3.down, out hit, 0.1f))
            {
                _logger.LogInfo("CheckGrounded: Raycast hit, setting _isGrounded to true");
                _isGrounded = true;
            }
            else
            {
                _logger.LogInfo("CheckGrounded: Raycast did not hit, setting _isGrounded to false");
                _isGrounded = false;
            }
        }

        private bool CheckForwardBlocked(out RaycastHit hitInfo)
        {
            hitInfo = default;

            if (_cameraTransform == null) return false;

            Vector3 origin = _cameraTransform.position;
            Vector3 direction = _cameraTransform.forward;

            if (Physics.Raycast(origin, direction, out hitInfo, FORWARD_BLOCK_DISTANCE))
            {
                // ignore triggers
                if (!hitInfo.collider.isTrigger)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasLineOfSight(VRRig targetRig)
        {
            if (targetRig == null || _cameraTransform == null) return false;

            Vector3 origin = _cameraTransform.position + Vector3.up * LOS_HEIGHT_OFFSET;
            Vector3 target = targetRig.transform.position + Vector3.up * LOS_HEIGHT_OFFSET;

            if (Physics.Linecast(origin, target, out RaycastHit hit))
            {
                // If we hit something that is part of the target rig, LOS is good.
                if (hit.transform != null && hit.transform.IsChildOf(targetRig.transform))
                {
                    return true;
                }

                // If the linecast hit something else, LOS is blocked
                return false;
            }

            // No hit on the linecast means clear LOS
            return true;
        }

        private async Task SendTelemetry()
        {
            try
            {
                if (!_isSocketConnected || _webSocket == null || _webSocket.State != WebSocketState.Open)
                    return;

                VRRig myRig = GorillaTagger.Instance?.myVRRig;
                if (myRig == null) return;

                Vector3 myPos = myRig.transform.position;
                bool myTagged = myRig.setMatIndex == 1;
                string myName = PhotonNetwork.LocalPlayer?.NickName ?? "Unknown";

                VRig closestRig = null;
                float closestDist = float.MaxValue;

                // Find closest VRRig (exclude self)
                foreach (VRRig rig in FindObjectsOfType<VRRig>())
                {
                    if (rig == myRig) continue;
                    if (rig.creator == null) continue;

                    float dist = Vector3.Distance(myPos, rig.transform.position);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestRig = rig;
                    }
                }

                bool los = closestRig != null && HasLineOfSight(closestRig);

                bool forwardBlocked = CheckForwardBlocked(out RaycastHit blockHit);

                object payload = new
                {
                    type = "telemetry",

                    self = new
                    {
                        name = myName,
                        position = new { x = myPos.x, y = myPos.y, z = myPos.z },
                        tagged = myTagged
                    },

                    closestPlayer = closestRig == null ? null : new
                    {
                        name = closestRig.creator.NickName,
                        position = new
                        {
                            x = closestRig.transform.position.x,
                            y = closestRig.transform.position.y,
                            z = closestRig.transform.position.z
                        },
                        tagged = closestRig.setMatIndex == 1,
                        distance = closestDist,
                        lineOfSight = los
                    },

                    forwardBlocked = forwardBlocked,
                    forwardBlock = forwardBlocked ? new
                    {
                        collider = blockHit.collider != null ? blockHit.collider.name : "unknown",
                        distance = blockHit.distance
                    } : null
                };

                string json = JsonSerializer.Serialize(payload);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
            }
            catch (Exception e)
            {
                _logger.LogError($"SendTelemetry exception: {e}");
            }
        }

        [HarmonyPatch(typeof(GTPlayer), "Awake")]
        [HarmonyPostfix]
        static void Postfix_PlayerMovementAwake(GTPlayer __instance)
        {
            _logger.LogInfo("Postfix_PlayerMovementAwake called.");
            NeuroButGtag instance = NeuroButGtag.Instance;
            if (instance != null)
            {
                try
                {
                    instance._rigidbody = __instance.GetComponent<Rigidbody>();
                    instance._playerTransform = __instance.transform;
                    instance._headTransform = __instance.transform.Find("rig/head");
                    _logger.LogInfo($"Postfix_PlayerMovementAwake: _headTransform assigned: {instance._headTransform != null}");
                    Camera mainCamera = Camera.main;
                    if (mainCamera != null)
                    {
                        instance._cameraTransform = mainCamera.transform;
                        _logger.LogInfo("Postfix_PlayerMovementAwake: Camera Transform found");
                    }
                    else
                    {
                        _logger.LogWarning("Postfix_PlayerMovementAwake: Main camera not found.  Rotation may be incorrect.");
                    }

                    if (instance._rigidbody != null && instance._playerTransform != null)
                    {
                        _logger.LogInfo("Postfix_PlayerMovementAwake: Rigidbody and Player Transform assigned successfully.");
                    }
                    else
                    {
                        _logger.LogError("Postfix_PlayerMovementAwake: Failed to assign Rigidbody or Player Transform.");
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError($"Postfix_PlayerMovementAwake: Exception occurred: {e}");
                }
            }
            else
            {
                _logger.LogError("Postfix_PlayerMovementAwake: instance is null!");
            }
        }
    }
}
