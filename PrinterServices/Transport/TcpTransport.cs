using System;
using System.Configuration;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace PrinterServices.Transport
{
    public class TcpTransport : ITransport
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(TcpTransport));

        private readonly string _ip;
        private readonly int _port;
        private readonly int _connectTimeoutMs;
        private readonly int _sendTimeoutMs;

        private TcpClient _client;
        private NetworkStream _stream;
        private bool _disposed;

        public bool IsConnected
        {
            get { return _client != null && _client.Connected; }
        }

        public TcpTransport(string ip, int port)
        {
            _ip = ip;
            _port = port;

            int connectTimeout;
            string connectTimeoutStr = ConfigurationManager.AppSettings["TcpConnectTimeoutMs"];
            _connectTimeoutMs = (!string.IsNullOrEmpty(connectTimeoutStr) && int.TryParse(connectTimeoutStr, out connectTimeout))
                ? connectTimeout : 3000;

            int sendTimeout;
            string sendTimeoutStr = ConfigurationManager.AppSettings["TcpTimeoutMs"];
            _sendTimeoutMs = (!string.IsNullOrEmpty(sendTimeoutStr) && int.TryParse(sendTimeoutStr, out sendTimeout))
                ? sendTimeout : 5000;
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            Disconnect();

            _client = new TcpClient();
            _client.SendTimeout = _sendTimeoutMs;
            _client.ReceiveTimeout = _sendTimeoutMs;
            _client.NoDelay = true;

            Log.DebugFormat("[TCP] Conectando a {0}:{1} (timeout={2}ms)", _ip, _port, _connectTimeoutMs);

            // ConnectAsync con timeout
            var connectTask = _client.ConnectAsync(_ip, _port);
            var timeoutTask = Task.Delay(_connectTimeoutMs, ct);

            var completed = await Task.WhenAny(connectTask, timeoutTask);

            if (completed == timeoutTask)
            {
                Disconnect();
                throw new TimeoutException(
                    string.Format("Timeout conectando a {0}:{1} después de {2}ms", _ip, _port, _connectTimeoutMs));
            }

            // Propagar excepción si la conexión falló
            await connectTask;

            _stream = _client.GetStream();
            Log.DebugFormat("[TCP] Conectado a {0}:{1}", _ip, _port);
        }

        public async Task SendAsync(byte[] data, CancellationToken ct)
        {
            if (_stream == null || !IsConnected)
            {
                throw new InvalidOperationException("No hay conexión TCP activa");
            }

            await _stream.WriteAsync(data, 0, data.Length, ct);
            await _stream.FlushAsync(ct);
        }

        public async Task<byte[]> ReceiveAsync(int length, int timeoutMs, CancellationToken ct)
        {
            if (_stream == null || !IsConnected)
            {
                throw new InvalidOperationException("No hay conexión TCP activa");
            }

            var buffer = new byte[length];
            int totalRead = 0;

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeoutMs);

                try
                {
                    while (totalRead < length)
                    {
                        int bytesRead = await _stream.ReadAsync(buffer, totalRead, length - totalRead, cts.Token);
                        if (bytesRead == 0)
                            break;
                        totalRead += bytesRead;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout de lectura, devolver lo que se leyó
                    Log.DebugFormat("[TCP] Timeout de lectura después de {0}ms, bytes leídos: {1}", timeoutMs, totalRead);
                }
            }

            if (totalRead < length)
            {
                var result = new byte[totalRead];
                Array.Copy(buffer, result, totalRead);
                return result;
            }

            return buffer;
        }

        public void Disconnect()
        {
            try
            {
                if (_stream != null)
                {
                    _stream.Close();
                    _stream = null;
                }
            }
            catch { }

            try
            {
                if (_client != null)
                {
                    _client.Close();
                    _client = null;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _disposed = true;
            }
        }
    }
}
