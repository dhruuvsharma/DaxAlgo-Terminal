using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TL;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;
using WTelegram;

namespace TradingTerminal.Infrastructure.MarketData.Archive.Telegram;

/// <summary>
/// <see cref="IArchiveTransport"/> backed by WTelegramClient (MTProto user-account API). Ships
/// archive parts to Telegram as documents — Saved Messages by default, or any chat / channel the
/// signed-in account can post to. Native 2 GB upload limit; we sit below that with
/// <see cref="ArchiveOptions.MaxPartBytes"/> headroom.
///
/// The session file (auth keys + DC mapping) lives in <see cref="TelegramArchiveOptions.SessionFilePath"/>
/// — typically %LocalAppData%/DaxAlgoTerminal/telegram-session.bin — so subsequent runs skip the
/// phone/code dance. First-time login uses the injected <see cref="ITelegramAuthPrompt"/> bridge
/// so the UI can pop a dialog asking for the verification code and (when enabled) 2FA password.
/// </summary>
public sealed class TelegramArchiveTransport : IArchiveTransport, IAsyncDisposable
{
    private readonly IOptionsMonitor<TelegramArchiveOptions> _opts;
    private readonly ITelegramAuthPrompt _prompt;
    private readonly ILogger<TelegramArchiveTransport> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Client? _client;
    private User? _selfUser;

    public TelegramArchiveTransport(
        IOptionsMonitor<TelegramArchiveOptions> opts,
        ITelegramAuthPrompt prompt,
        ILogger<TelegramArchiveTransport> logger)
    {
        _opts = opts;
        _prompt = prompt;
        _logger = logger;
    }

    public string Name => "telegram";

    public bool IsReady => _client is not null && _selfUser is not null;

