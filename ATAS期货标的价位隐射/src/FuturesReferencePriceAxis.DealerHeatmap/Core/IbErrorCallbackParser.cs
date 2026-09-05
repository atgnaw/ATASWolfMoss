namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public readonly record struct IbErrorCallback(
    int RequestId,
    int ErrorCode,
    string Message);

public static class IbErrorCallbackParser
{
    public static bool TryParse(object?[] args, out IbErrorCallback callback)
    {
        callback = default;

        if (args.Length < 3 || !TryInt32(args[0], out var requestId))
            return false;

        // TWS API 10.33+ adds errorTime between request id and error code:
        // error(id, errorTime, errorCode, errorString, advancedRejectJson).
        var codeIndex = args.Length >= 5 ? 2 : 1;
        var messageIndex = args.Length >= 5 ? 3 : 2;

        if (!TryInt32(args[codeIndex], out var errorCode))
            return false;

        callback = new IbErrorCallback(
            requestId,
            errorCode,
            Convert.ToString(args[messageIndex], CultureInfo.InvariantCulture)
            ?? "IB Gateway error");
        return true;
    }

    private static bool TryInt32(object? value, out int result)
    {
        try
        {
            result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = 0;
            return false;
        }
    }
}
