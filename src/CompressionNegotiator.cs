using System.Globalization;

namespace DuLowAllocWebSocket;

public readonly record struct CompressionOptions(
    bool Enabled,
    bool ClientNoContextTakeover,
    bool ServerNoContextTakeover,
    int? ClientMaxWindowBits);

public static class CompressionNegotiator
{
    public static string BuildClientOfferHeader() =>
        "permessage-deflate; client_no_context_takeover; server_no_context_takeover; client_max_window_bits";

    public static CompressionOptions ParseNegotiatedOptions(ReadOnlySpan<char> extensionHeader)
    {
        if (extensionHeader.IsEmpty)
        {
            return new CompressionOptions(false, false, false, null);
        }

        var header = extensionHeader.ToString();
        var extensions = header.Split(',');
        foreach (var ext in extensions)
        {
            var trimmed = ext.Trim();
            if (!trimmed.StartsWith("permessage-deflate", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool clientNoContextTakeover = false;
            bool serverNoContextTakeover = false;
            int? clientMaxWindowBits = null;

            var tokens = trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 1; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.Equals("client_no_context_takeover", StringComparison.OrdinalIgnoreCase))
                {
                    clientNoContextTakeover = true;
                    continue;
                }

                if (token.Equals("server_no_context_takeover", StringComparison.OrdinalIgnoreCase))
                {
                    serverNoContextTakeover = true;
                    continue;
                }

                if (token.StartsWith("client_max_window_bits", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = token.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits))
                    {
                        clientMaxWindowBits = bits;
                    }
                }
            }

            return new CompressionOptions(true, clientNoContextTakeover, serverNoContextTakeover, clientMaxWindowBits);
        }

        return new CompressionOptions(false, false, false, null);
    }
}