    /// <summary>Open (or resume) the WTelegramClient session. Safe to call repeatedly — the
    /// session file makes re-runs cheap. First call may take seconds while WTelegramClient
    /// connects to the DC and runs the auth flow.</summary>
    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null && _selfUser is not null) return;

            var opts = _opts.CurrentValue;
            if (opts.ApiId <= 0 || string.IsNullOrWhiteSpace(opts.ApiHash))
                throw new InvalidOperationException(
                    "Telegram api_id / api_hash are not configured. Set them in Archive Settings.");

            _client = new Client(ConfigCallback);
            _selfUser = await _client.LoginUserIfNeeded().ConfigureAwait(false);
            _logger.LogInformation("Telegram archive transport ready as @{Username} (id {Id})",
                _selfUser.username ?? "(no username)", _selfUser.id);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? ConfigCallback(string key)
    {
        var opts = _opts.CurrentValue;
        switch (key)
        {
            case "api_id": return opts.ApiId.ToString();
            case "api_hash": return opts.ApiHash;
            case "phone_number":
                return !string.IsNullOrWhiteSpace(opts.PhoneNumber)
                    ? opts.PhoneNumber
                    : _prompt.PromptAsync("phone_number", CancellationToken.None).GetAwaiter().GetResult();
            case "verification_code":
                return _prompt.PromptAsync("verification_code", CancellationToken.None).GetAwaiter().GetResult();
            case "password":
                return _prompt.PromptAsync("password", CancellationToken.None).GetAwaiter().GetResult();
            case "first_name":
                return _prompt.PromptAsync("first_name", CancellationToken.None).GetAwaiter().GetResult() ?? "DaxAlgo";
            case "last_name":
                return _prompt.PromptAsync("last_name", CancellationToken.None).GetAwaiter().GetResult() ?? "Archive";
            case "session_pathname":
                return opts.SessionFilePath ?? DefaultSessionPath();
            default:
                return null;
        }
    }

    public async Task<ArchiveBlobRef> UploadAsync(
        Stream content, string displayName, long contentLength,
        ArchiveTarget target,
        IProgress<long>? progress, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var peer = await ResolvePeerAsync(target, ct).ConfigureAwait(false);

        // WTelegramClient handles chunked upload + large file split automatically. We just hand
        // it the stream. UploadFileAsync returns an InputFileBase ready to attach.
        var uploaded = await _client!.UploadFileAsync(content, displayName).ConfigureAwait(false);

        // Wrap as a generic document so Telegram renders it as a download tile (not an image /
        // video preview). DocumentAttributeFilename preserves the part name so the user sees
        // "daxalgo-marketdata-2026-W21.zip.part01" in their chat.
        var media = new InputMediaUploadedDocument
        {
            file = uploaded,
            mime_type = "application/zip",
            attributes = new DocumentAttribute[] { new DocumentAttributeFilename { file_name = displayName } },
        };
        var msg = await _client.SendMessageAsync(peer, string.Empty, media).ConfigureAwait(false);

        // Extract the Document reference from the resulting message so we can re-fetch later.
        // SendMessageAsync returns the inserted Message; cast to Message and read its Media.
        long messageId = msg.id;
        if (msg.media is not MessageMediaDocument mediaDoc || mediaDoc.document is not Document doc)
            throw new InvalidOperationException(
                "Telegram accepted the upload but the resulting message has no document — unexpected.");

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message_id"] = messageId.ToString(),
            ["document_id"] = doc.id.ToString(),
            ["access_hash"] = doc.access_hash.ToString(),
            ["dc_id"] = doc.dc_id.ToString(),
            ["target_kind"] = target.Kind,
        };
        if (target.ChatRef is not null) metadata["target_chat_ref"] = target.ChatRef;

        return new ArchiveBlobRef(
            TransportName: Name,
            PartName: displayName,
            SizeBytes: contentLength,
            Sha256Hex: string.Empty, // archiver fills in / verifies separately
            Metadata: metadata);
    }

    public async Task DownloadAsync(
        ArchiveBlobRef blob, Stream destination,
        IProgress<long>? progress, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (!blob.Metadata.TryGetValue("document_id", out var docIdStr) ||
            !blob.Metadata.TryGetValue("access_hash", out var hashStr) ||
            !blob.Metadata.TryGetValue("dc_id", out var dcStr))
            throw new InvalidOperationException(
                $"Archive blob '{blob.PartName}' is missing Telegram metadata — cannot download.");

        var doc = new Document
        {
            id = long.Parse(docIdStr),
            access_hash = long.Parse(hashStr),
            dc_id = int.Parse(dcStr),
            size = blob.SizeBytes,
            // file_reference is reconstructed lazily by WTelegramClient; for older messages we'd
            // refresh via Channels_GetMessages / Messages_GetMessages, but the library handles
            // FILE_REFERENCE_EXPIRED retries automatically when the underlying chat is known.
            file_reference = Array.Empty<byte>(),
        };
        await _client!.DownloadFileAsync(doc, destination).ConfigureAwait(false);
    }

    private async Task<InputPeer> ResolvePeerAsync(ArchiveTarget target, CancellationToken ct)
    {
        if (target.IsSavedMessages) return InputPeer.Self;
        if (string.IsNullOrWhiteSpace(target.ChatRef))
            throw new ArgumentException("ArchiveTarget.Chat requires a non-empty ChatRef.");

        var refStr = target.ChatRef.TrimStart('@');
        var resolved = await _client!.Contacts_ResolveUsername(refStr).ConfigureAwait(false);
        // The resolved.peer tells us whether it's a user/chat/channel; pull the matching entity
        // out of the parallel users/chats arrays Telegram returns alongside.
        switch (resolved.peer)
        {
            case PeerUser pu:
                foreach (var u in resolved.users.Values)
                    if (u.id == pu.user_id) return new InputPeerUser(u.id, u.access_hash);
                break;
            case PeerChannel pc:
                foreach (var c in resolved.chats.Values)
                    if (c is Channel ch && ch.id == pc.channel_id)
                        return new InputPeerChannel(ch.id, ch.access_hash);
                break;
            case PeerChat pchat:
                return new InputPeerChat(pchat.chat_id);
        }
        throw new InvalidOperationException($"Could not resolve Telegram chat '{target.ChatRef}'.");
    }

    private static string DefaultSessionPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DaxAlgoTerminal", "telegram-session.bin");

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            _client.Dispose();
            _client = null;
        }
        await Task.CompletedTask;
    }
}
