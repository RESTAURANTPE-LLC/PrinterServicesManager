using System;
using System.IO;
using System.Reflection;
using Topshelf;
using Topshelf.Logging;

namespace PrinterServices
{
    class Program
    {
        static void Main(string[] args)
        {
            // Fijar el directorio de trabajo al directorio del exe
            // RAZÓN: Cuando corre como servicio Windows, el working dir es C:\Windows\System32
            // y no encuentra log4net.config ni otros archivos relativos al exe.
            var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Directory.SetCurrentDirectory(exeDir);

            HostFactory.Run(x =>
            {
                x.Service<PrinterServicesHost>(s =>
                {
                    s.ConstructUsing(name => new PrinterServicesHost());
                    s.WhenStarted(tc => tc.Start());
                    s.WhenStopped(tc => tc.Stop());
                });

                x.RunAsLocalSystem();
                x.SetDescription("Servicio centralizado de impresión ESC/POS para Quipunet");
                x.SetDisplayName("Printer Services LCA");
                x.SetServiceName("PrinterServicesLCA");

                x.UseLog4Net("log4net.config");

                x.EnableServiceRecovery(r =>
                {
                    r.RestartService(1); // Reiniciar después de 1 minuto en primer fallo
                    r.RestartService(3); // Reiniciar después de 3 minutos en segundo fallo
                    r.RestartService(5); // Reiniciar después de 5 minutos en tercer fallo
                    r.SetResetPeriod(1); // Resetear contador de fallos después de 1 día
                });

                x.SetStartTimeout(TimeSpan.FromSeconds(120));
                x.StartAutomatically();
            });
        }
    }
}
