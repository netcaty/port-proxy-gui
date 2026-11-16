using System.Runtime.InteropServices;
using System.Text;

namespace PortProxyManager.Services;

/// <summary>
/// 把子进程输出的原始字节按系统 ANSI 代码页（GetACP）解码。
/// netsh / netstat 等工具直接写 ANSI 字节，硬编码 936 只在中文系统正确；
/// 用 GetACP 可以适配任意语言版本，且不依赖 System.Text.Encoding.CodePages 包。
/// </summary>
internal static class AnsiText
{
    private const uint CpAcp = 0;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte[] lpMultiByteStr, int cbMultiByte,
        char[]? lpWideCharStr, int cchWideChar);

    private static readonly uint Acp = SafeAcp();

    private static uint SafeAcp()
    {
        try
        {
            var acp = GetACP();
            return acp == 0 ? 936u : acp;
        }
        catch
        {
            return 936u;
        }
    }

    public static string Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;

        try
        {
            var chars = MultiByteToWideChar(Acp, 0, bytes, bytes.Length, null, 0);
            if (chars <= 0) return Encoding.UTF8.GetString(bytes);

            var buffer = new char[chars];
            var written = MultiByteToWideChar(Acp, 0, bytes, bytes.Length, buffer, chars);
            if (written <= 0) return Encoding.UTF8.GetString(bytes);

            return new string(buffer, 0, written);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public static byte[] ReadAllBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            ms.Write(buffer, 0, read);
        return ms.ToArray();
    }
}
