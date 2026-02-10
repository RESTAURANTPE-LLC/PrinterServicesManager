using log4net;

namespace PrinterServices.Drivers
{
    public static class DriverFactory
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(DriverFactory));

        // Constantes de modelos (mismos valores que Util.PRINTER_MODEL_* en QuipuNetX)
        public const string MODEL_GENERICA = "GENERICA";
        public const string MODEL_BIXOLON_SRP270 = "BIXOLON_SRP270";
        public const string MODEL_STAR_SP = "STAR_SP";
        public const string MODEL_EPSON_TM_U220 = "EPSON_TM_U220";
        public const string MODEL_EPSON_TM_T20II = "EPSON_TM_T20II";
        public const string MODEL_START_BSC10 = "START_BSC10";
        public const string MODEL_CBX = "CBX";
        public const string MODEL_ZKT_ECO = "ZKT_ECO";

        public static IPrinterDriver GetDriver(string printerModel)
        {
            if (string.IsNullOrEmpty(printerModel))
            {
                Log.Debug("[DRIVER] Modelo vacío, usando GenericEscPosDriver");
                return new GenericEscPosDriver();
            }

            switch (printerModel.ToUpperInvariant())
            {
                case MODEL_EPSON_TM_T20II:
                case MODEL_EPSON_TM_U220:
                    return new EpsonDriver();

                case MODEL_STAR_SP:
                case MODEL_START_BSC10:
                    return new StarDriver();

                case MODEL_BIXOLON_SRP270:
                    return new BixolonDriver();

                case MODEL_CBX:
                case MODEL_ZKT_ECO:
                case MODEL_GENERICA:
                default:
                    return new GenericEscPosDriver();
            }
        }
    }
}
