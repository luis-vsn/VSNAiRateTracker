using System;
using System.Threading.Tasks;
using VSNAiRateTracker.Models;

namespace VSNAiRateTracker.Services;

public interface ITrayIconManager : IDisposable
{
    void Initialize(Action onOpenSettings, Func<Task> onRefreshNow, Action onExit);
    void UpdateState(UsageState state);
    void SetStatus(string shortText, string tooltip, double deltaHours);
    void SetAlert(string tooltip, bool isCritical = false);
}

