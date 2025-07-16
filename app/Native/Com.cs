namespace Transom.Native;

// Calls COM interfaces through their vtables directly, which keeps the app trimming/AOT
// friendly without generated wrappers. Slot numbers are a method's position in its Windows SDK
// header, plus 3 for the IUnknown methods every interface starts with.
internal static unsafe class Com
{
    public static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    public static int Call<T>(nint obj, int index, T* result) where T : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, T*, int>)Slot(obj, index))(obj, result);

    public static int QueryInterface(nint obj, Guid iid, out nint result)
    {
        nint value;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &iid, &value);
        result = hr >= 0 ? value : 0;
        return hr;
    }

    public static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
    }
}
