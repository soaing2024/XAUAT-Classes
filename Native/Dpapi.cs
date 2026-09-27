using System.Runtime.InteropServices;
using System.Text;

namespace XauatSchedule.Native;

/// <summary>
/// Windows DPAPI（CryptProtectData / CryptUnprotectData）：
/// 用当前 Windows 账户的密钥加密密码，密文只能在本机、本账户下解开。
/// 这样"刷新"可以一键完成，而磁盘上不会出现明文密码。
/// </summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pInput, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob pOutput);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pInput, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob pOutput);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>加密成 base64；失败返回 null（不抛，调用方按"没存过"处理）。</summary>
    public static string? Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = Encoding.UTF8.GetBytes(plain);
        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            if (!CryptProtectData(ref input, "XauatSchedule", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output)) return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return Convert.ToBase64String(result);
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    /// <summary>解密；失败（换机器 / 换用户 / 数据损坏）返回 null。</summary>
    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch { return null; }

        var input = new DataBlob { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                var text = Encoding.UTF8.GetString(result);
                Array.Clear(result, 0, result.Length);
                return text;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }
}
