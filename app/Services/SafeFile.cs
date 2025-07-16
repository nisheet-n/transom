using System.Diagnostics;

namespace Transom.Services;

// Notes, reminders, the shelf and clipboard history are saved through here so a crash or a bad
// write can't wipe them: each save goes to a temp file first and the previous version is kept as
// .bak, and a file that can't be read is set aside instead of being overwritten by an empty list.
internal static class SafeFile
{
    public static void Write(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(temp, path);
    }

    public static void SetAside(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
