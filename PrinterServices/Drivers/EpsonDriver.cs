using System.Text;

namespace PrinterServices.Drivers
{
    public class EpsonDriver : IPrinterDriver
    {
        public string ModelName { get { return "EPSON"; } }

        public byte[] GetInitSequence()
        {
            return new byte[] { 0x1B, 0x40 };
        }

        public byte[] GetCutCommand(CutType cutType)
        {
            // Epson: GS V 1 (full) o GS V 66 (partial)
            if (cutType == CutType.Full)
            {
                return new byte[] { 0x1D, 0x56, 0x01 };
            }
            return new byte[] { 0x1D, 0x56, 0x42, 0x00 };
        }

        public byte[] GetTextBytes(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new byte[0];

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
            return new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA };
        }
    }
}
