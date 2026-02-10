using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterServices.Transport
{
    public interface ITransport : IDisposable
    {
        Task ConnectAsync(CancellationToken ct);
        Task SendAsync(byte[] data, CancellationToken ct);
        Task<byte[]> ReceiveAsync(int length, int timeoutMs, CancellationToken ct);
        bool IsConnected { get; }
        void Disconnect();
    }
}
