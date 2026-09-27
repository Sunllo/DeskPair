using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Security;

/// <summary>What happened when this program asked for administrator rights.</summary>
public enum AuthorizationOutcome
{
    /// <summary>The tool was started as root.</summary>
    Ran,

    /// <summary>The person closed the dialog or gave the wrong password. Not a fault.</summary>
    Declined,

    /// <summary>The ask itself could not be made; the caller should try another way.</summary>
    Unavailable,
}

/// <summary>
/// Asking for administrator rights with this application's name on the dialog.
///
/// The usual way -- osascript with "do shell script ... with administrator privileges" -- puts the wrong
/// name on it. macOS attributes the prompt to the process that raised it, so a person sees a password box
/// from a scripting tool they have never heard of, on behalf of a program that does remote access. That is
/// the shape of something a careful person should refuse, and asking them to ignore that instinct is not a
/// trade worth making.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacAuthorization
{
    /// <summary>errAuthorizationCanceled: the dialog was closed, or the password was not given.</summary>
    private const int Cancelled = -60006;

    public static bool IsAvailable => MacShim.IsAvailable;

    /// <summary>
    /// Runs <paramref name="tool"/> as root, after a dialog naming this application and carrying
    /// <paramref name="prompt"/> as its explanation.
    /// </summary>
    /// <param name="tool">An absolute path to the program to run as root.</param>
    /// <param name="arguments">Its arguments, not including the tool itself.</param>
    /// <param name="prompt">The sentence shown above the password field, or null for the system's wording.</param>
    /// <param name="output">Whatever the tool wrote to its standard output.</param>
    /// <remarks>
    /// The tool's exit status is not available: the security server starts it, so it is not this process's
    /// child to collect. The caller decides what success looks like from <paramref name="output"/>, which
    /// is why the thing being run prints a line saying so.
    /// </remarks>
    public static AuthorizationOutcome Run(string tool, string[] arguments, string? prompt, out string output)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        output = string.Empty;

        // A NULL-terminated char*[] of the arguments, not including the tool, which is what the API takes.
        nint[] pointers = new nint[arguments.Length + 1];
        var buffer = new byte[64 * 1024];
        try
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            }

            pointers[^1] = 0;
            GCHandle pinned = GCHandle.Alloc(pointers, GCHandleType.Pinned);
            try
            {
                int status = MacShim.fd_authorize_run(tool, pinned.AddrOfPinnedObject(), prompt, buffer, buffer.Length);
                if (status == Cancelled)
                {
                    return AuthorizationOutcome.Declined;
                }

                if (status != 0)
                {
                    return AuthorizationOutcome.Unavailable;
                }

                output = System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
                return AuthorizationOutcome.Ran;
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // An older shim, or none. The caller has another way of asking and should use it.
            return AuthorizationOutcome.Unavailable;
        }
        finally
        {
            foreach (nint p in pointers)
            {
                if (p != 0)
                {
                    Marshal.FreeCoTaskMem(p);
                }
            }
        }
    }
}
