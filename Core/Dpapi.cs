using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AIMeter.Core;

/// <summary>Windows DPAPI (current-user scope): only this Windows user on this machine can decrypt.</summary>
internal static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIMeter.tokens.v1");

    public static string Protect(string plaintext)
        => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plaintext), protect: true));

    public static string Unprotect(string protectedBase64)
        => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedBase64), protect: false));

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DATA_BLOB();
        var entropy = new DATA_BLOB();
        var output = new DATA_BLOB();
        var inHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = inHandle.AddrOfPinnedObject();
            entropy.cbData = Entropy.Length;
            entropy.pbData = entHandle.AddrOfPinnedObject();

            bool ok = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            inHandle.Free();
            entHandle.Free();
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
