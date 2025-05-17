namespace Transom.Native;

// Copies every memory-based clipboard format so the clipboard can be put back exactly as it was
// after Transom borrows it. Handle-based formats (old GDI bitmaps, metafiles) can't be copied this
// way; Windows rebuilds CF_BITMAP from CF_DIB on its own.
internal sealed unsafe class ClipboardSnapshot
{
    private const uint GMEM_MOVEABLE = 0x0002;
    private static readonly HashSet<uint> HandleFormats = [2, 3, 9, 14, 0x80, 0x82, 0x83, 0x8E];
    private static readonly uint NoHistory = Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint NoMonitor = Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");

    private readonly List<(uint Format, byte[] Data)> _formats = [];

    public static ClipboardSnapshot? Take()
    {
        if (!Open()) return null;
        var snapshot = new ClipboardSnapshot();
        try
        {
            for (var format = Win32.EnumClipboardFormats(0); format != 0; format = Win32.EnumClipboardFormats(format))
            {
                if (HandleFormats.Contains(format)) continue;
                var handle = Win32.GetClipboardData(format);
                if (handle == 0) continue;
                var size = (int)Win32.GlobalSize(handle);
                var source = Win32.GlobalLock(handle);
                if (source == 0) continue;
                var data = new byte[size];
                new ReadOnlySpan<byte>((void*)source, size).CopyTo(data);
                Win32.GlobalUnlock(handle);
                snapshot._formats.Add((format, data));
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }
        return snapshot;
    }

    public static string? ReadText()
    {
        if (!Open()) return null;
        try
        {
            var handle = Win32.GetClipboardData(13);
            var text = handle == 0 ? 0 : Win32.GlobalLock(handle);
            if (text == 0) return null;
            var value = new string((char*)text);
            Win32.GlobalUnlock(handle);
            return value;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    // The restored copy is marked so Windows' clipboard history and other clipboard tools
    // don't record it a second time.
    public void Restore()
    {
        if (!Open()) return;
        try
        {
            Win32.EmptyClipboard();
            foreach (var (format, data) in _formats)
            {
                if (format == NoHistory || format == NoMonitor) continue;
                Set(format, data);
            }
            Set(NoHistory, [0, 0, 0, 0]);
            Set(NoMonitor, [0, 0, 0, 0]);
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static void Set(uint format, byte[] data)
    {
        var handle = Win32.GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        var target = Win32.GlobalLock(handle);
        if (target == 0) return;
        data.CopyTo(new Span<byte>((void*)target, data.Length));
        Win32.GlobalUnlock(handle);
        Win32.SetClipboardData(format, handle);
    }

    private static bool Open()
    {
        for (var i = 0; i < 10; i++)
        {
            if (Win32.OpenClipboard(0)) return true;
            Thread.Sleep(20);
        }
        return false;
    }
}
