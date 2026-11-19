using System;
using System.IO;
using System.Runtime.InteropServices;
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
            string appBaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

            string guiGameDir;
            string guiSteamDir;
            bool? guiEnableAchievements;
            GuiLaunchMode guiMode;
            if (!TryParseGuiStartupOptions(args, out guiGameDir, out guiSteamDir, out guiEnableAchievements, out guiMode))
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
            Application.Run(new SetupForm(appBaseDir, guiGameDir, guiSteamDir, guiEnableAchievements, guiMode));
            return 0;
        }

        internal static bool TryParseGuiStartupArgs(
            string[] args,
            out string guiGameDir,
            out bool? guiEnableAchievements)
        {
            string steamDir;
            GuiLaunchMode mode;
            return TryParseGuiStartupOptions(args, out guiGameDir, out steamDir, out guiEnableAchievements, out mode);
        }

        internal static bool TryParseGuiStartupOptions(
            string[] args,
            out string guiGameDir,
            out string guiSteamDir,
            out bool? guiEnableAchievements,
            out GuiLaunchMode launchMode)
        {
            guiGameDir = null;
            guiSteamDir = null;
            guiEnableAchievements = null;
            launchMode = GuiLaunchMode.Default;

            if (args == null || args.Length == 0)
            {
                return true;
            }

            bool hasUacFlag = false;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (string.Equals(arg, "--uac-install", StringComparison.OrdinalIgnoreCase))
                {
                    launchMode = GuiLaunchMode.UacInstall;
                    hasUacFlag = true;
                }
                else if (string.Equals(arg, "--uac-uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    launchMode = GuiLaunchMode.UacUninstall;
                    hasUacFlag = true;
                }
                else if (string.Equals(arg, "--gui-game-dir", StringComparison.OrdinalIgnoreCase)
                    || (hasUacFlag && string.Equals(arg, "--game-dir", StringComparison.OrdinalIgnoreCase)))
                {
                    if (i + 1 >= args.Length)
                    {
                        return false;
                    }
                    guiGameDir = args[++i];
                    if (!guiEnableAchievements.HasValue)
                    {
                        guiEnableAchievements = true;
                    }
                }
                else if (arg.StartsWith("--gui-game-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    guiGameDir = arg.Substring("--gui-game-dir=".Length);
                    if (!guiEnableAchievements.HasValue)
                    {
                        guiEnableAchievements = true;
                    }
                }
                else if (hasUacFlag && arg.StartsWith("--game-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    guiGameDir = arg.Substring("--game-dir=".Length);
                    if (!guiEnableAchievements.HasValue)
                    {
                        guiEnableAchievements = true;
                    }
                }
                else if (hasUacFlag && string.Equals(arg, "--steam-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        return false;
                    }
                    guiSteamDir = args[++i];
                }
                else if (hasUacFlag && arg.StartsWith("--steam-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    guiSteamDir = arg.Substring("--steam-dir=".Length);
                }
                else if (string.Equals(arg, "--gui-no-achievements", StringComparison.OrdinalIgnoreCase)
                    || (hasUacFlag && string.Equals(arg, "--no-achievements", StringComparison.OrdinalIgnoreCase)))
                {
                    guiEnableAchievements = false;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private static int RunCli(string appBaseDir, string[] args)
        {
            ConfigureCliStreams();

            bool doInstall = false;
            bool doRestore = false;
            bool doStatus = false;
            bool noAchievements = false;
            bool skipAssembly = false;
            string gameDir = null;
            string steamDir = null;
            string steamStatsDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (string.Equals(arg, "--install", StringComparison.OrdinalIgnoreCase))
                {
                    doInstall = true;
                }
                else if (string.Equals(arg, "--restore", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    doRestore = true;
                }
                else if (string.Equals(arg, "--status", StringComparison.OrdinalIgnoreCase))
                {
                    doStatus = true;
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
                else if (string.Equals(arg, "--steam-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Ошибка: после флага --steam-dir требуется указать путь.");
                        return 1;
                    }
                    steamDir = args[++i];
                }
                else if (arg.StartsWith("--steam-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    steamDir = arg.Substring("--steam-dir=".Length);
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

            int modeCount = (doInstall ? 1 : 0) + (doRestore ? 1 : 0) + (doStatus ? 1 : 0);
            if (modeCount != 1)
            {
                Console.Error.WriteLine("Укажите ровно одно действие: --install, --uninstall (--restore) или --status.");
                PrintCliHelp();
                return 1;
            }

            try
            {
                if (doStatus)
                {
                    return InstallerEngine.RunStatusCli(appBaseDir, gameDir, steamDir, steamStatsDir);
                }
                if (doInstall)
                {
                    InstallOptions opts = new InstallOptions
                    {
                        GameDir = gameDir,
                        SteamDir = steamDir,
                        SteamStatsDir = steamStatsDir,
                        EnableAchievementsUi = !noAchievements,
                        SkipAssemblyPatch = skipAssembly
                    };
                    InstallerEngine.Install(appBaseDir, opts, Console.WriteLine);
                }
                else
                {
                    InstallerEngine.Restore(appBaseDir, gameDir, steamDir, steamStatsDir, Console.WriteLine);
                }
                return 0;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine(
                    "ОШИБКА: Отказано в доступе при записи файлов игры. "
                    + "Запустите установщик или командную строку от имени администратора. ("
                    + ex.Message + ")");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ОШИБКА: " + InstallerDiagnostics.FormatUserError(ex));
                return 1;
            }
        }

        private static void ConfigureCliStreams()
        {
            EnsureConsoleAttached();
            bool utf8Set = false;
            try
            {
                Console.OutputEncoding = BinaryUtils.Utf8NoBom;
                utf8Set = (Console.OutputEncoding.CodePage == 65001);
            }
            catch
            {
                utf8Set = false;
            }

            if (!utf8Set)
            {
                try
                {
                    Stream rawOut = Console.OpenStandardOutput();
                    if (rawOut != null && rawOut != Stream.Null)
                    {
                        Console.SetOut(new StreamWriter(rawOut, BinaryUtils.Utf8NoBom) { AutoFlush = true });
                    }
                    Stream rawErr = Console.OpenStandardError();
                    if (rawErr != null && rawErr != Stream.Null)
                    {
                        Console.SetError(new StreamWriter(rawErr, BinaryUtils.Utf8NoBom) { AutoFlush = true });
                    }
                }
                catch
                {
                }
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
            Console.WriteLine("We March as One: Русификатор v1.2 (автор: neoru-droid)");
            Console.WriteLine("Использование:");
            Console.WriteLine("  WeMarchAsOne-RU-Setup.exe                            Запуск графического окна");
            Console.WriteLine("  WeMarchAsOne-RU-Setup.exe --status [параметры]       Проверка состояния и путей");
            Console.WriteLine("  WeMarchAsOne-RU-Setup.exe --install [параметры]      Тихая установка русификатора");
            Console.WriteLine("  WeMarchAsOne-RU-Setup.exe --uninstall [параметры]    Возврат оригинальных файлов");
            Console.WriteLine();
            Console.WriteLine("Параметры:");
            Console.WriteLine("  --game-dir <путь>      Путь к папке игры We March as One");
            Console.WriteLine("  --steam-dir <путь>     Путь к папке клиента Steam");
            Console.WriteLine("  --no-achievements      Не добавлять кнопку «Достижения» в главное меню");
        }
    }
}
