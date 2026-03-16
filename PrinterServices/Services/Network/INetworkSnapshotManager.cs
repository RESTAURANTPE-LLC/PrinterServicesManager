using System;
using PrinterServices.Core.Network;

namespace PrinterServices.Services.Network
{
    /// <summary>
    /// INTERFAZ: Gestión de snapshots de red conocidas buenas.
    /// PROPÓSITO: Abstracción para CRUD de snapshots (DIP).
    /// PRINCIPIO SRP: Solo define contrato de gestión, no implementación.
    /// </summary>
    public interface INetworkSnapshotManager
    {
        /// <summary>
        /// Registra o actualiza snapshot después de impresión exitosa.
        /// RAZÓN: Marca esta red como "confiable" para futuras comparaciones.
        /// </summary>
        void RecordSuccessfulPrint(NetworkConfig config);

        /// <summary>
        /// Obtiene última red conocida buena (más reciente con impresión exitosa).
        /// RAZÓN: Para comparar con config actual y detectar cambio de red.
        /// </summary>
        NetworkConfig GetLastKnownGoodNetwork();

        /// <summary>
        /// Verifica si existe snapshot para una config específica.
        /// RAZÓN: Para saber si ya se registró esta red antes.
        /// </summary>
        bool HasSnapshotFor(NetworkConfig config);
    }
}
