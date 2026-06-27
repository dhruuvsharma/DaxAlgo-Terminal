using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Production signature inspector. Extracts the Authenticode signer certificate and verifies the
/// embedded signature via <c>WinVerifyTrust</c> (so a tampered file fails verification, not just an
/// unsigned one). Windows-only. Every failure path returns a non-trusted result (<c>Unsigned</c> or
/// <c>IsValid: false</c>) rather than throwing — so a verification problem can only make the trust
/// policy REJECT a plugin, never wrongly accept one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeSignatureInspector : IPluginSignatureInspector
{
    public PluginSignature Inspect(string assemblyPath)
    {
        if (!OperatingSystem.IsWindows())
            return PluginSignature.Unsigned;

        string thumbprint, subject;
        try
        {
            // CreateFromSignedFile pulls the embedded signer cert; it throws when the file is unsigned.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(assemblyPath));
            thumbprint = cert.Thumbprint;
            subject = cert.Subject;
        }
        catch
        {
            return PluginSignature.Unsigned;
        }

        var valid = VerifyEmbeddedSignature(assemblyPath);
        return new PluginSignature(IsSigned: true, IsValid: valid, Thumbprint: thumbprint, Subject: subject);
    }

    /// <summary>Verifies the file's embedded Authenticode signature (integrity + cert chain) via
    /// WinVerifyTrust. Returns false on any error — the safe direction.</summary>
    private static bool VerifyEmbeddedSignature(string path)
    {
        var pFile = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, pFile, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_IGNORE,
                dwProvFlags = WTD_SAFER_FLAG,
            };

            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0; // S_OK: signature present and valid
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pFile != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }

    // ── WinVerifyTrust interop ──────────────────────────────────────────────────────────────────────

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private const uint WTD_SAFER_FLAG = 0x100;

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
