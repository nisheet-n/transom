using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Transom.Native;

[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
    void GetParent(out nint ppsi);
    [PreserveSig]
    int GetDisplayName(uint sigdnName, out nint ppszName);
}

internal static partial class ShellItem
{
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private const uint SIGDN_NORMALDISPLAY = 0;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string pszPath, nint pbc, in Guid riid, out nint ppv);

    public static string? StartMenuName(string appUserModelId)
    {
        if (SHCreateItemFromParsingName($@"shell:AppsFolder\{appUserModelId}", 0, IID_IShellItem, out var pointer) != 0) return null;
        try
        {
            var item = (IShellItem)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            if (item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name) != 0) return null;
            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
