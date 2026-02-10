using System.Text;

namespace PrinterServices.Drivers
{
    public class BixolonDriver : IPrinterDriver
    {
        public string ModelName { get { return "BIXOLON"; } }

        public byte[] GetInitSequence()
        {
            return new byte[] { 0x1B, 0x40 };
        }

        public byte[] GetCutCommand(CutType cutType)
        {
            // Bixolon: GS V 66 (partial cut)
            if (cutType == CutType.Full)
            {
                return new byte[] { 0x1D, 0x56, 0x41 };
            }
            return new byte[] { 0x1D, 0x56, 0x42 };
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
