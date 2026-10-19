using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace ViaSonic;

/// <summary>API Key 等用 Windows DPAPI 加密后存进 config.json，只有本机当前用户能解开。</summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";

    public static string Get(JsonObject c, string key)
    {
        var v = c.Str(key);
        if (!v.StartsWith(Prefix)) return v;
        try
        {
            return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(v[Prefix.Length..])));
        }
        catch (Exception e)
        {
            Log.Warn($"{key}: {e.Message}"); // 换了电脑或用户，解不开就当没填
            return "";
        }
    }

    public static string Protect(string plain) =>
        plain.Length == 0 ? "" : Prefix + Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(plain)));

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;

    private static byte[] Protect(byte[] data) => Run(data, true);
    private static byte[] Unprotect(byte[] data) => Run(data, false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "ViaSonic", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, output.Size);
                return bytes;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            h.Free();
        }
    }
}
