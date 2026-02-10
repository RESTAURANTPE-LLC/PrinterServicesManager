using System.Text;

namespace PrinterServices.Drivers
{
    public class StarDriver : IPrinterDriver
    {
        public string ModelName { get { return "STAR"; } }

        public byte[] GetInitSequence()
        {
            return new byte[] { 0x1B, 0x40 };
        }

        public byte[] GetCutCommand(CutType cutType)
        {
            // Star: ESC d 2 (partial cut with feed)
            if (cutType == CutType.Full)
            {
                return new byte[] { 0x1B, 0x64, 0x03 };
            }
            return new byte[] { 0x1B, 0x64, 0x02 };
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
            // Star cash drawer: BEL
            return new byte[] { 0x07 };
        }
    }
}
