using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace ViviTune.Windows;

// Запуск вместе с Windows: задача Планировщика при входе пользователя, с наивысшими правами.
// Обычный автозапуск (Run в реестре) программу с requireAdministrator молча не запускает.
// Включён ли автозапуск — есть ли задача; в settings.json не хранится.
public static class Autostart
{
    public const string Flag = "--autostart";
    const string TaskName = "ViviTune";

    // Подключение при автозапуске: до attempts попыток с паузой, retrying(n) — перед попыткой n.
    // false — все попытки не удались (показать окно); отмена (пользователь сам нажал кнопку) — true, окно не нужно.
    public static async Task<bool> Connect(Func<Task<bool>> attempt, int attempts, TimeSpan pause, Action<int> retrying, CancellationToken ct)
    {
        try
        {
            for (int i = 1; ; i++)
            {
                if (await attempt()) return true;
                if (i == attempts) return false;
                await Task.Delay(pause, ct);
                retrying(i + 1);
            }
        }
        catch (OperationCanceledException) { return true; }
    }

    // Priority 4 — обычный: по умолчанию у задач 7 (ниже обычного), и sing-box унаследовал бы его.
    // Без батарейных ограничений и без лимита 72 ч: иначе на ноутбуке не стартует, а через трое суток Windows убьёт.
    public static string TaskXml(string exePath, string userId) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>ViviTune: запуск в трее и подключение при входе в Windows</Description></RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(userId)}</UserId></LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userId)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec><Command>{SecurityElement.Escape(exePath)}</Command><Arguments>{Flag}</Arguments></Exec>
          </Actions>
        </Task>
        """;

    public static bool IsEnabled() => Schtasks("/query", "/tn", TaskName) == 0;

    // Создаёт задачу или заменяет её (новый путь, если папку с программой перенесли).
    // ponytail: задача для того, под кем запущен ViviTune; запуск «от имени другого администратора» её не учитывает.
    public static void Enable(string exePath)
    {
        var xml = Path.Combine(Path.GetTempPath(), "vivitune-task.xml");
        File.WriteAllText(xml, TaskXml(exePath, WindowsIdentity.GetCurrent().Name), Encoding.Unicode); // schtasks ждёт UTF-16
        try
        {
            if (Schtasks("/create", "/tn", TaskName, "/xml", xml, "/f") is int code and not 0)
                throw new InvalidOperationException($"Планировщик заданий не принял задачу (код {code})");
        }
        finally { try { File.Delete(xml); } catch { } }
    }

    public static void Disable()
    {
        if (IsEnabled() && Schtasks("/delete", "/tn", TaskName, "/f") is int code and not 0)
            throw new InvalidOperationException($"Планировщик заданий не удалил задачу (код {code})");
    }

    static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(); // оба потока сразу: иначе полный буфер одного может повесить
        p.StandardOutput.ReadToEnd();
        err.Wait();
        p.WaitForExit();
        return p.ExitCode;
    }
}
