using System.Text.Json.Nodes;

public static class Helper
{
    public static JsonObject GetJsonObject(BinaryData data)
    {
        var parsed = JsonNode.Parse(data);
        if (parsed == null) throw new InvalidOperationException("Failed to parse JSON from event data");
        return parsed.AsObject();
    }

    public static string GetCallerId(JsonObject jsonObject)
    {
        try
        {
            var fromNode = jsonObject["from"];
            if (fromNode == null) throw new InvalidOperationException("'from' field not found in event");

            var rawIdNode = fromNode["rawId"];
            if (rawIdNode == null) throw new InvalidOperationException("'from.rawId' field not found in event");

            var rawId = rawIdNode.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawId)) throw new InvalidOperationException("'from.rawId' is empty");

            // Handle SIP format: "sip:+14155551234@sbc.example.com"
            if (rawId.Contains("@"))
            {
                rawId = rawId.Split('@')[0];
            }

            if (rawId.StartsWith("sip:"))
            {
                rawId = rawId.Substring(4);
            }

            // Ensure E.164 format
            if (!rawId.StartsWith("+") && !rawId.StartsWith("4:"))
            {
                rawId = "+" + rawId;
            }

            return rawId;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to extract caller ID from event: {ex.Message}", ex);
        }
    }

    public static string GetIncomingCallContext(JsonObject jsonObject)
    {
        try
        {
            var contextNode = jsonObject["incomingCallContext"];
            if (contextNode == null) throw new InvalidOperationException("'incomingCallContext' field not found in event");

            var context = contextNode.GetValue<string>();
            if (string.IsNullOrWhiteSpace(context)) throw new InvalidOperationException("IncomingCallContext is empty");

            var parts = context.Split('.');
            if (parts.Length < 2)
            {
                throw new InvalidOperationException($"IncomingCallContext has invalid format. Expected at least 2 parts, got {parts.Length}");
            }

            if (context.Length < 50)
            {
                throw new InvalidOperationException($"IncomingCallContext suspiciously short ({context.Length} chars).");
            }

            // Validate JWT payload is decodable
            try
            {
                var payload = parts[1];
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var decodedBytes = Convert.FromBase64String(payload);
                if (decodedBytes.Length == 0)
                {
                    throw new InvalidOperationException("Decoded JWT payload is empty");
                }
            }
            catch (Exception decodeEx)
            {
                throw new InvalidOperationException($"IncomingCallContext JWT payload is malformed: {decodeEx.Message}");
            }

            return context;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to extract/validate IncomingCallContext: {ex.Message}", ex);
        }
    }
}
