using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using PrecisionMotion.Core.Interop;

namespace PrecisionMotion.Launcher;

internal static class Program
{
    private const int NameLength = 13;
    private const string MutexRename = @"Local\SystemDynamics_App_Rename";
    private const string MutexExecution = @"Local\SystemDynamics_App_Executando";
    private const string EventReady = @"Local\SystemDynamics_App_Pronto";

    private const int ErrorAlreadyExists = 183;
    private const uint Synchronize = 0x00100000;
    private const uint EventModifyState = 0x0002;
    private const uint WaitObject0 = 0x00000000;

    [STAThread]
    public static void Main(string[] args)
    {
        string currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        string baseDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;

        // 1. Check administrative privileges
        if (!IsAdmin())
        {
            RelaunchAsAdmin(currentExe, args);
            return;
        }

        // 2. Polymorphic execution check
        bool isExecuteMode = args.Contains("--executar", StringComparer.OrdinalIgnoreCase);

        if (!isExecuteMode && !Debugger.IsAttached)
        {
            IntPtr hMutexRename = NativeMethods.CreateMutexW(IntPtr.Zero, false, MutexRename);
            int lastErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (lastErr == ErrorAlreadyExists)
            {
                NativeMethods.CloseHandle(hMutexRename);
                return;
            }

            IntPtr hEvent = NativeMethods.CreateEventW(IntPtr.Zero, true, false, EventReady);
            string? newExePath = null;

            try
            {
                for (int i = 0; i < 100; i++)
                {
                    string candidate = Path.Combine(baseDir, GenerateRandomName(NameLength) + ".exe");
                    if (!File.Exists(candidate))
                    {
                        newExePath = candidate;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(newExePath))
                    throw new InvalidOperationException("Não foi possível gerar um nome de processo disponível.");

                File.Copy(currentExe, newExePath, true);

                var psi = new ProcessStartInfo
                {
                    FileName = newExePath,
                    Arguments = $"--executar --apagar \"{currentExe}\"",
                    WorkingDirectory = baseDir,
                    UseShellExecute = true
                };

                using var childProcess = Process.Start(psi);
                if (childProcess != null)
                {
                    NativeMethods.WaitForSingleObject(hEvent, 10000);
                }

                NativeMethods.CloseHandle(hEvent);
                NativeMethods.CloseHandle(hMutexRename);
                return;
            }
            catch (Exception ex)
            {
                NativeMethods.CloseHandle(hEvent);
                NativeMethods.CloseHandle(hMutexRename);
                NativeMethods.MessageBoxW(IntPtr.Zero, $"Falha ao preparar o ambiente de execução:\n\n{ex.Message}", "Erro", 16);
                return;
            }
        }

        // 3. Main process execution mode
        IntPtr hMutexExec = NativeMethods.CreateMutexW(IntPtr.Zero, false, MutexExecution);
        if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() == ErrorAlreadyExists)
        {
            NativeMethods.CloseHandle(hMutexExec);
            return;
        }

        // Signal ready event
        IntPtr hEventReady = NativeMethods.OpenEventW(EventModifyState, false, EventReady);
        if (hEventReady != IntPtr.Zero)
        {
            NativeMethods.SetEvent(hEventReady);
            NativeMethods.CloseHandle(hEventReady);
        }

        // Clean up previous executable if requested
        int apagarIndex = Array.FindIndex(args, a => a.Equals("--apagar", StringComparison.OrdinalIgnoreCase));
        if (apagarIndex >= 0 && apagarIndex + 1 < args.Length)
        {
            string oldPath = args[apagarIndex + 1].Trim('"');
            DeleteWithRetry(oldPath, 40, 250);
        }

        // Launch UI process if present
        try
        {
            string uiExe = Path.Combine(baseDir, "PrecisionMotion.UI.exe");
            if (!File.Exists(uiExe))
            {
                // Look in parent or sister build folders
                string sister = Path.Combine(baseDir, "..", "PrecisionMotion.UI", "PrecisionMotion.UI.exe");
                if (File.Exists(sister)) uiExe = Path.GetFullPath(sister);
            }

            if (File.Exists(uiExe))
            {
                var uiPsi = new ProcessStartInfo
                {
                    FileName = uiExe,
                    WorkingDirectory = baseDir,
                    UseShellExecute = false
                };
                using var uiProc = Process.Start(uiPsi);
                uiProc?.WaitForExit();
            }
            else
            {
                Console.WriteLine("PrecisionMotion.UI.exe não encontrado no diretório. Pressione ENTER para sair.");
                Console.ReadLine();
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hMutexExec);
        }
    }

    private static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void RelaunchAsAdmin(string exePath, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
                Verb = "runas",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch { }
    }

    private static string GenerateRandomName(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private static void DeleteWithRetry(string path, int attempts = 40, int delayMs = 250)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (!File.Exists(path)) return;
            try
            {
                File.Delete(path);
                return;
            }
            catch
            {
                Thread.Sleep(delayMs);
            }
        }
    }
}
