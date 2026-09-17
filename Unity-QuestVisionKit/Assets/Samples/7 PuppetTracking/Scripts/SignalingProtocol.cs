using System;

namespace QuestCameraKit.WebRTC {
    public enum SignalingMessageType {
        NEWPEER,
        NEWPEERACK,
        OFFER,
        ANSWER,
        CANDIDATE,
        DATA,
        DISPOSE,
        COMPLETE,
        OTHER
    }

    // Wire format: "TYPE|SenderPeerId|ReceiverPeerId|Message|ConnectionCount|IsVideoAudioSender".
    // Matches the protocol already implemented by SignalingServer/public/client.js and originally
    // defined by the SimpleWebRTC package's SignalingMessage.cs - kept identical so the existing
    // server/browser viewer needs no changes.
    public readonly struct SignalingEnvelope {
        public const string BroadcastPeerId = "ALL";

        public readonly SignalingMessageType Type;
        public readonly string SenderPeerId;
        public readonly string ReceiverPeerId;
        public readonly string Message;
        public readonly int ConnectionCount;
        public readonly bool IsVideoAudioSender;

        private SignalingEnvelope(SignalingMessageType type, string senderPeerId, string receiverPeerId,
            string message, int connectionCount, bool isVideoAudioSender) {
            Type = type;
            SenderPeerId = senderPeerId;
            ReceiverPeerId = receiverPeerId;
            Message = message;
            ConnectionCount = connectionCount;
            IsVideoAudioSender = isVideoAudioSender;
        }

        public bool IsAddressedTo(string localPeerId) => ReceiverPeerId == localPeerId;
        public bool IsBroadcast => ReceiverPeerId == BroadcastPeerId;

        public static bool TryParse(string raw, out SignalingEnvelope envelope) {
            envelope = default;
            if (string.IsNullOrEmpty(raw)) return false;

            var parts = raw.Split('|');
            if (parts.Length < 6) return false;

            if (!Enum.TryParse(parts[0], out SignalingMessageType type)) {
                type = SignalingMessageType.OTHER;
            }

            // Message is normally parts[3], but rejoin defensively in case the SDP/candidate JSON
            // ever contains a literal '|' - the last two fields are always fixed-position.
            var message = string.Join("|", parts, 3, parts.Length - 5);

            if (!int.TryParse(parts[parts.Length - 2], out var connectionCount)) connectionCount = 0;
            if (!bool.TryParse(parts[parts.Length - 1], out var isVideoAudioSender)) isVideoAudioSender = false;

            envelope = new SignalingEnvelope(type, parts[1], parts[2], message, connectionCount, isVideoAudioSender);
            return true;
        }

        public static string Format(SignalingMessageType type, string senderPeerId, string receiverPeerId,
            string message, int connectionCount, bool isVideoAudioSender) {
            return $"{type}|{senderPeerId}|{receiverPeerId}|{message}|{connectionCount}|{isVideoAudioSender}";
        }
    }

    [Serializable]
    public class SdpPayload {
        public string type;
        public string sdp;
    }

    [Serializable]
    public class IceCandidatePayload {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex;
    }
}
