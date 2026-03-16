using System;
using System.Linq;

namespace PrinterServices.Core.Network
{
    /// <summary>
    /// Normaliza formatos de direcciones MAC según principio SRP (Single Responsibility).
    /// RESPONSABILIDAD ÚNICA: Transformar MACs de cualquier formato a uno estándar.
    /// NO hace: Búsquedas ARP, validación de red, persistencia en BD.
    /// </summary>
    public static class MacAddressNormalizer
    {
        /// <summary>
        /// Normaliza formato de MAC address a uppercase sin separadores.
        /// RAZÓN: QuipuNetX puede enviar MACs con : o -, necesitamos formato único para comparar.
        /// </summary>
        /// <param name="mac">MAC en cualquier formato: AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF, AABBCCDDEEFF</param>
        /// <returns>MAC normalizada (AABBCCDDEEFF) o null si inválida</returns>
        /// <example>
        /// Normalize("AA:BB:CC:DD:EE:FF") → "AABBCCDDEEFF"
        /// Normalize("aa-bb-cc-dd-ee-ff") → "AABBCCDDEEFF"
        /// Normalize("invalid") → null
        /// </example>
        public static string Normalize(string mac)
        {
            // Validar entrada no nula ni vacía
            // RAZÓN: Evitar NullReferenceException en operaciones posteriores
            if (string.IsNullOrEmpty(mac))
            {
                return null; // Retornar null indica formato inválido
            }

            try
            {
                // Remover separadores comunes (: y -)
                // RAZÓN: Diferentes routers y sistemas operativos formatean MACs diferente
                string normalized = mac.Replace(":", "").Replace("-", "").ToUpper();

                // Validar longitud exacta de 12 caracteres hexadecimales
                // RAZÓN: Una MAC Ethernet válida son 6 octetos = 12 dígitos hex
                if (normalized.Length != 12)
                {
                    return null; // Longitud incorrecta = MAC inválida
                }

                // Validar que todos los caracteres sean hexadecimales válidos (0-9, A-F)
                // RAZÓN: Prevenir que se almacenen MACs corruptas en BD
                foreach (char c in normalized)
                {
                    // Uri.IsHexDigit() es más eficiente que regex para validar hex
                    // RAZÓN: No requiere compilación de patrón, validación directa por carácter
                    if (!Uri.IsHexDigit(c))
                    {
                        return null; // Contiene caracteres no hexadecimales
                    }
                }

                // Retornar MAC normalizada (uppercase, sin separadores)
                // RAZÓN: Formato consistente permite comparaciones directas con ==
                return normalized;
            }
            catch (Exception)
            {
                // Capturar cualquier error inesperado (ej: OutOfMemoryException en Replace)
                // RAZÓN: No propagar excepciones, retornar null es más seguro para callers
                return null;
            }
        }

        /// <summary>
        /// Compara dos MACs normalizando ambas antes de comparar.
        /// RAZÓN: Facilitar comparaciones sin preocuparse por formato.
        /// </summary>
        /// <param name="mac1">Primera MAC</param>
        /// <param name="mac2">Segunda MAC</param>
        /// <returns>true si son la misma MAC física (ignorando formato)</returns>
        public static bool AreEqual(string mac1, string mac2)
        {
            // Normalizar ambas MACs primero
            // RAZÓN: "AA:BB:CC" debe ser igual a "AA-BB-CC"
            string normalized1 = Normalize(mac1);
            string normalized2 = Normalize(mac2);

            // Si alguna es inválida, no son iguales
            // RAZÓN: null != null en contexto de negocio (ambas inválidas)
            if (normalized1 == null || normalized2 == null)
            {
                return false;
            }

            // Comparar las versiones normalizadas
            // RAZÓN: Comparación de strings es case-sensitive, pero ya convertimos a upper
            return normalized1 == normalized2;
        }

        /// <summary>
        /// Formatea MAC normalizada a formato legible con separadores.
        /// RAZÓN: Para logging y UI, formato AA:BB:CC:DD:EE:FF es más legible.
        /// </summary>
        /// <param name="normalizedMac">MAC normalizada (12 chars sin separadores)</param>
        /// <param name="separator">Separador a usar (default ":")</param>
        /// <returns>MAC formateada o null si entrada inválida</returns>
        /// <example>
        /// Format("AABBCCDDEEFF") → "AA:BB:CC:DD:EE:FF"
        /// Format("AABBCCDDEEFF", "-") → "AA-BB-CC-DD-EE-FF"
        /// </example>
        public static string Format(string normalizedMac, string separator = ":")
        {
            // Validar que la MAC ya esté normalizada
            // RAZÓN: Este método asume entrada normalizada, no debe validar de nuevo
            if (string.IsNullOrEmpty(normalizedMac) || normalizedMac.Length != 12)
            {
                return null;
            }

            // Insertar separador cada 2 caracteres
            // RAZÓN: Formato estándar IEEE 802 para MACs (6 octetos separados)
            return string.Format("{0}{6}{1}{6}{2}{6}{3}{6}{4}{6}{5}",
                normalizedMac.Substring(0, 2),  // Primer octeto
                normalizedMac.Substring(2, 2),  // Segundo octeto
                normalizedMac.Substring(4, 2),  // Tercer octeto
                normalizedMac.Substring(6, 2),  // Cuarto octeto
                normalizedMac.Substring(8, 2),  // Quinto octeto
                normalizedMac.Substring(10, 2), // Sexto octeto
                separator);                      // Separador elegido
        }
    }
}
