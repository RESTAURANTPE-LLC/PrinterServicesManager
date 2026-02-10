using System.Collections.Generic;
using System.Text;

namespace PrinterServices.Drivers
{
    public class GenericEscPosDriver : IPrinterDriver
    {
        public string ModelName { get { return "GENERICA"; } }

        public byte[] GetInitSequence()
        {
            // ESC @ — Initialize printer
            return new byte[] { 0x1B, 0x40 };
        }

        public byte[] GetCutCommand(CutType cutType)
        {
            // GS V B 0x00 — Partial cut (feeds and cuts)
            if (cutType == CutType.Full)
            {
                return new byte[] { 0x1D, 0x56, 0x41, 0x00 };
            }
            return new byte[] { 0x1D, 0x56, 0x42, 0x00 };
        }

        public byte[] GetTextBytes(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new byte[0];

            // Codificar como CP437 (codepage estándar ESC/POS)
            Encoding encoding;
            try
            {
                encoding = Encoding.GetEncoding(437);
            }
            catch
            {
                encoding = Encoding.ASCII;
            }

            return encoding.GetBytes(text);
        }

        public byte[] GetCashDrawerCommand()
        {
            // ESC p 0 25 250 — Open cash drawer pin 2
            return new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA };
        }
    }
}
