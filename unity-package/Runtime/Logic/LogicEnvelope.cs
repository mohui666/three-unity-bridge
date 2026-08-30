using System;
using System.Globalization;
using UnityEngine;

namespace ThreeUnity.Bridge.Logic
{
    [Serializable]
    public sealed class LogicEnvelopeHeader
    {
        public int protocol;
        public string sessionId;
        public string type;
        public long seq;
    }

    public static class LogicEnvelopeParser
    {
        public const int ProtocolVersion = 1;
        public const int MaxSessionIdLength = 128;

        public static bool TryParseHeader(string json, out LogicEnvelopeHeader header, out string error)
        {
            header = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Logic envelope is empty.";
                return false;
            }

            try
            {
                header = JsonUtility.FromJson<LogicEnvelopeHeader>(json);
            }
            catch (ArgumentException exception)
            {
                error = "Invalid logic envelope JSON: " + exception.Message;
                return false;
            }

            if (header == null)
            {
                error = "Logic envelope must be a JSON object.";
                return false;
            }
            var hasExplicitSessionId = HasTopLevelProperty(json, "sessionId");
            if (hasExplicitSessionId && string.IsNullOrWhiteSpace(header.sessionId))
            {
                error = "Logic envelope sessionId cannot be empty or whitespace.";
                return false;
            }
            if (header.sessionId != null
                && !string.Equals(header.sessionId, header.sessionId.Trim(), StringComparison.Ordinal))
            {
                error = "Logic envelope sessionId cannot have leading or trailing whitespace.";
                return false;
            }
            if (header.sessionId != null && header.sessionId.Length > MaxSessionIdLength)
            {
                error = "Logic envelope sessionId exceeds " + MaxSessionIdLength + " characters.";
                return false;
            }
            if (header.protocol != ProtocolVersion)
            {
                error = "Unsupported logic protocol " + header.protocol;
                return false;
            }
            if (string.IsNullOrWhiteSpace(header.type))
            {
                error = "Logic envelope requires a non-empty type.";
                return false;
            }
            if (header.seq < 0)
            {
                error = "Logic envelope requires a non-negative sequence.";
                return false;
            }
            return true;
        }

        private static bool HasTopLevelProperty(string json, string propertyName)
        {
            var depth = 0;
            for (var index = 0; index < json.Length; index++)
            {
                var character = json[index];
                if (character == '{' || character == '[')
                {
                    depth++;
                    continue;
                }
                if (character == '}' || character == ']')
                {
                    depth--;
                    continue;
                }
                if (character != '"')
                    continue;

                var start = ++index;
                var escaped = false;
                while (index < json.Length)
                {
                    character = json[index];
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        break;
                    }
                    index++;
                }

                if (depth != 1 || index >= json.Length || escaped)
                    continue;
                var after = index + 1;
                while (after < json.Length && char.IsWhiteSpace(json[after]))
                    after++;
                if (after >= json.Length || json[after] != ':')
                    continue;
                if (index - start == propertyName.Length
                    && string.CompareOrdinal(json, start, propertyName, 0, propertyName.Length) == 0)
                    return true;
            }
            return false;
        }
    }

    public static class LogicEnvelopeWriter
    {
        public static string Encode(string type, long sequence, object payload)
        {
            return Encode(type, sequence, null, payload);
        }

        public static string Encode(string type, long sequence, string sessionId, object payload)
        {
            if (string.IsNullOrWhiteSpace(type))
                throw new ArgumentException("Message type is required.", nameof(type));
            if (sequence < 0)
                throw new ArgumentOutOfRangeException(nameof(sequence));
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (sessionId != null && string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session id cannot be empty or whitespace.", nameof(sessionId));
            if (sessionId != null && !string.Equals(sessionId, sessionId.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("Session id cannot have leading or trailing whitespace.", nameof(sessionId));
            if (sessionId != null && sessionId.Length > LogicEnvelopeParser.MaxSessionIdLength)
                throw new ArgumentOutOfRangeException(nameof(sessionId));

            var session = sessionId == null
                ? string.Empty
                : ",\"sessionId\":" + JsonString(sessionId);
            return "{\"protocol\":1" + session + ",\"type\":" + JsonString(type)
                + ",\"seq\":" + sequence.ToString(CultureInfo.InvariantCulture)
                + ",\"payload\":" + JsonUtility.ToJson(payload) + "}";
        }

        private static string JsonString(string value)
        {
            return JsonUtility.ToJson(new StringValue { value = value }).Substring(9, JsonUtility.ToJson(new StringValue { value = value }).Length - 10);
        }

        [Serializable]
        private sealed class StringValue
        {
            public string value;
        }
    }
}
