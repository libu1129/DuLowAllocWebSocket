using System.Globalization;
using System.Text;

namespace DuLowAllocWebSocket;

public readonly record struct CompressionOptions(
    bool Enabled,
    bool ClientNoContextTakeover,
    bool ServerNoContextTakeover,
    int? ClientMaxWindowBits,
    int? ServerMaxWindowBits);

public static class CompressionNegotiator
{
    public static string BuildClientOfferHeader(WebSocketClientOptions options)
    {
        if (!options.EnablePerMessageDeflate)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(96);
        sb.Append("permessage-deflate");

        if (!options.ClientContextTakeover)
        {
            sb.Append("; client_no_context_takeover");
        }

        if (!options.ServerContextTakeover)
        {
            sb.Append("; server_no_context_takeover");
        }

        if (options.ClientMaxWindowBits is int clientBits)
        {
            sb.Append("; client_max_window_bits=");
            sb.Append(clientBits.ToString(CultureInfo.InvariantCulture));
        }

        if (options.ServerMaxWindowBits is int serverBits)
        {
            sb.Append("; server_max_window_bits=");
            sb.Append(serverBits.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public static CompressionOptions ParseNegotiatedOptions(ReadOnlySpan<char> extensionHeader)
    {
        if (extensionHeader.IsEmpty)
        {
            return new CompressionOptions(false, false, false, null, null);
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
            int? serverMaxWindowBits = null;

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

                    continue;
                }

                if (token.StartsWith("server_max_window_bits", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = token.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits))
                    {
                        serverMaxWindowBits = bits;
                    }
                }
            }

            return new CompressionOptions(true, clientNoContextTakeover, serverNoContextTakeover, clientMaxWindowBits, serverMaxWindowBits);
        }

        return new CompressionOptions(false, false, false, null, null);
    }
}
