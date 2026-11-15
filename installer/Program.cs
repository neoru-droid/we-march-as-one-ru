using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WmaoRuInstaller
{
    internal static class Program
    {
        private const int AttachParentProcess = -1;
        private const int StdOutputHandle = -11;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [STAThread]
        private static int Main(string[] args)
        {
            string appBaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            if (args != null && args.Length > 0)
            {
                return RunCli(appBaseDir, args);
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(appBaseDir));
            return 0;
        }

        private static int RunCli(string appBaseDir, string[] args)
        {
            EnsureConsoleAttached();
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
            }

            bool doInstall = false;
            bool doRestore = false;
            bool noAchievements = false;
            bool skipAssembly = false;
            string gameDir = null;
            string steamStatsDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase))
                {
                    doInstall = true;
                }
                else if (string.Equals(arg, "--restore", StringComparison.OrdinalIgnoreCase))
                {
                    doRestore = true;
                }
                else if (string.Equals(arg, "--no-achievements", StringComparison.OrdinalIgnoreCase))
                {
                    noAchievements = true;
                }
                else if (string.Equals(arg, "--skip-assembly", StringComparison.OrdinalIgnoreCase))
                {
                    skipAssembly = true;
                }
                else if (string.Equals(arg, "--game-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Ошибка: после флага --game-dir требуется указать путь к папке игры.");
                        return 1;
                    }
                    gameDir = args[++i];
                }
                else if (arg.StartsWith("--game-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    gameDir = arg.Substring("--game-dir=".Length);
                }
                else if (string.Equals(arg, "--steam-stats-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Ошибка: после флага --steam-stats-dir требуется указать путь.");
                        return 1;
                    }
                    steamStatsDir = args[++i];
                }
                else if (arg.StartsWith("--steam-stats-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    steamStatsDir = arg.Substring("--steam-stats-dir=".Length);
                }
                else if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase))
                {
                    PrintCliHelp();
                    return 0;
                }
                else
                {
                    Console.Error.WriteLine("Неизвестный аргумент командной строки: " + arg);
                    PrintCliHelp();
                    return 1;
                }
            }

            if (doInstall == doRestore)
            {
                Console.Error.WriteLine("Укажите ровно одно действие: --install или --restore.");
                PrintCliHelp();
                return 1;
            }

            try
            {
                if (doInstall)
                {
                    InstallOptions opts = new InstallOptions
                    {
                        GameDir = gameDir,
                        SteamStatsDir = steamStatsDir,
                        EnableAchievementsUi = !noAchievements,
                        SkipAssemblyPatch = skipAssembly
                    };
                    InstallerEngine.Install(appBaseDir, opts, Console.WriteLine);
                }
                else
                {
                    InstallerEngine.Restore(appBaseDir, gameDir, steamStatsDir, Console.WriteLine);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ОШИБКА: " + ex.Message);
                return 1;
            }
        }

        private static void EnsureConsoleAttached()
        {
            try
            {
                IntPtr stdOut = GetStdHandle(StdOutputHandle);
                if (stdOut == IntPtr.Zero)
                {
                    AttachConsole(AttachParentProcess);
                }
            }
            catch
            {
            }
        }

        private static void PrintCliHelp()
        {
            Console.WriteLine("We March as One — Русификатор v1.0 (автор: neoru-droid)");
            Console.WriteLine("Использование:");
            Console.WriteLine("  WeMarchAsOne-RU-Setup.exe                          Запуск графического окна");
            Console.WriteLine("  WeMarchAsOne-RU-Setup.exe --install [параметры]    Тихая установка русификатора");
            Console.WriteLine("  WeMarchAsOne-RU-Setup.exe --restore [параметры]    Возврат оригинальных файлов");
            Console.WriteLine();
            Console.WriteLine("Параметры:");
            Console.WriteLine("  --game-dir <путь>      Путь к папке игры We March as One");
            Console.WriteLine("  --no-achievements      Не добавлять кнопку «Достижения» в главное меню");
        }
    }
}
