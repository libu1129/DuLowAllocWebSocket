using System.Globalization;
using System.Text;

namespace DuLowAllocWebSocket;

/// <summary>
/// permessage-deflate(RFC 7692) 협상 결과를 나타내는 압축 옵션입니다.
/// </summary>
/// <param name="Enabled">압축 확장이 협상되었는지 여부입니다.</param>
/// <param name="ClientNoContextTakeover">클라이언트가 메시지마다 압축 컨텍스트를 초기화하는지 여부입니다.</param>
/// <param name="ServerNoContextTakeover">서버가 메시지마다 압축 컨텍스트를 초기화하는지 여부입니다.</param>
/// <param name="ClientMaxWindowBits">클라이언트 deflate 윈도우 비트 크기(8~15)입니다. 미지정 시 <see langword="null"/>.</param>
/// <param name="ServerMaxWindowBits">서버 deflate 윈도우 비트 크기(8~15)입니다. 미지정 시 <see langword="null"/>.</param>
public readonly record struct CompressionOptions(
    bool Enabled,
    bool ClientNoContextTakeover,
    bool ServerNoContextTakeover,
    int? ClientMaxWindowBits,
    int? ServerMaxWindowBits);

/// <summary>
/// permessage-deflate(RFC 7692) 확장 헤더의 생성과 파싱을 담당합니다.
/// </summary>
public static class CompressionNegotiator
{
    /// <summary>
    /// 클라이언트가 서버에 제안할 permessage-deflate 확장 헤더 값을 생성합니다.
    /// </summary>
    /// <param name="options">압축 관련 설정을 포함하는 클라이언트 옵션입니다.</param>
    /// <returns>Sec-WebSocket-Extensions 헤더 값 문자열. 압축 비활성화 시 빈 문자열.</returns>
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

    /// <summary>
    /// 서버 응답의 Sec-WebSocket-Extensions 헤더를 파싱하여 협상된 압축 옵션을 반환합니다.
    /// </summary>
    /// <param name="extensionHeader">서버 응답의 확장 헤더 값입니다.</param>
    /// <returns>파싱된 압축 옵션. permessage-deflate가 없으면 <see cref="CompressionOptions.Enabled"/>가 <see langword="false"/>.</returns>
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
