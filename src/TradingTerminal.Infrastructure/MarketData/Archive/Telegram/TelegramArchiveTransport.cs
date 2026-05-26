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
    /// <summary>Set by the explicit-credentials <see cref="EnsureConnectedAsync(TelegramArchiveOptions, CancellationToken)"/>
    /// overload to bypass the IOptionsMonitor reload debounce. Null = fall back to options.</summary>
    private TelegramArchiveOptions? _activeOpts;

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

    /// <summary>Open (or resume) the WTelegramClient session using whatever
    /// <see cref="IOptionsMonitor{TOptions}"/> currently has. Safe to call repeatedly — the
    /// session file makes re-runs cheap. Use the explicit-credentials overload from the login
    /// screen to avoid the configuration-reload race after Save().</summary>
    public Task EnsureConnectedAsync(CancellationToken ct = default) =>
        EnsureConnectedAsync(_opts.CurrentValue, ct);

    /// <summary>Open (or resume) the session using the supplied credentials. The VM uses this
    /// overload right after Save() because <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>
    /// lags the on-disk JSON by however long the file-watcher debounce takes — long enough that
    /// the first connect attempt sees stale (often empty) values and trips WTelegramClient's
    /// "value cannot be an empty string" guard.</summary>
    public async Task EnsureConnectedAsync(TelegramArchiveOptions opts, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null && _selfUser is not null) return;

            if (opts.ApiId <= 0 || string.IsNullOrWhiteSpace(opts.ApiHash))
                throw new InvalidOperationException(
                    "Telegram api_id / api_hash are not configured. Set them in Archive Settings.");

            // Pin the credentials for the duration of LoginUserIfNeeded so ConfigCallback's reads
            // match what the caller validated, not whatever IOptionsMonitor happens to hold right
            // now. Cleared in finally regardless.
            _activeOpts = opts;
            _client = new Client(ConfigCallback);
            _selfUser = await _client.LoginUserIfNeeded().ConfigureAwait(false);
            _logger.LogInformation("Telegram archive transport ready as @{Username} (id {Id})",
                _selfUser.username ?? "(no username)", _selfUser.id);
        }
        finally
        {
            _activeOpts = null;
            _gate.Release();
        }
    }

    private string? ConfigCallback(string key)
    {
        // Snapshot of the credentials to use. _activeOpts wins (set explicitly by the login flow)
        // over IOptionsMonitor.CurrentValue (which lags Save() due to the file-watcher debounce).
        var opts = _activeOpts ?? _opts.CurrentValue;
        switch (key)
        {
            case "api_id":
                return opts.ApiId > 0
                    ? opts.ApiId.ToString()
                    : throw new InvalidOperationException("Telegram api_id is not configured.");
            case "api_hash":
                return !string.IsNullOrWhiteSpace(opts.ApiHash)
                    ? opts.ApiHash
                    : throw new InvalidOperationException("Telegram api_hash is not configured.");
            case "phone_number":
                if (!string.IsNullOrWhiteSpace(opts.PhoneNumber)) return opts.PhoneNumber;
                return RequirePrompt("phone_number",
                    "Phone number is required to sign in. Add it in Archive Settings or type it when prompted.");
            case "verification_code":
                return RequirePrompt("verification_code",
                    "Verification code is required. Telegram sent it to your phone or the Telegram app on another device.");
            case "password":
                return RequirePrompt("password",
                    "Two-factor password is required (your Telegram account has 2FA enabled).");
            case "first_name":
                // Only requested if this phone number is signing up to Telegram for the first time.
                var fn = _prompt.PromptAsync("first_name", CancellationToken.None).GetAwaiter().GetResult();
                return string.IsNullOrWhiteSpace(fn) ? "DaxAlgo" : fn;
            case "last_name":
                var ln = _prompt.PromptAsync("last_name", CancellationToken.None).GetAwaiter().GetResult();
                return string.IsNullOrWhiteSpace(ln) ? "Archive" : ln;
            case "session_pathname":
                // Configuration binders happily turn an absent JSON value into "" rather than null;
                // treat empty-or-whitespace the same as missing so DefaultSessionPath() fires.
                return string.IsNullOrWhiteSpace(opts.SessionFilePath)
                    ? DefaultSessionPath()
                    : opts.SessionFilePath;
            default:
                return null;
        }
    }

    /// <summary>Show the auth prompt and treat cancel / empty input as an explicit abort instead
    /// of letting WTelegramClient surface its less-friendly "value cannot be an empty string"
    /// exception. The login flow catches <see cref="OperationCanceledException"/> separately.</summary>
    private string RequirePrompt(string key, string friendlyMessage)
    {
        var value = _prompt.PromptAsync(key, CancellationToken.None).GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(value)) return value;
        throw new OperationCanceledException(friendlyMessage);
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
