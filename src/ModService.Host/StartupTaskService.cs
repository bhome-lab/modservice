using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ModService.Host;

[SupportedOSPlatform("windows")]
public sealed class StartupTaskService
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;
    private const uint FileNotFoundHResult = 0x80070002;

    public string TaskName => "ModService Startup";

    public bool IsEnabled()
    {
        try
        {
            dynamic service = CreateService();
            dynamic rootFolder = service.GetFolder("\\");
            dynamic task = rootFolder.GetTask(TaskName);
            return task.Enabled;
        }
        catch (COMException exception) when ((uint)exception.HResult == FileNotFoundHResult)
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Register();
            return;
        }

        Unregister();
    }

    private void Register()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the current executable path.");
        var currentUser = $"{Environment.UserDomainName}\\{Environment.UserName}";

        dynamic service = CreateService();
        dynamic rootFolder = service.GetFolder("\\");
        dynamic taskDefinition = service.NewTask(0);

        taskDefinition.RegistrationInfo.Description = "Starts ModService in the tray with elevated privileges at user logon.";
        taskDefinition.Principal.UserId = currentUser;
        taskDefinition.Principal.LogonType = TaskLogonInteractiveToken;
        taskDefinition.Principal.RunLevel = TaskRunLevelHighest;
        taskDefinition.Settings.Enabled = true;
        taskDefinition.Settings.StartWhenAvailable = true;
        taskDefinition.Settings.DisallowStartIfOnBatteries = false;
        taskDefinition.Settings.StopIfGoingOnBatteries = false;
        taskDefinition.Settings.AllowDemandStart = true;

        dynamic trigger = taskDefinition.Triggers.Create(TaskTriggerLogon);
        trigger.Enabled = true;
        trigger.UserId = currentUser;

        dynamic action = taskDefinition.Actions.Create(TaskActionExec);
        action.Path = executablePath;
        action.WorkingDirectory = AppContext.BaseDirectory;

        rootFolder.RegisterTaskDefinition(
            TaskName,
            taskDefinition,
            TaskCreateOrUpdate,
            currentUser,
            null,
            TaskLogonInteractiveToken,
            null);
    }

    private void Unregister()
    {
        try
        {
            dynamic service = CreateService();
            dynamic rootFolder = service.GetFolder("\\");
            rootFolder.DeleteTask(TaskName, 0);
        }
        catch (COMException exception) when ((uint)exception.HResult == FileNotFoundHResult)
        {
        }
    }

    private static dynamic CreateService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Task Scheduler is not available.");
        dynamic service = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Failed to create the Task Scheduler service.");
        service.Connect();
        return service;
    }
}
