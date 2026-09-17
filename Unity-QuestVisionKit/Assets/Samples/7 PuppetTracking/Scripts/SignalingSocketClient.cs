using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

namespace QuestCameraKit.WebRTC {
    public enum SignalingConnectionState {
        Idle,
        Connecting,
        Connected,
        Disconnected,
        Failed
    }

    // Thin wrapper around NativeWebSocket.WebSocket speaking the pipe-delimited signaling
    // protocol. Not a MonoBehaviour: the owner must call Pump() every frame from its own
    // Update() - that is the only place OnMessage/OnOpen/OnClose actually get dispatched,
    // since NativeWebSocket buffers everything on a background thread otherwise.
    public sealed class SignalingSocketClient : IDisposable {
        private const float MinReconnectDelaySeconds = 1f;
        private const float MaxReconnectDelaySeconds = 10f;

        private readonly string _serverUrl;
        private readonly bool _autoReconnect;
        private readonly object _lock = new object();
        private readonly Queue<SignalingEnvelope> _incoming = new Queue<SignalingEnvelope>();

        private WebSocket _socket;
        private bool _wantsConnection;
        private float _reconnectDelay = MinReconnectDelaySeconds;
        private float _reconnectAt = -1f;

        private volatile bool _openedFlag;
        private volatile bool _closedFlag;
        private string _pendingError;

        public SignalingConnectionState State { get; private set; } = SignalingConnectionState.Idle;
        public string LocalPeerId { get; }
        public string LastError { get; private set; }

        public event Action OnConnected;
        public event Action<SignalingConnectionState> OnDisconnected;
        public event Action<SignalingEnvelope> OnEnvelope;

        public SignalingSocketClient(string serverUrl, string localPeerId, bool autoReconnect = true) {
            _serverUrl = serverUrl;
            LocalPeerId = localPeerId;
            _autoReconnect = autoReconnect;
        }

        public void Connect() {
            _wantsConnection = true;
            ConnectInternal();
        }

        private void ConnectInternal() {
            State = SignalingConnectionState.Connecting;
            _socket = new WebSocket(_serverUrl);

            _socket.OnOpen += () => { _openedFlag = true; };
            _socket.OnMessage += bytes => {
                var text = Encoding.UTF8.GetString(bytes);
                if (!SignalingEnvelope.TryParse(text, out var envelope)) return;
                if (envelope.SenderPeerId == LocalPeerId) return; // insurance against a duplicated peer id
                lock (_lock) { _incoming.Enqueue(envelope); }
            };
            _socket.OnError += error => { _pendingError = error; };
            _socket.OnClose += _ => { _closedFlag = true; };

            _ = _socket.Connect(); // fire-and-forget: Connect() runs the receive loop until the socket dies
        }

        // Must be called every frame from the owner's Update().
        public void Pump() {
#if !UNITY_WEBGL || UNITY_EDITOR
            _socket?.DispatchMessageQueue();
#endif

            if (_openedFlag) {
                _openedFlag = false;
                _reconnectDelay = MinReconnectDelaySeconds;
                State = SignalingConnectionState.Connected;
                OnConnected?.Invoke();
            }

            while (true) {
                SignalingEnvelope envelope;
                lock (_lock) {
                    if (_incoming.Count == 0) break;
                    envelope = _incoming.Dequeue();
                }
                OnEnvelope?.Invoke(envelope);
            }

            if (_pendingError != null) {
                LastError = _pendingError;
                _pendingError = null;
                Debug.LogWarning($"[SignalingSocketClient] {LastError}");
            }

            if (_closedFlag) {
                _closedFlag = false;
                State = _wantsConnection ? SignalingConnectionState.Failed : SignalingConnectionState.Disconnected;
                OnDisconnected?.Invoke(State);
                if (_autoReconnect && _wantsConnection) {
                    _reconnectAt = Time.unscaledTime + _reconnectDelay;
                    _reconnectDelay = Mathf.Min(_reconnectDelay * 2f, MaxReconnectDelaySeconds);
                }
            }

            if (_reconnectAt >= 0f && _wantsConnection && Time.unscaledTime >= _reconnectAt) {
                _reconnectAt = -1f;
                ConnectInternal();
            }
        }

        public void Send(SignalingMessageType type, string receiverPeerId, string message,
            int connectionCount, bool isVideoAudioSender = true) {
            if (State != SignalingConnectionState.Connected) {
                Debug.LogWarning($"[SignalingSocketClient] Dropping {type} to {receiverPeerId} - not connected.");
                return;
            }
            var formatted = SignalingEnvelope.Format(type, LocalPeerId, receiverPeerId, message, connectionCount, isVideoAudioSender);
            _ = _socket.SendText(formatted);
        }

        public async Task CloseAsync() {
            _wantsConnection = false;
            if (_socket != null) {
                await _socket.Close();
            }
        }

        public void Dispose() {
            _wantsConnection = false;
            _socket?.Close();
            _socket = null;
        }
    }
}
