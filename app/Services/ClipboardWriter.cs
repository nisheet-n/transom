using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace Transom.Services;

// Another app can hold the clipboard open for a moment, which makes SetContent throw.
// Every write goes through here so that retries briefly instead of crashing Transom.
public static class ClipboardWriter
{
    public static bool TrySet(DataPackage package)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                Thread.Sleep(40);
            }
        }
        return false;
    }
}
