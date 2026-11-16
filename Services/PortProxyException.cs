namespace PortProxyManager.Services;

/// <summary>带用户可读中文消息的业务异常。</summary>
public sealed class PortProxyException : Exception
{
    public ProcResult? Result { get; }

    public PortProxyException(string message, ProcResult? result = null, Exception? inner = null)
        : base(message, inner)
    {
        Result = result;
    }
}
