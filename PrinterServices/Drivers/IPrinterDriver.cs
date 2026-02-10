namespace PrinterServices.Drivers
{
    public enum CutType
    {
        Full,
        Partial
    }

    public interface IPrinterDriver
    {
        byte[] GetInitSequence();
        byte[] GetCutCommand(CutType cutType);
        byte[] GetTextBytes(string text);
        byte[] GetCashDrawerCommand();
        string ModelName { get; }
    }
}
