using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FastVRecorder.Infrastructure.Shell;

/// <summary>Windows shell integration: Explorer, clipboard and Recycle Bin deletion.</summary>
public static class ShellService
{
    public static void OpenFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public static void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void RevealFile(string path)
    {
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static bool CopyText(string text)
    {
        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
            }

            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    /// <summary>Deletes one file to the Recycle Bin (undoable), never permanently.</summary>
    public static bool DeleteToRecycleBin(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            error = "The file no longer exists.";
            return false;
        }

        try
        {
            var op = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = path + "\0",
                fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
            };
            var result = SHFileOperation(ref op);
            if (result != 0 || op.fAnyOperationsAborted)
            {
                error = $"Windows refused to move the file to the Recycle Bin (code {result}).";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            error = ex.Message;
            return false;
        }
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
}
