using System.Net.Sockets;

namespace PortProxyManager.Services;

public static class PortProbe
{
    /// <summary>探测目标地址是否真的能连上，用于编辑对话框里的「测试」按钮。</summary>
    public static async Task<(bool Ok, string Detail)> TestConnectAsync(
        string host, int port, int timeoutMs = 2500)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (false, "目标地址为空。");

        var target = host == "*" ? "127.0.0.1" : host;

        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(target, port);
            var finished = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);

            if (finished != connect)
                return (false, $"连接超时（超过 {timeoutMs} ms）。");

            await connect.ConfigureAwait(false);
            return (true, "连接成功，目标可达。");
        }
        catch (SocketException ex)
        {
            return (false, ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝：目标端口上没有程序在监听。",
                SocketError.TimedOut => $"连接超时（超过 {timeoutMs} ms）。",
                SocketError.HostNotFound or SocketError.NoData => "地址无法解析。",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "网络不可达。",
                _ => $"连接失败：{ex.SocketErrorCode}（{ex.Message}）",
            });
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }
}
