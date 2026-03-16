using System;

namespace PrinterServices.Core.Network
{
    /// <summary>
    /// VALUE OBJECT: Timing de latencias de una impresión.
    /// PROPÓSITO: Encapsular todos los timestamps y latencias calculadas.
    /// PRINCIPIO: Value Object pattern - datos inmutables con lógica de cálculo interna.
    /// </summary>
    public class LatencyTiming
    {
        // RAZÓN: Timestamps absolutos para trazabilidad completa
        public string JobId { get; private set; }
        public string ImpresoraId { get; private set; }
        public string ImpresoraIp { get; private set; }
        public DateTime EnqueuedAt { get; private set; }
        public DateTime StartedAt { get; private set; }
        public DateTime? TcpConnectedAt { get; private set; }
        public DateTime? DataSentAt { get; private set; }
        public DateTime? CompletedAt { get; private set; }

        // RAZÓN: Latencias calculadas (milisegundos)
        public int? QueueWaitMs { get; private set; }
        public int? TcpConnectMs { get; private set; }
        public int? DataSendMs { get; private set; }
        public int? TotalPrintMs { get; private set; }

        // RAZÓN: Contexto adicional
        public int? DataSizeBytes { get; private set; }
        public int RetryCount { get; private set; }
        public bool Success { get; private set; }
        public string ErrorMessage { get; private set; }

        // RAZÓN: Red donde ocurrió (para correlación)
        public string GatewayMac { get; private set; }
        public string NetworkId { get; private set; }
        public string WifiSsid { get; private set; }

        /// <summary>
        /// Constructor privado para forzar uso de builder pattern.
        /// </summary>
        private LatencyTiming() { }

        /// <summary>
        /// Builder para construir LatencyTiming paso a paso.
        /// RAZÓN: Permite ir seteando timestamps a medida que avanza la impresión.
        /// </summary>
        public class Builder
        {
            private LatencyTiming _timing = new LatencyTiming();

            public Builder(string jobId, string impresoraId, string impresoraIp, DateTime enqueuedAt)
            {
                // RAZÓN: Campos obligatorios se setean en constructor
                _timing.JobId = jobId;
                _timing.ImpresoraId = impresoraId;
                _timing.ImpresoraIp = impresoraIp;
                _timing.EnqueuedAt = enqueuedAt;
            }

            public Builder Started(DateTime startedAt)
            {
                // RAZÓN: Calcular queue_wait_ms automáticamente
                _timing.StartedAt = startedAt;
                _timing.QueueWaitMs = (int)(startedAt - _timing.EnqueuedAt).TotalMilliseconds;
                return this;
            }

            public Builder TcpConnected(DateTime tcpConnectedAt)
            {
                // RAZÓN: Calcular tcp_connect_ms automáticamente
                _timing.TcpConnectedAt = tcpConnectedAt;
                _timing.TcpConnectMs = (int)(tcpConnectedAt - _timing.StartedAt).TotalMilliseconds;
                return this;
            }

            public Builder DataSent(DateTime dataSentAt, int dataSizeBytes)
            {
                // RAZÓN: Calcular data_send_ms automáticamente
                _timing.DataSentAt = dataSentAt;
                _timing.DataSizeBytes = dataSizeBytes;
                if (_timing.TcpConnectedAt.HasValue)
                {
                    _timing.DataSendMs = (int)(dataSentAt - _timing.TcpConnectedAt.Value).TotalMilliseconds;
                }
                return this;
            }

            public Builder Completed(DateTime completedAt, bool success, string errorMessage = null)
            {
                // RAZÓN: Calcular total_print_ms automáticamente
                _timing.CompletedAt = completedAt;
                _timing.TotalPrintMs = (int)(completedAt - _timing.StartedAt).TotalMilliseconds;
                _timing.Success = success;
                _timing.ErrorMessage = errorMessage;
                return this;
            }

            public Builder WithRetryCount(int retryCount)
            {
                _timing.RetryCount = retryCount;
                return this;
            }

            public Builder WithNetworkContext(string gatewayMac, string networkId, string wifiSsid)
            {
                // RAZÓN: Correlacionar latencia con red específica
                _timing.GatewayMac = gatewayMac;
                _timing.NetworkId = networkId;
                _timing.WifiSsid = wifiSsid;
                return this;
            }

            public LatencyTiming Build()
            {
                // RAZÓN: Validar que se hayan seteado campos críticos
                if (_timing.StartedAt == DateTime.MinValue)
                    throw new InvalidOperationException("Debe llamar Started() antes de Build()");
                if (!_timing.CompletedAt.HasValue)
                    throw new InvalidOperationException("Debe llamar Completed() antes de Build()");

                return _timing;
            }
        }

        /// <summary>
        /// Verifica si el TCP connect fue lento (>2000ms).
        /// RAZÓN: Indicador de problema de red.
        /// </summary>
        public bool IsTcpConnectSlow()
        {
            return TcpConnectMs.HasValue && TcpConnectMs.Value > 2000;
        }

        /// <summary>
        /// Verifica si el envío de datos fue lento (>500ms para <10KB).
        /// RAZÓN: Indicador de saturación de red.
        /// </summary>
        public bool IsDataSendSlow()
        {
            if (!DataSendMs.HasValue || !DataSizeBytes.HasValue) return false;
            // RAZÓN: >500ms para <10KB es anormal (debería ser <100ms)
            return DataSendMs.Value > 500 && DataSizeBytes.Value < 10240;
        }

        /// <summary>
        /// Calcula throughput en KB/s.
        /// RAZÓN: Diagnóstico de velocidad de red.
        /// </summary>
        public double? GetThroughputKbps()
        {
            if (!DataSendMs.HasValue || !DataSizeBytes.HasValue || DataSendMs.Value == 0)
                return null;
            // RAZÓN: KB/s = (bytes / 1024) / (ms / 1000)
            return (DataSizeBytes.Value / 1024.0) / (DataSendMs.Value / 1000.0);
        }

        public override string ToString()
        {
            return $"Latency[Job:{JobId}, Total:{TotalPrintMs}ms, TCP:{TcpConnectMs}ms, Send:{DataSendMs}ms, Success:{Success}]";
        }
    }
}
