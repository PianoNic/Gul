using System.Runtime.InteropServices;

namespace Gul.Client;

public static class Ui
{
    private static readonly string Esc = ((char)27).ToString();
    private static readonly bool Color = Detect();

    private static bool Detect()
    {
        if (Console.IsOutputRedirected) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            TryEnableVt();
        return true;
    }

    private static string Wrap(string s, string code) => Color ? $"{Esc}[{code}m{s}{Esc}[0m" : s;

    public static string Bold(string s) => Wrap(s, "1");
    public static string Dim(string s) => Wrap(s, "2");
    public static string Red(string s) => Wrap(s, "31");
    public static string Green(string s) => Wrap(s, "32");
    public static string Yellow(string s) => Wrap(s, "33");
    public static string Url(string s) => Wrap(s, "1;36");

    public static string Badge => Color ? $"{Esc}[30;107m 굴 {Esc}[0m" : "[굴]";

    public static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine($"  {Badge}  {Bold("gul")}  {Dim("- instant public URLs for your localhost")}");
        Console.WriteLine();
    }

    public static void Err(string message) => Console.Error.WriteLine($"{Red("error:")} {message}");

    private static void TryEnableVt()
    {
        try
        {
            var handle = GetStdHandle(-11);
            if (GetConsoleMode(handle, out var mode))
                SetConsoleMode(handle, mode | 0x0004);
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(nint handle, uint mode);
}
