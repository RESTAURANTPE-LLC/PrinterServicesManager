namespace PrinterServices.Core
{
    /// <summary>
    /// Versión centralizada del servicio.
    /// RAZÓN: Un solo lugar para cambiar la versión. Se muestra en:
    /// - Log de inicio (PrinterServicesHost)
    /// - GET /api/health (HealthController)
    /// - GET /api/dashboard/data (DashboardController)
    /// - Dashboard HTML (header)
    /// </summary>
    public static class ServiceVersion
    {
        public const string Version = "1.1.1";
        public const string BuildTag = "printjob-lca-v1";
        public const string FullVersion = Version + "-" + BuildTag;
    }
}
